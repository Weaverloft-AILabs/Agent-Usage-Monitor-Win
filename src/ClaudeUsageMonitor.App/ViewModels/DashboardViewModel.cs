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

    // 차트가 사용한 전역 모델 순서(추세 범위 기준). 모델 분해 도트 색을 차트 세그먼트 색과 일치시키는 데 사용.
    private List<string> _chartModelOrder = [];

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

    /// <summary>시작/업데이트 직후 첫 사용률 스냅샷을 받기 전 = 로딩중(게이지 %대신 "로딩중" 표시).</summary>
    [ObservableProperty]
    private bool _isLoading = true;

    private bool _dashHasData;

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

    /// <summary>비용 스파크라인용 기간별 USD 값 — 코드비하인드가 Polyline으로 렌더(별도 차트 인스턴스 없이).</summary>
    [ObservableProperty]
    private double[] _costPoints = [];

    /// <summary>비용 스파크라인의 X축 날짜 라벨(메인 차트와 동일). hover 툴팁·기본 축 표시에 사용.</summary>
    [ObservableProperty]
    private string[] _costLabels = [];

    /// <summary>총계·모델분해의 현재 기간 라벨(오늘/이번 주/이번 달).</summary>
    [ObservableProperty]
    private string _periodScopeText = "오늘";

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
            IsLoading = true; // 아직 폴링 결과 없음 (시작/업데이트 직후)
            return;
        }
        var snapshot = message.State.Snapshot;
        IsLoading = LoadingIndicator.IsLoading(_dashHasData, snapshot is not null, message.State.Status);
        if (snapshot is null)
        {
            IsStale = true;
            return;
        }
        _dashHasData = true;
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
        PeriodScopeText = PeriodIndex switch { 1 => "이번 주", 2 => "이번 달", _ => "오늘" };
        BuildTrendChart();                       // 차트 = 기간 추세(모델 스택) + 비용 스파크라인
        var current = CurrentPeriodByModel();    // 총계·모델분해 = 현재 기간(당일/이번주 일~토/이번달)
        ApplyPeriodTotals(current);
        UpdateModelBreakdown(current);
        UpdateCoverageNote();
    }

    private void BuildTrendChart()
    {
        switch (PeriodIndex)
        {
            case 1:
                BuildWeeklyTrend();
                break;
            case 2:
                BuildMonthlyTrend();
                break;
            default:
                BuildDailyTrend();
                break;
        }
    }

    private void BuildDailyTrend()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var range = _rollup.Range(today.AddDays(-13), today);
        // InvariantCulture: 커스텀 날짜 포맷의 '/'는 문화권 날짜 구분자 자리표시자 → 현재 문화권에선 '-'로 렌더됨.
        // 리터럴 슬래시를 원하므로 InvariantCulture(구분자='/')로 강제.
        var labels = range.Select(d => d.Date.ToString("MM/dd", CultureInfo.InvariantCulture)).ToArray();
        var byModel = range.Select(d => d.ByModel.ToDictionary(kv => kv.Key, kv => kv.Value.Tokens)).ToList();
        var costs = range.Select(DayCost).ToArray();
        BuildStackedChart(labels, byModel, costs);
    }

    private void BuildWeeklyTrend()
    {
        // 일요일 시작 주로 집계 — 마지막 막대 = 이번 주(KPI/모델분해 경계와 일치). 약 12주 커버.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = today.AddDays(-(int)today.DayOfWeek).AddDays(-7 * 11);
        var weeks = _rollup.Range(start, today)
            .GroupBy(d => d.Date.AddDays(-(int)d.Date.DayOfWeek))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var m = new Dictionary<string, TokenCounts>();
                foreach (var d in g)
                {
                    foreach (var (model, u) in d.ByModel)
                    {
                        m[model] = m.TryGetValue(model, out var t) ? t + u.Tokens : u.Tokens;
                    }
                }
                return (Sunday: g.Key, ByModel: m);
            })
            .ToList();
        var labels = weeks
            .Select(x => x.Sunday.ToString("MM/dd", CultureInfo.InvariantCulture)
                + " - " + x.Sunday.AddDays(6).ToString("MM/dd", CultureInfo.InvariantCulture))
            .ToArray();
        var byModel = weeks.Select(x => x.ByModel).ToList();
        var costs = weeks.Select(x => (double)CostOf(x.ByModel)).ToArray();
        BuildStackedChart(labels, byModel, costs);
    }

    private void BuildMonthlyTrend()
    {
        var months = _rollup.MonthlyTotals().TakeLast(12).ToList();
        var labels = months.Select(m => m.Month.Replace('-', '/')).ToArray();
        var byModel = months.Select(m => m.ByModel).ToList();
        var costs = months.Select(m => (double)CostOf(m.ByModel)).ToArray();
        BuildStackedChart(labels, byModel, costs);
    }

    /// <summary>모델별 스택 컬럼(일/주/월 공통). 모델명 오름차순 팔레트로 색 일관성 유지.</summary>
    private void BuildStackedChart(string[] labels, IReadOnlyList<Dictionary<string, TokenCounts>> byModelPerBucket, double[] costs)
    {
        var models = byModelPerBucket.SelectMany(b => b.Keys).Distinct().OrderBy(m => m).ToList();
        _chartModelOrder = models; // 모델 분해가 같은 순서로 색을 매기도록 보관
        // 세그먼트 값 라벨 하한 = 가장 높은 막대 합의 4% — 너무 작아 안 보이는 세그먼트에 라벨만 떠 있는 것 방지
        double maxTotal = 0;
        foreach (var b in byModelPerBucket)
        {
            double sum = 0;
            foreach (var t in b.Values)
            {
                sum += t.Total;
            }
            if (sum > maxTotal)
            {
                maxTotal = sum;
            }
        }
        var labelMin = Math.Max(1_000_000d, maxTotal * 0.04);
        var series = new List<ISeries>();
        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            // 값이 0인 버킷은 null로 둔다 — 스택엔 0 기여(시각 동일)이지만 hover 툴팁에는 해당 모델 행이 표시되지 않음
            var values = byModelPerBucket
                .Select(b => b.TryGetValue(model, out var t) && t.Total > 0 ? (double?)t.Total : null)
                .ToArray();
            series.Add(new StackedColumnSeries<double?>
            {
                Name = ShortModelName(model),
                Values = values,
                ScalesYAt = 0,
                Fill = new SolidColorPaint(Palette[i % Palette.Length]),
                // 세그먼트 값 라벨 — 1M 미만은 겹침 방지로 생략
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsSize = 10,
                DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.Middle,
                DataLabelsFormatter = point =>
                    point.Coordinate.PrimaryValue >= labelMin
                        ? FormatTokens((long)point.Coordinate.PrimaryValue)
                        : "",
                YToolTipLabelFormatter = point => FormatTokens((long)point.Coordinate.PrimaryValue),
            });
        }
        ApplyChart(series, labels, costs);
    }

    /// <summary>현재 기간(일간=당일 / 주간=이번 주 일요일~토요일 / 월간=이번 달)의 모델별 토큰 —
    /// 총계 카드·모델 분해의 소스. 차트가 보여주는 추세 범위와 별개로 "선택 기간" 집계다.</summary>
    private Dictionary<string, TokenCounts> CurrentPeriodByModel()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly from, to;
        switch (PeriodIndex)
        {
            case 1: // 이번 주 (일요일~토요일)
                from = today.AddDays(-(int)DateTime.Now.DayOfWeek);
                to = from.AddDays(6);
                break;
            case 2: // 이번 달
                from = new DateOnly(today.Year, today.Month, 1);
                to = from.AddMonths(1).AddDays(-1);
                break;
            default: // 당일
                from = today;
                to = today;
                break;
        }
        var merged = new Dictionary<string, TokenCounts>();
        foreach (var day in _rollup.Range(from, to))
        {
            foreach (var (model, usage) in day.ByModel)
            {
                merged[model] = merged.TryGetValue(model, out var t) ? t + usage.Tokens : usage.Tokens;
            }
        }
        return merged;
    }

    private void ApplyPeriodTotals(Dictionary<string, TokenCounts> byModel)
    {
        long totalTokens = 0;
        foreach (var t in byModel.Values)
        {
            totalTokens += t.Total;
        }
        PeriodTotalTokensText = FormatTokens(totalTokens);
        PeriodTotalCostText = "$" + ((double)CostOf(byModel)).ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void UpdateModelBreakdown(Dictionary<string, TokenCounts> byModel)
    {
        var rows = byModel
            .Select(kv => (Model: kv.Key, Tokens: kv.Value, Cost: CostCalculator.Cost(kv.Value, _pricing.Resolve(kv.Key))))
            .OrderByDescending(r => r.Cost)
            .ToList();
        var totalCost = rows.Sum(r => r.Cost);

        ModelBreakdown.Clear();
        foreach (var (model, tokens, cost) in rows)
        {
            // 색은 차트의 전역 모델 순서로 매겨 차트 세그먼트와 도트 색을 일치시킨다
            var gi = _chartModelOrder.IndexOf(model);
            var color = Palette[(gi < 0 ? 0 : gi) % Palette.Length];
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

    // 이중 Y축 제거: 토큰만 단일 좌축 스택. 비용은 별도 스파크라인(CostPoints)으로 분리.
    private void ApplyChart(IReadOnlyList<ISeries> series, string[] labels, double[] costs)
    {
        var labelPaint = ChartTextPaint();
        Series = series.ToArray();
        XAxes = [new Axis { Labels = labels, LabelsRotation = 0, TextSize = 11, LabelsPaint = labelPaint }];
        YAxes = [new Axis { TextSize = 11, MinLimit = 0, LabelsPaint = labelPaint, Labeler = v => FormatTokens((long)v) }];
        // 라벨을 먼저 대입해야 CostPoints 변경으로 트리거되는 DrawSparkline이 "현재 기간" 라벨을 읽는다
        // (이전엔 CostPoints를 먼저 대입 → DrawSparkline이 직전 기간 CostLabels로 그려 hover 날짜가 한 전환 지연됨)
        CostLabels = labels;
        CostPoints = costs;
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
