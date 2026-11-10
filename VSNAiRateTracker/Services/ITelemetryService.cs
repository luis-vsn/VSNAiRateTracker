using System;
using System.Diagnostics;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public interface ITelemetryService : IDisposable
{
    bool IsEnabled { get; }
    void Initialize(TelemetryConfig config);
    Activity? StartActivity(string name);
    void RecordUsageSync(AnthropicUsageResult usageResult, UsageState usageState, long durationMs);
    void RecordUsageSync(double percentConsumed, double deltaHours, RateStatus status, long durationMs);
    void RecordClaudeCodeStats(ClaudeCodeLocalStats stats);
    void RecordApiCall(int statusCode, bool isSuccess, long durationMs);
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
    void ForceFlush(int timeoutMs = 5000);
}

