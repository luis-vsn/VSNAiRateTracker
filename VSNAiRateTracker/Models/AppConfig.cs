using System;
using System.Collections.Generic;

namespace VSNAiRateTracker.Models;

public class WorkTimeSlot
{
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public bool IsEnabled { get; set; } = true;

    public WorkTimeSlot() { }

    public WorkTimeSlot(DayOfWeek dayOfWeek, TimeOnly startTime, TimeOnly endTime, bool isEnabled = true)
    {
        DayOfWeek = dayOfWeek;
        StartTime = startTime;
        EndTime = endTime;
        IsEnabled = isEnabled;
    }

    public TimeSpan Duration => (IsEnabled && EndTime >= StartTime) ? EndTime - StartTime : TimeSpan.Zero;
}

public class WorkSchedule
{
    public List<WorkTimeSlot> Slots { get; set; } = new();
    public string TimeZoneId { get; set; } = "Local";

    public TimeZoneInfo ResolveTimeZone()
    {
        if (string.IsNullOrWhiteSpace(TimeZoneId) || TimeZoneId.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    public static WorkSchedule CreateDefault(
        TimeOnly? startTime = null,
        TimeOnly? endTime = null,
        string? timeZoneId = "Local")
    {
        var start = startTime ?? new TimeOnly(9, 0);
        var end = endTime ?? new TimeOnly(18, 30);
        var schedule = new WorkSchedule
        {
            TimeZoneId = timeZoneId ?? "Local"
        };

        var allDays = new[]
        {
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday,
            DayOfWeek.Saturday,
            DayOfWeek.Sunday
        };

        foreach (var day in allDays)
        {
            bool isWorkDay = day != DayOfWeek.Saturday && day != DayOfWeek.Sunday;
            schedule.Slots.Add(new WorkTimeSlot(day, start, end, isWorkDay));
        }

        return schedule;
    }
}

public class RateThresholdConfig
{
    public double OptimalMarginHours { get; set; } = 0.5;
    public double HighRateMarginHours { get; set; } = 5.0;

    public string ColorOptimalHex { get; set; } = "#2ECC71";      // Verde
    public string ColorWarningFastHex { get; set; } = "#F39C12";  // Amarillo
    public string ColorDangerFastHex { get; set; } = "#E74C3C";   // Rojo
    public string ColorSlowHex { get; set; } = "#3498DB";         // Azul
}

public class TelemetryConfig
{
    public bool Enabled { get; set; } = false;
    public string Endpoint { get; set; } = string.Empty;
    public string Protocol { get; set; } = "HttpProtobuf"; // "HttpProtobuf" o "Grpc"
    public string Headers { get; set; } = string.Empty;    // ej. "Authorization=Basic <token>"
    public string ServiceName { get; set; } = "vsn-ai-rate-tracker";
}

public class AppConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public OAuthTokens? AuthTokens { get; set; }
    public int SyncIntervalMinutes { get; set; } = 5;
    public WorkSchedule Schedule { get; set; } = WorkSchedule.CreateDefault();
    public RateThresholdConfig Thresholds { get; set; } = new();
    public TelemetryConfig Telemetry { get; set; } = new();

    public bool HasCredentials =>
        (AuthTokens != null && !string.IsNullOrWhiteSpace(AuthTokens.AccessToken)) ||
        !string.IsNullOrWhiteSpace(ApiKey);
}

