namespace ClaudeUsageMonitor.Core.Ingest;

/// <summary>
/// message.id 기준 전역 중복 제거. 항상 마지막(최신)으로 관측된 라인을 유지한다.
/// 근거(실측 검증): 메인 파일은 최종 usage가 모든 라인에 반복되고,
/// 서브에이전트 파일은 중간 라인이 부분 스트리밍 usage를 가지므로 last-wins만이 양쪽 모두 정확하다.
/// 파일 내 라인 순서대로 Accept를 호출해야 한다.
/// </summary>
public sealed class UsageDeduplicator
{
    private readonly Dictionary<string, RawUsageLine> _latest = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, RawUsageLine> Latest => _latest;

    public void Accept(RawUsageLine line) => _latest[line.MessageId] = line;

    public void Clear() => _latest.Clear();
}
