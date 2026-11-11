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
    private double _lastTotalWorkHours;
    private double _lastElapsedWorkHours;
    private double _lastExpectedRatioPercent;

    private double _lastSevenDayUtilization;
    private double _lastSevenDayResetsInHours;
    private double _lastFiveHourUtilization;
    private double _lastFiveHourResetsInHours;
    private double _lastSevenDayOpusUtilization;
    private double _lastSevenDaySonnetUtilization;
    private double _lastExtraUsageEnabled;
    private double _lastExtraUsageUtilization;
    private double _lastExtraUsageUsedCredits;
    private double _lastSpendUsedAmount;
    private double _lastSpendPercent;
    private double _lastExtraUsageMonthlyLimit;
    private double _lastSpendLimitAmount;

    // Snapshots read by the multi-dimensional gauges (replaced atomically on every sync)
    private volatile IReadOnlyList<UsageLimitWindow> _lastLimits = Array.Empty<UsageLimitWindow>();
    private volatile ClaudeCodeLocalStats? _lastClaudeCodeStats;

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

        // Primary Rate Tracker burndown gauges
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.utilization_percent", () => _lastPercentConsumed, "%", "Current primary rate tracker quota utilization percent");
        _meter.CreateObservableGauge("vsn.rate_tracker.delta_hours", () => _lastDeltaHours, "hours", "Difference between quota spent and working hours elapsed");
        _meter.CreateObservableGauge("vsn.rate_tracker.status_code", () => _lastStatusValue, description: "Current rate status: 0=Optimal, 1=WarningFast, 2=DangerFast, 3=Slow");
        _meter.CreateObservableGauge("vsn.rate_tracker.total_work_hours", () => _lastTotalWorkHours, "hours", "Total working hours scheduled in the cycle");
        _meter.CreateObservableGauge("vsn.rate_tracker.elapsed_work_hours", () => _lastElapsedWorkHours, "hours", "Elapsed working hours in the cycle");
        _meter.CreateObservableGauge("vsn.rate_tracker.expected_ratio_percent", () => _lastExpectedRatioPercent, "%", "Expected quota consumption percentage based on elapsed working hours");

        // Anthropic detailed multi-window metrics
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.seven_day_utilization_percent", () => _lastSevenDayUtilization, "%", "Anthropic 7-day rolling window utilization percent");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.seven_day_resets_in_hours", () => _lastSevenDayResetsInHours, "hours", "Hours remaining until Anthropic 7-day reset");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.five_hour_utilization_percent", () => _lastFiveHourUtilization, "%", "Anthropic 5-hour window utilization percent");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.five_hour_resets_in_hours", () => _lastFiveHourResetsInHours, "hours", "Hours remaining until Anthropic 5-hour reset");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.seven_day_opus_utilization_percent", () => _lastSevenDayOpusUtilization, "%", "Anthropic 7-day Opus utilization percent");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.seven_day_sonnet_utilization_percent", () => _lastSevenDaySonnetUtilization, "%", "Anthropic 7-day Sonnet utilization percent");

        // Anthropic extra usage & spend
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.extra_usage_enabled", () => _lastExtraUsageEnabled, description: "1 if extra usage credits are enabled, 0 otherwise");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.extra_usage_utilization_percent", () => _lastExtraUsageUtilization, "%", "Extra usage credit utilization percent");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.extra_usage_used_credits", () => _lastExtraUsageUsedCredits, description: "Extra usage credits used");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.spend_used_amount", () => _lastSpendUsedAmount, description: "Spend budget amount used");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.spend_percent", () => _lastSpendPercent, "%", "Spend budget percentage used");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.extra_usage_monthly_limit", () => _lastExtraUsageMonthlyLimit, description: "Extra usage monthly credit limit");
        _meter.CreateObservableGauge("vsn.rate_tracker.anthropic.spend_limit_amount", () => _lastSpendLimitAmount, description: "Spend budget limit amount");

        // Every limit window of the account (Usage tab), one series per window: limit="five_hour" | "seven_day" | "seven_day_opus" | ...
        _meter.CreateObservableGauge<double>("vsn.rate_tracker.anthropic.limit.utilization_percent", ObserveLimitUtilization, "%", "Utilization percent of each Anthropic rate-limit window");
        _meter.CreateObservableGauge<double>("vsn.rate_tracker.anthropic.limit.resets_in_hours", ObserveLimitResets, "hours", "Hours remaining until each Anthropic rate-limit window resets");

        // Local Claude Code activity (what /usage and /context show), read from ~/.claude/projects transcripts
        _meter.CreateObservableGauge<long>("vsn.rate_tracker.claude_code.tokens_today", ObserveClaudeCodeTokens, "tokens", "Claude Code tokens used today on this machine, by model and token type");
        _meter.CreateObservableGauge<long>("vsn.rate_tracker.claude_code.messages_today", ObserveClaudeCodeMessages, description: "Claude Code API responses received today on this machine, by model");
        _meter.CreateObservableGauge<long>("vsn.rate_tracker.claude_code.sessions_today", ObserveClaudeCodeSessions, description: "Claude Code sessions with activity today on this machine");
        _meter.CreateObservableGauge<long>("vsn.rate_tracker.claude_code.context_tokens", ObserveClaudeCodeContext, "tokens", "Context window tokens in use by the most recently active Claude Code session");
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
                var appVersion = typeof(TelemetryService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

                var resourceBuilder = ResourceBuilder.CreateDefault()
                    .AddService(serviceName, serviceVersion: appVersion, serviceInstanceId: machineName)
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
                            opt.ExportProcessorType = ExportProcessorType.Simple;
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

        if (trimmed.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
        {
            var value = trimmed["Authorization:".Length..].TrimStart();
            return $"Authorization={value}";
        }

        if (trimmed.StartsWith("Authorization=", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return $"Authorization={trimmed}";
        }

        // If user entered "instanceId:token" (contains ':' but no '=')
        if (trimmed.Contains(':') && !trimmed.Contains('='))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(trimmed);
            var b64 = Convert.ToBase64String(bytes);
            return $"Authorization=Basic {b64}";
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

    private IEnumerable<Measurement<double>> ObserveLimitUtilization()
    {
        foreach (var window in _lastLimits)
        {
            yield return new Measurement<double>(window.UtilizationPercent, new KeyValuePair<string, object?>("limit", window.Name));
        }
    }

    private IEnumerable<Measurement<double>> ObserveLimitResets()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var window in _lastLimits)
        {
            if (!window.ResetsAt.HasValue) continue;
            var hours = Math.Max(0, (window.ResetsAt.Value - now).TotalHours);
            yield return new Measurement<double>(hours, new KeyValuePair<string, object?>("limit", window.Name));
        }
    }

    private IEnumerable<Measurement<long>> ObserveClaudeCodeTokens()
    {
        var stats = _lastClaudeCodeStats;
        if (stats == null) yield break;

        foreach (var model in stats.Models)
        {
            yield return TokenMeasurement(model.InputTokens, model.Model, "input");
            yield return TokenMeasurement(model.OutputTokens, model.Model, "output");
            yield return TokenMeasurement(model.CacheReadTokens, model.Model, "cache_read");
            yield return TokenMeasurement(model.CacheCreationTokens, model.Model, "cache_creation");
        }
    }

    private static Measurement<long> TokenMeasurement(long value, string model, string type) =>
        new(value, new KeyValuePair<string, object?>("model", model), new KeyValuePair<string, object?>("type", type));

    private IEnumerable<Measurement<long>> ObserveClaudeCodeMessages()
    {
        var stats = _lastClaudeCodeStats;
        if (stats == null) yield break;

        foreach (var model in stats.Models)
        {
            yield return new Measurement<long>(model.Messages, new KeyValuePair<string, object?>("model", model.Model));
        }
    }

    private IEnumerable<Measurement<long>> ObserveClaudeCodeSessions()
    {
        var stats = _lastClaudeCodeStats;
        if (stats == null) yield break;

        yield return new Measurement<long>(stats.SessionsToday);
    }

    private IEnumerable<Measurement<long>> ObserveClaudeCodeContext()
    {
        var stats = _lastClaudeCodeStats;
        if (stats?.ContextTokens == null) yield break;

        yield return new Measurement<long>(stats.ContextTokens.Value, new KeyValuePair<string, object?>("model", stats.ContextModel ?? "unknown"));
    }

    public void RecordClaudeCodeStats(ClaudeCodeLocalStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        _lastClaudeCodeStats = stats;
    }

    public void RecordUsageSync(AnthropicUsageResult usageResult, UsageState usageState, long durationMs)
    {
        ArgumentNullException.ThrowIfNull(usageResult);
        ArgumentNullException.ThrowIfNull(usageState);

        _lastPercentConsumed = usageResult.PercentConsumed;
        _lastDeltaHours = usageState.DeltaHours;
        _lastStatusValue = (int)usageState.Status;
        _lastTotalWorkHours = usageState.TotalWorkHours;
        _lastElapsedWorkHours = usageState.ElapsedWorkHours;
        _lastExpectedRatioPercent = usageState.ExpectedRatio * 100.0;

        _lastSevenDayUtilization = usageResult.SevenDayUtilization ?? usageResult.PercentConsumed;
        if (usageResult.SevenDayResetsAt.HasValue)
        {
            _lastSevenDayResetsInHours = Math.Max(0, (usageResult.SevenDayResetsAt.Value - DateTimeOffset.UtcNow).TotalHours);
        }
        _lastFiveHourUtilization = usageResult.FiveHourUtilization ?? 0.0;
        if (usageResult.FiveHourResetsAt.HasValue)
        {
            _lastFiveHourResetsInHours = Math.Max(0, (usageResult.FiveHourResetsAt.Value - DateTimeOffset.UtcNow).TotalHours);
        }

        _lastSevenDayOpusUtilization = usageResult.SevenDayOpusUtilization ?? 0.0;
        _lastSevenDaySonnetUtilization = usageResult.SevenDaySonnetUtilization ?? 0.0;

        _lastExtraUsageEnabled = (usageResult.ExtraUsageEnabled == true) ? 1.0 : 0.0;
        _lastExtraUsageUtilization = usageResult.ExtraUsageUtilization ?? 0.0;
        _lastExtraUsageUsedCredits = usageResult.ExtraUsageUsedCredits ?? 0.0;
        _lastSpendUsedAmount = usageResult.SpendUsedAmount ?? 0.0;
        _lastSpendPercent = usageResult.SpendPercent ?? 0.0;
        _lastExtraUsageMonthlyLimit = usageResult.ExtraUsageMonthlyLimit ?? 0.0;
        _lastSpendLimitAmount = usageResult.SpendLimitAmount ?? 0.0;
        _lastLimits = usageResult.Limits ?? Array.Empty<UsageLimitWindow>();

        _syncCounter.Add(1, new KeyValuePair<string, object?>("status", usageState.Status.ToString()));
        _syncDuration.Record(durationMs, new KeyValuePair<string, object?>("status", usageState.Status.ToString()));

        using var activity = _activitySource.StartActivity("SyncUsageCycle");
        activity?.SetTag("anthropic.percent_consumed", usageResult.PercentConsumed);
        activity?.SetTag("anthropic.seven_day_utilization", _lastSevenDayUtilization);
        activity?.SetTag("anthropic.seven_day_resets_in_hours", _lastSevenDayResetsInHours);
        activity?.SetTag("anthropic.five_hour_utilization", _lastFiveHourUtilization);
        activity?.SetTag("anthropic.five_hour_resets_in_hours", _lastFiveHourResetsInHours);
        activity?.SetTag("vsn.delta_hours", usageState.DeltaHours);
        activity?.SetTag("vsn.total_work_hours", usageState.TotalWorkHours);
        activity?.SetTag("vsn.elapsed_work_hours", usageState.ElapsedWorkHours);
        activity?.SetTag("vsn.expected_ratio_percent", _lastExpectedRatioPercent);
        activity?.SetTag("vsn.status", usageState.Status.ToString());

        if (usageResult.ExtraUsageEnabled.HasValue)
        {
            activity?.SetTag("anthropic.extra_usage_enabled", usageResult.ExtraUsageEnabled.Value);
        }
        if (usageResult.SpendUsedAmount.HasValue)
        {
            activity?.SetTag("anthropic.spend_used_amount", usageResult.SpendUsedAmount.Value);
        }
    }

    public void RecordUsageSync(double percentConsumed, double deltaHours, RateStatus status, long durationMs)
    {
        _lastPercentConsumed = percentConsumed;
        _lastDeltaHours = deltaHours;
        _lastStatusValue = (int)status;
        _lastSevenDayUtilization = percentConsumed;

        _syncCounter.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));
        _syncDuration.Record(durationMs, new KeyValuePair<string, object?>("status", status.ToString()));

        using var activity = _activitySource.StartActivity("SyncUsageCycle");
        activity?.SetTag("anthropic.percent_consumed", percentConsumed);
        activity?.SetTag("anthropic.seven_day_utilization", percentConsumed);
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
