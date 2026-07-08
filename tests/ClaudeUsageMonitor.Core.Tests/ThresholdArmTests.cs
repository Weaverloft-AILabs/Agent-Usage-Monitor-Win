using ClaudeUsageMonitor.Core.RateLimit;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class ThresholdArmTests
{
    private static readonly DateTimeOffset Reset1 = DateTimeOffset.Parse("2026-07-08T10:00:00Z");
    private static readonly DateTimeOffset Reset2 = DateTimeOffset.Parse("2026-07-08T15:00:00Z");

    [Fact]
    public void Fires_WhenCrossingThreshold()
    {
        var arm = new ThresholdArm();

        Assert.False(arm.ShouldFire(79, 80, Reset1));
        Assert.True(arm.ShouldFire(81, 80, Reset1));
    }

    [Fact]
    public void DoesNotRefire_WithinSameResetWindow()
    {
        var arm = new ThresholdArm();
        arm.ShouldFire(85, 80, Reset1);

        Assert.False(arm.ShouldFire(90, 80, Reset1));
        Assert.False(arm.ShouldFire(99, 80, Reset1));
        // 같은 윈도우에서 임계값 아래로 내려갔다 다시 올라와도 재발사 없음
        Assert.False(arm.ShouldFire(70, 80, Reset1));
        Assert.False(arm.ShouldFire(85, 80, Reset1));
    }

    [Fact]
    public void Refires_WhenResetWindowChanges()
    {
        var arm = new ThresholdArm();
        Assert.True(arm.ShouldFire(85, 80, Reset1));

        Assert.True(arm.ShouldFire(85, 80, Reset2));
    }

    [Fact]
    public void NullResetsAt_FiresOnceOnly()
    {
        var arm = new ThresholdArm();

        Assert.True(arm.ShouldFire(85, 80, null));
        Assert.False(arm.ShouldFire(90, 80, null));
        // 이후 실제 resets_at이 오면 새 윈도우로 간주
        Assert.True(arm.ShouldFire(90, 80, Reset1));
    }

    [Fact]
    public void ExactThreshold_Fires()
    {
        var arm = new ThresholdArm();

        Assert.True(arm.ShouldFire(80, 80, Reset1));
    }
}
