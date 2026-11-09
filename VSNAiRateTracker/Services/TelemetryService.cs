using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public class TelemetryService : ITelemetryService
{
    public const string MeterName = "VSNAiRateTracker";
    public const string ActivitySourceName = "VSNAiRateTracker";

    private readonly object _lock = new();
    private MeterProvider? _meterProvider;
    private TracerProvider? _tracerProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;

    private readonly Meter _meter;
    private readonly ActivitySource _activitySource;

    private readonly Counter<long> _syncCounter;
    private readonly Histogram<long> _syncDuration;
    private readonly Histogram<long> _apiDuration;

    private double _lastPercentConsumed;
    private double _lastDeltaHours;
    private int _lastStatusValue;

    private bool _isEnabled;
    private bool _isDisposed;

    public bool IsEnabled => _isEnabled;

    public TelemetryService()
    {
        _meter = new Meter(MeterName, "2.0.0");
        _activitySource = new ActivitySource(ActivitySourceName, "2.0.0");

        _syncCounter = _meter.CreateCounter<long>("vsn.rate_tracker.syncs.total", description: "Total synchronization cycles");
        _syncDuration = _meter.CreateHistogram<long>("vsn.rate_tracker.sync.duration_ms", "ms", "Duration of sync cycle");
        _apiDuration = _meter.CreateHistogram<long>("vsn.rate_tracker.api.duration_ms", "ms", "Duration of Anthropic API call");

        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.utilization_percent", () => _lastPercentConsumed, "%", "Current Anthropic quota utilization percent");
        _meter.CreateObservableGauge("vsn.rate_tracker.delta_hours", () => _lastDeltaHours, "hours", "Difference between quota spent and working hours elapsed");
        _meter.CreateObservableGauge("vsn.rate_tracker.status_code", () => _lastStatusValue, description: "Current rate status: 0=Optimal, 1=WarningFast, 2=DangerFast, 3=Slow");
    }

    public void Initialize(TelemetryConfig config)
    {
        lock (_lock)
        {
            ShutdownProviders();

            if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.Endpoint))
            {
                _isEnabled = false;
                return;
            }

            try
            {
                var endpointUri = new Uri(config.Endpoint);
                var serviceName = string.IsNullOrWhiteSpace(config.ServiceName) ? "vsn-ai-rate-tracker" : config.ServiceName;
                var resourceBuilder = ResourceBuilder.CreateDefault()
                    .AddService(serviceName, serviceVersion: "2.0.0")
                    .AddTelemetrySdk();

                var protocol = OtlpExportProtocol.HttpProtobuf;
                if (string.Equals(config.Protocol, "Grpc", StringComparison.OrdinalIgnoreCase))
                {
                    protocol = OtlpExportProtocol.Grpc;
                }

                // 1. Meter Provider
                _meterProvider = Sdk.CreateMeterProviderBuilder()
                    .SetResourceBuilder(resourceBuilder)
                    .AddMeter(MeterName)
                    .AddOtlpExporter(opt =>
                    {
                        opt.Endpoint = endpointUri;
                        opt.Protocol = protocol;
                        if (!string.IsNullOrWhiteSpace(config.Headers))
                        {
                            opt.Headers = config.Headers;
                        }
                    })
                    .Build();

                // 2. Tracer Provider
                _tracerProvider = Sdk.CreateTracerProviderBuilder()
                    .SetResourceBuilder(resourceBuilder)
                    .AddSource(ActivitySourceName)
                    .AddOtlpExporter(opt =>
                    {
                        opt.Endpoint = endpointUri;
                        opt.Protocol = protocol;
                        if (!string.IsNullOrWhiteSpace(config.Headers))
                        {
                            opt.Headers = config.Headers;
                        }
                    })
                    .Build();

                // 3. Logger Factory
                _loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.AddOpenTelemetry(logging =>
                    {
                        logging.SetResourceBuilder(resourceBuilder);
                        logging.AddOtlpExporter(opt =>
                        {
                            opt.Endpoint = endpointUri;
                            opt.Protocol = protocol;
                            if (!string.IsNullOrWhiteSpace(config.Headers))
                            {
                                opt.Headers = config.Headers;
                            }
                        });
                    });
                });

                _logger = _loggerFactory.CreateLogger("VSNAiRateTracker");
                _isEnabled = true;
                _logger.LogInformation("OpenTelemetry telemetry exporter successfully initialized to {Endpoint} with protocol {Protocol}", endpointUri, protocol);
            }
            catch (Exception ex)
            {
                _isEnabled = false;
                Debug.WriteLine($"Failed to initialize OpenTelemetry: {ex.Message}");
            }
        }
    }

    public void RecordUsageSync(double percentConsumed, double deltaHours, RateStatus status, long durationMs)
    {
        _lastPercentConsumed = percentConsumed;
        _lastDeltaHours = deltaHours;
        _lastStatusValue = (int)status;

        _syncCounter.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));
        _syncDuration.Record(durationMs);

        using var activity = _activitySource.StartActivity("SyncUsageCycle");
        activity?.SetTag("anthropic.percent_consumed", percentConsumed);
        activity?.SetTag("vsn.delta_hours", deltaHours);
        activity?.SetTag("vsn.status", status.ToString());
    }

    public void RecordApiCall(int statusCode, bool isSuccess, long durationMs)
    {
        _apiDuration.Record(durationMs,
            new KeyValuePair<string, object?>("status_code", statusCode),
            new KeyValuePair<string, object?>("success", isSuccess));
    }

    public void LogInfo(string message) => _logger?.LogInformation(message);
    public void LogWarning(string message) => _logger?.LogWarning(message);
    public void LogError(string message, Exception? ex = null) => _logger?.LogError(ex, message);

    private void ShutdownProviders()
    {
        _meterProvider?.Dispose();
        _meterProvider = null;
        _tracerProvider?.Dispose();
        _tracerProvider = null;
        _loggerFactory?.Dispose();
        _loggerFactory = null;
        _logger = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        lock (_lock)
        {
            ShutdownProviders();
            _meter.Dispose();
            _activitySource.Dispose();
        }
    }
}
