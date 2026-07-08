using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Rollup;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public sealed class RollupTests : IDisposable
{
    private static readonly TimeZoneInfo Kst = TimeZoneInfo.CreateCustomTimeZone(
        "Test-KST", TimeSpan.FromHours(9), "Test-KST", "Test-KST");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cum-rollup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static UsageEvent Event(
        string messageId,
        string timestampUtc,
        long output = 100,
        string model = "claude-sonnet-5",
        string project = @"d:\proj\alpha")
        => new(
            messageId,
            model,
            DateTimeOffset.Parse(timestampUtc),
            "sess-1",
            project,
            new TokenCounts(10, output, 0, 0, 0));

    [Fact]
    public void BucketsByLocalDate_NotUtc()
    {
        var data = new RollupData();
        var agg = new RollupAggregator(data, Kst);

        // UTC 2026-07-08 23:30 = KST 2026-07-09 08:30 → 로컬 기준 7/9로 집계되어야 함
        agg.Apply(Event("m1", "2026-07-08T23:30:00Z"));

        Assert.True(data.Days.ContainsKey("2026-07-09"));
        Assert.False(data.Days.ContainsKey("2026-07-08"));
    }

    [Fact]
    public void ReApply_UpdatedMessage_DoesNotDoubleCount()
    {
        var data = new RollupData();
        var agg = new RollupAggregator(data, Kst);

        // 스트리밍 부분 usage(9) → 최종 usage(486)로 갱신
        agg.Apply(Event("m1", "2026-07-08T05:00:00Z", output: 9));
        agg.Apply(Event("m1", "2026-07-08T05:00:00Z", output: 486));

        var day = data.Days["2026-07-08"];
        Assert.Equal(486, day.TotalTokens.Output);
        Assert.Equal(1, day.TotalRequests);
    }

    [Fact]
    public void ReApply_IdenticalMessage_ReturnsFalse_NoChange()
    {
        var data = new RollupData();
        var agg = new RollupAggregator(data, Kst);

        Assert.True(agg.Apply(Event("m1", "2026-07-08T05:00:00Z")));
        Assert.False(agg.Apply(Event("m1", "2026-07-08T05:00:00Z")));

        Assert.Equal(1, data.Days["2026-07-08"].TotalRequests);
    }

    [Fact]
    public void ModelFallback_ReattributesToLastModel()
    {
        var data = new RollupData();
        var agg = new RollupAggregator(data, Kst);

        agg.Apply(Event("m1", "2026-07-08T05:00:00Z", output: 104, model: "claude-fable-5"));
        agg.Apply(Event("m1", "2026-07-08T05:00:00Z", output: 994, model: "claude-opus-4-8"));

        var day = data.Days["2026-07-08"];
        Assert.False(day.ByModel.ContainsKey("claude-fable-5"));
        Assert.Equal(994, day.ByModel["claude-opus-4-8"].Tokens.Output);
    }

    [Fact]
    public void CoverageStart_IsEarliestLocalDate()
    {
        var data = new RollupData();
        var agg = new RollupAggregator(data, Kst);

        agg.Apply(Event("m1", "2026-07-08T05:00:00Z"));
        agg.Apply(Event("m2", "2026-07-01T05:00:00Z"));
        agg.Apply(Event("m3", "2026-07-05T05:00:00Z"));

        Assert.Equal("2026-07-01", data.CoverageStart);
    }

    [Fact]
    public void MonthlyTotals_SplitAtMonthBoundary()
    {
        var data = new RollupData();
        var agg = new RollupAggregator(data, Kst);

        agg.Apply(Event("m1", "2026-06-30T05:00:00Z", output: 10)); // KST 6/30
        agg.Apply(Event("m2", "2026-07-01T05:00:00Z", output: 20)); // KST 7/1

        var months = data.MonthlyTotals();

        Assert.Equal(2, months.Count);
        Assert.Equal("2026-06", months[0].Month);
        Assert.Equal(10, months[0].Tokens.Output);
        Assert.Equal("2026-07", months[1].Month);
        Assert.Equal(20, months[1].Tokens.Output);
    }

    [Fact]
    public void WeeklyTotals_StartMonday()
    {
        Assert.Equal(new DateOnly(2026, 7, 6), RollupQueries.StartOfWeek(new DateOnly(2026, 7, 8)));  // 수 → 월
        Assert.Equal(new DateOnly(2026, 7, 6), RollupQueries.StartOfWeek(new DateOnly(2026, 7, 6)));  // 월 → 자기 자신
        Assert.Equal(new DateOnly(2026, 6, 29), RollupQueries.StartOfWeek(new DateOnly(2026, 7, 5))); // 일 → 전주 월
    }

    [Fact]
    public void Range_FillsMissingDaysWithEmpty()
    {
        var data = new RollupData();
        var agg = new RollupAggregator(data, Kst);
        agg.Apply(Event("m1", "2026-07-08T05:00:00Z"));

        var range = data.Range(new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 9));

        Assert.Equal(3, range.Count);
        Assert.Equal(0, range[0].TotalRequests);
        Assert.Equal(1, range[1].TotalRequests);
    }

    [Fact]
    public void Store_RoundTrips_AllFields()
    {
        var data = new RollupData();
        var agg = new RollupAggregator(data, Kst);
        agg.Apply(Event("m1", "2026-07-08T05:00:00Z", output: 486));

        var store = new RollupStore(_dir);
        store.Save(data);
        var loaded = store.Load();

        Assert.Equal(data.CoverageStart, loaded.CoverageStart);
        Assert.Equal(486, loaded.Days["2026-07-08"].TotalTokens.Output);
        Assert.Single(loaded.Applied);
        Assert.Contains(@"d:\proj\alpha", loaded.Days["2026-07-08"].ByModel["claude-sonnet-5"].Projects);
    }

    [Fact]
    public void PruneApplied_RemovesOldEntries_KeepsRollups()
    {
        var data = new RollupData();
        var agg = new RollupAggregator(data, Kst);
        agg.Apply(Event("old", "2026-05-01T05:00:00Z"));
        agg.Apply(Event("new", "2026-07-08T05:00:00Z"));

        agg.PruneApplied(DateTimeOffset.Parse("2026-07-08T12:00:00Z"));

        Assert.False(data.Applied.ContainsKey("old"));
        Assert.True(data.Applied.ContainsKey("new"));
        Assert.True(data.Days.ContainsKey("2026-05-01")); // 수치는 유지
    }
}
