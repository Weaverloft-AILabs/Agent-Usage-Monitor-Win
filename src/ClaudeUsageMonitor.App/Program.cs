using System;
using System.IO;
using System.Windows;
using ClaudeUsageMonitor.Core;

namespace ClaudeUsageMonitor.App;

/// <summary>
/// 명시적 진입점 — Velopack 훅을 WPF가 초기화되기 전에 가장 먼저 실행하기 위함.
///
/// 이전에는 <see cref="App.OnStartup"/> 안에서 VelopackApp.Run()을 호출했는데, 그 시점이면
/// 이미 WPF 런타임·App 리소스가 로드된 뒤라 install/update 훅에서 Setup의 --veloapp-install
/// 대기(최대 30초)에 잡히는 시간이 커진다(느린/AV PC에서 타임아웃 위험 — Velopack #297 패턴).
/// Run()은 install/update/uninstall 훅 인자를 만나면 그 자리에서 프로세스를 종료하므로, 여기서
/// 최우선 실행하면 훅 처리 비용이 순수 런타임 부트로 최소화된다.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        // 반드시 다른 어떤 초기화보다 먼저. 설치/제거/업데이트 이벤트면 여기서 프로세스가 종료됨.
        Velopack.VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                // Windows 앱에서 제거 시 로컬 데이터(설정/롤업/캐시)도 함께 삭제
                try
                {
                    Directory.Delete(MonitorPaths.Default().DataDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 삭제 실패해도 제거 자체는 계속
                }
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
