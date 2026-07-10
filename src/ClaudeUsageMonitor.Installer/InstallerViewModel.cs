using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using ClaudeUsageMonitor.Installer.Install;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeUsageMonitor.Installer;

/// <summary>설치 창 표시 상태 (디자인 카드의 4상태와 1:1).</summary>
public enum InstallerState
{
    Ready,
    Progress,
    Done,
    Error,
}

public partial class InstallerViewModel : ObservableObject
{
    /// <summary>본문 컬럼 폭(px) — 게이지 실폭 계산 기준 (560 − 36×2).</summary>
    private const double BodyWidth = 488;

    private readonly string? _setupArgPath;
    private CancellationTokenSource? _cancelSource;
    private int _stageToken; // 단계 전환 세대 — 지연 힌트/게이지 크리프의 늦은 콜백 무효화 (UI 스레드 전용)

    [ObservableProperty]
    private InstallerState _state = InstallerState.Ready;

    [ObservableProperty]
    private double _gaugePct;

    [ObservableProperty]
    private string _headlineText = "";

    /// <summary>우측 수치 — 다운로드 단계만 실측 %, 그 외 "n/4 단계" (근사 진행에 % 금지).</summary>
    [ObservableProperty]
    private string _progressValueText = "";

    [ObservableProperty]
    private bool _showSlowHint;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private string _errorDetail = "";

    [ObservableProperty]
    private string _errorAdvice = "";

    // 단계 표시 상태: "Done" | "Active" | "Todo" (XAML DataTrigger 매칭)
    [ObservableProperty]
    private string _downloadStepState = "Todo";

    [ObservableProperty]
    private string _scanStepState = "Todo";

    [ObservableProperty]
    private string _installStepState = "Todo";

    [ObservableProperty]
    private string _completeStepState = "Todo";

    /// <summary>완료 후 [시작하기] 또는 취소 불가 상태 정리 시 창 닫기 요청.</summary>
    public event Action? CloseRequested;

    public InstallerViewModel(string? setupArgPath)
    {
        _setupArgPath = setupArgPath;
    }

    public string VersionText { get; } = ResolveVersionText();

    public string InstallLocationText =>
        @"%LOCALAPPDATA%\AgentUsageMonitor · 약 70 MB · 설치 후 자동 실행";

    /// <summary>게이지 필 실폭(px).</summary>
    public double GaugeWidth => Math.Clamp(GaugePct, 0, 100) / 100.0 * BodyWidth;

    partial void OnGaugePctChanged(double value) => OnPropertyChanged(nameof(GaugeWidth));

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (State == InstallerState.Progress)
        {
            return;
        }

        ResetProgressUi();
        State = InstallerState.Progress;
        _cancelSource = new CancellationTokenSource();
        var cancellationToken = _cancelSource.Token;
        try
        {
            var setupPath = SetupLocator.Locate(_setupArgPath, AppContext.BaseDirectory, File.Exists);
            if (setupPath is null)
            {
                setupPath = await DownloadSetupAsync(cancellationToken);
            }
            else
            {
                DownloadStepState = "Done"; // 로컬 Setup 동반 — 다운로드 단계 건너뜀
            }

            var runner = new SetupRunner();
            var progress = new Progress<StageProgress>(p => ApplyStage(p.Stage));
            var result = await runner.RunAsync(setupPath, progress, cancellationToken);
            if (result.Success)
            {
                _stageToken++; // 크리프 중지
                GaugePct = 100;
                State = InstallerState.Done;
            }
            else
            {
                ShowFailure(result.Failure!);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 사용자 취소 — 준비 상태로 복귀
            State = InstallerState.Ready;
            ResetProgressUi();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Setup 프로세스 자체가 시작되지 못함 — AV 차단 가능성이 가장 높음
            ShowFailure(new InstallFailure(
                InstallFailureClass.AntivirusHold,
                "Setup could not start: " + ex.Message,
                InstallDiagnostics.AntivirusAdvice));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // OperationCanceledException 여기 도달 = 사용자 취소가 아닌 HTTP 타임아웃
            ShowFailure(InstallDiagnostics.FromDownloadError(ex));
        }
        finally
        {
            _cancelSource?.Dispose();
            _cancelSource = null;
            CanCancel = false;
        }
    }

