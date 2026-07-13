using ClaudeUsageMonitor.Core.Updates;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class IdleGateTests
{
    private const uint ThirtyMin = 30u * 60u * 1000u; // 1,800,000 ms

    [Fact]
    public void IsIdle_AtExactThreshold_True()
        => Assert.True(IdleGate.IsIdle(lastInputTickMs: 1000, nowTickMs: 1000 + ThirtyMin, ThirtyMin));

    [Fact]
    public void IsIdle_BelowThreshold_False()
        => Assert.False(IdleGate.IsIdle(lastInputTickMs: 1000, nowTickMs: 1000 + ThirtyMin - 1, ThirtyMin));

    [Fact]
    public void IsIdle_AboveThreshold_True()
        => Assert.True(IdleGate.IsIdle(lastInputTickMs: 1000, nowTickMs: 1000 + ThirtyMin + 5000, ThirtyMin));

    [Fact]
    public void IdleMilliseconds_WrapsAround_ComputesElapsed()
    {
        // now가 tick 랩어라운드로 last보다 작아진 경우: last=MaxValue-100, now=200 → 경과 301ms
        uint last = uint.MaxValue - 100; // 100ms 전 tick
        uint now = 200;                  // 랩 후 200ms
        Assert.Equal(301u, IdleGate.IdleMilliseconds(last, now));
    }

    [Fact]
    public void IsIdle_AcrossWraparound_HonoursThreshold()
    {
        uint last = uint.MaxValue - 100;
        uint now = 200; // 경과 301ms
        Assert.True(IdleGate.IsIdle(last, now, 250));
        Assert.False(IdleGate.IsIdle(last, now, 400));
    }
}
