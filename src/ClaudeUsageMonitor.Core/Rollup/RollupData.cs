using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Rollup;

/// <summary>메시지 1건이 롤업에 반영된 내역. 스트리밍 부분→최종 갱신 시 차감/재반영에 사용.</summary>
public sealed class AppliedMessage
{
    public required string Date { get; set; }   // yyyy-MM-dd (로컬)
    public required string Model { get; set; }
    public TokenCounts Tokens { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// 모니터 소유 영속 롤업. Claude Code가 30일(기본) 지난 트랜스크립트를 삭제하므로
/// 월간 통계의 유일한 신뢰 소스다. rollups.json으로 저장.
/// </summary>
public sealed class RollupData
{
    /// <summary>집계가 시작된 최초 로컬 날짜(yyyy-MM-dd). 이전 기간은 "부분 데이터"로 표시.</summary>
    public string? CoverageStart { get; set; }

    /// <summary>yyyy-MM-dd(로컬) → 하루 집계.</summary>
    public Dictionary<string, DailyRollup> Days { get; set; } = new(StringComparer.Ordinal);

    /// <summary>messageId → 반영 내역. 보존기간(45일) 경과 시 정리.</summary>
    public Dictionary<string, AppliedMessage> Applied { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 대시보드(UI 스레드) 읽기용 깊은 복사 — Days/ByModel 딕셔너리를 새로 만든다.
    /// 인제스트 스레드가 in-place로 Days/ByModel을 변경하는 동안 UI가 원본을 열거하면
    /// "Collection was modified" 크래시가 나므로, 경계에서 독립 스냅샷을 넘긴다.
    /// (TokenCounts는 값/불변이라 참조 공유해도 안전. Applied는 대시보드가 안 쓰므로 제외.)
    /// </summary>
    public RollupData SnapshotForRead()
    {
        var copy = new RollupData { CoverageStart = CoverageStart };
        foreach (var (key, day) in Days)
        {
            var dayCopy = new DailyRollup { Date = day.Date };
            foreach (var (model, usage) in day.ByModel)
            {
                dayCopy.ByModel[model] = new ModelDayUsage
                {
                    Tokens = usage.Tokens,
                    RequestCount = usage.RequestCount,
                    Projects = new HashSet<string>(usage.Projects, StringComparer.OrdinalIgnoreCase),
                };
            }
            copy.Days[key] = dayCopy;
        }
        return copy;
    }
}
