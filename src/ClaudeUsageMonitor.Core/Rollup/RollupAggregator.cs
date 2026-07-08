using System.Globalization;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Rollup;

/// <summary>
/// UsageEvent를 RollupData에 반영한다.
/// 같은 messageId가 갱신되어 다시 오면(스트리밍 부분→최종) 이전 기여분을 차감 후 재반영 — 멱등.
/// </summary>
public sealed class RollupAggregator
{
    private static readonly TimeSpan AppliedRetention = TimeSpan.FromDays(45);

    private readonly RollupData _data;
    private readonly TimeZoneInfo _timeZone;

    public RollupAggregator(RollupData data, TimeZoneInfo? timeZone = null)
    {
        _data = data;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <returns>롤업이 변경되었으면 true.</returns>
    public bool Apply(UsageEvent e)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.Timestamp, _timeZone).DateTime);
        var dateKey = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (_data.Applied.TryGetValue(e.MessageId, out var prev))
        {
            if (prev.Date == dateKey && prev.Model == e.Model && prev.Tokens == e.Tokens)
            {
                return false; // 동일 내용 재관측 — 변경 없음
            }
            Subtract(prev);
        }

        var day = GetOrAddDay(dateKey, localDate);
        var usage = GetOrAddModel(day, e.Model);
        usage.Tokens += e.Tokens;
        usage.RequestCount += 1;
        if (!string.IsNullOrEmpty(e.ProjectPath))
        {
            usage.Projects.Add(e.ProjectPath);
        }

        _data.Applied[e.MessageId] = new AppliedMessage
        {
            Date = dateKey,
            Model = e.Model,
            Tokens = e.Tokens,
            Timestamp = e.Timestamp,
        };

        if (_data.CoverageStart is null ||
            string.CompareOrdinal(dateKey, _data.CoverageStart) < 0)
        {
            _data.CoverageStart = dateKey;
        }

        return true;
    }

    /// <summary>보존기간이 지난 Applied 항목 정리 (rollup 수치는 유지).</summary>
    public void PruneApplied(DateTimeOffset now)
    {
        var cutoff = now - AppliedRetention;
        var expired = _data.Applied
            .Where(kv => kv.Value.Timestamp < cutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
        {
            _data.Applied.Remove(key);
        }
    }

    private void Subtract(AppliedMessage prev)
    {
        if (!_data.Days.TryGetValue(prev.Date, out var day) ||
            !day.ByModel.TryGetValue(prev.Model, out var usage))
        {
            return;
        }

        usage.Tokens -= prev.Tokens;
        usage.RequestCount -= 1;
        if (usage.RequestCount <= 0 && usage.Tokens.Total <= 0)
        {
            day.ByModel.Remove(prev.Model);
        }
    }

    private DailyRollup GetOrAddDay(string dateKey, DateOnly date)
    {
        if (!_data.Days.TryGetValue(dateKey, out var day))
        {
            day = new DailyRollup { Date = date };
            _data.Days[dateKey] = day;
        }
        return day;
    }

    private static ModelDayUsage GetOrAddModel(DailyRollup day, string model)
    {
        if (!day.ByModel.TryGetValue(model, out var usage))
        {
            usage = new ModelDayUsage();
            day.ByModel[model] = usage;
        }
        return usage;
    }
}
