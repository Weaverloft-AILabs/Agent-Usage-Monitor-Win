using System.Windows.Media;
using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace ClaudeUsageMonitor.App.ViewModels;

public partial class WidgetViewModel : ObservableObject, IRecipient<RateLimitUpdatedMessage>
{
    private static readonly Brush NormalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0xAA, 0xFA)));
    private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x30)));
    private static readonly Brush CriticalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x3C)));
    private static readonly Brush StaleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)));

    private readonly MonitorSettings _settings;
    private DateTimeOffset? _fiveHourResetsAt;
    private DateTimeOffset? _sevenDayResetsAt;

    [ObservableProperty]
    private double _fiveHourPct;

    [ObservableProperty]
    private double _sevenDayPct;

    [ObservableProperty]
    private string _fiveHourResetText = "-";

    [ObservableProperty]
    private string _sevenDayResetText = "-";

    [ObservableProperty]
    private Brush _fiveHourBrush = StaleBrush;

    [ObservableProperty]
    private Brush _sevenDayBrush = StaleBrush;

    [ObservableProperty]
    private bool _isStale = true;

    public WidgetViewModel(MonitorSettings settings)
    {
        _settings = settings;
        WeakReferenceMessenger.Default.Register(this);
    }

    public void Receive(RateLimitUpdatedMessage message)
    {
        var snapshot = message.State.Snapshot;
        if (snapshot is null)
        {
            IsStale = true;
            FiveHourBrush = StaleBrush;
            SevenDayBrush = StaleBrush;
            return;
        }

        FiveHourPct = snapshot.FiveHourPct;
        SevenDayPct = snapshot.SevenDayPct;
        _fiveHourResetsAt = snapshot.FiveHourResetsAt;
        _sevenDayResetsAt = snapshot.SevenDayResetsAt;
        IsStale = snapshot.IsStale;

        FiveHourBrush = PickBrush(snapshot.FiveHourPct, snapshot.IsStale);
        SevenDayBrush = PickBrush(snapshot.SevenDayPct, snapshot.IsStale);
        Tick(DateTimeOffset.UtcNow);
    }

    /// <summary>1초 타이머에서 호출 — 리셋 카운트다운 갱신.</summary>
    public void Tick(DateTimeOffset now)
    {
        FiveHourResetText = FormatCountdown(_fiveHourResetsAt, now);
        SevenDayResetText = FormatCountdown(_sevenDayResetsAt, now);
    }

    private Brush PickBrush(double pct, bool stale)
    {
        if (stale)
        {
            return StaleBrush;
        }
        if (pct >= 95)
        {
            return CriticalBrush;
        }
        return pct >= _settings.WarnThresholdPct ? WarnBrush : NormalBrush;
    }

    private static string FormatCountdown(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset)
        {
            return "-";
        }

        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "0m";
        }
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
            : $"{remaining.Minutes}m";
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
