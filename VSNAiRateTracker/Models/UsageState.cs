using System;

namespace VSNAiRateTracker.Models;

public enum RateStatus
{
    Optimal,       // |DeltaHours| <= OptimalMargin (Verde)
    WarningFast,   // OptimalMargin < DeltaHours <= HighRateMargin (Amarillo)
    DangerFast,    // DeltaHours > HighRateMargin (Rojo)
    Slow,          // DeltaHours < -OptimalMargin (Azul)

    // Aliases para compatibilidad hacia atrás
    Green = Optimal,
    Amber = WarningFast,
    Red = DangerFast
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
    public string StatusColorHex { get; set; } = "#2ECC71";

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
            if (DeltaHours > 0)
            {
                if (Math.Abs(DeltaHours) >= 10.0)
                    return $"+{DeltaHours:F0}";
                return $"+{DeltaHours:F1}";
            }
            if (DeltaHours < 0)
            {
                if (Math.Abs(DeltaHours) >= 10.0)
                    return $"{DeltaHours:F0}";
                return $"{DeltaHours:F1}";
            }
            return "0.0";
        }
    }

    public string StatusDescription
    {
        get
        {
            var loc = Services.LocalizationService.Instance;
            return Status switch
            {
                RateStatus.Optimal => loc.GetString("RateStatus.Optimal"),
                RateStatus.WarningFast => loc.GetString("RateStatus.WarningFast"),
                RateStatus.DangerFast => loc.GetString("RateStatus.DangerFast"),
                RateStatus.Slow => loc.GetString("RateStatus.Slow"),
                _ => loc.GetString("RateStatus.Optimal")
            };
        }
    }

    public string TooltipText
    {
        get
        {
            var loc = Services.LocalizationService.Instance;
            return loc.GetString("Tooltip.Format", PercentConsumed, FormattedDelta, StatusDescription);
        }
    }
}

