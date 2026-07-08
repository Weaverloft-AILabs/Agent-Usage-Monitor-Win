using System.Runtime.InteropServices;
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

/// <summary>개별 작업표시줄(주/보조) — 물리 픽셀 RECT + 소속 모니터 장치명.</summary>
public readonly record struct TaskbarInstance(
    int Left, int Top, int Right, int Bottom, TaskbarEdge Edge, bool IsPrimary, string MonitorDevice)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

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
    /// 모든 작업표시줄(주 Shell_TrayWnd + 보조 Shell_SecondaryTrayWnd) 열거.
    /// 멀티모니터에서 위젯이 어느 작업표시줄에든 도킹할 수 있도록 한다.
    /// </summary>
    public static IReadOnlyList<TaskbarInstance> GetAllTaskbars()
    {
        var list = new List<TaskbarInstance>();

        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero && GetWindowRect(primary, out var primaryRect))
        {
            list.Add(Describe(primary, primaryRect, isPrimary: true));
        }

        var secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (GetWindowRect(secondary, out var rect))
            {
                list.Add(Describe(secondary, rect, isPrimary: false));
            }
        }

        return list;
    }

    private static TaskbarInstance Describe(IntPtr hwnd, RECT rect, bool isPrimary)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        var device = GetMonitorInfo(monitor, ref info) ? info.szDevice : "";

        // 가로/세로 형태와 모니터 내 위치로 도킹 변(edge) 추론
        TaskbarEdge edge;
        if (rect.Right - rect.Left >= rect.Bottom - rect.Top)
        {
            edge = rect.Top - info.rcMonitor.Top <= info.rcMonitor.Bottom - rect.Bottom
                ? TaskbarEdge.Top
                : TaskbarEdge.Bottom;
        }
        else
        {
            edge = rect.Left - info.rcMonitor.Left <= info.rcMonitor.Right - rect.Right
                ? TaskbarEdge.Left
                : TaskbarEdge.Right;
        }

        return new TaskbarInstance(rect.Left, rect.Top, rect.Right, rect.Bottom, edge, isPrimary, device);
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
