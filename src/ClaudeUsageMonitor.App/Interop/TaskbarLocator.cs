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
}
