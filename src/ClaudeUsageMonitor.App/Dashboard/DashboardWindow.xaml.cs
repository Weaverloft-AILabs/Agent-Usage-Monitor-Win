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
            }
            else
            {
                _liveTimer.Stop();
            }
        };
    }

    /// <summary>닫기 대신 숨김 — 상태(기간 선택 등) 유지.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
