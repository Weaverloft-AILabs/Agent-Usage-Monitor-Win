namespace ClaudeUsageMonitor.Core.Updates;

/// <summary>
/// 유휴 자동 업데이트 발화 판정(순수). 설정 ON + 업데이트 존재 + 메이저 점프 아님 + 유휴,
/// 네 조건이 모두 참일 때만 자동 적용한다. (메이저 점프는 수동 설치 대상이므로 자동 제외.)
/// </summary>
public static class AutoUpdateGate
{
    public static bool ShouldAutoApply(bool enabled, bool updateAvailable, bool isMajorJump, bool isIdle)
        => enabled && updateAvailable && !isMajorJump && isIdle;
}
