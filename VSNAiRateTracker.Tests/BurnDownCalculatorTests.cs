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
            schedule.Slots.Add(new WorkTimeSlot(day, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0)));
        }

        return schedule;
    }

    [Fact]
    public void Calculate_WhenUsageIsAheadOfSchedule_ReturnsPositiveDeltaAndGreenStatus()
    {
        // Arrange: Mon 2026-11-02 09:00 UTC to Fri 2026-11-06 17:00 UTC (40h total)
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        
        // Wednesday 17:00 = 24h elapsed (3 days * 8h)
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
        double percentConsumed = 40.0; // Expected is 24/40 = 60%, actual is 40%

        // Act
        var result = _sut.Calculate(schedule, percentConsumed, cycleStart, cycleEnd, now);

        // Assert
        result.TotalWorkHours.Should().BeApproximately(40.0, 0.01);
        result.ElapsedWorkHours.Should().BeApproximately(24.0, 0.01);
        result.ExpectedRatio.Should().BeApproximately(0.60, 0.001);
        result.ActualRatio.Should().BeApproximately(0.40, 0.001);
        result.DeltaHours.Should().BeApproximately(8.0, 0.01); // (0.60 - 0.40) * 40 = +8h
        result.Status.Should().Be(RateStatus.Green);
        result.FormattedDelta.Should().Be("+8.0h");
        result.ShortText.Should().Be("+8h");
    }

    [Fact]
    public void Calculate_WhenUsageIsSlightlyBehindSchedule_ReturnsAmberStatus()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        
        // Wednesday 17:00 = 24h elapsed (Expected = 0.60)
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
        double percentConsumed = 64.0; // Actual = 0.64 -> Delta = (0.60 - 0.64) * 40 = -1.6h

        // Act
        var result = _sut.Calculate(schedule, percentConsumed, cycleStart, cycleEnd, now);

        // Assert
        result.DeltaHours.Should().BeApproximately(-1.6, 0.01);
        result.Status.Should().Be(RateStatus.Amber);
        result.FormattedDelta.Should().Be("-1.6h");
        result.ShortText.Should().Be("-2h");
    }

    [Fact]
    public void Calculate_WhenUsageIsSeverelyBehindSchedule_ReturnsRedStatus()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        
        // Wednesday 17:00 = 24h elapsed (Expected = 0.60)
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
        double percentConsumed = 70.0; // Actual = 0.70 -> Delta = (0.60 - 0.70) * 40 = -4.0h

        // Act
        var result = _sut.Calculate(schedule, percentConsumed, cycleStart, cycleEnd, now);

        // Assert
        result.DeltaHours.Should().BeApproximately(-4.0, 0.01);
        result.Status.Should().Be(RateStatus.Red);
        result.FormattedDelta.Should().Be("-4.0h");
        result.ShortText.Should().Be("-4h");
    }

    [Theory]
    [InlineData(-2.0, RateStatus.Amber)] // Exact boundary: -2h is Amber
    [InlineData(-2.01, RateStatus.Red)]  // Just below boundary: -2.01h is Red
    [InlineData(0.0, RateStatus.Green)]  // Exact 0h is Green
    [InlineData(-0.01, RateStatus.Amber)]// Just below 0 is Amber
    public void Calculate_StatusThresholdBoundaries_ClassifyCorrectly(double targetDelta, RateStatus expectedStatus)
    {
        // Total = 100h, Expected = 50h (50%), Target delta = (0.50 - actual) * 100 => actual = 0.50 - targetDelta/100
        var schedule = CreateStandardSchedule(9, 19); // 10h per day * 10 days
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 13, 19, 0, 0, TimeSpan.Zero); // 2 weeks (100h)
        var now = new DateTimeOffset(2026, 11, 6, 19, 0, 0, TimeSpan.Zero); // End of week 1 (50h = 50%)

        double actualRatio = 0.50 - (targetDelta / 100.0);
        double percentConsumed = actualRatio * 100.0;

        var result = _sut.Calculate(schedule, percentConsumed, cycleStart, cycleEnd, now);

        result.DeltaHours.Should().BeApproximately(targetDelta, 0.001);
        result.Status.Should().Be(expectedStatus);
    }

    [Fact]
    public void Calculate_WithSplitSlotsPerDay_ComputesExcludingLunchBreak()
    {
        // Schedule: Mon 09:00 - 13:00 (4h) and 14:00 - 18:00 (4h) -> 8h total with 1h lunch
        var schedule = new WorkSchedule { TimeZoneId = "UTC" };
        schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(13, 0)));
        schedule.Slots.Add(new WorkTimeSlot(DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(18, 0)));

        var cycleStart = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 2, 23, 59, 59, TimeSpan.Zero);

        // During lunch break at 13:30 -> elapsed should be exactly 4h (morning slot only)
        var duringLunch = new DateTimeOffset(2026, 11, 2, 13, 30, 0, TimeSpan.Zero);
        var resultLunch = _sut.Calculate(schedule, 25.0, cycleStart, cycleEnd, duringLunch);

        resultLunch.TotalWorkHours.Should().BeApproximately(8.0, 0.01);
        resultLunch.ElapsedWorkHours.Should().BeApproximately(4.0, 0.01);

        // Afternoon at 16:00 -> elapsed should be 4h morning + 2h afternoon = 6h
        var afternoon = new DateTimeOffset(2026, 11, 2, 16, 0, 0, TimeSpan.Zero);
        var resultAfternoon = _sut.Calculate(schedule, 50.0, cycleStart, cycleEnd, afternoon);

        resultAfternoon.ElapsedWorkHours.Should().BeApproximately(6.0, 0.01);
    }

    [Fact]
    public void Calculate_OutsideWorkingHoursAtNight_ElapsedDoesNotAccumulate()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        
        var mondayEvening = new DateTimeOffset(2026, 11, 2, 17, 0, 0, TimeSpan.Zero);
        var mondayNight = new DateTimeOffset(2026, 11, 2, 23, 30, 0, TimeSpan.Zero);

        // Act
        var resultAtClose = _sut.Calculate(schedule, 20.0, cycleStart, cycleEnd, mondayEvening);
        var resultAtNight = _sut.Calculate(schedule, 20.0, cycleStart, cycleEnd, mondayNight);

        // Assert
        resultAtClose.ElapsedWorkHours.Should().BeApproximately(8.0, 0.01);
        resultAtNight.ElapsedWorkHours.Should().BeApproximately(8.0, 0.01);
        resultAtNight.DeltaHours.Should().Be(resultAtClose.DeltaHours);
    }

    [Fact]
    public void Calculate_DuringWeekend_ElapsedDoesNotAccumulate()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

        var fridayNight = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);
        var saturdayAfternoon = new DateTimeOffset(2026, 11, 7, 15, 0, 0, TimeSpan.Zero);
        var sundayEvening = new DateTimeOffset(2026, 11, 8, 22, 0, 0, TimeSpan.Zero);

        // Act
        var resultFri = _sut.Calculate(schedule, 50.0, cycleStart, cycleEnd, fridayNight);
        var resultSat = _sut.Calculate(schedule, 50.0, cycleStart, cycleEnd, saturdayAfternoon);
        var resultSun = _sut.Calculate(schedule, 50.0, cycleStart, cycleEnd, sundayEvening);

        // Assert
        resultFri.ElapsedWorkHours.Should().BeApproximately(40.0, 0.01);
        resultSat.ElapsedWorkHours.Should().BeApproximately(40.0, 0.01);
        resultSun.ElapsedWorkHours.Should().BeApproximately(40.0, 0.01);
    }

    [Fact]
    public void Calculate_AtStartOfCycle_ElapsedWorkHoursIsZero()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        var now = cycleStart;

        // Act
        var result = _sut.Calculate(schedule, 0.0, cycleStart, cycleEnd, now);

        // Assert
        result.ElapsedWorkHours.Should().Be(0.0);
        result.ExpectedRatio.Should().Be(0.0);
        result.ActualRatio.Should().Be(0.0);
        result.DeltaHours.Should().Be(0.0);
        result.Status.Should().Be(RateStatus.Green);
        result.FormattedDelta.Should().Be("0.0h");
    }

    [Fact]
    public void Calculate_WhenTotalWorkHoursIsZero_AvoidsDivisionByZero()
    {
        // Arrange: Schedule with empty slots
        var emptySchedule = new WorkSchedule { TimeZoneId = "UTC" };
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        // Act
        var result = _sut.Calculate(emptySchedule, 50.0, cycleStart, cycleEnd, now);

        // Assert
        result.TotalWorkHours.Should().Be(0.0);
        result.ElapsedWorkHours.Should().Be(0.0);
        result.ExpectedRatio.Should().Be(0.0);
        result.DeltaHours.Should().Be(0.0);
        result.Status.Should().Be(RateStatus.Green);
    }

    [Fact]
    public void Calculate_ClampsPercentConsumedNegativeOrOverHundred()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero); // 24h of 40h = 60%

        // Act
        var negativeResult = _sut.Calculate(schedule, -10.0, cycleStart, cycleEnd, now);
        var overResult = _sut.Calculate(schedule, 150.0, cycleStart, cycleEnd, now);

        // Assert
        negativeResult.PercentConsumed.Should().Be(0.0);
        negativeResult.ActualRatio.Should().Be(0.0);

        overResult.PercentConsumed.Should().Be(150.0);
        overResult.ActualRatio.Should().Be(1.50);
        overResult.Status.Should().Be(RateStatus.Red);
    }

    [Fact]
    public void Calculate_TooltipFormatting_MatchesSpecification()
    {
        // Arrange
        var schedule = CreateStandardSchedule(9, 17);
        var cycleStart = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var cycleEnd = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero); // 24h = 0.60
        double percentConsumed = 48.0; // Actual = 0.48 -> Delta = (0.60 - 0.48) * 40 = +4.8h

        // Act
        var result = _sut.Calculate(schedule, percentConsumed, cycleStart, cycleEnd, now);

        // Assert
        result.TooltipText.Should().Be("Anthropic: 48% gastado | Desfase: +4.8h");
    }
}
