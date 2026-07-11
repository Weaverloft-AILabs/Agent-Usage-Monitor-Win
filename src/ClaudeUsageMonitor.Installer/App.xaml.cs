using System.Windows;

namespace ClaudeUsageMonitor.Installer;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 단일 인스턴스 — 두 설치가 같은 설치 루트/임시 파일을 두고 경합하는 것 방지
        _instanceMutex = new Mutex(
            initiallyOwned: true, @"Local\AgentUsageMonitorInstaller_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // 마지막 방어선: 미처리 예외 → 조용한 크래시 대신 안내 후 종료
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "설치 프로그램에 오류가 발생했습니다:\n" + args.Exception.Message,
                "Agent Usage Monitor 설치", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };

        // --setup <경로>: 로컬 Setup.exe 지정 (오프라인 번들/테스트용 — 없으면 GitHub 다운로드)
        string? setupArg = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--setup", StringComparison.OrdinalIgnoreCase))
            {
                setupArg = e.Args[i + 1];
            }
        }

        var viewModel = new InstallerViewModel(setupArg);
        new MainWindow(viewModel).Show();
        // 창을 먼저 띄우고 설치본·최신 릴리스 감지를 비동기로 — 감지 완료 시 준비 상태 UI가 확정된다.
        _ = viewModel.DetectAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
