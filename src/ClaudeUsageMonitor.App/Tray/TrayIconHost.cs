using ClaudeUsageMonitor.App.ViewModels;
using H.NotifyIcon;

namespace ClaudeUsageMonitor.App.Tray;

/// <summary>H.NotifyIcon TaskbarIcon 생성과 컨텍스트 메뉴 배선 (메뉴는 TrayMenuFactory 공유).</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly TrayViewModel _viewModel;

    public TrayIconHost(TrayViewModel viewModel)
    {
        _viewModel = viewModel;

        _icon = new TaskbarIcon
        {
            ToolTipText = viewModel.TooltipText,
            ContextMenu = TrayMenuFactory.Create(viewModel),
        };
        _icon.TrayLeftMouseUp += (_, _) => _viewModel.OpenDashboardCommand.Execute(null);

        _viewModel.IconStateChanged += RefreshVisual;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrayViewModel.TooltipText))
            {
                _icon.Dispatcher.Invoke(() => _icon.ToolTipText = _viewModel.TooltipText);
            }
        };

        RefreshVisual();
    }

    /// <summary>임계값 경고 풍선 알림.</summary>
    public void ShowWarning(string title, string message) =>
        _icon.Dispatcher.Invoke(() => _icon.ShowNotification(title, message));

    /// <summary>Explorer 재시작(TaskbarCreated) 후 아이콘 재등록.</summary>
    public void Reinstall() => _icon.Dispatcher.Invoke(() =>
    {
        _icon.ForceCreate(enablesEfficiencyMode: false);
        RefreshVisual();
    });

    private void RefreshVisual() => _icon.Dispatcher.Invoke(() =>
    {
        var rendered = TrayIconRenderer.Render(_viewModel.FiveHourPct, _viewModel.IsWarning, _viewModel.IsStale);
        _icon.Icon = rendered;
    });

    public void Dispose() => _icon.Dispose();
}
