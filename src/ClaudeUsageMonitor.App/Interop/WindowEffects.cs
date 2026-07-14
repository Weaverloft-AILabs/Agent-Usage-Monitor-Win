using System.Windows;
using System.Windows.Interop;

namespace ClaudeUsageMonitor.App.Interop;

/// <summary>커스텀 크롬 창의 Win11 모서리 라운딩 (DWM DWMWA_WINDOW_CORNER_PREFERENCE).
/// Win10 이하에서는 DWM 호출이 무시되며 안전(반환값 미검사).</summary>
internal static class WindowEffects
{
    /// <summary>창 모서리를 Win11 표준 라운드로 설정. SourceInitialized 이후(HWND 존재) 호출.</summary>
    public static void EnableRoundedCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        var preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }
}
