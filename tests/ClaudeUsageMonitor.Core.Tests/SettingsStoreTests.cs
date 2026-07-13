using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "aum-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void WarnNotification_DefaultsToEnabled()
        => Assert.True(new MonitorSettings().WarnNotificationEnabled);

    [Fact]
    public void WarnNotification_Disabled_RoundTrips()
    {
        var store = new SettingsStore(_dir);
        store.Save(new MonitorSettings { WarnNotificationEnabled = false });

        Assert.False(store.Load().WarnNotificationEnabled);
    }

    [Fact]
    public void MissingKey_LoadsAsEnabled_ForBackCompat()
    {
        // 구버전 settings.json(키 없음)에서도 기본 켜짐 유지
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "PollIntervalSeconds": 180 }""");

        Assert.True(new SettingsStore(_dir).Load().WarnNotificationEnabled);
    }

    [Fact]
    public void WarnThreshold_DefaultsTo70()
        => Assert.Equal(70, new MonitorSettings().WarnThresholdPct);

    [Fact]
    public void WarnThreshold_MissingKey_LoadsAsDefault70()
    {
        // 구버전 settings.json(임계값 키 없음)은 기본 70%로 로드
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "PollIntervalSeconds": 180 }""");

        Assert.Equal(70, new SettingsStore(_dir).Load().WarnThresholdPct);
    }

    [Fact]
    public void AutoUpdateWhenIdle_DefaultsToOff()
        => Assert.False(new MonitorSettings().AutoUpdateWhenIdle);

    [Fact]
    public void AutoUpdateWhenIdle_MissingKey_LoadsAsOff()
    {
        // 구버전 settings.json(키 없음)은 자동 업데이트 꺼짐으로 로드 (기본 OFF)
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "PollIntervalSeconds": 180 }""");

        Assert.False(new SettingsStore(_dir).Load().AutoUpdateWhenIdle);
    }

    [Fact]
    public void AutoUpdateWhenIdle_RoundTrips()
    {
        var store = new SettingsStore(_dir);
        store.Save(new MonitorSettings { AutoUpdateWhenIdle = true });

        Assert.True(store.Load().AutoUpdateWhenIdle);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
