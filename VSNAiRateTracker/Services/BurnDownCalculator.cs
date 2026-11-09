using System;
using System.Linq;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public class BurnDownCalculator : IBurnDownCalculator
{
    private const double Epsilon = 1e-6;

    public UsageState Calculate(
        WorkSchedule schedule,
        double percentConsumed,
        DateTimeOffset cycleStart,
        DateTimeOffset cycleEnd,
        DateTimeOffset now,
        RateThresholdConfig? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var cfg = thresholds ?? new RateThresholdConfig();
        var optimalMargin = Math.Max(0, cfg.OptimalMarginHours);
        var highMargin = Math.Max(optimalMargin, cfg.HighRateMarginHours);

        var normalizedPercent = percentConsumed < 0 ? 0 : percentConsumed;
        var state = new UsageState
        {
            PercentConsumed = normalizedPercent,
            CycleStart = cycleStart,
            CycleEnd = cycleEnd,
            CalculatedAt = now
        };

        if (cycleEnd <= cycleStart || schedule.Slots.Count == 0)
        {
            state.TotalWorkHours = 0;
            state.ElapsedWorkHours = 0;
            state.ExpectedRatio = 0;
            state.ActualRatio = normalizedPercent / 100.0;
            state.DeltaHours = 0;
            state.Status = RateStatus.Optimal;
            state.StatusColorHex = cfg.ColorOptimalHex;
            return state;
        }

        var tz = schedule.ResolveTimeZone();
        var localStart = TimeZoneInfo.ConvertTime(cycleStart, tz);
        var localEnd = TimeZoneInfo.ConvertTime(cycleEnd, tz);

        var clampedNow = now < cycleStart ? cycleStart : (now > cycleEnd ? cycleEnd : now);
        var localNow = TimeZoneInfo.ConvertTime(clampedNow, tz);

        var totalHours = CalculateWorkingHours(schedule, localStart, localEnd, tz);
        var elapsedHours = CalculateWorkingHours(schedule, localStart, localNow, tz);

        state.TotalWorkHours = totalHours;
        state.ElapsedWorkHours = elapsedHours;

        if (totalHours > 0)
        {
            var expectedRatio = elapsedHours / totalHours;
            var actualRatio = normalizedPercent / 100.0;
            // DeltaHours: positive when burning faster than elapsed time, negative when burning slower
            var deltaHours = Math.Round((actualRatio - expectedRatio) * totalHours, 6);

            state.ExpectedRatio = expectedRatio;
            state.ActualRatio = actualRatio;
            state.DeltaHours = deltaHours;

            if (Math.Abs(deltaHours) <= optimalMargin + Epsilon)
            {
                state.Status = RateStatus.Optimal;
                state.StatusColorHex = cfg.ColorOptimalHex;
            }
            else if (deltaHours > optimalMargin && deltaHours <= highMargin + Epsilon)
            {
                state.Status = RateStatus.WarningFast;
                state.StatusColorHex = cfg.ColorWarningFastHex;
            }
            else if (deltaHours > highMargin)
            {
                state.Status = RateStatus.DangerFast;
                state.StatusColorHex = cfg.ColorDangerFastHex;
            }
            else // deltaHours < -optimalMargin
            {
                state.Status = RateStatus.Slow;
                state.StatusColorHex = cfg.ColorSlowHex;
            }
        }
        else
        {
            state.ExpectedRatio = 0;
            state.ActualRatio = normalizedPercent / 100.0;
            state.DeltaHours = 0;
            state.Status = RateStatus.Optimal;
            state.StatusColorHex = cfg.ColorOptimalHex;
        }

        return state;
    }

    private static double CalculateWorkingHours(
        WorkSchedule schedule,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeZoneInfo tz)
    {
        if (to <= from)
            return 0;

        double totalHours = 0;
        var currentDay = from.Date;
        var lastDay = to.Date;

        while (currentDay <= lastDay)
        {
            var dayOfWeek = currentDay.DayOfWeek;
            var matchingSlots = schedule.Slots.Where(s => s.DayOfWeek == dayOfWeek && s.IsEnabled);

            foreach (var slot in matchingSlots)
            {
                if (slot.EndTime <= slot.StartTime)
                    continue;

                var slotStart = new DateTimeOffset(
                    currentDay.Year, currentDay.Month, currentDay.Day,
                    slot.StartTime.Hour, slot.StartTime.Minute, slot.StartTime.Second,
                    tz.GetUtcOffset(currentDay));

                var slotEnd = new DateTimeOffset(
                    currentDay.Year, currentDay.Month, currentDay.Day,
                    slot.EndTime.Hour, slot.EndTime.Minute, slot.EndTime.Second,
                    tz.GetUtcOffset(currentDay));

                var overlapStart = slotStart > from ? slotStart : from;
                var overlapEnd = slotEnd < to ? slotEnd : to;

                if (overlapEnd > overlapStart)
                {
                    totalHours += (overlapEnd - overlapStart).TotalHours;
                }
            }

            currentDay = currentDay.AddDays(1);
        }

        return totalHours;
    }
}
