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
    private SingleInstance? _singleInstance;
    private FullscreenDetector? _fullscreenDetector;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        _host = BuildHost(paths);
        _host.Start();

        // 인제스트 이벤트 → 메신저 브리지
        var ingest = _host.Services.GetRequiredService<IngestService>();
        ingest.RollupUpdated += rollup =>
            WeakReferenceMessenger.Default.Send(new RollupUpdatedMessage(rollup));

        // 가격표 갱신은 부트 지연을 막기 위해 백그라운드로
        _ = _host.Services.GetRequiredService<PricingService>().RefreshAsync();

        var trayViewModel = _host.Services.GetRequiredService<TrayViewModel>();
        trayViewModel.ExitRequested += Shutdown;
        trayViewModel.DashboardRequested += ShowDashboard;
        _tray = new TrayIconHost(trayViewModel);

        var settings = _host.Services.GetRequiredService<Core.Models.MonitorSettings>();
        _widget = new WidgetWindow(_host.Services.GetRequiredService<WidgetViewModel>())
        {
            ContextMenu = TrayMenuFactory.Create(trayViewModel), // 트레이와 동일 메뉴
        };
        _widgetController = new WidgetController(
            _widget, settings, _host.Services.GetRequiredService<SettingsStore>());
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
        _settingsWindow.Owner = _dashboard;
        _settingsWindow.Show();
        _settingsWindow.Activate();
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
        builder.Services.AddSingleton(sp => new IngestService(paths.ProjectsRoot, paths.DataDirectory));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestService>());
        builder.Services.AddSingleton<RateLimitPollingService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RateLimitPollingService>());
        builder.Services.AddSingleton<TrayViewModel>();
        builder.Services.AddSingleton<WidgetViewModel>();
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();

        return builder.Build();
    }
}
