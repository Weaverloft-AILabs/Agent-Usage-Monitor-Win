namespace ClaudeUsageMonitor.Core.Models;

/// <summary>USD per MTok (1,000,000 토큰당 달러).</summary>
public sealed record ModelPricing(
    decimal InputPerMTok,
    decimal OutputPerMTok,
    decimal Cache5mPerMTok,
    decimal Cache1hPerMTok,
    decimal CacheReadPerMTok);
