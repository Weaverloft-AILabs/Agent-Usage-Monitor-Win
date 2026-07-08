namespace ClaudeUsageMonitor.Core.Models;

public enum WidgetMode
{
    Taskbar,
    Floating,
    Hidden,
}

/// <summary>앱 설정. settings.json으로 영속.</summary>
public sealed class MonitorSettings
{
    public const int MinPollIntervalSeconds = 180;

    private int _pollIntervalSeconds = MinPollIntervalSeconds;

    /// <summary>usage API 폴링 주기(초). 레이트리밋 보호를 위해 180초 미만은 강제 상향.</summary>
    public int PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set => _pollIntervalSeconds = Math.Max(MinPollIntervalSeconds, value);
    }

    /// <summary>5시간 사용률 경고 임계값(%).</summary>
    public double WarnThresholdPct { get; set; } = 80;

    public WidgetMode Mode { get; set; } = WidgetMode.Taskbar;

    public bool AutoStart { get; set; }

    public bool DarkTheme { get; set; } = true;

    /// <summary>Floating 모드 마지막 위치 (DIP). null이면 기본 위치.</summary>
    public double? FloatingLeft { get; set; }
    public double? FloatingTop { get; set; }
}
