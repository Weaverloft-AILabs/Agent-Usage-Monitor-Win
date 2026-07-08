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
    /// LiveCharts2 2.0.4의 렌더 누락 대응 — 창 표시 직후 첫 measure가 지오메트리를 만들지 못해
    /// 캔버스가 빈 채 남는 문제(예외 없음). CoreChart.Update 강제나 시작 시 LiveCharts.Configure만으로는
    /// 복구되지 않고, 오직 "요소 크기 변경"에 의한 재측정만이 렌더를 살린다(실기기 3회 반복 검증).
    /// 표시 시점마다 차트 마진을 1px 바꿨다 복원해 크기 변경 렌더 경로를 강제로 태운다.
    /// </summary>
    private void NudgeChart() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var margin = UsageChart.Margin;
            UsageChart.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + 1);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => UsageChart.Margin = margin);
        });

    /// <summary>닫기 대신 숨김 — 상태(기간 선택 등) 유지.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
