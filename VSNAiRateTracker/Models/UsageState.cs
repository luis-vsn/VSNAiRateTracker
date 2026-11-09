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
            var abs = Math.Abs(DeltaHours);
            var sign = DeltaHours >= 0 ? "+" : "-";
            if (abs >= 10.0)
            {
                return $"{sign}{Math.Round(abs):F0}h";
            }
            return $"{sign}{abs:F1}h";
        }
    }

    public string ShortText
    {
        get
        {
            var abs = Math.Abs(DeltaHours);
            var sign = DeltaHours >= 0 ? "+" : "-";
            if (abs >= 10.0)
            {
                return $"{sign}{Math.Round(abs):F0}";
            }
            return $"{sign}{abs:F1}";
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

