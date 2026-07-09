using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.App.Tray;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.RateLimit;
using CommunityToolkit.Mvvm.Messaging;

namespace ClaudeUsageMonitor.App.Notifications;

/// <summary>
/// 5시간 사용률이 설정 임계값을 넘으면 트레이 풍선 알림.
/// 한 번 발사 후에는 재발사하지 않는 Flag 방식 — 리셋 윈도우 변경(ThresholdArm),
/// 앱 재시작(인메모리), 계정 변경(AccountChangedMessage → Rearm) 시에만 다시 알린다.
/// </summary>
public sealed class ThresholdNotifier :
    IRecipient<RateLimitUpdatedMessage>, IRecipient<AccountChangedMessage>, IDisposable
{
    private readonly MonitorSettings _settings;
    private readonly TrayIconHost _tray;
    private readonly ThresholdArm _arm = new();

    public ThresholdNotifier(MonitorSettings settings, TrayIconHost tray)
    {
        _settings = settings;
        _tray = tray;
        WeakReferenceMessenger.Default.Register<RateLimitUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<AccountChangedMessage>(this);
    }

    public void Receive(AccountChangedMessage message) => _arm.Rearm();

    public void Receive(RateLimitUpdatedMessage message)
    {
        var snapshot = message.State.Snapshot;
        if (snapshot is null || snapshot.IsStale)
        {
            return;
        }

        if (_arm.ShouldFire(snapshot.FiveHourPct, _settings.WarnThresholdPct, snapshot.FiveHourResetsAt))
        {
            _tray.ShowWarning(
                "Claude 사용량 경고",
                $"5시간 사용률 {snapshot.FiveHourPct:0}% — 임계값 {_settings.WarnThresholdPct:0}%를 넘었습니다.");
        }
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.Unregister<RateLimitUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<AccountChangedMessage>(this);
    }
}
