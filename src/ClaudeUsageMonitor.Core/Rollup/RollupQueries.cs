using System.Globalization;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Rollup;

/// <summary>대시보드용 기간 조회 헬퍼.</summary>
public static class RollupQueries
{
    public static IReadOnlyList<DailyRollup> Range(this RollupData data, DateOnly from, DateOnly to)
    {
        var result = new List<DailyRollup>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            result.Add(data.Days.TryGetValue(key, out var day)
                ? day
                : new DailyRollup { Date = date });
        }
        return result;
    }

    /// <summary>월(yyyy-MM)별 합계. 키 오름차순.</summary>
    public static IReadOnlyList<(string Month, TokenCounts Tokens, Dictionary<string, TokenCounts> ByModel)> MonthlyTotals(this RollupData data)
    {
        return data.Days
            .GroupBy(kv => kv.Key[..7], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var byModel = new Dictionary<string, TokenCounts>(StringComparer.Ordinal);
                var total = TokenCounts.Zero;
                foreach (var day in g.Select(kv => kv.Value))
                {
                    foreach (var (model, usage) in day.ByModel)
                    {
                        byModel[model] = byModel.TryGetValue(model, out var t) ? t + usage.Tokens : usage.Tokens;
                        total += usage.Tokens;
                    }
                }
                return (g.Key, total, byModel);
            })
            .ToList();
    }

    /// <summary>주간 합계(월요일 시작). 반환 키는 해당 주 월요일 날짜.</summary>
    public static IReadOnlyList<(DateOnly WeekStart, TokenCounts Tokens, Dictionary<string, TokenCounts> ByModel)> WeeklyTotals(this RollupData data)
    {
        return data.Days.Values
            .GroupBy(day => StartOfWeek(day.Date))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var byModel = new Dictionary<string, TokenCounts>(StringComparer.Ordinal);
                var total = TokenCounts.Zero;
                foreach (var day in g)
                {
                    foreach (var (model, usage) in day.ByModel)
                    {
                        byModel[model] = byModel.TryGetValue(model, out var t) ? t + usage.Tokens : usage.Tokens;
                        total += usage.Tokens;
                    }
                }
                return (g.Key, total, byModel);
            })
            .ToList();
    }

    public static DateOnly StartOfWeek(DateOnly date)
    {
        var diff = ((int)date.DayOfWeek + 6) % 7; // Monday=0
        return date.AddDays(-diff);
    }
}
