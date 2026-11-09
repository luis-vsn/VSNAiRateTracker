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
                return $"+{DeltaHours:F1}";
            if (DeltaHours < 0)
                return $"{DeltaHours:F1}";
            return "0.0";
        }
    }

    public string StatusDescription => Status switch
    {
        RateStatus.Optimal => "Ritmo óptimo",
        RateStatus.WarningFast => "Ritmo acelerado",
        RateStatus.DangerFast => "Ritmo excesivo",
        RateStatus.Slow => "Ritmo lento",
        _ => "En margen"
    };

    public string TooltipText =>
        $"Anthropic: {PercentConsumed:F0}% gastado | Desfase: {FormattedDelta} ({StatusDescription})";
}

