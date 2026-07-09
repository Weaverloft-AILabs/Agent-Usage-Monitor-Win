using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ClaudeUsageMonitor.App.ViewModels;

namespace ClaudeUsageMonitor.App.Dashboard;

public partial class DashboardWindow : Window
{
    private readonly DashboardViewModel _viewModel;
    private readonly DispatcherTimer _liveTimer;

    public event Action? SettingsRequested;

    public DashboardWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        DailyToggle.Checked += (_, _) => _viewModel.PeriodIndex = 0;
        WeeklyToggle.Checked += (_, _) => _viewModel.PeriodIndex = 1;
        MonthlyToggle.Checked += (_, _) => _viewModel.PeriodIndex = 2;
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();

        // Series 교체(기간 토글/롤업 갱신) 후에도 렌더가 고착될 수 있어 매번 킥 — 없으면 토글이 무반응
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DashboardViewModel.Series) && IsVisible)
            {
                NudgeChart();
            }
        };

        _liveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _liveTimer.Tick += (_, _) => _viewModel.RefreshLiveSessions();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _viewModel.RefreshLiveSessions();
                _liveTimer.Start();
                NudgeChart();
            }
            else
            {
                _liveTimer.Stop();
            }
        };
        ContentRendered += (_, _) => NudgeChart();
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

    private void BounceChartMargin()
    {
        var margin = UsageChart.Margin;
        UsageChart.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + 1);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => UsageChart.Margin = margin);
    }

    /// <summary>닫기 대신 숨김 — 상태(기간 선택 등) 유지.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
