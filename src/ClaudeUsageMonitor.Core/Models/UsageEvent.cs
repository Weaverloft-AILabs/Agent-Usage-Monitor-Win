namespace ClaudeUsageMonitor.Core.Models;

/// <summary>중복 제거가 끝난 1개 API 응답의 최종 사용량 (message.id당 1건).</summary>
public sealed record UsageEvent(
    string MessageId,
    string Model,
    DateTimeOffset Timestamp,
    string SessionId,
    string ProjectPath,
    TokenCounts Tokens);
