namespace ClaudeUsageMonitor.Core.Ui;

/// <summary>대시보드 초기 창 크기 — 현재 모니터 작업영역의 절반(논리 DIP), 단 최소 하한 보장.</summary>
public static class DashboardSizing
{
    /// <param name="workWidthPx">현재 모니터 작업영역 폭(물리 px).</param>
    /// <param name="workHeightPx">작업영역 높이(물리 px).</param>
    /// <param name="dpiScaleX">가로 DPI 스케일(1.5 = 150%). 0 이하면 1.0으로 취급.</param>
    /// <param name="dpiScaleY">세로 DPI 스케일.</param>
    /// <param name="minWidth">논리 최소 폭.</param>
    /// <param name="minHeight">논리 최소 높이.</param>
    /// <returns>논리 단위(DIP) (Width, Height).</returns>
    public static (double Width, double Height) ComputeInitialSize(
        double workWidthPx, double workHeightPx, double dpiScaleX, double dpiScaleY,
        double minWidth, double minHeight)
    {
        var sx = dpiScaleX <= 0 ? 1.0 : dpiScaleX;
        var sy = dpiScaleY <= 0 ? 1.0 : dpiScaleY;
        var w = workWidthPx / sx / 2.0;
        var h = workHeightPx / sy / 2.0;
        return (Math.Max(minWidth, w), Math.Max(minHeight, h));
    }
}
