namespace ClaudeUsageMonitor.Core.Models;

public enum WidgetMode
{
    Taskbar,
    Floating,
    Hidden,
}

public enum ThemePreference
{
    Dark,
    Light,
    System,
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

    public ThemePreference Theme { get; set; } = ThemePreference.Dark;

    /// <summary>Floating 모드 마지막 위치 (DIP). null이면 기본 위치.</summary>
    public double? FloatingLeft { get; set; }
    public double? FloatingTop { get; set; }

    /// <summary>Taskbar 모드에서 위젯이 도킹된 모니터 장치명. null이면 주 모니터 기본 위치.</summary>
    public string? TaskbarMonitorDevice { get; set; }

    /// <summary>
    /// Taskbar 내 위젯 위치 비율(0~1, 사용 가능 구간 내 시작점 기준).
    /// 해상도/DPI가 달라져도 비율로 복원되므로 자동 적응한다. null이면 트레이 왼쪽 기본 위치.
    /// </summary>
    public double? TaskbarOffsetRatio { get; set; }
}
