using System.Windows.Controls;
using ClaudeUsageMonitor.App.ViewModels;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.App.Tray;

/// <summary>트레이 아이콘과 위젯 우클릭이 공유하는 컨텍스트 메뉴 생성.</summary>
public static class TrayMenuFactory
{
    private static readonly double[] ThresholdPresets = [50, 70, 80, 90, 95];

    public static ContextMenu Create(TrayViewModel viewModel)
    {
        var menu = new ContextMenu();
        var threshold = new MenuItem { Header = "5시간 경고 임계값" };
        var mode = new MenuItem { Header = "표시 모드" };

        menu.Items.Add(NewItem("새로고침", () => viewModel.RefreshCommand.Execute(null)));
        menu.Items.Add(NewItem("대시보드 열기", () => viewModel.OpenDashboardCommand.Execute(null)));
        menu.Items.Add(new Separator());

        foreach (var preset in ThresholdPresets)
        {
            var item = new MenuItem { Header = $"{preset:0}%", IsCheckable = true };
            item.Click += (_, _) =>
            {
                viewModel.SetThresholdCommand.Execute(preset.ToString());
                SyncChecks();
            };
            threshold.Items.Add(item);
        }
        menu.Items.Add(threshold);

        foreach (var (label, value) in new[]
                 {
                     ("작업표시줄 도킹", WidgetMode.Taskbar),
                     ("플로팅", WidgetMode.Floating),
                     ("숨김", WidgetMode.Hidden),
                 })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, Tag = value };
            item.Click += (_, _) =>
            {
                viewModel.SwitchModeCommand.Execute(value.ToString());
                SyncChecks();
            };
            mode.Items.Add(item);
        }
        menu.Items.Add(mode);

        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("종료", () => viewModel.ExitCommand.Execute(null)));

        menu.Opened += (_, _) => SyncChecks();

        void SyncChecks()
        {
            foreach (var item in threshold.Items.OfType<MenuItem>())
            {
                item.IsChecked = item.Header is string h &&
                                 h.TrimEnd('%') == viewModel.WarnThresholdPct.ToString("0");
            }
            foreach (var item in mode.Items.OfType<MenuItem>())
            {
                item.IsChecked = item.Tag is WidgetMode m && m == viewModel.CurrentMode;
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
}
