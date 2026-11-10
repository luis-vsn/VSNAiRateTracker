using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using VSNAiRateTracker.Services;
using Xunit;

namespace VSNAiRateTracker.Tests;

public class ClaudeCodeStatsServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public ClaudeCodeStatsServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "VSNAiRateTrackerClaudeStats_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "project-a"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup
        }
    }

    private ClaudeCodeStatsService CreateSut() => new(new[] { _root }, () => Now);

    private static string Assistant(string timestamp, string messageId, string requestId, string model,
        long input, long output, long cacheRead, long cacheCreation, bool sidechain = false) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"sessionId\":\"s1\",\"isSidechain\":" + (sidechain ? "true" : "false") +
        ",\"requestId\":\"" + requestId + "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"" + model + "\",\"role\":\"assistant\"," +
        "\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output +
        ",\"cache_read_input_tokens\":" + cacheRead + ",\"cache_creation_input_tokens\":" + cacheCreation + "}}}";

    private string WriteTranscript(string name, params string[] lines)
    {
        var path = Path.Combine(_root, "project-a", name);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
        return path;
    }

    [Fact]
    public async Task GetTodayStatsAsync_WhenNoClaudeCodeFolderExists_ReturnsNull()
    {
        var sut = new ClaudeCodeStatsService(new[] { Path.Combine(_root, "does-not-exist") }, () => Now);

        var stats = await sut.GetTodayStatsAsync();

        stats.Should().BeNull();
    }

    [Fact]
    public async Task GetTodayStatsAsync_AggregatesTodayTokensPerModelAndIgnoresOlderEntries()
    {
        WriteTranscript("session1.jsonl",
            "{\"type\":\"user\",\"timestamp\":\"2026-11-10T09:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"hola\"}}",
            Assistant("2026-11-09T23:00:00.000Z", "msg_old", "req_old", "claude-opus-4-5", 999, 999, 999, 999),
            Assistant("2026-11-10T09:00:01.000Z", "msg_1", "req_1", "claude-opus-4-5", 10, 100, 1000, 200),
            Assistant("2026-11-10T09:05:00.000Z", "msg_2", "req_2", "claude-haiku-4-5", 5, 50, 0, 0, sidechain: true),
            "this line is not json but mentions \"usage\"",
            Assistant("2026-11-10T09:10:00.000Z", "msg_3", "req_3", "claude-opus-4-5", 20, 300, 5000, 400));

        var stats = await CreateSut().GetTodayStatsAsync();

        stats.Should().NotBeNull();
        stats!.SessionsToday.Should().Be(1);
        stats.Models.Select(m => m.Model).Should().Equal("claude-haiku-4-5", "claude-opus-4-5");

        var opus = stats.Models.Single(m => m.Model == "claude-opus-4-5");
        opus.InputTokens.Should().Be(30);
        opus.OutputTokens.Should().Be(400);
        opus.CacheReadTokens.Should().Be(6000);
        opus.CacheCreationTokens.Should().Be(600);
        opus.Messages.Should().Be(2);
        opus.TotalTokens.Should().Be(7030);

        var haiku = stats.Models.Single(m => m.Model == "claude-haiku-4-5");
        haiku.OutputTokens.Should().Be(50);
        haiku.Messages.Should().Be(1);

        // Context = input + cache read + cache creation of the last main-chain request
        stats.ContextTokens.Should().Be(20 + 5000 + 400);
        stats.ContextModel.Should().Be("claude-opus-4-5");
        stats.LastActivity.Should().Be(DateTimeOffset.Parse("2026-11-10T09:10:00.000Z"));
    }

    [Fact]
    public async Task GetTodayStatsAsync_CountsEachRequestOnce_AcrossRepeatedLinesAndResumedSessions()
    {
        // Same response written once per content block, the last one carrying the final output count
        WriteTranscript("session1.jsonl",
            Assistant("2026-11-10T10:00:00.000Z", "msg_1", "req_1", "claude-sonnet-4-5", 10, 1, 100, 0),
            Assistant("2026-11-10T10:00:02.000Z", "msg_1", "req_1", "claude-sonnet-4-5", 10, 80, 100, 0));

        // A resumed session copies the history into a new transcript and continues
        WriteTranscript("session2.jsonl",
            Assistant("2026-11-10T10:00:02.000Z", "msg_1", "req_1", "claude-sonnet-4-5", 10, 80, 100, 0),
            Assistant("2026-11-10T11:00:00.000Z", "msg_2", "req_2", "claude-sonnet-4-5", 7, 20, 300, 0));

        var stats = await CreateSut().GetTodayStatsAsync();

        var sonnet = stats!.Models.Single();
        sonnet.Messages.Should().Be(2);
        sonnet.InputTokens.Should().Be(17);
        sonnet.OutputTokens.Should().Be(100);
        sonnet.CacheReadTokens.Should().Be(400);
        stats.SessionsToday.Should().Be(2);
        stats.ContextTokens.Should().Be(307);
    }

    [Fact]
    public async Task GetTodayStatsAsync_ReparsesTranscriptWhenItGrows()
    {
        var path = WriteTranscript("session1.jsonl",
            Assistant("2026-11-10T10:00:00.000Z", "msg_1", "req_1", "claude-sonnet-4-5", 10, 10, 0, 0));
        var sut = CreateSut();

        var first = await sut.GetTodayStatsAsync();
        first!.Models.Single().OutputTokens.Should().Be(10);

        File.AppendAllLines(path, new[] { Assistant("2026-11-10T10:30:00.000Z", "msg_2", "req_2", "claude-sonnet-4-5", 10, 25, 0, 0) });
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddMinutes(1));

        var second = await sut.GetTodayStatsAsync();
        second!.Models.Single().OutputTokens.Should().Be(35);
    }

    [Fact]
    public async Task GetTodayStatsAsync_SkipsTranscriptsNotModifiedToday()
    {
        var path = WriteTranscript("old-session.jsonl",
            Assistant("2026-11-08T10:00:00.000Z", "msg_1", "req_1", "claude-sonnet-4-5", 10, 10, 0, 0));
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddDays(-2));

        var stats = await CreateSut().GetTodayStatsAsync();

        stats.Should().NotBeNull();
        stats!.Models.Should().BeEmpty();
        stats.SessionsToday.Should().Be(0);
        stats.ContextTokens.Should().BeNull();
    }
}
