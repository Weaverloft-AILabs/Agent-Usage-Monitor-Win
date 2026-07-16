using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeUsageMonitor.App.Interop;
using ClaudeUsageMonitor.App.ViewModels;
using ClaudeUsageMonitor.Core.Ui;

namespace ClaudeUsageMonitor.App.Dashboard;

public partial class DashboardWindow : Window
{
    private static readonly Color CostGreen = Color.FromRgb(0x7B, 0xD8, 0x8F);
    private static readonly Color SparkAxis = Color.FromRgb(0x9A, 0x9C, 0xB0);
    private static readonly Color SparkMarkerFill = Color.FromRgb(0x1E, 0x1F, 0x29);
    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Consolas, Courier New");

    private readonly DashboardViewModel _viewModel;
    private DateTime _lastHoverKick;
    private bool _initialSized;
    // 차트 재측정 킥(BounceChartMargin)의 복원 기준 높이 — XAML 고정값(248)을 1회 캡처.
    // live Height를 기준으로 쓰면 겹친 바운스에서 복원값이 누적 증가한다(아래 BounceChartMargin 참조).
    private readonly double _chartBaseHeight;

    // 비용 스파크라인 hover 상태 (점 좌표·값·라벨·hover 오버레이)
    private readonly List<Point> _sparkPts = new();
    private readonly List<UIElement> _sparkHover = new();
    private double[] _sparkVals = [];
    private string[] _sparkLbls = [];
    private double _sparkPlotTop, _sparkPlotBottom;

    public event Action? SettingsRequested;
    public event Action? InquiryRequested;

    public DashboardWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _chartBaseHeight = UsageChart.Height;   // XAML Height="248" — 바운스 복원의 고정 기준

        DailyToggle.Checked += (_, _) => _viewModel.PeriodIndex = 0;
        WeeklyToggle.Checked += (_, _) => _viewModel.PeriodIndex = 1;
        MonthlyToggle.Checked += (_, _) => _viewModel.PeriodIndex = 2;
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        InquiryButton.Click += (_, _) => InquiryRequested?.Invoke();

