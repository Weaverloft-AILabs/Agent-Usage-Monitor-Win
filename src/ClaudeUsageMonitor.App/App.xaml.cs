using System.IO;
using System.Windows;
using ClaudeUsageMonitor.App.Dashboard;
using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.App.Interop;
using ClaudeUsageMonitor.App.Notifications;
using ClaudeUsageMonitor.App.Services;
using ClaudeUsageMonitor.App.Startup;
using ClaudeUsageMonitor.App.Tray;
using ClaudeUsageMonitor.App.ViewModels;
using ClaudeUsageMonitor.App.Widget;
using CommunityToolkit.Mvvm.Messaging;
using LiveChartsCore.SkiaSharpView;
using ClaudeUsageMonitor.Core;
using ClaudeUsageMonitor.Core.Ingest;
using ClaudeUsageMonitor.Core.Pricing;
using ClaudeUsageMonitor.Core.RateLimit;
using ClaudeUsageMonitor.Core.Sessions;
using ClaudeUsageMonitor.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeUsageMonitor.App;

public partial class App : Application
{
    private IHost? _host;
    private TrayIconHost? _tray;
    private WidgetWindow? _widget;
    private WidgetController? _widgetController;
    private ThresholdNotifier? _notifier;
    private DashboardWindow? _dashboard;
    private Settings.SettingsWindow? _settingsWindow;
    private Inquiry.InquiryWindow? _inquiryWindow;
    private SingleInstance? _singleInstance;
    private FullscreenDetector? _fullscreenDetector;
    private UpdateUi.UpdateProgressWindow? _updateWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Velopack 훅(설치/제거/업데이트)은 Program.Main에서 WPF 초기화 전에 이미 실행됨 —
        // install/update 훅이면 그 시점에 프로세스가 종료되어 여기에 도달하지 않는다.

