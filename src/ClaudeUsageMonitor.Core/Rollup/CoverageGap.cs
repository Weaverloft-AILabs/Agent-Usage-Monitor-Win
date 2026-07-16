namespace ClaudeUsageMonitor.Core.Rollup;

/// <summary>
/// 커버리지 갭 판정(순수). 앱이 보존기간(기본 30일)보다 오래 꺼져 있었으면, 그 사이 발생한
/// 사용량 트랜스크립트가 인제스트 전에 CLI 청소로 삭제돼 영구 소실됐을 수 있다.
/// (앱이 계속 실행 중이면 생성 즉시 인제스트되므로 갭이 없다.)
/// </summary>
public static class CoverageGap
{
    /// <param name="lastScanUtc">마지막으로 성공적으로 스캔/저장한 시각. null이면 최초 실행.</param>
    /// <param name="nowUtc">현재 시각.</param>
    /// <param name="retentionDays">CLI 트랜스크립트 보존일수(기본 30).</param>
    /// <returns>오프라인 구간이 보존창 이상이라 잠재적 영구 손실이 있으면 true.</returns>
    public static bool HasPotentialGap(DateTimeOffset? lastScanUtc, DateTimeOffset nowUtc, int retentionDays = 30)
    {
        if (lastScanUtc is null)
        {
            return false; // 이전 커버리지가 없으니 갭 개념도 없음
        }

        var offline = nowUtc - lastScanUtc.Value;
        return offline.TotalDays >= retentionDays;
    }
}
