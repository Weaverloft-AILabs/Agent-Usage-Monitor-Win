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

public partial class SettingsViewModel : ObservableObject
{
    private readonly MonitorSettings _settings;
    private readonly SettingsStore _store;
    private readonly MonitorPaths _paths;

    [ObservableProperty]
    private int _pollIntervalSeconds;

    [ObservableProperty]
    private double _warnThresholdPct;

    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private string _statusText = "";

    public SettingsViewModel(MonitorSettings settings, SettingsStore store, MonitorPaths paths)
    {
        _settings = settings;
        _store = store;
        _paths = paths;

        _pollIntervalSeconds = settings.PollIntervalSeconds;
        _warnThresholdPct = settings.WarnThresholdPct;
        _modeIndex = (int)settings.Mode;
        _autoStart = AutoStartManager.IsEnabled();
    }

    [RelayCommand]
    private void Save()
    {
        var previousMode = _settings.Mode;

        _settings.PollIntervalSeconds = PollIntervalSeconds; // setter가 180 하한 강제
        _settings.WarnThresholdPct = Math.Clamp(WarnThresholdPct, 1, 100);
        _settings.Mode = (WidgetMode)Math.Clamp(ModeIndex, 0, 2);
        _settings.AutoStart = AutoStart;
        _store.Save(_settings);

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

        if (_settings.Mode != previousMode)
        {
            WeakReferenceMessenger.Default.Send(new WidgetModeChangedMessage(_settings.Mode));
        }

        StatusText = "저장되었습니다";
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
