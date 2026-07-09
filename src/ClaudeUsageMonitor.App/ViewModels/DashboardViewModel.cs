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

    private static readonly SKColor CostColor = new(0x7B, 0xD8, 0x8F);

    private readonly LiveSessionService _sessions;
    private readonly PricingService _pricing;
    private readonly MonitorSettings _settings;
    private readonly BurnRateEstimator _estimator;
    private RollupData _rollup = new();
    private DateTimeOffset? _fiveHourResetsAt;

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

        // VM이 대시보드 최초 오픈 시점에 생성되므로, 그 이전에 발행된 폴링 결과를 즉시 반영
        if (poller.Current is { } lastState)
        {
            Receive(new RateLimitUpdatedMessage(lastState));
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

    public void Receive(RateLimitUpdatedMessage message)
    {
        var snapshot = message.State.Snapshot;
        if (snapshot is null)
        {
            IsStale = true;
            return;
        }
        OnUi(() =>
        {
            FiveHourPct = snapshot.FiveHourPct;
            SevenDayPct = snapshot.SevenDayPct;
            IsStale = snapshot.IsStale;
            _fiveHourResetsAt = snapshot.FiveHourResetsAt;
            FiveHourResetText = snapshot.FiveHourResetsAt is { } f ? f.ToLocalTime().ToString("HH:mm") + " 리셋" : "-";
            SevenDayResetText = snapshot.SevenDayResetsAt is { } s ? s.ToLocalTime().ToString("MM/dd HH:mm") + " 리셋" : "-";
            ExhaustionNote = BuildExhaustionNote(DateTimeOffset.UtcNow);
        });
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

    public void Receive(RollupUpdatedMessage message)
    {
        _rollup = message.Rollup;
        OnUi(RebuildChart);
    }

    public void SetRollup(RollupData rollup)
    {
        _rollup = rollup;
        RebuildChart();
    }

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
        seriesList.Add(MakeCostLine(costs));

        ApplyChart(seriesList, labels,
            range.Sum(d => d.TotalTokens.Total),
            costs.Sum());
    }

    private void BuildWeekly()
    {
        var weeks = _rollup.WeeklyTotals().TakeLast(12).ToList();
        var labels = weeks.Select(w => w.WeekStart.ToString("MM-dd") + "~").ToArray();
        var tokens = weeks.Select(w => (double)w.Tokens.Total).ToArray();
        var costs = weeks.Select(w => (double)CostOf(w.ByModel)).ToArray();

        ApplyChart(
            [
                MakeTokenColumns(tokens, Palette[0]),
                MakeCostLine(costs),
            ],
            labels,
            weeks.Sum(w => w.Tokens.Total),
            costs.Sum());
    }

    private void BuildMonthly()
    {
        var months = _rollup.MonthlyTotals().TakeLast(12).ToList();
        var labels = months.Select(m => m.Month).ToArray();
        var tokens = months.Select(m => (double)m.Tokens.Total).ToArray();
        var costs = months.Select(m => (double)CostOf(m.ByModel)).ToArray();

        ApplyChart(
            [
                MakeTokenColumns(tokens, Palette[1]),
                MakeCostLine(costs),
            ],
            labels,
            months.Sum(m => m.Tokens.Total),
            costs.Sum());
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

    private void ApplyChart(IReadOnlyList<ISeries> series, string[] labels, long totalTokens, double totalCost)
    {
        var labelPaint = ChartTextPaint();
        Series = series.ToArray();
        XAxes = [new Axis { Labels = labels, LabelsRotation = 0, TextSize = 11, LabelsPaint = labelPaint }];
        YAxes =
        [
            new Axis { Name = "Tokens", TextSize = 11, MinLimit = 0, LabelsPaint = labelPaint, NamePaint = labelPaint },
            new Axis
            {
                Name = "USD",
                Position = LiveChartsCore.Measure.AxisPosition.End,
                TextSize = 11,
                MinLimit = 0,
                LabelsPaint = labelPaint,
                NamePaint = labelPaint,
            },
        ];
        PeriodTotalTokensText = FormatTokens(totalTokens);
        PeriodTotalCostText = "$" + totalCost.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static LineSeries<double> MakeCostLine(double[] costs) => new()
    {
        Name = "비용($)",
        Values = costs,
        ScalesYAt = 1,
        Stroke = new SolidColorPaint(CostColor) { StrokeThickness = 2.5f },
        Fill = null,
        GeometryStroke = new SolidColorPaint(CostColor) { StrokeThickness = 2f },
        GeometryFill = new SolidColorPaint(new SKColor(0x1E, 0x1F, 0x29)),
        GeometrySize = 7,
        // USD 수치 라벨 — $1 미만은 생략
        DataLabelsPaint = new SolidColorPaint(CostColor),
        DataLabelsSize = 10,
        DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.Top,
        DataLabelsFormatter = point =>
            point.Coordinate.PrimaryValue >= 1
                ? "$" + point.Coordinate.PrimaryValue.ToString("0", CultureInfo.InvariantCulture)
                : "",
        YToolTipLabelFormatter = point =>
            "$" + point.Coordinate.PrimaryValue.ToString("0.00", CultureInfo.InvariantCulture),
    };

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
