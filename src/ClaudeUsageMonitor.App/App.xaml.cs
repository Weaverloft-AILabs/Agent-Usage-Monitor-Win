using System.IO;
using System.Windows;
using ClaudeUsageMonitor.App.Services;
using ClaudeUsageMonitor.App.Tray;
using ClaudeUsageMonitor.App.ViewModels;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = MonitorPaths.Default();
        Directory.CreateDirectory(paths.DataDirectory);

        _host = BuildHost(paths);
        _host.Start();

        // 가격표 갱신은 부트 지연을 막기 위해 백그라운드로
        _ = _host.Services.GetRequiredService<PricingService>().RefreshAsync();

        var trayViewModel = _host.Services.GetRequiredService<TrayViewModel>();
        trayViewModel.ExitRequested += Shutdown;
        _tray = new TrayIconHost(trayViewModel);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
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

        return builder.Build();
    }
}
