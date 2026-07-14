using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.RateLimit;

/// <summary>사용률(5H/7D) 초기 로딩 판정 — 시작/업데이트 직후 첫 스냅샷을 받기 전 구간.
/// UI는 이 구간에서 "0%" 대신 "로딩중" 표시.</summary>
public static class LoadingIndicator
{
    /// <param name="hadDataBefore">이전에 스냅샷을 한 번이라도 받았는가.</param>
    /// <param name="snapshotPresent">이번 응답에 스냅샷이 있는가.</param>
    /// <param name="status">이번 응답의 상태.</param>
    /// <returns>로딩 표시 여부. 스냅샷을 받았거나(과거/현재), 확정 오류(NoCredentials/AuthRequired)면 false.</returns>
    public static bool IsLoading(bool hadDataBefore, bool snapshotPresent, RateLimitStatus status)
    {
        if (hadDataBefore || snapshotPresent)
        {
            return false;
        }
        // 아직 데이터 없음 — 전송이 진행/일시오류(Ok/Stale)면 로딩, 확정 오류면 로딩 아님(무한 로딩 방지)
        return status is RateLimitStatus.Ok or RateLimitStatus.Stale;
    }
}
