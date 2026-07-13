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

    /// <summary>메이저 점프 업데이트 버전 (빈 문자열이면 수동 설치 안내 숨김).</summary>
    [ObservableProperty]
    private string _majorUpdateVersion = "";

    public string CurrentVersionText { get; }

    /// <summary>수동 다운로드 링크 대상 — URL 단일 소스(UpdateService 상수)를 XAML NavigateUri에 바인딩.</summary>
    public Uri ReleasesPageUri { get; } = new(Services.UpdateService.ReleasesPageUrl);

    public SettingsViewModel(
        MonitorSettings settings, SettingsStore store, MonitorPaths paths, Services.UpdateService updater)
    {
        _settings = settings;
        _store = store;
        _paths = paths;
        _updater = updater;
        CurrentVersionText = "v" + updater.CurrentVersionText;
        WeakReferenceMessenger.Default.Register(this);
        // Register 후 스냅샷 — 등록 전에 발행된 메시지의 유실 창을 닫는다.
        // (동시 CheckAsync가 사이에 끼어도 그 결과는 곧 도착하는 메시지가 재보정)
        if (updater.AvailableSnapshot is { } available)
        {
            if (available.MajorJump)
            {
                _majorUpdateVersion = available.Version;
            }
            else
            {
                _updateVersionText = available.Version;
            }
        }

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
        _settings.WarnThresholdPct = Math.Clamp(WarnThresholdPct, 10, 100);
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
        // message.Version/MajorJump는 같은 UpdateInfo에서 계산된 원자 쌍 — 라이브 재독취 금지 (경합 시 불일치)
        if (message.MajorJump)
        {
            // 메이저 업그레이드: 인앱 설치 버튼을 숨기고 수동 설치 링크로 안내
            UpdateVersionText = "";
            MajorUpdateVersion = message.Version;
            UpdateStatusText = $"새 주요 버전 v{message.Version} — 인앱 업데이트 미지원";
        }
        else
        {
            MajorUpdateVersion = "";
            UpdateVersionText = message.Version;
            UpdateStatusText = $"새 버전 v{message.Version} 사용 가능";
        }
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
        if (!found)
        {
            UpdateVersionText = "";
            MajorUpdateVersion = "";
            UpdateStatusText = "최신 버전입니다";
        }
        // found면 CheckAsync가 보낸 UpdateAvailableMessage(버전+메이저 원자 쌍)를 Receive가 이미 처리함
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (!_updater.IsInstalled)
        {
            UpdateStatusText = "포터블/개발 실행 — 업데이트는 설치판에서 지원됩니다";
            return;
        }

        // 진행률·오류는 공용 업데이트 창(UpdateProgressWindow — 인스톨러와 동일 UX)이 표시.
        // 인라인 "다운로드 중 X%" 텍스트 경로는 창으로 일원화되어 제거됨.
        WeakReferenceMessenger.Default.Send(new OpenUpdateWindowMessage());
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
