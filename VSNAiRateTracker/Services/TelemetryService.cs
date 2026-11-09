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

            // Check config or fallback to standard OTEL environment variables
            var endpoint = !string.IsNullOrWhiteSpace(config?.Endpoint)
                ? config.Endpoint
                : Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

            var headers = !string.IsNullOrWhiteSpace(config?.Headers)
                ? config.Headers
                : Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");

            var serviceName = !string.IsNullOrWhiteSpace(config?.ServiceName)
                ? config.ServiceName
                : (Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "vsn-ai-rate-tracker");

            bool isEnabled = (config != null && config.Enabled) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

            if (!isEnabled || string.IsNullOrWhiteSpace(endpoint))
            {
                _isEnabled = false;
                return;
            }

            try
            {
                var normalizedHeaders = NormalizeHeaders(headers);
                var metricsUri = ResolveSignalEndpoint(endpoint, "v1/metrics");
                var tracesUri = ResolveSignalEndpoint(endpoint, "v1/traces");
                var logsUri = ResolveSignalEndpoint(endpoint, "v1/logs");

                var machineName = Environment.MachineName;

                var resourceBuilder = ResourceBuilder.CreateDefault()
                    .AddService(serviceName, serviceVersion: "0.0.1")
                    .AddAttributes(new[]
                    {
                        new KeyValuePair<string, object>("host.name", machineName),
                        new KeyValuePair<string, object>("service.name", serviceName),
                        new KeyValuePair<string, object>("service.version", "0.0.1")
                    })
                    .AddTelemetrySdk();

                const OtlpExportProtocol protocol = OtlpExportProtocol.HttpProtobuf;

                // 1. Meter Provider (pointed to /v1/metrics)
                _meterProvider = Sdk.CreateMeterProviderBuilder()
                    .SetResourceBuilder(resourceBuilder)
                    .AddMeter(MeterName)
                    .AddOtlpExporter(opt =>
                    {
                        opt.Endpoint = metricsUri;
                        opt.Protocol = protocol;
                        if (!string.IsNullOrWhiteSpace(normalizedHeaders))
                        {
                            opt.Headers = normalizedHeaders;
                        }
                    })
                    .Build();

                // 2. Tracer Provider (pointed to /v1/traces)
                _tracerProvider = Sdk.CreateTracerProviderBuilder()
                    .SetResourceBuilder(resourceBuilder)
                    .AddSource(ActivitySourceName)
                    .AddOtlpExporter(opt =>
                    {
                        opt.Endpoint = tracesUri;
                        opt.Protocol = protocol;
                        if (!string.IsNullOrWhiteSpace(normalizedHeaders))
                        {
                            opt.Headers = normalizedHeaders;
                        }
                    })
                    .Build();

                // 3. Logger Factory (pointed to /v1/logs)
                _loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.AddOpenTelemetry(logging =>
                    {
                        logging.SetResourceBuilder(resourceBuilder);
                        logging.AddOtlpExporter(opt =>
                        {
                            opt.Endpoint = logsUri;
                            opt.Protocol = protocol;
                            if (!string.IsNullOrWhiteSpace(normalizedHeaders))
                            {
                                opt.Headers = normalizedHeaders;
                            }
                        });
                    });
                });

                _logger = _loggerFactory.CreateLogger("VSNAiRateTracker");
                _isEnabled = true;
                _logger.LogInformation("OpenTelemetry telemetry exporter successfully initialized to {Endpoint} with protocol {Protocol}", endpoint, protocol);
            }
            catch (Exception ex)
            {
                _isEnabled = false;
                Debug.WriteLine($"Failed to initialize OpenTelemetry: {ex.Message}");
            }
        }
    }

    public static string NormalizeHeaders(string? headers)
    {
        if (string.IsNullOrWhiteSpace(headers)) return string.Empty;
        var trimmed = headers.Trim().Trim('"', '\'');
        if (trimmed.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return $"Authorization={trimmed}";
        }
        return trimmed;
    }

    public static Uri ResolveSignalEndpoint(string rawEndpoint, string signalPath)
    {
        var trimmed = rawEndpoint.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/" + signalPath, StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }
        if (trimmed.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/v1/metrics", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase))
        {
            var idx = trimmed.LastIndexOf("/v1/", StringComparison.OrdinalIgnoreCase);
            trimmed = trimmed[..idx];
        }
        return new Uri($"{trimmed}/{signalPath}");
    }

    public Activity? StartActivity(string name)
    {
        if (!_isEnabled) return null;
        return _activitySource.StartActivity(name);
    }

    public void ForceFlush(int timeoutMs = 5000)
    {
        if (!_isEnabled) return;
        try
        {
            _tracerProvider?.ForceFlush(timeoutMs);
            _meterProvider?.ForceFlush(timeoutMs);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to flush telemetry: {ex.Message}");
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
