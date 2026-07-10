using System.Windows.Controls;
using ClaudeUsageMonitor.App.ViewModels;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.App.Tray;

/// <summary>트레이 아이콘과 위젯 우클릭이 공유하는 컨텍스트 메뉴 생성.</summary>
public static class TrayMenuFactory
{
    public static ContextMenu Create(TrayViewModel viewModel)
    {
        var menu = new ContextMenu();
        var mode = new MenuItem { Header = "표시 모드" };

        // 새 버전 발견 시에만 표시 (SyncChecks에서 갱신)
        var update = new MenuItem
        {
            Visibility = System.Windows.Visibility.Collapsed,
            FontWeight = System.Windows.FontWeights.SemiBold,
        };
        update.Click += (_, _) => viewModel.InstallUpdateCommand.Execute(null);
        menu.Items.Add(update);

        menu.Items.Add(NewItem("새로고침", () => viewModel.RefreshCommand.Execute(null)));
        menu.Items.Add(NewItem("대시보드 열기", () => viewModel.OpenDashboardCommand.Execute(null)));
        menu.Items.Add(new Separator());

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
        menu.Items.Add(NewItem("설정", () => viewModel.OpenSettingsCommand.Execute(null)));

        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("종료", () => viewModel.ExitCommand.Execute(null)));

        menu.Opened += (_, _) => SyncChecks();

        void SyncChecks()
        {
            if (viewModel.UpdateVersion is { } version)
            {
                // 메이저 점프는 인앱 설치 불가 — 클릭 시 릴리스 페이지가 열린다 (InstallUpdateCommand 내 분기)
                update.Header = viewModel.UpdateIsMajorJump
                    ? $"⬆ v{version} — GitHub에서 수동 다운로드"
                    : $"⬆ v{version} 업데이트 설치";
                update.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                update.Visibility = System.Windows.Visibility.Collapsed;
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