        // 커스텀 창 버튼 — 닫기는 Close()→OnClosing이 Cancel+Hide(상태 보존)
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        StateChanged += (_, _) => MaxIcon.Data = Geometry.Parse(
            WindowState == WindowState.Maximized
                ? "M2,4 L8,4 L8,10 L2,10 Z M4,4 L4,2 L10,2 L10,8 L8,8"  // 복원(겹친 사각형)
                : "M2,2 L10,2 L10,10 L2,10 Z");                          // 최대화(단일 사각형)

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
            // CostPoints/CostLabels 어느 쪽이 바뀌어도 다시 그려 라벨-점 일관 보장(대입 순서 의존 제거)
            else if (args.PropertyName == nameof(DashboardViewModel.CostPoints)
                     || args.PropertyName == nameof(DashboardViewModel.CostLabels))
            {
                DrawSparkline();
            }
        };

        // 모델 공유 바(100% 스택)는 비율(star) 컬럼으로 코드비하인드에서 구성
        _viewModel.ModelBreakdown.CollectionChanged += (_, _) => BuildShareBar();

        SparkCanvas.SizeChanged += (_, _) => DrawSparkline();
        SparkCanvas.MouseMove += OnSparkMove;
        SparkCanvas.MouseLeave += (_, _) => ClearSparkHover();

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

    /// <summary>비용 추이 스파크라인 — CostPoints를 SparkCanvas에 영역+선+끝점+날짜축으로 직접 그린다(단일 $축).
    /// 점 좌표를 보관해 hover 시 해당 날짜의 금액 툴팁을 표시한다.</summary>
    private void DrawSparkline()
    {
        SparkCanvas.Children.Clear();
        _sparkHover.Clear();
        _sparkPts.Clear();
        var pts = _viewModel.CostPoints ?? [];
        _sparkVals = pts;
        _sparkLbls = _viewModel.CostLabels ?? [];
        double w = SparkCanvas.ActualWidth, h = SparkCanvas.ActualHeight;
        if (pts.Length == 0 || w <= 2 || h <= 2)
        {
            return;
        }

        const double padL = 34, padR = 12, padT = 10, padB = 24; // padB로 날짜 라벨 공간 확보
        double plotW = w - padL - padR, plotH = h - padT - padB;
        if (plotW <= 4 || plotH <= 4)
        {
            return;
        }
        _sparkPlotTop = padT;
        _sparkPlotBottom = padT + plotH;

        double max = 0;
        foreach (var v in pts)
        {
            if (v > max) max = v;
        }
        if (max <= 0) max = 1;

        int n = pts.Length;
        double dx = n > 1 ? plotW / (n - 1) : 0;
        for (var i = 0; i < n; i++)
        {
            _sparkPts.Add(new Point(padL + (n > 1 ? i * dx : plotW / 2), padT + plotH - pts[i] / max * plotH));
        }

        var stroke = new SolidColorBrush(CostGreen);
        stroke.Freeze();
        var cardBg = (TryFindResource("ChartCardBrush") as SolidColorBrush)?.Color ?? SparkMarkerFill;

        // 영역(그라데이션)
        var fig = new PathFigure { StartPoint = new Point(padL, padT + plotH) };
        foreach (var p in _sparkPts)
        {
            fig.Segments.Add(new LineSegment(p, false));
        }
        fig.Segments.Add(new LineSegment(new Point(_sparkPts[^1].X, padT + plotH), false));
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
        foreach (var p in _sparkPts)
        {
            line.Points.Add(p);
        }
        SparkCanvas.Children.Add(line);

        // 끝점 마커
        var last = _sparkPts[^1];
        var dot = new Ellipse { Width = 7, Height = 7, Stroke = stroke, StrokeThickness = 2, Fill = new SolidColorBrush(cardBg) };
        Canvas.SetLeft(dot, last.X - 3.5);
        Canvas.SetTop(dot, last.Y - 3.5);
        SparkCanvas.Children.Add(dot);

        // $축 라벨 — 소액 구간은 소수 자리를 살려 상단이 "$0"으로 반올림돼 기준선과 구분 불가한 문제 방지(hover와 동일 정밀도)
        var maxFmt = max >= 10 ? "0" : max >= 1 ? "0.0" : "0.00";
        AddSparkText("$0", 4, padT + plotH - 8);
        AddSparkText("$" + max.ToString(maxFmt, System.Globalization.CultureInfo.InvariantCulture), 4, padT - 4);

        // X 날짜 라벨 — 각 데이터 점에 정렬해 표시(토큰 차트처럼 전체), 겹치면 그 라벨만 건너뜀
        if (_sparkLbls.Length == n)
        {
            var lastRight = double.NegativeInfinity;
            for (var i = 0; i < n; i++)
            {
                var t = MakeSparkText(_sparkLbls[i]);
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var lw = t.DesiredSize.Width;
                var lx = Math.Max(0, Math.Min(w - lw, _sparkPts[i].X - lw / 2));
                if (lx < lastRight + 5)
                {
                    continue; // 이전 라벨과 겹침 — 건너뜀(점 정렬은 유지)
                }
                lastRight = lx + lw;
                Canvas.SetLeft(t, lx);
                Canvas.SetTop(t, padT + plotH + 6);
                SparkCanvas.Children.Add(t);
            }
        }
    }

    private TextBlock MakeSparkText(string text) => new()
    {
        Text = text, FontSize = 10, FontFamily = MonoFamily, Foreground = new SolidColorBrush(SparkAxis),
    };

    private void AddSparkText(string text, double x, double y)
    {
        var t = MakeSparkText(text);
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        SparkCanvas.Children.Add(t);
    }

    private void OnSparkMove(object sender, MouseEventArgs e)
    {
        if (_sparkPts.Count == 0)
        {
            return;
        }
        var mx = e.GetPosition(SparkCanvas).X;
        int best = 0;
        double bestDist = double.MaxValue;
        for (var i = 0; i < _sparkPts.Count; i++)
        {
            var d = Math.Abs(_sparkPts[i].X - mx);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        ShowSparkHover(best);
    }

    private void ClearSparkHover()
    {
        foreach (var el in _sparkHover)
        {
            SparkCanvas.Children.Remove(el);
        }
        _sparkHover.Clear();
    }

    /// <summary>hover: 해당 날짜에 십자선+마커+툴팁(날짜·금액)을 표시.</summary>
    private void ShowSparkHover(int i)
    {
        ClearSparkHover();
        if (i < 0 || i >= _sparkPts.Count)
        {
            return;
        }
        var p = _sparkPts[i];
        double w = SparkCanvas.ActualWidth, h = SparkCanvas.ActualHeight;
        var stroke = new SolidColorBrush(CostGreen);
        var cardBg = (TryFindResource("ChartCardBrush") as SolidColorBrush)?.Color ?? SparkMarkerFill;

        var cross = new Line
        {
            X1 = p.X, X2 = p.X, Y1 = _sparkPlotTop, Y2 = _sparkPlotBottom,
            Stroke = new SolidColorBrush(SparkAxis) { Opacity = 0.55 }, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 },
        };
        SparkCanvas.Children.Add(cross);
        _sparkHover.Add(cross);

        var mk = new Ellipse { Width = 9, Height = 9, Stroke = stroke, StrokeThickness = 2, Fill = new SolidColorBrush(cardBg) };
        Canvas.SetLeft(mk, p.X - 4.5);
        Canvas.SetTop(mk, p.Y - 4.5);
        SparkCanvas.Children.Add(mk);
        _sparkHover.Add(mk);

        var date = i < _sparkLbls.Length ? _sparkLbls[i] : "";
        var val = i < _sparkVals.Length ? _sparkVals[i] : 0;
        var box = new Border
        {
            Background = TryFindResource("CardBackgroundBrush") as Brush ?? Brushes.Black,
            BorderBrush = TryFindResource("CardBorderBrush") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 5, 9, 6),
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = date, FontSize = 10.5, FontFamily = MonoFamily,
            Foreground = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White, Opacity = 0.7,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "$" + val.ToString("0.00", CultureInfo.InvariantCulture),
            FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = MonoFamily,
            Foreground = TryFindResource("SuccessTextBrush") as Brush ?? stroke,
        });
        box.Child = sp;
        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = box.DesiredSize.Width, bh = box.DesiredSize.Height;
        double bx = p.X > w / 2 ? p.X - bw - 10 : p.X + 10;
        bx = Math.Max(2, Math.Min(w - bw - 2, bx));
        double by = Math.Max(2, Math.Min(h - bh - 2, p.Y - bh - 6));
        Canvas.SetLeft(box, bx);
        Canvas.SetTop(box, by);
        SparkCanvas.Children.Add(box);
        _sparkHover.Add(box);
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
        // 복원 대상은 항상 고정 기준 높이(_chartBaseHeight)여야 한다 — live Height를 기준으로 삼으면
        // 새로고침당 다중 NudgeChart(클릭 핸들러 + Series PropertyChanged, 각 즉시+350ms) 및 hover 바운스가
        // 겹칠 때 복원값이 이미 +1된 값으로 캡처돼 매 호출마다 Height가 영구 증가한다(차트가 아래로 커져 x축이 클립됨).
        // 고정 기준으로 복원하면 몇 번 겹쳐 호출돼도 항상 기준 높이로 수렴(멱등)한다.
        if (double.IsNaN(_chartBaseHeight) || _chartBaseHeight <= 0)
        {
            return;
        }
        UsageChart.Height = _chartBaseHeight + 1;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => UsageChart.Height = _chartBaseHeight);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!_initialSized)
        {
            _initialSized = true;   // hide-on-close라 인스턴스 유지 → 최초 1회만(사용자 리사이즈 보존)
            ApplyInitialSize();
        }
        // 커스텀 크롬 최대화 시 작업표시줄을 덮지 않도록 MINMAXINFO 보정 훅
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WndProc);
        WindowEffects.EnableRoundedCorners(this); // Win11 모서리 라운딩
    }

    /// <summary>WM_GETMINMAXINFO 보정 — 최대화 크기를 현재 모니터 작업영역으로 제한(작업표시줄 가림 방지).</summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_GETMINMAXINFO)
        {
            var hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (hMon != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMon, ref mi))
            {
                var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
                mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
                mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
                Marshal.StructureToPtr(mmi, lParam, true);
                // handled=true 필수 — 안 하면 DefWindowProc가 기본값(모니터 전체)으로 덮어써 작업표시줄을 가림
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>현재(커서) 모니터 작업영역의 절반 크기로 열되, 최소 하한 미만이면 하한. 해당 모니터 중앙 배치.</summary>
    private void ApplyInitialSize()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
        {
            return;
        }
        var hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref mi))
        {
            return;
        }
        double workW = mi.rcWork.Right - mi.rcWork.Left;
        double workH = mi.rcWork.Bottom - mi.rcWork.Top;
        double dpiX = 1.0, dpiY = 1.0;
        if (NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out var dx, out var dy) == 0 && dx > 0)
        {
            dpiX = dx / 96.0;
            dpiY = dy / 96.0;
        }
        var (w, h) = DashboardSizing.ComputeInitialSize(workW, workH, dpiX, dpiY, MinWidth, MinHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = w;
        Height = h;
        // 대상 모니터 작업영역 중앙 (물리 rect를 해당 모니터 DPI로 논리 변환)
        Left = mi.rcWork.Left / dpiX + (workW / dpiX - w) / 2.0;
        Top = mi.rcWork.Top / dpiY + (workH / dpiY - h) / 2.0;
    }

    /// <summary>닫기 대신 숨김 — 상태(기간 선택 등) 유지.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
