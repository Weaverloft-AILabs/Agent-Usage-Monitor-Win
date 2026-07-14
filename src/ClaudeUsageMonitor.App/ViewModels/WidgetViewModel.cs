using System.Windows.Media;
using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.RateLimit;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace ClaudeUsageMonitor.App.ViewModels;

public partial class WidgetViewModel : ObservableObject,
    IRecipient<RateLimitUpdatedMessage>, IRecipient<UpdateAvailableMessage>
{
    private static readonly Brush NormalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0xAA, 0xFA)));
    private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x30)));
    private static readonly Brush CriticalBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x3C)));
    private static readonly Brush StaleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)));

    private readonly MonitorSettings _settings;
    private readonly BurnRateEstimator _estimator;
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

    /// <summary>시작/업데이트 직후 첫 사용률 스냅샷을 받기 전 = 로딩중(게이지 대신 "로딩중" 표시).</summary>
    [ObservableProperty]
    private bool _isLoading = true;

    private bool _hasData;

    /// <summary>현재 속도 기준 5시간 한도 소진 예상 (예: "~1h 20m"). 표시 조건 미달이면 빈 문자열.</summary>
    [ObservableProperty]
    private string _exhaustionText = "";

    /// <summary>새 버전 존재 — 위젯에 업데이트 아이콘 표시.</summary>
    [ObservableProperty]
    private bool _updateAvailable;

    /// <summary>업데이트 배지 툴팁 — 메이저 점프면 "수동 다운로드" 안내로 문구 전환.</summary>
    [ObservableProperty]
    private string _updateBadgeToolTip = "";

    /// <summary>Claude Code CLI 미설치/로그인 정보 불가독 — 게이지 대신 경고 문구 표시.</summary>
    [ObservableProperty]
    private bool _cliMissing;

    public WidgetViewModel(MonitorSettings settings, BurnRateEstimator estimator, Services.RateLimitPollingService poller)
    {
        _settings = settings;
        _estimator = estimator;
        WeakReferenceMessenger.Default.Register<RateLimitUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<UpdateAvailableMessage>(this);

        // VM 생성 전에 발행된 첫 폴링 결과 재생 — NoCredentials처럼 HTTP 없이 즉시 끝나는
        // 폴링은 host.Start() 직후(위젯 생성 전)에 발행되어 유실될 수 있음
        if (poller.Current is { } last)
        {
            Receive(new RateLimitUpdatedMessage(last));
        }
    }

    public void Receive(UpdateAvailableMessage message)
    {
        UpdateAvailable = true;
        UpdateBadgeToolTip = message.MajorJump
            ? "새 주요 버전 사용 가능 — 우클릭 메뉴에서 다운로드 페이지 열기"
            : "새 버전 사용 가능 — 우클릭 메뉴에서 업데이트 설치";
    }

    public void Receive(RateLimitUpdatedMessage message)
    {
        // credentials 파일이 없거나 읽을 수 없음 = CLI 미설치/미로그인 — 경고 표시 (복구되면 자동 해제)
        CliMissing = message.State.Status == RateLimitStatus.NoCredentials;

        var snapshot = message.State.Snapshot;
        IsLoading = LoadingIndicator.IsLoading(_hasData, snapshot is not null, message.State.Status);
        if (snapshot is not null)
        {
            _hasData = true;
        }
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

    /// <summary>1초 타이머에서 호출 — 리셋 카운트다운/소진 예측 갱신.</summary>
    public void Tick(DateTimeOffset now)
    {
        FiveHourResetText = FormatCountdown(_fiveHourResetsAt, now);
        SevenDayResetText = FormatCountdown(_sevenDayResetsAt, now);
        ExhaustionText = BuildExhaustionText(now);
    }

    /// <summary>리셋 전에 한도가 소진될 것으로 예측될 때만 표시.</summary>
    private string BuildExhaustionText(DateTimeOffset now)
    {
        if (!_settings.ShowExhaustionPrediction || IsStale)
        {
            return "";
        }
        if (_estimator.EstimateTimeToExhaustion(now) is not { } eta)
        {
            return "";
        }
        if (_fiveHourResetsAt is { } reset && now + eta >= reset)
        {
            return ""; // 소진 전에 리셋됨 — 경고 불필요
        }
        return eta <= TimeSpan.Zero ? "소진 임박" : "소진 ~" + FormatDuration(eta);
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{Math.Max(1, span.Minutes)}m";

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
        if (remaining.TotalDays >= 1)
        {
            // 하루 이상(주간 리셋)은 일/시간으로 — 분은 생략 (예: 4d 4h)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
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
