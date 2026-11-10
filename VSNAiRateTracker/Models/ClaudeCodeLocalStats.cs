using System;
using System.Collections.Generic;

namespace VSNAiRateTracker.Models;

/// <summary>
/// A single rate-limit window reported by the Anthropic usage endpoint
/// (e.g. five_hour, seven_day, seven_day_opus, seven_day_sonnet, extra_usage...).
/// </summary>
public sealed record UsageLimitWindow(string Name, double UtilizationPercent, DateTimeOffset? ResetsAt);

/// <summary>
/// Token usage of one model, aggregated from the local Claude Code transcripts for the current day.
/// </summary>
public sealed class ClaudeCodeModelUsage
{
    public string Model { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public long Messages { get; set; }

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>
/// Snapshot of local Claude Code activity (what /usage and /context show inside Claude Code),
/// computed from ~/.claude/projects/**/*.jsonl. Only counters and model names are read; never message content.
/// </summary>
public sealed class ClaudeCodeLocalStats
{
    /// <summary>Start of the local day the counters refer to.</summary>
    public DateTimeOffset DayStart { get; set; }

    public IReadOnlyList<ClaudeCodeModelUsage> Models { get; set; } = Array.Empty<ClaudeCodeModelUsage>();

    /// <summary>Number of distinct sessions with activity today.</summary>
    public int SessionsToday { get; set; }

    /// <summary>
    /// Context window occupancy (input + cache read + cache creation tokens) of the last request
    /// of the most recently active session. Null if there has been no activity today.
    /// </summary>
    public long? ContextTokens { get; set; }

    public string? ContextModel { get; set; }

    public DateTimeOffset? LastActivity { get; set; }
}
