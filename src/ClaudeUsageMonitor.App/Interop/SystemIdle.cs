using System.Runtime.InteropServices;
using ClaudeUsageMonitor.Core.Updates;

namespace ClaudeUsageMonitor.App.Interop;

/// <summary>
/// 마지막 입력(키보드/마우스) 이후 경과 시간. OS가 유지하는 값을 GetLastInputInfo로
/// 한 번 읽을 뿐 — 폴링·전역 입력 훅·백그라운드 스레드가 없어 배터리/CPU 부담이 없다.
/// </summary>
internal static class SystemIdle
{
    public static TimeSpan GetIdleDuration()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero; // 조회 실패 시 '유휴 아님'으로 간주 (자동 업데이트 보류)
        }

        uint idleMs = IdleGate.IdleMilliseconds(info.dwTime, unchecked((uint)Environment.TickCount));
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
