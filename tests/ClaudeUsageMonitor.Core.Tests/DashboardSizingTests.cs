using ClaudeUsageMonitor.Core.Ui;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class DashboardSizingTests
{
    [Fact]
    public void HalfOfWorkArea_WhenAboveFloor()
    {
        // 4K 물리 3840x2160 @150% → 논리 2560x1440, 절반 1280x720
        var (w, h) = DashboardSizing.ComputeInitialSize(3840, 2160, 1.5, 1.5, 720, 560);
        Assert.Equal(1280, w, 3);
        Assert.Equal(720, h, 3);
    }

    [Fact]
    public void ClampsToFloor_WhenHalfIsTooSmall()
    {
        // 1920x1080 @150% → 논리 1280x720, 절반 640x360 → 하한 720x560
        var (w, h) = DashboardSizing.ComputeInitialSize(1920, 1080, 1.5, 1.5, 720, 560);
        Assert.Equal(720, w, 3);
        Assert.Equal(560, h, 3);
    }

    [Fact]
    public void Dpi100_HalfWidth_FloorHeight()
    {
        // 1920x1080 @100% → 절반 960x540 → 폭 960(>하한), 높이 max(560,540)=560
        var (w, h) = DashboardSizing.ComputeInitialSize(1920, 1080, 1.0, 1.0, 720, 560);
        Assert.Equal(960, w, 3);
        Assert.Equal(560, h, 3);
    }

    [Fact]
    public void GuardsNonPositiveDpi()
    {
        // dpi<=0이면 1.0으로 취급 (divide-by-zero 방지)
        var (w, h) = DashboardSizing.ComputeInitialSize(1920, 1080, 0, 0, 720, 560);
        Assert.Equal(960, w, 3);
        Assert.Equal(560, h, 3);
    }
}
