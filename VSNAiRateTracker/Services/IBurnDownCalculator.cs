using System;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public interface IBurnDownCalculator
{
    UsageState Calculate(
        WorkSchedule schedule,
        double percentConsumed,
        DateTimeOffset cycleStart,
        DateTimeOffset cycleEnd,
        DateTimeOffset now);
}

