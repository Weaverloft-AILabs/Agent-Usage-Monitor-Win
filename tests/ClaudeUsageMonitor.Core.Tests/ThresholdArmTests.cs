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

    [Fact]
    public void DoesNotRefire_WhenResetsAtSubSecondJitters_SameWindow()
    {
        // 실측(2026-07-13): usage API는 매 응답마다 resets_at의 초 미만 소수부를 다르게 반환한다
        // (예: 10:59:59.542213 → .918093 → .123456). 같은 5시간 윈도우이므로 재발사하면 안 된다.
        // 이 지터를 정규화하지 않으면 임계값 초과 상태에서 폴링(180초)마다 경고가 반복 발사된다.
        var arm = new ThresholdArm();
        var a = DateTimeOffset.Parse("2026-07-13T10:59:59.542213+00:00");
        var b = DateTimeOffset.Parse("2026-07-13T10:59:59.918093+00:00");
        var c = DateTimeOffset.Parse("2026-07-13T10:59:59.123456+00:00");

        Assert.True(arm.ShouldFire(85, 80, a));   // 최초 1회 발사
        Assert.False(arm.ShouldFire(85, 80, b));  // 소수부만 다름 — 재발사 금지
        Assert.False(arm.ShouldFire(90, 80, c));  // 폴링 반복에도 금지
    }

    [Fact]
    public void Refires_WhenResetWindowChanges_DespiteJitter()
    {
        // 정규화가 서로 다른 윈도우(5시간 간격)까지 뭉개지 않는지 보증.
        var arm = new ThresholdArm();
        var window1 = DateTimeOffset.Parse("2026-07-13T10:59:59.542213+00:00");
        var window2 = DateTimeOffset.Parse("2026-07-13T15:59:59.918093+00:00");

        Assert.True(arm.ShouldFire(85, 80, window1));
        Assert.True(arm.ShouldFire(85, 80, window2)); // 다른 윈도우 — 재무장 발사
    }

    [Fact]
    public void DoesNotRefire_WhenResetsAtBecomesNull_InSameEpisode()
    {
        // resets_at가 지터로 잠깐 누락(null)돼도 같은 초과 에피소드는 재발사하지 않아야 한다.
        var arm = new ThresholdArm();
        var window = DateTimeOffset.Parse("2026-07-13T10:59:59.5+00:00");

        Assert.True(arm.ShouldFire(85, 80, window));   // 실제 윈도우로 1회 발사
        Assert.False(arm.ShouldFire(86, 80, null));    // 같은 에피소드, resets_at 누락 — 재발사 금지
        Assert.False(arm.ShouldFire(86, 80, window));  // 윈도우 복귀해도 여전히 금지
    }
}
