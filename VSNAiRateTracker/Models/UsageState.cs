using System;

namespace VSNAiRateTracker.Models;

public enum RateStatus
{
    Green,   // DeltaHours >= 0 (En margen o positivo)
    Amber,   // 0 > DeltaHours >= -2.0 (Retraso leve)
    Red      // DeltaHours < -2.0 (Retraso severo)
}

public class UsageState
{
    public double PercentConsumed { get; set; }
    public DateTimeOffset CycleStart { get; set; }
    public DateTimeOffset CycleEnd { get; set; }
    public DateTimeOffset CalculatedAt { get; set; }

    public double TotalWorkHours { get; set; }
    public double ElapsedWorkHours { get; set; }
    public double ExpectedRatio { get; set; }
    public double ActualRatio { get; set; }
    public double DeltaHours { get; set; }
    public RateStatus Status { get; set; }

    public string FormattedDelta
    {
        get
        {
            if (DeltaHours > 0)
                return $"+{DeltaHours:F1}h";
            if (DeltaHours < 0)
                return $"{DeltaHours:F1}h";
            return "0.0h";
        }
    }

    public string ShortText
    {
        get
        {
            var rounded = Math.Round(DeltaHours);
            if (rounded > 0)
                return $"+{rounded:F0}h";
            if (rounded < 0)
                return $"{rounded:F0}h";
            return "0h";
        }
    }

    public string TooltipText =>
        $"Anthropic: {PercentConsumed:F0}% gastado | Desfase: {FormattedDelta}";
}