        // LiveCharts 전역 초기화 — 차트 최초 생성 시점의 lazy auto-init 경합(간헐적 무렌더)을 피하기 위해
        // 시작 시 명시 구성한다. AddDarkTheme "단독" 호출은 렌더러/매퍼 기본 설정을 파괴하므로
        // 반드시 AddSkiaSharp + AddDefaultMappers와 함께 체인해야 한다.
        LiveChartsCore.LiveCharts.Configure(config => config
            .AddSkiaSharp()
            .AddDefaultMappers()
            .AddDarkTheme());

        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryAcquire())
        {
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }
        _singleInstance.StartServer(() => Dispatcher.BeginInvoke(ShowDashboard));

        var paths = MonitorPaths.Default();
        Directory.CreateDirectory(paths.DataDirectory);
        Widget.Native.NativeWidgetLog.Initialize(paths.DataDirectory);

        _host = BuildHost(paths);
        _host.Start();

        // 인제스트 이벤트 → 메신저 브리지
        var ingest = _host.Services.GetRequiredService<IngestService>();
        ingest.RollupUpdated += rollup =>
            WeakReferenceMessenger.Default.Send(new RollupUpdatedMessage(rollup));

        // 가격표 갱신은 부트 지연을 막기 위해 백그라운드로
        _ = _host.Services.GetRequiredService<PricingService>().RefreshAsync();

        var settings = _host.Services.GetRequiredService<Core.Models.MonitorSettings>();
        Theming.ThemeManager.Initialize(settings.Theme);

        var trayViewModel = _host.Services.GetRequiredService<TrayViewModel>();
        trayViewModel.ExitRequested += Shutdown;
        trayViewModel.DashboardRequested += ShowDashboard;
        trayViewModel.SettingsRequested += ShowSettings;
        _tray = new TrayIconHost(trayViewModel);

        var widgetViewModel = _host.Services.GetRequiredService<WidgetViewModel>();
        _widget = new WidgetWindow(widgetViewModel)
        {
            ContextMenu = TrayMenuFactory.Create(trayViewModel), // 트레이와 동일 메뉴
        };
        _widget.DashboardRequested += ShowDashboard; // 더블클릭 → 대시보드
        var nativeWidgetHost = new Widget.Native.NativeWidgetHost(
            _widget, widgetViewModel, settings, _host.Services.GetRequiredService<SettingsStore>());
        nativeWidgetHost.DashboardRequested += ShowDashboard; // 임베드 위젯 더블클릭 → 대시보드
        _widgetController = new WidgetController(
            _widget, settings, _host.Services.GetRequiredService<SettingsStore>(), nativeWidgetHost);
        _widgetController.TaskbarRecreated += () => _tray?.Reinstall();
        _widgetController.ApplyMode(settings.Mode);

        _notifier = new ThresholdNotifier(settings, _tray);

        _fullscreenDetector = new FullscreenDetector();
        _fullscreenDetector.FullscreenChanged += suppressed =>
            _widgetController?.SetFullscreenSuppressed(suppressed);
        _fullscreenDetector.Start();

        // 인앱 업데이트 창 열기 요청 (트레이 메뉴/설정 공용) — 단일 창 소유
        WeakReferenceMessenger.Default.Register<App, OpenUpdateWindowMessage>(
            this, (recipient, _) => recipient.Dispatcher.BeginInvoke(recipient.ShowUpdateWindow));

        if (e.Args.Contains("--dashboard"))
        {
            ShowDashboard();
        }

        HandleUpdateContinuity(e.Args);
    }

    /// <summary>인앱 업데이트의 재시작 연속성 — 적용~재시작 사이 무창 구간의 결과를 이어받는다.
    /// ① --update-done &lt;ver&gt;: Update.exe가 적용 후 재시작하며 전달 — 버전 대조로 완료/실패 카드.
    /// ② 인자 유실(마커만 존재): 다음 실행에서 마커 조정 — 완료/실패/진행 중 유예/스테일 폐기.</summary>
    private void HandleUpdateContinuity(string[] args)
    {
        if (_host is null)
        {
            return;
        }

        var updater = _host.Services.GetRequiredService<UpdateService>();
        var current = updater.CurrentVersionText;

        string? doneVersion = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--update-done", StringComparison.OrdinalIgnoreCase))
            {
                doneVersion = args[i + 1];
            }
        }

        if (doneVersion is not null)
        {
            updater.PendingMarker.Delete();
            ShowUpdateResult(Core.Updates.UpdatePendingMarker.IsApplied(current, doneVersion), doneVersion, current);
            return;
        }

        var marker = updater.PendingMarker.TryRead();
        switch (Core.Updates.UpdatePendingMarker.Assess(marker, current, DateTimeOffset.UtcNow))
        {
            case Core.Updates.PendingUpdateAssessment.Completed:
                updater.PendingMarker.Delete();
                ShowUpdateResult(applied: true, marker!.TargetVersion, current);
                break;
            case Core.Updates.PendingUpdateAssessment.Failed:
                updater.PendingMarker.Delete();
                ShowUpdateResult(applied: false, marker!.TargetVersion, current);
                break;
            case Core.Updates.PendingUpdateAssessment.Stale:
                updater.PendingMarker.Delete();
                break;
            // InProgress: 유예 창 — 적용이 진행 중일 수 있으므로 침묵 (오경보 차단, 수정 필수 ②)
        }
    }

    /// <summary>인앱 업데이트 진행 창 — 이미 열려 있으면 Activate (단일 창 소유, 수정 필수 ③).
    /// TryBeginInstall이 null이면 창을 열지 않는다 (메이저 점프는 VM 분기가 릴리스 페이지로 안내).</summary>
    private void ShowUpdateWindow()
    {
        if (_host is null)
        {
            return;
        }

        if (_updateWindow is { IsLoaded: true })
        {
            _updateWindow.Activate();
            return;
        }

        var updater = _host.Services.GetRequiredService<UpdateService>();
        if (updater.TryBeginInstall() is not { } flow)
        {
            return; // 게이트 차단/업데이트 소멸 — 기존 안내(설정 문구·메뉴 분기) 유지
        }

        var viewModel = new UpdateUi.UpdateFlowViewModel(
            BuildAppUpdateTexts(flow.TargetVersion),
            versionText: "v" + updater.CurrentVersionText,
            logPath: VelopackLogPath,
            repoUrl: UpdateService.RepoUrl,
            releasesPageUrl: UpdateService.ReleasesPageUrl)
        {
            // 재시도 포함 매 시도마다 재캡처 — UpdateGate 캡처-스냅샷 판정이 항상 최신 스냅샷 기준
            FlowFactory = updater.TryBeginInstall,
        };
        // Update.exe가 이 프로세스의 graceful 종료를 기다린다 — OnExit(임베드/트레이/뮤텍스 해제)가 실행됨
        viewModel.PendingRestartRequested += () => Dispatcher.BeginInvoke(new Action(Shutdown));

        _updateWindow = new UpdateUi.UpdateProgressWindow(viewModel);
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
        _ = viewModel.StartFlowAsync(); // 앱 경로는 준비 상태 없이 즉시 진행 (사용자가 이미 설치를 눌렀음)
    }

    /// <summary>재시작 후 완료/실패 카드 — 진행 창과 같은 브랜드 창으로 결과만 표시.</summary>
    private void ShowUpdateResult(bool applied, string targetVersion, string currentVersion)
    {
        if (_host is null || _updateWindow is { IsLoaded: true })
        {
            return;
        }

        var updater = _host.Services.GetRequiredService<UpdateService>();
        var viewModel = new UpdateUi.UpdateFlowViewModel(
            BuildAppUpdateTexts(targetVersion),
            versionText: "v" + currentVersion,
            logPath: VelopackLogPath,
            repoUrl: UpdateService.RepoUrl,
            releasesPageUrl: UpdateService.ReleasesPageUrl)
        {
            FlowFactory = updater.TryBeginInstall, // 실패 카드의 [다시 시도] — 재확인 전이면 null(no-op)
        };
        viewModel.PendingRestartRequested += () => Dispatcher.BeginInvoke(new Action(Shutdown));
        if (applied)
        {
            viewModel.ShowDone();
        }
        else
        {
            viewModel.ShowFailure(new UpdateUi.InstallFailure(
                UpdateUi.InstallFailureClass.Unknown,
                $"expected v{targetVersion}, running v{currentVersion}",
                "업데이트가 적용되지 않았습니다 — 다시 시도하거나 로그를 확인해 주세요."));
        }

        _updateWindow = new UpdateUi.UpdateProgressWindow(viewModel);
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    private static string VelopackLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "velopack", "velopack.log");

    private static UpdateUi.UpdateFlowTexts BuildAppUpdateTexts(string targetVersion) => new()
    {
        WindowTitle = "Agent Usage Monitor 업데이트",
        StepLabels = ["다운로드", "검증", "설치", "완료"],
        ProgressHeadlines =
        [
            "다운로드 중입니다",
            "다운로드를 검증하는 중입니다",
            "설치 중입니다 — 앱을 다시 시작합니다",
            "업데이트가 끝났습니다",
        ],
        DoneHeadline = "업데이트가 끝났습니다",
        DoneSecondary = $"v{targetVersion} 이 적용되었습니다. 위젯이 작업표시줄에 다시 표시됩니다.",
        DoneButton = "확인",
        ErrorHeadline = "업데이트를 완료하지 못했습니다",
        SlowHintText = "네트워크 상태에 따라 다운로드가 오래 걸릴 수 있습니다 — 잠시만 기다려 주세요.",
        SlowHintStepIndex = 0,          // 앱 경로의 대기 구간은 다운로드뿐 (AV 관찰 구간 없음 — 허위 표시 금지)
        LastCancellableStepIndex = 1,   // 마커 기록·적용 예약(2단계) 전까지만 취소 허용
    };

    private void ShowDashboard()
    {
        if (_host is null)
        {
            return;
        }

        if (_dashboard is null)
        {
            var viewModel = _host.Services.GetRequiredService<DashboardViewModel>();
            viewModel.SetRollup(_host.Services.GetRequiredService<IngestService>().CurrentRollup);
            _dashboard = new DashboardWindow(viewModel);
            _dashboard.SettingsRequested += ShowSettings;
            _dashboard.InquiryRequested += ShowInquiry;
        }

        _dashboard.Show();
        // 이미 열려 있고 최소화된 상태면 Show()/Activate()만으론 복원되지 않는다 —
        // WindowState를 Normal로 되돌려 다시 보이게 한 뒤 활성화
        if (_dashboard.WindowState == WindowState.Minimized)
        {
            _dashboard.WindowState = WindowState.Normal;
        }
        _dashboard.Activate();
    }

    private void ShowSettings()
    {
        if (_host is null)
        {
            return;
        }

        _settingsWindow ??= new Settings.SettingsWindow(
            _host.Services.GetRequiredService<SettingsViewModel>());
        // 위젯 메뉴에서 대시보드 없이 열릴 수 있음 — 소유자 없으면 화면 중앙
        _settingsWindow.Owner = _dashboard is { IsVisible: true } ? _dashboard : null;
        if (_settingsWindow.Owner is null)
        {
            _settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowInquiry()
    {
        if (_host is null)
        {
            return;
        }

        _inquiryWindow ??= new Inquiry.InquiryWindow(
            _host.Services.GetRequiredService<InquiryViewModel>());
        _inquiryWindow.Owner = _dashboard is { IsVisible: true } ? _dashboard : null;
        if (_inquiryWindow.Owner is null)
        {
            _inquiryWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        _inquiryWindow.Show();
        _inquiryWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _fullscreenDetector?.Dispose();
        _notifier?.Dispose();
        _widgetController?.Dispose();
        _widget?.Close();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host.Dispose();
        }
        base.OnExit(e);
    }

    private static IHost BuildHost(MonitorPaths paths)
    {
        var builder = Host.CreateApplicationBuilder();

        var settingsStore = new SettingsStore(paths.DataDirectory);
        var settings = settingsStore.Load();
        var cliVersion = LiveSessionService.DetectCliVersion(paths.SessionsDirectory) ?? "2.1.204";

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(settingsStore);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(new CredentialsReader(paths.CredentialsPath));
        builder.Services.AddSingleton(sp => new RateLimitClient(
            sp.GetRequiredService<CredentialsReader>(), cliVersion: cliVersion));
        builder.Services.AddSingleton(new LiveSessionService(paths.SessionsDirectory));
        builder.Services.AddSingleton(new PricingService(paths.DataDirectory));
        builder.Services.AddSingleton(new BurnRateEstimator());
        builder.Services.AddSingleton(sp => new IngestService(paths.ProjectsRoot, paths.DataDirectory));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestService>());
        builder.Services.AddSingleton<RateLimitPollingService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RateLimitPollingService>());
        builder.Services.AddSingleton(sp => new ProfileClient(
            sp.GetRequiredService<CredentialsReader>(), cliVersion: cliVersion));
        builder.Services.AddSingleton<AccountWatchService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AccountWatchService>());
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        builder.Services.AddSingleton<TrayViewModel>();
        builder.Services.AddSingleton<WidgetViewModel>();
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<InquiryViewModel>();

        return builder.Build();
    }
}
