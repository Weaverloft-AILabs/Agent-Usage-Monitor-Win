using System.Windows;
using System.Windows.Controls;
using ClaudeUsageMonitor.App.ViewModels;
using ClaudeUsageMonitor.Core.Models;
using H.NotifyIcon;

namespace ClaudeUsageMonitor.App.Tray;

/// <summary>H.NotifyIcon TaskbarIcon 생성과 컨텍스트 메뉴 배선.</summary>
public sealed class TrayIconHost : IDisposable
{
    private static readonly double[] ThresholdPresets = [50, 70, 80, 90, 95];

    private readonly TaskbarIcon _icon;
    private readonly TrayViewModel _viewModel;

    public TrayIconHost(TrayViewModel viewModel)
    {
        _viewModel = viewModel;

        _icon = new TaskbarIcon
        {
            ToolTipText = viewModel.TooltipText,
            ContextMenu = BuildMenu(),
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

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var threshold = new MenuItem { Header = "5시간 경고 임계값" };
        var mode = new MenuItem { Header = "표시 모드" };

        menu.Items.Add(NewItem("새로고침", () => _viewModel.RefreshCommand.Execute(null)));
        menu.Items.Add(NewItem("대시보드 열기", () => _viewModel.OpenDashboardCommand.Execute(null)));
        menu.Items.Add(new Separator());

        foreach (var preset in ThresholdPresets)
        {
            var item = new MenuItem { Header = $"{preset:0}%", IsCheckable = true };
            item.Click += (_, _) =>
            {
                _viewModel.SetThresholdCommand.Execute(preset.ToString());
                SyncChecks();
            };
            threshold.Items.Add(item);
        }
        menu.Items.Add(threshold);

        foreach (var (label, value) in new[] { ("작업표시줄 도킹", WidgetMode.Taskbar), ("플로팅", WidgetMode.Floating), ("숨김", WidgetMode.Hidden) })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, Tag = value };
            item.Click += (_, _) =>
            {
                _viewModel.SwitchModeCommand.Execute(value.ToString());
                SyncChecks();
            };
            mode.Items.Add(item);
        }
        menu.Items.Add(mode);

        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("종료", () => _viewModel.ExitCommand.Execute(null)));

        menu.Opened += (_, _) => SyncChecks();

        void SyncChecks()
        {
            foreach (var item in threshold.Items.OfType<MenuItem>())
            {
                item.IsChecked = item.Header is string h &&
                                 h.TrimEnd('%') == _viewModel.WarnThresholdPct.ToString("0");
            }
            foreach (var item in mode.Items.OfType<MenuItem>())
            {
                item.IsChecked = item.Tag is WidgetMode m && m == _viewModel.CurrentMode;
            }
        }

        return menu;

        static MenuItem NewItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            return item;
        }
    }

    public void Dispose() => _icon.Dispose();
}
