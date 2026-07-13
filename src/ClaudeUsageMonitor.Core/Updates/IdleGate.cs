namespace ClaudeUsageMonitor.Core.Updates;

/// <summary>
/// 시스템 유휴 판정(순수, Win32 무의존). tick 값은 GetTickCount/Environment.TickCount 기반
/// 32비트라 약 49.7일마다 랩어라운드하므로 unchecked uint 뺄셈으로 경과를 계산한다.
/// (App/Interop.SystemIdle가 GetLastInputInfo로 tick을 얻어 이 함수에 넘긴다.)
/// </summary>
public static class IdleGate
{
    /// <summary>마지막 입력 이후 경과 밀리초. 랩어라운드(now &lt; last)에도 올바른 경과를 준다.</summary>
    public static uint IdleMilliseconds(uint lastInputTickMs, uint nowTickMs)
        => unchecked(nowTickMs - lastInputTickMs);

    /// <summary>경과가 임계값 이상이면 유휴로 본다.</summary>
    public static bool IsIdle(uint lastInputTickMs, uint nowTickMs, uint thresholdMs)
        => IdleMilliseconds(lastInputTickMs, nowTickMs) >= thresholdMs;
}
