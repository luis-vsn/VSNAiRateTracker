using System;

namespace VSNAiRateTracker.Models;

public class AnthropicUsageResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public double PercentConsumed { get; set; }
    public DateTimeOffset CycleStart { get; set; }
    public DateTimeOffset CycleEnd { get; set; }
    public long? TokensLimit { get; set; }
    public long? TokensRemaining { get; set; }
    public int StatusCode { get; set; }

    public static AnthropicUsageResult Success(
        double percentConsumed,
        DateTimeOffset cycleStart,
        DateTimeOffset cycleEnd,
        long? tokensLimit = null,
        long? tokensRemaining = null) => new()
    {
        IsSuccess = true,
        PercentConsumed = percentConsumed,
        CycleStart = cycleStart,
        CycleEnd = cycleEnd,
        TokensLimit = tokensLimit,
        TokensRemaining = tokensRemaining,
        StatusCode = 200
    };

    public static AnthropicUsageResult Failure(string errorMessage, int statusCode = 0) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage,
        StatusCode = statusCode
    };
}
