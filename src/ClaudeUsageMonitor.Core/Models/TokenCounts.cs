namespace ClaudeUsageMonitor.Core.Models;

/// <summary>토큰 집계 단위. cache_creation은 5분/1시간 TTL로 분리(요율이 1.25x vs 2x로 다름).</summary>
public readonly record struct TokenCounts(
    long Input,
    long Output,
    long Cache5m,
    long Cache1h,
    long CacheRead)
{
    public long Total => Input + Output + Cache5m + Cache1h + CacheRead;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new(
        a.Input + b.Input,
        a.Output + b.Output,
        a.Cache5m + b.Cache5m,
        a.Cache1h + b.Cache1h,
        a.CacheRead + b.CacheRead);

    public static TokenCounts Zero => default;
}
