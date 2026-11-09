using System;
using FluentAssertions;
using VSNAiRateTracker.Models;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class BurnDownCalculatorTests
{
    private readonly IBurnDownCalculator _sut;

    public BurnDownCalculatorTests()
    {
        _sut = new BurnDownCalculator();
    }

    private WorkSchedule CreateStandardSchedule(int startHour = 9, int endHour = 17)
    {
        var schedule = new WorkSchedule { TimeZoneId = "UTC" };
        var days = new[]
        {
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday
        };

        foreach (var day in days)
        {
            schedule.Slots.Add(new WorkTimeSlot(day, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0), isEnabled: true));
        }

        return schedule;
    }

    [Fact]
    public void Calculate_WhenUsageMatchesExpectedWithinOptimalMargin_ReturnsOptimalGreenStatus()
    {
        // Arrange: Mon 09:00 to Fri 17:00 (40h total)
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        
        // Wednesday 17:00 = 24h elapsed (Expected = 24/40 = 60%)
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
        // Actual is 60.5% -> Delta = (0.605 - 0.60) * 40 = +0.2h (within optimal margin <= 0.5h)
        double percentConsumed = 60.5;

        // Act
        var result = _sut.Calculate(schedule, percentConsumed, cycleStart, cycleEnd, now);

        // Assert
        result.TotalWorkHours.Should().BeApproximately(40.0, 0.01);
        result.ElapsedWorkHours.Should().BeApproximately(24.0, 0.01);
        result.DeltaHours.Should().BeApproximately(0.2, 0.01);
        result.Status.Should().Be(RateStatus.Optimal);
        result.ShortText.Should().Be("+0.2");
        result.FormattedDelta.Should().Be("+0.2h");
        result.TooltipText.Should().Contain("Ritmo óptimo");
    }

    [Fact]
    public void Calculate_WhenUsageIsSlightlyAheadBetweenHalfHourAndFiveHours_ReturnsWarningFastAmber()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        
        // Wednesday 17:00 = 24h elapsed (Expected = 0.60)
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
        // Actual = 65% -> Delta = (0.65 - 0.60) * 40 = +2.0h (WarningFast: 0.5h to 5.0h)
        double percentConsumed = 65.0;

        // Act
        var result = _sut.Calculate(schedule, percentConsumed, cycleStart, cycleEnd, now);

        // Assert
        result.DeltaHours.Should().BeApproximately(2.0, 0.01);
        result.Status.Should().Be(RateStatus.WarningFast);
        result.ShortText.Should().Be("+2.0");
        result.FormattedDelta.Should().Be("+2.0h");
        result.TooltipText.Should().Contain("Ritmo acelerado");
    }

    [Fact]
    public void Calculate_WhenUsageExceedsFiveHoursAhead_ReturnsDangerFastRed()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        
        // Wednesday 17:00 = 24h elapsed (Expected = 0.60)
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
        // Actual = 75% -> Delta = (0.75 - 0.60) * 40 = +6.0h (> 5.0h -> DangerFast)
        double percentConsumed = 75.0;

        // Act
        var result = _sut.Calculate(schedule, percentConsumed, cycleStart, cycleEnd, now);

        // Assert
        result.DeltaHours.Should().BeApproximately(6.0, 0.01);
        result.Status.Should().Be(RateStatus.DangerFast);
        result.ShortText.Should().Be("+6.0");
        result.FormattedDelta.Should().Be("+6.0h");
        result.TooltipText.Should().Contain("Ritmo excesivo");
    }

    [Fact]
    public void Calculate_WhenUsageIsSlowerThanMinusHalfHour_ReturnsSlowBlue()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        
        // Wednesday 17:00 = 24h elapsed (Expected = 0.60)
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
        // Actual = 55% -> Delta = (0.55 - 0.60) * 40 = -2.0h (< -0.5h -> Slow)
        double percentConsumed = 55.0;

        // Act
        var result = _sut.Calculate(schedule, percentConsumed, cycleStart, cycleEnd, now);

        // Assert
        result.DeltaHours.Should().BeApproximately(-2.0, 0.01);
        result.Status.Should().Be(RateStatus.Slow);
        result.ShortText.Should().Be("-2.0");
        result.FormattedDelta.Should().Be("-2.0h");
        result.TooltipText.Should().Contain("Ritmo lento");
    }

    [Fact]
    public void Calculate_WithCustomThresholdsAndColors_AppliesProperly()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero); // 24h of 40h = 0.60

        var customThresholds = new RateThresholdConfig
        {
            OptimalMarginHours = 1.0,
            HighRateMarginHours = 3.0,
            ColorOptimalHex = "#00FF00",
            ColorWarningFastHex = "#FFAA00",
            ColorDangerFastHex = "#FF0000",
            ColorSlowHex = "#0000FF"
        };

        // Delta = (0.62 - 0.60) * 40 = +0.8h -> inside custom optimal margin (1.0h)
        var result = _sut.Calculate(schedule, 62.0, cycleStart, cycleEnd, now, customThresholds);

        result.Status.Should().Be(RateStatus.Optimal);
        result.StatusColorHex.Should().Be("#00FF00");
    }

    [Fact]
    public void Calculate_WithIndividualDailySchedules_CalculatesAsymmetricDaysCorrectly()
    {
        // User scenario: Monday from 9 to 19 (10h), Tuesday from 6 to 13 (7h), Wednesday disabled (0h)
        var schedule = new WorkSchedule { TimeZoneId = "UTC" };
        schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(19, 0), isEnabled: true));
        schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Tuesday, new TimeOnly(6, 0), new TimeOnly(13, 0), isEnabled: true));
        schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Wednesday, new TimeOnly(9, 0), new TimeOnly(17, 0), isEnabled: false));

        var cycleStart = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero); // Monday
        var cycleEnd = new DateTimeOffset(2026, 11, 4, 23, 59, 59, TimeSpan.Zero); // Wednesday

        // Total should be: Monday (10h) + Tuesday (7h) + Wednesday (0h) = 17h
        var tuesdayNoon = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero); // Mon 10h + Tue 6h = 16h elapsed

        var result = _sut.Calculate(schedule, 50.0, cycleStart, cycleEnd, tuesdayNoon);

        result.TotalWorkHours.Should().BeApproximately(17.0, 0.01);
        result.ElapsedWorkHours.Should().BeApproximately(16.0, 0.01);
    }

    [Fact]
    public void Calculate_WithSplitSlotsPerDay_ComputesExcludingLunchBreak()
    {
        var schedule = new WorkSchedule { TimeZoneId = "UTC" };
        schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(13, 0), isEnabled: true));
        schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(18, 0), isEnabled: true));

        var cycleStart = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 2, 23, 59, 59, TimeSpan.Zero);

        var duringLunch = new DateTimeOffset(2026, 11, 2, 13, 30, 0, TimeSpan.Zero);
        var resultLunch = _sut.Calculate(schedule, 25.0, cycleStart, cycleEnd, duringLunch);

        resultLunch.TotalWorkHours.Should().BeApproximately(8.0, 0.01);
        resultLunch.ElapsedWorkHours.Should().BeApproximately(4.0, 0.01);
    }

    [Fact]
    public void Calculate_OutsideWorkingHoursAtNight_ElapsedDoesNotAccumulate()
    {
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        
        var mondayEvening = new DateTimeOffset(2026, 11, 2, 17, 0, 0, TimeSpan.Zero);
        var mondayNight = new DateTimeOffset(2026, 11, 2, 23, 30, 0, TimeSpan.Zero);

        var resultAtClose = _sut.Calculate(schedule, 20.0, cycleStart, cycleEnd, mondayEvening);
        var resultAtNight = _sut.Calculate(schedule, 20.0, cycleStart, cycleEnd, mondayNight);

        resultAtClose.ElapsedWorkHours.Should().BeApproximately(8.0, 0.01);
        resultAtNight.ElapsedWorkHours.Should().BeApproximately(8.0, 0.01);
        resultAtNight.DeltaHours.Should().Be(resultAtClose.DeltaHours);
    }

    [Fact]
    public void Calculate_AtStartOfCycle_ElapsedWorkHoursIsZero()
    {
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        var now = cycleStart;

        var result = _sut.Calculate(schedule, 0.0, cycleStart, cycleEnd, now);

        result.ElapsedWorkHours.Should().Be(0.0);
        result.ExpectedRatio.Should().Be(0.0);
        result.ActualRatio.Should().Be(0.0);
        result.DeltaHours.Should().Be(0.0);
        result.Status.Should().Be(RateStatus.Optimal);
        result.FormattedDelta.Should().Be("+0.0h");
        result.ShortText.Should().Be("+0.0");
    }

    [Fact]
    public void Calculate_WhenTotalWorkHoursIsZero_AvoidsDivisionByZero()
    {
        var emptySchedule = new WorkSchedule { TimeZoneId = "UTC" };
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        var result = _sut.Calculate(emptySchedule, 50.0, cycleStart, cycleEnd, now);

        result.TotalWorkHours.Should().Be(0.0);
        result.ElapsedWorkHours.Should().Be(0.0);
        result.ExpectedRatio.Should().Be(0.0);
        result.DeltaHours.Should().Be(0.0);
        result.Status.Should().Be(RateStatus.Optimal);
    }
}
