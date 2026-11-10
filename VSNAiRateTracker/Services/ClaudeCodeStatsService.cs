using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public interface IClaudeCodeStatsService
{
    /// <summary>
    /// Aggregates today's local Claude Code activity. Returns null when Claude Code has no data on this machine.
    /// </summary>
    Task<ClaudeCodeLocalStats?> GetTodayStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the Claude Code session transcripts (~/.claude/projects/**/*.jsonl) and aggregates the token
/// counters that Claude Code itself shows in /usage and /context. Files are parsed incrementally:
/// a transcript is only re-read when its size or modification time changes.
/// </summary>
public class ClaudeCodeStatsService : IClaudeCodeStatsService
{
    private sealed class MessageUsage
    {
        public string Key = string.Empty;
        public string Model = string.Empty;
        public DateTimeOffset Timestamp;
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheCreation;
    }

    private sealed class FileSummary
    {
        public long Length;
        public DateTime LastWriteUtc;
        public List<MessageUsage> Messages = new();
        public long? ContextTokens;
        public string? ContextModel;
        public DateTimeOffset? LastTimestamp;
    }

    private readonly IReadOnlyList<string> _projectRoots;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, FileSummary> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeCodeStatsService(IEnumerable<string>? projectRoots = null, Func<DateTimeOffset>? nowProvider = null)
    {
        _projectRoots = (projectRoots ?? GetDefaultProjectRoots()).ToList();
        _now = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public static IEnumerable<string> GetDefaultProjectRoots()
    {
        var roots = new List<string>();

        // Claude Code honours CLAUDE_CONFIG_DIR (optionally a comma-separated list)
        var configDirs = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDirs))
        {
            foreach (var dir in configDirs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                roots.Add(Path.Combine(dir, "projects"));
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            roots.Add(Path.Combine(home, ".claude", "projects"));
            roots.Add(Path.Combine(home, ".config", "claude", "projects"));
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public Task<ClaudeCodeLocalStats?> GetTodayStatsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Compute(cancellationToken), cancellationToken);

    private ClaudeCodeLocalStats? Compute(CancellationToken cancellationToken)
    {
        var now = _now();
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var dayStartUtc = dayStart.UtcDateTime;

        lock (_lock)
        {
            bool anyRoot = false;
            var summaries = new List<FileSummary>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in _projectRoots)
            {
                if (!Directory.Exists(root)) continue;
                anyRoot = true;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists || info.LastWriteTimeUtc < dayStartUtc) continue;

                        seenFiles.Add(file);

                        if (!_cache.TryGetValue(file, out var summary) ||
                            summary.Length != info.Length ||
                            summary.LastWriteUtc != info.LastWriteTimeUtc)
                        {
                            summary = ParseFile(file, cancellationToken);
                            summary.Length = info.Length;
                            summary.LastWriteUtc = info.LastWriteTimeUtc;
                            _cache[file] = summary;
                        }

                        summaries.Add(summary);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Transcript locked or removed while reading: skip it this cycle
                    }
                }
            }

            // Drop cache entries of files that are no longer relevant (previous days, deleted sessions)
            foreach (var stale in _cache.Keys.Where(k => !seenFiles.Contains(k)).ToList())
            {
                _cache.Remove(stale);
            }

            if (!anyRoot) return null;

            var perModel = new Dictionary<string, ClaudeCodeModelUsage>(StringComparer.Ordinal);
            var seenMessages = new HashSet<string>(StringComparer.Ordinal);
            int sessionsToday = 0;
            FileSummary? latest = null;

            foreach (var summary in summaries)
            {
                bool hasActivityToday = false;

                foreach (var message in summary.Messages)
                {
                    if (message.Timestamp < dayStart) continue;
                    hasActivityToday = true;

                    // Resumed sessions copy earlier messages into a new transcript: count each request once
                    if (!seenMessages.Add(message.Key)) continue;

                    if (!perModel.TryGetValue(message.Model, out var usage))
                    {
                        usage = new ClaudeCodeModelUsage { Model = message.Model };
                        perModel[message.Model] = usage;
                    }

                    usage.InputTokens += message.Input;
                    usage.OutputTokens += message.Output;
                    usage.CacheReadTokens += message.CacheRead;
                    usage.CacheCreationTokens += message.CacheCreation;
                    usage.Messages++;
                }

                if (!hasActivityToday) continue;
                sessionsToday++;

                if (summary.ContextTokens.HasValue && summary.LastTimestamp.HasValue &&
                    (latest == null || summary.LastTimestamp > latest.LastTimestamp))
                {
                    latest = summary;
                }
            }

            return new ClaudeCodeLocalStats
            {
                DayStart = dayStart,
                Models = perModel.Values.OrderBy(m => m.Model, StringComparer.Ordinal).ToList(),
                SessionsToday = sessionsToday,
                ContextTokens = latest?.ContextTokens,
                ContextModel = latest?.ContextModel,
                LastActivity = latest?.LastTimestamp
            };
        }
    }

    private static FileSummary ParseFile(string path, CancellationToken cancellationToken)
    {
        var summary = new FileSummary();
        var byKey = new Dictionary<string, MessageUsage>(StringComparer.Ordinal);

        // Claude Code keeps appending to the active transcript, so open it without locking
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        int lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if ((lineNumber & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();

            // Cheap pre-filter: only assistant entries carry token usage
            if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeProp.GetString(), "assistant", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var model = message.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
                    ? modelProp.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(model) || model.StartsWith('<'))
                {
                    continue; // "<synthetic>" entries are local placeholders, not API requests
                }

                if (!root.TryGetProperty("timestamp", out var tsProp) || tsProp.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(tsProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
                {
                    continue;
                }

                var messageId = message.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;
                var requestId = root.TryGetProperty("requestId", out var reqProp) && reqProp.ValueKind == JsonValueKind.String
                    ? reqProp.GetString()
                    : null;
                var key = string.IsNullOrEmpty(messageId)
                    ? $"{path}#{lineNumber}"
                    : $"{messageId}:{requestId}";

                var entry = new MessageUsage
                {
                    Key = key,
                    Model = model,
                    Timestamp = timestamp,
                    Input = ReadTokens(usage, "input_tokens"),
                    Output = ReadTokens(usage, "output_tokens"),
                    CacheRead = ReadTokens(usage, "cache_read_input_tokens"),
                    CacheCreation = ReadTokens(usage, "cache_creation_input_tokens")
                };

                // A response is written once per content block with the same ids: keep the last (complete) one
                byKey[key] = entry;

                bool isSidechain = root.TryGetProperty("isSidechain", out var sideProp) && sideProp.ValueKind == JsonValueKind.True;
                if (!isSidechain && (!summary.LastTimestamp.HasValue || timestamp >= summary.LastTimestamp.Value))
                {
                    summary.LastTimestamp = timestamp;
                    summary.ContextTokens = entry.Input + entry.CacheRead + entry.CacheCreation;
                    summary.ContextModel = model;
                }
            }
            catch (JsonException)
            {
                // Partially written or malformed line: ignore
            }
        }

        summary.Messages = byKey.Values.ToList();
        return summary;
    }

    private static long ReadTokens(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value)
            ? value
            : 0;
}
