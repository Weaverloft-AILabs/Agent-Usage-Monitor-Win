using ClaudeUsageMonitor.Core.Updates;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class AutoUpdateGateTests
{
    [Fact]
    public void AllConditionsTrue_AppliesAutomatically()
        => Assert.True(AutoUpdateGate.ShouldAutoApply(enabled: true, updateAvailable: true, isMajorJump: false, isIdle: true));

    [Fact]
    public void Disabled_DoesNotApply()
        => Assert.False(AutoUpdateGate.ShouldAutoApply(enabled: false, updateAvailable: true, isMajorJump: false, isIdle: true));

    [Fact]
    public void NoUpdate_DoesNotApply()
        => Assert.False(AutoUpdateGate.ShouldAutoApply(enabled: true, updateAvailable: false, isMajorJump: false, isIdle: true));

    [Fact]
    public void MajorJump_DoesNotApply()
        => Assert.False(AutoUpdateGate.ShouldAutoApply(enabled: true, updateAvailable: true, isMajorJump: true, isIdle: true));

    [Fact]
    public void NotIdle_DoesNotApply()
        => Assert.False(AutoUpdateGate.ShouldAutoApply(enabled: true, updateAvailable: true, isMajorJump: false, isIdle: false));
}
