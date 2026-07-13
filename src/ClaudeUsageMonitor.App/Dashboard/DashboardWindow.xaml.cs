using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeUsageMonitor.App.ViewModels;

namespace ClaudeUsageMonitor.App.Dashboard;

public partial class DashboardWindow : Window
{
    private static readonly Color CostGreen = Color.FromRgb(0x7B, 0xD8, 0x8F);
    private static readonly Color SparkAxis = Color.FromRgb(0x9A, 0x9C, 0xB0);
    private static readonly Color SparkMarkerFill = Color.FromRgb(0x1E, 0x1F, 0x29);
    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Consolas, Courier New");

    private readonly DashboardViewModel _viewModel;
    private DateTime _lastHoverKick;

    public event Action? SettingsRequested;
    public event Action? InquiryRequested;

    public DashboardWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        DailyToggle.Checked += (_, _) => _viewModel.PeriodIndex = 0;
        WeeklyToggle.Checked += (_, _) => _viewModel.PeriodIndex = 1;
        MonthlyToggle.Checked += (_, _) => _viewModel.PeriodIndex = 2;
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        InquiryButton.Click += (_, _) => InquiryRequested?.Invoke();

        // 새로고침 — 대시보드는 자동 갱신하지 않으므로 이 버튼(또는 창 재오픈)으로 최신 데이터를 다시 반영
        RefreshButton.Click += (_, _) =>
        {
            _viewModel.Refresh();
            NudgeChart();
        };

        _viewModel.PropertyChanged += (_, args) =>
        {
            // Series 교체(기간 토글/롤업 갱신) 후에도 렌더가 고착될 수 있어 매번 킥 — 없으면 토글이 무반응
            if (args.PropertyName == nameof(DashboardViewModel.Series) && IsVisible)
            {
                NudgeChart();
            }
            // 비용 스파크라인은 별도 LiveCharts 인스턴스 없이 Polyline으로 직접 렌더 (측정 고착 회피)
            else if (args.PropertyName == nameof(DashboardViewModel.CostPoints))
            {
                DrawSparkline();
            }
        };

        // 모델 공유 바(100% 스택)는 비율(star) 컬럼으로 코드비하인드에서 구성
        _viewModel.ModelBreakdown.CollectionChanged += (_, _) => BuildShareBar();

        SparkCanvas.SizeChanged += (_, _) => DrawSparkline();

        // hover 툴팁도 같은 렌더 고착으로 그려지지 않음 — 차트 위 마우스 이동 시 스로틀 킥으로 페인트 강제
        UsageChart.MouseMove += (_, _) =>
        {
            var now = DateTime.UtcNow;
            if ((now - _lastHoverKick).TotalMilliseconds >= 250)
            {
                _lastHoverKick = now;
                BounceChartMargin();
            }
        };

