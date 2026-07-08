using static ClaudeUsageMonitor.App.Interop.NativeMethods;

namespace ClaudeUsageMonitor.App.Interop;

public static class WindowStyling
{
    /// <summary>Alt-Tab 미노출 + 포커스 스틸 방지 스타일 적용.</summary>
    public static void MakeToolWindowNoActivate(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Explorer 재시작 등으로 강등된 topmost를 재주장.</summary>
    public static void ReassertTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static void SendToBottom(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
