using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.App.Services;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace ClaudeUsageMonitor.App.ViewModels;

public partial class TrayViewModel : ObservableObject,
    IRecipient<RateLimitUpdatedMessage>, IRecipient<UpdateAvailableMessage>
{
    private readonly RateLimitPollingService _poller;
    private readonly MonitorSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly UpdateService _updater;

    [ObservableProperty]
    private string _tooltipText = "Agent Usage Monitor — 데이터 없음";

    [ObservableProperty]
    private double _fiveHourPct;

    [ObservableProperty]
    private bool _isWarning;

    [ObservableProperty]
    private bool _isStale = true;

    public event Action? IconStateChanged;
    public event Action? DashboardRequested;
    public event Action? ExitRequested;

    public TrayViewModel(
        RateLimitPollingService poller,
        MonitorSettings settings,
        SettingsStore settingsStore,
        UpdateService updater)
    {
        _poller = poller;
        _settings = settings;
        _settingsStore = settingsStore;
        _updater = updater;
        WeakReferenceMessenger.Default.Register<RateLimitUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Register<UpdateAvailableMessage>(this);
    }

    /// <summary>발견된 업데이트 버전 (없으면 null — 메뉴 항목 숨김).</summary>
    public string? UpdateVersion => _updater.AvailableVersionText;

    public void Receive(UpdateAvailableMessage message) => OnPropertyChanged(nameof(UpdateVersion));

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        try
        {
            await _updater.DownloadAndApplyAsync(); // 완료 시 앱이 재시작됨
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            // 네트워크/디스크 오류 — 다음 시도까지 무시 (메뉴에서 재시도 가능)
        }
    }

    public MonitorSettings Settings => _settings;

    public void Receive(RateLimitUpdatedMessage message)
    {
        var snapshot = message.State.Snapshot;
        if (snapshot is null)
        {
            TooltipText = message.State.Status switch
            {
                RateLimitStatus.NoCredentials => "Claude Code 로그인 정보를 찾을 수 없습니다",
                RateLimitStatus.AuthRequired => "재로그인 필요 — claude 명령을 실행해 주세요",
                _ => "Agent Usage Monitor — 데이터 없음",
            };
            IsStale = true;
        }
        else
        {
            FiveHourPct = snapshot.FiveHourPct;
            IsWarning = snapshot.FiveHourPct >= _settings.WarnThresholdPct;
            IsStale = snapshot.IsStale;
            var staleMark = snapshot.IsStale ? " (stale)" : "";
            TooltipText = $"5시간 {snapshot.FiveHourPct:0}% · 주간 {snapshot.SevenDayPct:0}%{staleMark}";
        }
        IconStateChanged?.Invoke();
    }

    [RelayCommand]
    private void Refresh() => _poller.TriggerNow();

    [RelayCommand]
    private void OpenDashboard() => DashboardRequested?.Invoke();

    [RelayCommand]
    private void SetThreshold(object? parameter)
    {
        if (parameter is string raw && double.TryParse(raw, out var pct))
        {
            _settings.WarnThresholdPct = pct;
            _settingsStore.Save(_settings);
            OnPropertyChanged(nameof(WarnThresholdPct));
            IconStateChanged?.Invoke();
        }
    }

    public double WarnThresholdPct => _settings.WarnThresholdPct;

    [RelayCommand]
    private void SwitchMode(object? parameter)
    {
        if (parameter is string raw && Enum.TryParse<WidgetMode>(raw, out var mode))
        {
            _settings.Mode = mode;
            _settingsStore.Save(_settings);
            OnPropertyChanged(nameof(CurrentMode));
            WeakReferenceMessenger.Default.Send(new WidgetModeChangedMessage(mode));
        }
    }

    public WidgetMode CurrentMode => _settings.Mode;

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();
}
