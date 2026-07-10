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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Velopack 설치/제거/업데이트 훅 — 설치 이벤트 처리 중에는 여기서 프로세스가 종료됨.
        // 반드시 다른 초기화보다 먼저 실행해야 한다.
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

        if (e.Args.Contains("--dashboard"))
        {
            ShowDashboard();
        }
    }

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
