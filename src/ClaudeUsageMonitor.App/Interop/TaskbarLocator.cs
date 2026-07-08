using static ClaudeUsageMonitor.App.Interop.NativeMethods;

namespace ClaudeUsageMonitor.App.Interop;

public enum TaskbarEdge
{
    Left,
    Top,
    Right,
    Bottom,
}

public readonly record struct TaskbarInfo(
    int Left, int Top, int Right, int Bottom, TaskbarEdge Edge, bool AutoHide);

/// <summary>주 모니터 작업표시줄 위치/상태 조회 (물리 픽셀).</summary>
public static class TaskbarLocator
{
    public static TaskbarInfo? GetTaskbar()
    {
        var data = new APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>() };
        var result = SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        if (result == 0)
        {
            return null;
        }

        var state = new APPBARDATA { cbSize = data.cbSize };
        var autoHide = ((int)SHAppBarMessage(ABM_GETSTATE, ref state) & ABS_AUTOHIDE) != 0;

        var edge = data.uEdge switch
        {
            ABE_LEFT => TaskbarEdge.Left,
            ABE_TOP => TaskbarEdge.Top,
            ABE_RIGHT => TaskbarEdge.Right,
            _ => TaskbarEdge.Bottom,
        };

        return new TaskbarInfo(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom, edge, autoHide);
    }

    /// <summary>
    /// 알림 영역(트레이 시계 블록)의 물리픽셀 RECT.
    /// 위젯을 작업표시줄 내부, 트레이 왼쪽 빈 공간에 배치할 때 사용. 실패 시 null.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom)? GetTrayNotifyRect()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            return null;
        }

        var notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero || !GetWindowRect(notify, out var rect))
        {
            return null;
        }

        return (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}
