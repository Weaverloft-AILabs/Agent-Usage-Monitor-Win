using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ClaudeUsageMonitor.App.Interop;
using ClaudeUsageMonitor.App.ViewModels;

namespace ClaudeUsageMonitor.App.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        TitleCloseButton.Click += (_, _) => Close(); // OnClosing이 Cancel+Hide (상태 보존)
        SourceInitialized += (_, _) => WindowEffects.EnableRoundedCorners(this); // Win11 모서리 라운딩
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnSourceLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 브라우저 실행 실패해도 설정 창은 계속 동작
        }
        e.Handled = true;
    }
}
