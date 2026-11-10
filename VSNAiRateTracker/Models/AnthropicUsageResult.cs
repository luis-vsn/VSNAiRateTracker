using System;
using System.Collections.Generic;

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
    public bool IsSessionExpired => StatusCode == 401;

    // Detailed metrics from Anthropic OAuth / Claude Code endpoint
    public double? FiveHourUtilization { get; set; }
    public DateTimeOffset? FiveHourResetsAt { get; set; }
    public double? SevenDayUtilization { get; set; }
    public DateTimeOffset? SevenDayResetsAt { get; set; }
    public double? SevenDayOpusUtilization { get; set; }
    public double? SevenDaySonnetUtilization { get; set; }
    public bool? ExtraUsageEnabled { get; set; }
    public double? ExtraUsageUtilization { get; set; }
    public double? ExtraUsageUsedCredits { get; set; }
    public double? ExtraUsageMonthlyLimit { get; set; }
    public double? SpendUsedAmount { get; set; }
    public double? SpendLimitAmount { get; set; }
    public double? SpendPercent { get; set; }
    public string? Currency { get; set; }

    /// <summary>
    /// Every rate-limit window returned by the usage endpoint, keyed by its API name
    /// (five_hour, seven_day, seven_day_opus, seven_day_sonnet, extra_usage and any window added in the future).
    /// </summary>
    public IReadOnlyList<UsageLimitWindow> Limits { get; set; } = Array.Empty<UsageLimitWindow>();

    public static AnthropicUsageResult Success(
        double percentConsumed,
        DateTimeOffset cycleStart,
        DateTimeOffset cycleEnd,
        long? tokensLimit = null,
        long? tokensRemaining = null,
        double? fiveHourUtilization = null,
        DateTimeOffset? fiveHourResetsAt = null,
        double? sevenDayUtilization = null,
        DateTimeOffset? sevenDayResetsAt = null,
        double? sevenDayOpusUtilization = null,
        double? sevenDaySonnetUtilization = null,
        bool? extraUsageEnabled = null,
        double? extraUsageUtilization = null,
        double? extraUsageUsedCredits = null,
        double? extraUsageMonthlyLimit = null,
        double? spendUsedAmount = null,
        double? spendLimitAmount = null,
        double? spendPercent = null,
        string? currency = null) => new()
    {
        IsSuccess = true,
        PercentConsumed = percentConsumed,
        CycleStart = cycleStart,
        CycleEnd = cycleEnd,
        TokensLimit = tokensLimit,
        TokensRemaining = tokensRemaining,
        StatusCode = 200,
        FiveHourUtilization = fiveHourUtilization,
        FiveHourResetsAt = fiveHourResetsAt,
        SevenDayUtilization = sevenDayUtilization,
        SevenDayResetsAt = sevenDayResetsAt,
        SevenDayOpusUtilization = sevenDayOpusUtilization,
        SevenDaySonnetUtilization = sevenDaySonnetUtilization,
        ExtraUsageEnabled = extraUsageEnabled,
        ExtraUsageUtilization = extraUsageUtilization,
        ExtraUsageUsedCredits = extraUsageUsedCredits,
        ExtraUsageMonthlyLimit = extraUsageMonthlyLimit,
        SpendUsedAmount = spendUsedAmount,
        SpendLimitAmount = spendLimitAmount,
        SpendPercent = spendPercent,
        Currency = currency
    };

    public static AnthropicUsageResult Failure(string errorMessage, int statusCode = 0) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage,
        StatusCode = statusCode
    };
}