    private async Task<string> DownloadSetupAsync(CancellationToken cancellationToken)
    {
        ApplyStage(InstallStage.Download);
        using var client = new GitHubReleaseClient();
        var url = await client.FindLatestSetupUrlAsync(cancellationToken)
            ?? throw new HttpRequestException("최신 릴리스에서 Setup 자산을 찾지 못했습니다");
        var destination = Path.Combine(Path.GetTempPath(), SetupLocator.SetupFileName);
        await client.DownloadAsync(url, destination, pct =>
        {
            GaugePct = pct * 0.55; // 다운로드 = 전체 게이지의 0~55% 구간
            ProgressValueText = $"{pct:0}%";
        }, cancellationToken);
        return destination;
    }

    /// <summary>단계 전환 — 헤드라인/수치/타임라인/취소 가능 여부를 한 번에 갱신.</summary>
    private void ApplyStage(InstallStage stage)
    {
        var token = ++_stageToken;
        (HeadlineText, ProgressValueText) = stage switch
        {
            InstallStage.Download => ("다운로드 중입니다", "0%"),
            InstallStage.SecurityScan => ("보안 검사 중입니다", "2/4 단계"),
            InstallStage.Install => ("설치 중입니다", "3/4 단계"),
            _ => ("설치가 끝났습니다", "4/4 단계"),
        };
        DownloadStepState = StepState(InstallStage.Download, stage);
        ScanStepState = StepState(InstallStage.SecurityScan, stage);
        InstallStepState = StepState(InstallStage.Install, stage);
        CompleteStepState = StepState(InstallStage.Complete, stage);
        CanCancel = stage <= InstallStage.SecurityScan; // 디스크 쓰기 전(다운로드·보안 검사)만 취소 허용
        ShowSlowHint = false;

        if (stage == InstallStage.SecurityScan)
        {
            _ = ShowSlowHintAfterDelayAsync(token);
            _ = CreepGaugeAsync(74, token);
        }
        else if (stage == InstallStage.Install)
        {
            _ = CreepGaugeAsync(94, token);
        }
    }

    private static string StepState(InstallStage step, InstallStage current) =>
        step < current ? "Done" : step == current ? "Active" : "Todo";

    /// <summary>보안 검사 15초 초과 시 ⚠ 힌트 노출 (카드 스펙).</summary>
    private async Task ShowSlowHintAfterDelayAsync(int token)
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (token == _stageToken && State == InstallerState.Progress)
        {
            ShowSlowHint = true;
        }
    }

    /// <summary>실측이 없는 단계의 부드러운 근사 진행 (수치 표기는 하지 않음).</summary>
    private async Task CreepGaugeAsync(double target, int token)
    {
        if (GaugePct < target - 20)
        {
            GaugePct = target - 20;
        }

        while (token == _stageToken && State == InstallerState.Progress && GaugePct < target)
        {
            GaugePct = Math.Min(target, GaugePct + 0.4);
            await Task.Delay(300);
        }
    }

    private void ShowFailure(InstallFailure failure)
    {
        _stageToken++;
        ErrorDetail = failure.Detail;
        ErrorAdvice = failure.Advice;
        State = InstallerState.Error;
    }

    private void ResetProgressUi()
    {
        _stageToken++;
        GaugePct = 0;
        ShowSlowHint = false;
        CanCancel = false;
        HeadlineText = "";
        ProgressValueText = "";
        DownloadStepState = "Todo";
        ScanStepState = "Todo";
        InstallStepState = "Todo";
        CompleteStepState = "Todo";
        ErrorDetail = "";
        ErrorAdvice = "";
    }

    [RelayCommand]
    private void Cancel() => _cancelSource?.Cancel();

    [RelayCommand]
    private void Launch()
    {
        TryShellStart(SetupRunner.DefaultInstalledExePath);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenLog()
    {
        var log = SetupRunner.DefaultVelopackLogPath;
        TryShellStart(File.Exists(log) ? log : Path.GetDirectoryName(log) ?? log);
    }

    [RelayCommand]
    private void OpenRepo() => TryShellStart(InstallerUrls.RepoUrl);

    [RelayCommand]
    private void OpenReleases() => TryShellStart(InstallerUrls.ReleasesPageUrl);

    private static void TryShellStart(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // 셸 실행 실패 — 설치 흐름 자체는 계속 (버튼 재시도 가능)
        }
    }

    private static string ResolveVersionText()
    {
        var info = typeof(InstallerViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return "v2.0.0";
        }

        var plus = info.IndexOf('+');
        return "v" + (plus > 0 ? info[..plus] : info);
    }
}
