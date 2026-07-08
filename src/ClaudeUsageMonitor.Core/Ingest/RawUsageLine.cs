using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Ingest;

/// <summary>
/// JSONL의 assistant 라인 1개에서 추출한 사용량.
/// 같은 message.id가 여러 라인에 반복되므로(콘텐츠 블록/스트리밍) 반드시 dedup 후 사용해야 한다.
/// </summary>
public sealed record RawUsageLine(
    string MessageId,
    string Model,
    DateTimeOffset Timestamp,
    string SessionId,
    string ProjectPath,
    TokenCounts Tokens)
{
    public UsageEvent ToUsageEvent() =>
        new(MessageId, Model, Timestamp, SessionId, ProjectPath, Tokens);
}
