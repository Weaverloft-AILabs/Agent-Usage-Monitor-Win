using ClaudeUsageMonitor.Core.Ingest;
using ClaudeUsageMonitor.Core.Models;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class JsonlParserTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static List<RawUsageLine> ParseAll(string fixture)
    {
        var result = new List<RawUsageLine>();
        foreach (var line in File.ReadLines(FixturePath(fixture)))
        {
            if (JsonlParser.TryParseLine(line, out var parsed))
            {
                result.Add(parsed!);
            }
        }
        return result;
    }

    [Fact]
    public void Parser_SkipsSynthetic_NonAssistant_NoUsage_And_Malformed()
    {
        var lines = ParseAll("main-session.jsonl");

        // msg_A x2 + msg_B x1 = 3 (user/attachment/synthetic/no-usage/malformed 제외)
        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain(lines, l => l.MessageId == "msg_S");
        Assert.DoesNotContain(lines, l => l.MessageId == "msg_N");
    }

    [Fact]
    public void Parser_SplitsCache5m1h_WhenBreakdownPresent()
    {
        var lines = ParseAll("main-session.jsonl");
        var msgA = lines.First(l => l.MessageId == "msg_A");

        Assert.Equal(0, msgA.Tokens.Cache5m);
        Assert.Equal(8562, msgA.Tokens.Cache1h);
        Assert.Equal(32304, msgA.Tokens.CacheRead);
        Assert.Equal(9936, msgA.Tokens.Input);
        Assert.Equal(305, msgA.Tokens.Output);
    }

    [Fact]
    public void Parser_FallsBackToFlat5m_WhenNoBreakdown()
    {
        var lines = ParseAll("main-session.jsonl");
        var msgB = lines.First(l => l.MessageId == "msg_B");

        Assert.Equal(469, msgB.Tokens.Cache5m);
        Assert.Equal(0, msgB.Tokens.Cache1h);
    }

    [Fact]
    public void Parser_ToleratesMalformedLine_WithoutThrowing()
    {
        Assert.False(JsonlParser.TryParseLine("this is not json at all {{{", out _));
        Assert.False(JsonlParser.TryParseLine("", out _));
        Assert.False(JsonlParser.TryParseLine("[1,2,3]", out _));
    }

    [Fact]
    public void Parser_ReadsTimestamp_AsUtc()
    {
        var lines = ParseAll("main-session.jsonl");
        var msgA = lines.First(l => l.MessageId == "msg_A");

        Assert.Equal(TimeSpan.Zero, msgA.Timestamp.Offset);
        Assert.Equal(new DateTimeOffset(2026, 7, 8, 5, 27, 11, 867, TimeSpan.Zero), msgA.Timestamp);
    }
}

public class UsageDeduplicatorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static UsageDeduplicator DedupFixture(params string[] fixtures)
    {
        var dedup = new UsageDeduplicator();
        foreach (var fixture in fixtures)
        {
            foreach (var line in File.ReadLines(FixturePath(fixture)))
            {
                if (JsonlParser.TryParseLine(line, out var parsed))
                {
                    dedup.Accept(parsed!);
                }
            }
        }
        return dedup;
    }

    [Fact]
    public void Dedup_TakesLastLine_PerMessageId()
    {
        var dedup = DedupFixture("subagent-partial.jsonl");

        // msg_C: out 9 -> 9 -> 486. 마지막 라인이 최종 usage.
        Assert.Equal(486, dedup.Latest["msg_C"].Tokens.Output);
        Assert.Equal(100, dedup.Latest["msg_C"].Tokens.Cache5m);
    }

    [Fact]
    public void Dedup_NaiveSumOvercounts_DedupMatchesExpected()
    {
        var dedup = DedupFixture("main-session.jsonl");

        long naiveOutput = 0;
        foreach (var line in File.ReadLines(FixturePath("main-session.jsonl")))
        {
            if (JsonlParser.TryParseLine(line, out var parsed))
            {
                naiveOutput += parsed!.Tokens.Output;
            }
        }

        long dedupOutput = dedup.Latest.Values.Sum(l => l.Tokens.Output);

        Assert.Equal(739, naiveOutput);  // 305 + 305(중복) + 129
        Assert.Equal(434, dedupOutput);  // 305 + 129
        Assert.Equal(2, dedup.Latest.Count);
    }

    [Fact]
    public void Dedup_ModelFallbackWithinGroup_UsesLastModel()
    {
        var dedup = DedupFixture("subagent-partial.jsonl");

        // msg_D: claude-fable-5 -> claude-opus-4-8 로 mid-message 폴백. 마지막 라인 기준.
        Assert.Equal("claude-opus-4-8", dedup.Latest["msg_D"].Model);
        Assert.Equal(994, dedup.Latest["msg_D"].Tokens.Output);
    }

    [Fact]
    public void Dedup_IsIdempotent_OnRescan()
    {
        var once = DedupFixture("main-session.jsonl");
        var twice = DedupFixture("main-session.jsonl", "main-session.jsonl");

        Assert.Equal(once.Latest.Count, twice.Latest.Count);
        Assert.Equal(
            once.Latest.Values.Sum(l => l.Tokens.Total),
            twice.Latest.Values.Sum(l => l.Tokens.Total));
    }
}