        // 대시보드는 실시간 자동 갱신하지 않음 — 창이 (다시) 열릴 때만 최신 데이터 스냅샷을 반영
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _viewModel.Refresh();
                NudgeChart();
            }
        };
        ContentRendered += (_, _) => NudgeChart();
    }

    /// <summary>비용 추이 스파크라인 — CostPoints를 SparkCanvas에 영역+선+끝점으로 직접 그린다(단일 $축).</summary>
    private void DrawSparkline()
    {
        SparkCanvas.Children.Clear();
        var pts = _viewModel.CostPoints;
        double w = SparkCanvas.ActualWidth, h = SparkCanvas.ActualHeight;
        if (pts is null || pts.Length == 0 || w <= 2 || h <= 2)
        {
            return;
        }

        const double padL = 32, padR = 10, padT = 10, padB = 14;
        double plotW = w - padL - padR, plotH = h - padT - padB;
        if (plotW <= 4 || plotH <= 4)
        {
            return;
        }

        double max = 0;
        foreach (var v in pts)
        {
            if (v > max) max = v;
        }
        if (max <= 0) max = 1;

        int n = pts.Length;
        double dx = n > 1 ? plotW / (n - 1) : 0;
        Point At(int i) => new(
            padL + (n > 1 ? i * dx : plotW / 2),
            padT + plotH - pts[i] / max * plotH);

        var stroke = new SolidColorBrush(CostGreen);
        stroke.Freeze();

        // 영역(그라데이션)
        var fig = new PathFigure { StartPoint = new Point(padL, padT + plotH) };
        for (var i = 0; i < n; i++)
        {
            fig.Segments.Add(new LineSegment(At(i), false));
        }
        fig.Segments.Add(new LineSegment(new Point(padL + (n > 1 ? plotW : plotW / 2 + 0.1), padT + plotH), false));
        fig.IsClosed = true;
        var areaBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x38, CostGreen.R, CostGreen.G, CostGreen.B), 0),
                new GradientStop(Color.FromArgb(0x00, CostGreen.R, CostGreen.G, CostGreen.B), 1),
            },
        };
        SparkCanvas.Children.Add(new Path { Data = new PathGeometry(new[] { fig }), Fill = areaBrush });

        // 선
        var line = new Polyline { Stroke = stroke, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        for (var i = 0; i < n; i++)
        {
            line.Points.Add(At(i));
        }
        SparkCanvas.Children.Add(line);

        // 끝점 마커
        var last = At(n - 1);
        var dot = new Ellipse
        {
            Width = 7, Height = 7, Stroke = stroke, StrokeThickness = 2,
            Fill = new SolidColorBrush(SparkMarkerFill),
        };
        Canvas.SetLeft(dot, last.X - 3.5);
        Canvas.SetTop(dot, last.Y - 3.5);
        SparkCanvas.Children.Add(dot);

        // $축 라벨
        AddSparkLabel("$0", 4, padT + plotH - 8);
        AddSparkLabel("$" + max.ToString("0"), 4, padT - 4);
    }

    private void AddSparkLabel(string text, double x, double y)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = 10, FontFamily = MonoFamily,
            Foreground = new SolidColorBrush(SparkAxis),
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        SparkCanvas.Children.Add(label);
    }

    /// <summary>모델별 비용 100% 공유 바 — ModelBreakdown의 Share 비율로 star 컬럼을 구성한다.</summary>
    private void BuildShareBar()
    {
        ShareBarHost.ColumnDefinitions.Clear();
        ShareBarHost.Children.Clear();
        var items = _viewModel.ModelBreakdown;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            ShareBarHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.0001, item.Share), GridUnitType.Star),
            });
            var seg = new Border
            {
                Background = item.Dot,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
            };
            Grid.SetColumn(seg, i);
            ShareBarHost.Children.Add(seg);
        }
    }

    /// <summary>
    /// LiveCharts2 2.0.4의 렌더 누락 대응 — 창 표시/Series 교체 후 첫 measure가 지오메트리를
    /// 만들지 못해 캔버스가 이전 상태로 고착되는 문제(예외 없음). CoreChart.Update 강제나
    /// LiveCharts.Configure만으로는 복구되지 않고, 오직 "요소 크기 변경"에 의한 재측정만이
    /// 렌더를 살린다(실기기 반복 검증). 즉시 1회 + 내부 업데이트 스로틀이 새 Series를 소화한
    /// 뒤(350ms) 1회 더 킥한다 — 한 번만 킥하면 직전 시리즈로 그려지는 1단계 지연이 발생.
    /// </summary>
    private void NudgeChart() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            BounceChartMargin();
            var followUp = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            followUp.Tick += (_, _) =>
            {
                followUp.Stop();
                BounceChartMargin();
            };
            followUp.Start();
        });

    // 고정 Height 차트에서는 margin 바운스가 렌더 크기를 바꾸지 못해 재측정이 안 됨 —
    // Height 자체를 1px 바운스해 크기 변경(=재측정)을 강제한다.
    private void BounceChartMargin()
    {
        var h = double.IsNaN(UsageChart.Height) ? UsageChart.ActualHeight : UsageChart.Height;
        if (h <= 0)
        {
            return;
        }
        UsageChart.Height = h + 1;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => UsageChart.Height = h);
    }

    /// <summary>닫기 대신 숨김 — 상태(기간 선택 등) 유지.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
