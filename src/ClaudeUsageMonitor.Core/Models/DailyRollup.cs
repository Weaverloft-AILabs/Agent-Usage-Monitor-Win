namespace ClaudeUsageMonitor.Core.Models;

/// <summary>모델별 하루 사용량 집계.</summary>
public sealed class ModelDayUsage
{
    public TokenCounts Tokens { get; set; }
    public int RequestCount { get; set; }
    public HashSet<string> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>로컬 날짜 기준 하루치 롤업.</summary>
public sealed class DailyRollup
{
    public required DateOnly Date { get; init; }
    public Dictionary<string, ModelDayUsage> ByModel { get; init; } = new(StringComparer.Ordinal);

    public TokenCounts TotalTokens
    {
        get
        {
            var sum = TokenCounts.Zero;
            foreach (var usage in ByModel.Values)
            {
                sum += usage.Tokens;
            }
            return sum;
        }
    }

    public int TotalRequests
    {
        get
        {
            var count = 0;
            foreach (var usage in ByModel.Values)
            {
                count += usage.RequestCount;
            }
            return count;
        }
    }
}
