using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Pricing;
using ClaudeUsageMonitor.Core.RateLimit;
using ClaudeUsageMonitor.Core.Rollup;
using ClaudeUsageMonitor.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace ClaudeUsageMonitor.App.ViewModels;

public sealed record LiveSessionItem(string ProjectName, string FullPath, string Status);

/// <summary>기간 내 모델별 비용 분해 행. Dot 색은 일간 차트의 모델 색과 동일 매핑.
/// Share(0~1)는 100% 비용 공유 바의 세그먼트 폭에 쓰인다.</summary>
public sealed record ModelCostItem(
    string Model, string TokensText, string CostText, string ShareText, double Share, System.Windows.Media.Brush Dot);

public partial class DashboardViewModel : ObservableObject,
    IRecipient<RateLimitUpdatedMessage>, IRecipient<RollupUpdatedMessage>
{
    // 테마 자동초기화에 의존하지 않도록 시리즈 색을 명시 지정
    private static readonly SKColor[] Palette =
    [
        new(0x5A, 0xAA, 0xFA),
        new(0x8A, 0x6C, 0xF0),
        new(0x4C, 0xC3, 0x8A),
        new(0xF0, 0xA0, 0x30),
        new(0xE0, 0x50, 0x3C),
        new(0x46, 0xC8, 0xC8),
    ];

    private readonly LiveSessionService _sessions;
    private readonly PricingService _pricing;
    private readonly MonitorSettings _settings;
    private readonly BurnRateEstimator _estimator;
    private RollupData _rollup = new();
    private DateTimeOffset? _fiveHourResetsAt;

    // 대시보드는 실시간 자동 갱신하지 않는다 — 최신 폴링 결과만 보관했다가 Refresh(창 열림/새로고침)에서 반영
    private RateLimitUpdatedMessage? _latestRateLimit;

    [ObservableProperty]
    private double _fiveHourPct;

    [ObservableProperty]
    private double _sevenDayPct;

    [ObservableProperty]
    private string _fiveHourResetText = "-";

    [ObservableProperty]
    private string _sevenDayResetText = "-";

    [ObservableProperty]
    private bool _isStale = true;

    /// <summary>5시간 경고 임계값(%) — 히어로 게이지의 임계값 틱 위치.</summary>
    [ObservableProperty]
    private double _warnThresholdPct;

    [ObservableProperty]
    private string _planBadge = "";

    [ObservableProperty]
    private ISeries[] _series = [];

    [ObservableProperty]
    private Axis[] _xAxes = [new Axis()];

    [ObservableProperty]
    private Axis[] _yAxes = [new Axis()];

    [ObservableProperty]
    private int _periodIndex; // 0=일간 1=주간 2=월간

    [ObservableProperty]
    private string _periodTotalTokensText = "-";

    [ObservableProperty]
    private string _periodTotalCostText = "-";

    [ObservableProperty]
    private string _periodTokensAvgText = "";

    [ObservableProperty]
    private string _periodCostAvgText = "";

    /// <summary>비용 스파크라인용 기간별 USD 값 — 코드비하인드가 Polyline으로 렌더(별도 차트 인스턴스 없이).</summary>
    [ObservableProperty]
    private double[] _costPoints = [];

    [ObservableProperty]
    private string _coverageNote = "";

    [ObservableProperty]
    private bool _hasNoLiveSessions = true;

    [ObservableProperty]
    private SolidColorPaint _legendTextPaint = new(new SKColor(0xC8, 0xC8, 0xD8));

    [ObservableProperty]
    private SolidColorPaint _tooltipTextPaint = new(new SKColor(0xE8, 0xE8, 0xF0));

    [ObservableProperty]
    private SolidColorPaint _tooltipBackgroundPaint = new(new SKColor(0x2A, 0x2C, 0x3A));

    /// <summary>현재 속도 기준 5시간 한도 소진 예측 문구. 조건 미달이면 빈 문자열(숨김).</summary>
    [ObservableProperty]
    private string _exhaustionNote = "";

    public ObservableCollection<LiveSessionItem> LiveSessions { get; } = [];

    /// <summary>선택된 기간의 모델별 토큰·비용 분해 (비용 내림차순).</summary>
    public ObservableCollection<ModelCostItem> ModelBreakdown { get; } = [];

    [ObservableProperty]
    private bool _hasBreakdown;

    public DashboardViewModel(
        LiveSessionService sessions,
        PricingService pricing,
        CredentialsReader credentials,
        Services.RateLimitPollingService poller,
        MonitorSettings settings,
        BurnRateEstimator estimator)
    {
        _sessions = sessions;
        _pricing = pricing;
        _settings = settings;
        _estimator = estimator;

        var creds = credentials.TryRead();
        PlanBadge = creds is null
            ? "로그인 정보 없음"
            : $"{creds.SubscriptionType ?? "?"} · {creds.RateLimitTier ?? ""}".TrimEnd(' ', '·');

        WeakReferenceMessenger.Default.Register<RateLimitUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<RollupUpdatedMessage>(this);

        // VM 생성 이전에 발행된 마지막 폴링 결과를 보관 — 최초 Refresh(창 열림)에서 게이지에 반영
        if (poller.Current is { } lastState)
        {
            _latestRateLimit = new RateLimitUpdatedMessage(lastState);
        }

        // 테마 변경 시 차트 텍스트 페인트 재적용 (VM은 앱 수명 동안 유지 — 구독 해제 불필요)
        Theming.ThemeManager.EffectiveThemeChanged += () => OnUi(() =>
        {
            LegendTextPaint = ChartTextPaint();
            TooltipTextPaint = TooltipText();
            TooltipBackgroundPaint = TooltipBackground();
            RebuildChart();
        });
        _legendTextPaint = ChartTextPaint();
        _tooltipTextPaint = TooltipText();
        _tooltipBackgroundPaint = TooltipBackground();
    }

    private static SolidColorPaint TooltipText() => new(
        Theming.ThemeManager.IsDarkEffective
            ? new SKColor(0xE8, 0xE8, 0xF0)
            : new SKColor(0x23, 0x25, 0x2F));

    private static SolidColorPaint TooltipBackground() => new(
        Theming.ThemeManager.IsDarkEffective
            ? new SKColor(0x2A, 0x2C, 0x3A)
            : new SKColor(0xFF, 0xFF, 0xFF));

    private static SolidColorPaint ChartTextPaint() => new(
        Theming.ThemeManager.IsDarkEffective
            ? new SKColor(0xC8, 0xC8, 0xD8)
            : new SKColor(0x4A, 0x4D, 0x5E));

    // 자동 갱신 안 함 — 최신 폴링 결과만 보관, 화면 반영은 Refresh(창 열림/새로고침)에서만
    public void Receive(RateLimitUpdatedMessage message) => _latestRateLimit = message;

    /// <summary>보관된 최신 폴링 결과를 게이지·리셋·예측 문구에 반영 (Refresh에서 UI 스레드로 호출).</summary>
    private void ApplyRateLimit()
    {
        if (_latestRateLimit is not { } message)
        {
            return;
        }
        var snapshot = message.State.Snapshot;
        if (snapshot is null)
        {
            IsStale = true;
            return;
        }
        FiveHourPct = snapshot.FiveHourPct;
        SevenDayPct = snapshot.SevenDayPct;
        IsStale = snapshot.IsStale;
        _fiveHourResetsAt = snapshot.FiveHourResetsAt;
        FiveHourResetText = snapshot.FiveHourResetsAt is { } f ? f.ToLocalTime().ToString("HH:mm") + " 리셋" : "-";
        SevenDayResetText = snapshot.SevenDayResetsAt is { } s ? s.ToLocalTime().ToString("MM/dd HH:mm") + " 리셋" : "-";
        ExhaustionNote = BuildExhaustionNote(DateTimeOffset.UtcNow);
    }

    /// <summary>리셋 전에 한도가 소진될 것으로 예측될 때만 문구 표시.</summary>
    private string BuildExhaustionNote(DateTimeOffset now)
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
            return "";
        }
        if (eta <= TimeSpan.Zero)
        {
            return "현재 속도로 곧 5시간 한도 소진";
        }
        var text = eta.TotalHours >= 1
            ? $"{(int)eta.TotalHours}시간 {eta.Minutes}분"
            : $"{Math.Max(1, eta.Minutes)}분";
        return $"현재 속도로 약 {text} 후 한도 소진 예상";
    }

    // 자동 갱신 안 함 — 최신 롤업만 보관, 차트 반영은 Refresh(창 열림/새로고침)에서만
    public void Receive(RollupUpdatedMessage message) => _rollup = message.Rollup;

    public void SetRollup(RollupData rollup) => _rollup = rollup;

    /// <summary>창이 (다시) 열리거나 새로고침 버튼을 누를 때 호출 — 보관된 최신 데이터(게이지·차트·현재 프로젝트)를
    /// 한 번에 화면에 반영한다. 대시보드는 실시간 자동 갱신하지 않고 이 시점의 스냅샷만 보여준다.</summary>
    public void Refresh() => OnUi(() =>
    {
        WarnThresholdPct = _settings.WarnThresholdPct;
        ApplyRateLimit();
        RefreshLiveSessions();
        RebuildChart();
    });

    partial void OnPeriodIndexChanged(int value) => RebuildChart();

    public void RefreshLiveSessions()
    {
        var live = _sessions.GetLive();
        LiveSessions.Clear();
        foreach (var session in live)
        {
            var name = session.Cwd.TrimEnd('\\', '/');
            var idx = name.LastIndexOfAny(['\\', '/']);
            if (idx >= 0)
            {
                name = name[(idx + 1)..];
            }
            LiveSessions.Add(new LiveSessionItem(name, session.Cwd, session.Status ?? ""));
        }
        HasNoLiveSessions = LiveSessions.Count == 0;
    }

    private void RebuildChart()
    {
        switch (PeriodIndex)
        {
            case 1:
                BuildWeekly();
                break;
            case 2:
                BuildMonthly();
                break;
            default:
                BuildDaily();
                break;
        }
        UpdateCoverageNote();
    }

    private void BuildDaily()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-13);
        var range = _rollup.Range(from, today);

        var labels = range.Select(d => d.Date.ToString("MM-dd")).ToArray();
        var models = range.SelectMany(d => d.ByModel.Keys).Distinct().OrderBy(m => m).ToList();

        var seriesList = new List<ISeries>();
        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var values = range
                .Select(d => d.ByModel.TryGetValue(model, out var u) ? (double)u.Tokens.Total : 0d)
                .ToArray();
            seriesList.Add(new StackedColumnSeries<double>
            {
                Name = ShortModelName(model),
                Values = values,
                ScalesYAt = 0,
                Fill = new SolidColorPaint(Palette[i % Palette.Length]),
                // 토큰 수치 라벨 — 1M 미만 세그먼트는 겹침 방지를 위해 생략
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsSize = 10,
                DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.Middle,
                DataLabelsFormatter = point =>
                    point.Coordinate.PrimaryValue >= 1_000_000
                        ? FormatTokens((long)point.Coordinate.PrimaryValue)
                        : "",
                YToolTipLabelFormatter = point => FormatTokens((long)point.Coordinate.PrimaryValue),
            });
        }

        var costs = range.Select(DayCost).ToArray();
        ApplyChart(seriesList, labels,
            range.Sum(d => d.TotalTokens.Total),
            costs.Sum(), costs, "/일");

        var merged = new Dictionary<string, TokenCounts>();
        foreach (var day in range)
        {
            foreach (var (model, usage) in day.ByModel)
            {
                merged[model] = merged.TryGetValue(model, out var t) ? t + usage.Tokens : usage.Tokens;
            }
        }
        UpdateModelBreakdown(merged);
    }

    private void BuildWeekly()
    {
        var weeks = _rollup.WeeklyTotals().TakeLast(12).ToList();
        var labels = weeks.Select(w => w.WeekStart.ToString("MM-dd") + "~").ToArray();
        var tokens = weeks.Select(w => (double)w.Tokens.Total).ToArray();
        var costs = weeks.Select(w => (double)CostOf(w.ByModel)).ToArray();

        ApplyChart(
            [MakeTokenColumns(tokens, Palette[0])],
            labels,
            weeks.Sum(w => w.Tokens.Total),
            costs.Sum(), costs, "/주");

        UpdateModelBreakdown(MergeByModel(weeks.Select(w => w.ByModel)));
    }

    private void BuildMonthly()
    {
        var months = _rollup.MonthlyTotals().TakeLast(12).ToList();
        var labels = months.Select(m => m.Month).ToArray();
        var tokens = months.Select(m => (double)m.Tokens.Total).ToArray();
        var costs = months.Select(m => (double)CostOf(m.ByModel)).ToArray();

        ApplyChart(
            [MakeTokenColumns(tokens, Palette[1])],
            labels,
            months.Sum(m => m.Tokens.Total),
            costs.Sum(), costs, "/월");

        UpdateModelBreakdown(MergeByModel(months.Select(m => m.ByModel)));
    }

    private static Dictionary<string, TokenCounts> MergeByModel(IEnumerable<Dictionary<string, TokenCounts>> parts)
    {
        var merged = new Dictionary<string, TokenCounts>();
        foreach (var part in parts)
        {
            foreach (var (model, tokens) in part)
            {
                merged[model] = merged.TryGetValue(model, out var t) ? t + tokens : tokens;
            }
        }
        return merged;
    }

    private void UpdateModelBreakdown(Dictionary<string, TokenCounts> byModel)
    {
        // 팔레트 인덱스는 일간 차트 시리즈와 동일한 모델명 오름차순 매핑 — 색 일관성 유지
        var paletteOrder = byModel.Keys.OrderBy(m => m).ToList();
        var rows = byModel
            .Select(kv => (Model: kv.Key, Tokens: kv.Value, Cost: CostCalculator.Cost(kv.Value, _pricing.Resolve(kv.Key))))
            .OrderByDescending(r => r.Cost)
            .ToList();
        var totalCost = rows.Sum(r => r.Cost);

        ModelBreakdown.Clear();
        foreach (var (model, tokens, cost) in rows)
        {
            var color = Palette[paletteOrder.IndexOf(model) % Palette.Length];
            var dot = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue));
            dot.Freeze();
            var share = totalCost > 0 ? (double)(cost / totalCost) : 0d;
            ModelBreakdown.Add(new ModelCostItem(
                ShortModelName(model),
                FormatTokens(tokens.Total),
                "$" + ((double)cost).ToString("0.00", CultureInfo.InvariantCulture),
                totalCost > 0 ? share.ToString("P0", CultureInfo.InvariantCulture) : "",
                share,
                dot));
        }
        HasBreakdown = ModelBreakdown.Count > 0;
    }

    private ColumnSeries<double> MakeTokenColumns(double[] tokens, SKColor color) => new()
    {
        Name = "토큰",
        Values = tokens,
        ScalesYAt = 0,
        Fill = new SolidColorPaint(color),
        DataLabelsPaint = ChartTextPaint(),
        DataLabelsSize = 10,
        DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.Top,
        DataLabelsFormatter = point =>
            point.Coordinate.PrimaryValue > 0
                ? FormatTokens((long)point.Coordinate.PrimaryValue)
                : "",
        YToolTipLabelFormatter = point => FormatTokens((long)point.Coordinate.PrimaryValue),
    };

    // 이중 Y축 제거: 토큰만 단일 좌축 스택. 비용은 별도 스파크라인(CostPoints) + KPI 평균 + 모델 공유 바로 분리.
    private void ApplyChart(IReadOnlyList<ISeries> series, string[] labels, long totalTokens, double totalCost,
        double[] costs, string avgUnit)
    {
        var labelPaint = ChartTextPaint();
        Series = series.ToArray();
        XAxes = [new Axis { Labels = labels, LabelsRotation = 0, TextSize = 11, LabelsPaint = labelPaint }];
        YAxes = [new Axis { TextSize = 11, MinLimit = 0, LabelsPaint = labelPaint, Labeler = v => FormatTokens((long)v) }];
        CostPoints = costs;
        PeriodTotalTokensText = FormatTokens(totalTokens);
        PeriodTotalCostText = "$" + totalCost.ToString("0.00", CultureInfo.InvariantCulture);
        var n = Math.Max(1, labels.Length);
        PeriodTokensAvgText = "≈ " + FormatTokens(totalTokens / n) + " " + avgUnit;
        PeriodCostAvgText = "≈ $" + (totalCost / n).ToString("0.00", CultureInfo.InvariantCulture) + " " + avgUnit;
    }

    private double DayCost(DailyRollup day)
    {
        decimal total = 0;
        foreach (var (model, usage) in day.ByModel)
        {
            total += CostCalculator.Cost(usage.Tokens, _pricing.Resolve(model));
        }
        return (double)total;
    }

    private decimal CostOf(Dictionary<string, TokenCounts> byModel)
    {
        decimal total = 0;
        foreach (var (model, tokens) in byModel)
        {
            total += CostCalculator.Cost(tokens, _pricing.Resolve(model));
        }
        return total;
    }

    private void UpdateCoverageNote()
    {
        CoverageNote = _rollup.CoverageStart is { } start
            ? $"집계 시작: {start} (이전 기간은 Claude Code 보존 정책으로 데이터 없음)"
            : "";
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => (tokens / 1_000_000_000.0).ToString("0.0") + "B",
        >= 1_000_000 => (tokens / 1_000_000.0).ToString("0.0") + "M",
        >= 1_000 => (tokens / 1_000.0).ToString("0.0") + "K",
        _ => tokens.ToString(),
    };

    private static string ShortModelName(string model) =>
        model.Replace("claude-", "", StringComparison.OrdinalIgnoreCase);

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
