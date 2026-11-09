using System;
using System.Collections.Generic;

namespace VSNAiRateTracker.Models;

public class WorkTimeSlot
{
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    public WorkTimeSlot() { }

    public WorkTimeSlot(DayOfWeek dayOfWeek, TimeOnly startTime, TimeOnly endTime)
    {
        DayOfWeek = dayOfWeek;
        StartTime = startTime;
        EndTime = endTime;
    }

    public TimeSpan Duration => EndTime >= StartTime ? EndTime - StartTime : TimeSpan.Zero;
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

        var workingDays = new[]
        {
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday
        };

        foreach (var day in workingDays)
        {
            schedule.Slots.Add(new WorkTimeSlot(day, start, end));
        }

        return schedule;
    }
}

public class AppConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public int SyncIntervalMinutes { get; set; } = 5;
    public WorkSchedule Schedule { get; set; } = WorkSchedule.CreateDefault();
}
