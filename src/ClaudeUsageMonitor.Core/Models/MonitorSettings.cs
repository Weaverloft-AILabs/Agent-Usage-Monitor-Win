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
    public const int MinPollIntervalSeconds = 20;
    public const int DefaultPollIntervalSeconds = 180;

    private int _pollIntervalSeconds = DefaultPollIntervalSeconds;

    /// <summary>
    /// usage API 폴링 주기(초). 20초 미만은 강제 상향.
    /// 비공식 API라 짧은 주기는 sticky 429 위험이 커짐 — 기본값은 안전한 180초, 429 시 지수 백오프가 동작.
    /// </summary>
    public int PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set => _pollIntervalSeconds = Math.Max(MinPollIntervalSeconds, value);
    }

    /// <summary>5시간 사용률 경고 임계값(%).</summary>
    public double WarnThresholdPct { get; set; } = 80;

    /// <summary>5시간 사용량 경고 알림(트레이 풍선) 사용 여부. 끄면 임계값 도달에도 알리지 않음.</summary>
    public bool WarnNotificationEnabled { get; set; } = true;

    public WidgetMode Mode { get; set; } = WidgetMode.Taskbar;

    public bool AutoStart { get; set; }

    public ThemePreference Theme { get; set; } = ThemePreference.Dark;

    /// <summary>최근 사용 속도 기준 5시간 한도 소진 예측 시간을 위젯/대시보드에 표시.</summary>
    public bool ShowExhaustionPrediction { get; set; } = true;

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
