using System.Windows;

namespace ClaudeUsageMonitor.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --setup <경로>: 로컬 Setup.exe 지정 (오프라인 번들/테스트용 — 없으면 GitHub 다운로드)
        string? setupArg = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--setup", StringComparison.OrdinalIgnoreCase))
            {
                setupArg = e.Args[i + 1];
            }
        }

        new MainWindow(new InstallerViewModel(setupArg)).Show();
    }
}
