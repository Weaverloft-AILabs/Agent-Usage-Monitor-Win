namespace ClaudeUsageMonitor.Core.Models;

/// <summary>usage API limits[] 항목. Kind: session | weekly_all | weekly_scoped (미지 값 허용).</summary>
public sealed record LimitEntry(
    string Kind,
    int Percent,
    string Severity,
    DateTimeOffset? ResetsAt,
    bool IsActive,
    string? ScopeLabel);

/// <summary>OAuth usage API 1회 조회 결과.</summary>
public sealed record RateLimitSnapshot(
    double FiveHourPct,
    DateTimeOffset? FiveHourResetsAt,
    double SevenDayPct,
    DateTimeOffset? SevenDayResetsAt,
    IReadOnlyList<LimitEntry> Limits,
    DateTimeOffset FetchedAt)
{
    public bool IsStale { get; init; }
}

public enum RateLimitStatus
{
    Ok,
    Stale,
    AuthRequired,
    NoCredentials,
}

/// <summary>폴링 서비스가 UI에 노출하는 현재 상태.</summary>
public sealed record RateLimitState(
    RateLimitSnapshot? Snapshot,
    RateLimitStatus Status,
    DateTimeOffset? NextPollAt);
