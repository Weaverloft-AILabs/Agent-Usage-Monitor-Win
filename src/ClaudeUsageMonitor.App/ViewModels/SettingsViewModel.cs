using System.Diagnostics;
using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.App.Startup;
using ClaudeUsageMonitor.Core;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace ClaudeUsageMonitor.App.ViewModels;

public partial class SettingsViewModel : ObservableObject, IRecipient<UpdateAvailableMessage>
{
    private readonly MonitorSettings _settings;
    private readonly SettingsStore _store;
    private readonly MonitorPaths _paths;
    private readonly Services.UpdateService _updater;

    [ObservableProperty]
    private int _pollIntervalSeconds;

    [ObservableProperty]
    private double _warnThresholdPct;

    [ObservableProperty]
    private bool _warnNotificationEnabled;

    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    private bool _taskbarEmbedEnabled;

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private bool _showExhaustionPrediction;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>발견된 새 버전 (빈 문자열이면 설치 버튼 숨김).</summary>
    [ObservableProperty]
    private string _updateVersionText = "";

    [ObservableProperty]
    private string _updateStatusText = "";

    public string CurrentVersionText { get; }

    public SettingsViewModel(
        MonitorSettings settings, SettingsStore store, MonitorPaths paths, Services.UpdateService updater)
    {
        _settings = settings;
        _store = store;
        _paths = paths;
        _updater = updater;
        CurrentVersionText = "v" + updater.CurrentVersionText;
        _updateVersionText = updater.AvailableVersionText ?? "";
        WeakReferenceMessenger.Default.Register(this);

        _pollIntervalSeconds = settings.PollIntervalSeconds;
        _warnThresholdPct = settings.WarnThresholdPct;
        _warnNotificationEnabled = settings.WarnNotificationEnabled;
        _modeIndex = (int)settings.Mode;
        _taskbarEmbedEnabled = settings.TaskbarEmbedEnabled;
        _themeIndex = (int)settings.Theme;
        _showExhaustionPrediction = settings.ShowExhaustionPrediction;
        _autoStart = AutoStartManager.IsEnabled();
    }

    [RelayCommand]
    private void Save()
    {
        var previousMode = _settings.Mode;
        var previousEmbed = _settings.TaskbarEmbedEnabled;

        _settings.PollIntervalSeconds = PollIntervalSeconds; // setter가 하한(20초) 강제
        _settings.WarnThresholdPct = Math.Clamp(WarnThresholdPct, 1, 100);
        _settings.WarnNotificationEnabled = WarnNotificationEnabled;
        _settings.Mode = (WidgetMode)Math.Clamp(ModeIndex, 0, 2);
        _settings.TaskbarEmbedEnabled = TaskbarEmbedEnabled;
        _settings.Theme = (ThemePreference)Math.Clamp(ThemeIndex, 0, 2);
        _settings.ShowExhaustionPrediction = ShowExhaustionPrediction;
        _settings.AutoStart = AutoStart;
        _store.Save(_settings);

        Theming.ThemeManager.Apply(_settings.Theme);

        PollIntervalSeconds = _settings.PollIntervalSeconds;
        WarnThresholdPct = _settings.WarnThresholdPct;

        try
        {
            AutoStartManager.SetEnabled(AutoStart);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            StatusText = "자동 시작 설정 실패 (레지스트리 접근 거부)";
            return;
        }

        if (_settings.Mode != previousMode || _settings.TaskbarEmbedEnabled != previousEmbed)
        {
            // 임베드 on/off 변경도 같은 경로로 재적용 (컨트롤러가 임베드↔오버레이 전환)
            WeakReferenceMessenger.Default.Send(new WidgetModeChangedMessage(_settings.Mode));
        }

        StatusText = "저장되었습니다";
    }

    public void Receive(UpdateAvailableMessage message)
    {
        UpdateVersionText = message.Version;
        UpdateStatusText = $"새 버전 v{message.Version} 사용 가능";
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (!_updater.IsInstalled)
        {
            UpdateStatusText = "포터블/개발 실행 — 업데이트는 설치판에서 지원됩니다";
            return;
        }

        UpdateStatusText = "확인 중...";
        var found = await _updater.CheckAsync();
        if (found)
        {
            UpdateVersionText = _updater.AvailableVersionText ?? "";
            UpdateStatusText = $"새 버전 v{UpdateVersionText} 사용 가능";
        }
        else
        {
            UpdateVersionText = "";
            UpdateStatusText = "최신 버전입니다";
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        try
        {
            UpdateStatusText = "다운로드 중...";
            await _updater.DownloadAndApplyAsync(p => UpdateStatusText = $"다운로드 중... {p}%");
            // 성공 시 앱이 재시작되므로 여기 도달하지 않음
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            UpdateStatusText = "업데이트 실패 — 네트워크 상태 확인 후 다시 시도해 주세요";
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = "폴더를 열 수 없습니다";
        }
    }
}
