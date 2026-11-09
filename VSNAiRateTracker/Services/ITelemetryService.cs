using System;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public interface ITelemetryService : IDisposable
{
    bool IsEnabled { get; }
    void Initialize(TelemetryConfig config);
    void RecordUsageSync(double percentConsumed, double deltaHours, RateStatus status, long durationMs);
    void RecordApiCall(int statusCode, bool isSuccess, long durationMs);
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
}

