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

    private readonly string _bakedVersion;
    private InstallPlan _plan;

    /// <summary>설치본·최신 릴리스 감지 완료 전(주 버튼 비활성).</summary>
    [ObservableProperty]
    private bool _isDetecting = true;

    public InstallerViewModel(string? setupArgPath)
    {
        _setupArgPath = setupArgPath;
        _bakedVersion = ResolveVersionText().TrimStart('v');
        // 잠정값 — DetectAsync가 설치본 버전과 "실제 설치할 대상(최신 릴리스)"을 읽어 확정한다.
        _plan = new InstallPlan(InstallMode.NotInstalled, null, _bakedVersion);
    }

    /// <summary>준비 상태에서 1회 호출 — 현재 설치본과 최신 릴리스를 비교해 설치/업데이트 모드를 확정.
    /// 비교 대상은 인스톨러 자체 버전이 아니라 <b>실제 다운로드 대상(/releases/latest)</b>이라야
    /// 라벨과 실제 설치 페이로드가 일치한다(구 인스톨러 재사용 시에도 정확).</summary>
    public async Task DetectAsync()
    {
        try
        {
            var installed = InstallProbe.ReadInstalledVersion(SetupRunner.DefaultInstalledExePath);
            string? latest = null;
            try
            {
                using var client = new GitHubReleaseClient();
                latest = await client.GetLatestVersionAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // 오프라인/일시 오류 — 인스톨러 자체 버전으로 폴백 비교(최선의 근사)
            }

            _plan = InstallProbe.Decide(installed, latest ?? _bakedVersion);
        }
        finally
        {
            IsDetecting = false;
            RaiseReadyProps();
        }
    }

    private void RaiseReadyProps()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(ReadyPrimaryText));
        OnPropertyChanged(nameof(ReadySecondaryText));
        OnPropertyChanged(nameof(DoneHeadlineText));
    }

    public string VersionText { get; } = ResolveVersionText();

    /// <summary>감지된 설치 상태(신규/업데이트/최신/다운그레이드) — E2E·진단용.</summary>
    public InstallMode Mode => _plan.Mode;

    /// <summary>감지 완료 전에는 주 버튼 비활성.</summary>
    public bool CanAct => !IsDetecting;

    /// <summary>업데이트 모드면 진행/완료 문구의 동사를 "업데이트"로 바꾼다.</summary>
    private string Verb => _plan.Mode == InstallMode.Update ? "업데이트" : "설치";

    /// <summary>준비 상태 주 버튼 라벨.</summary>
    public string PrimaryActionText => IsDetecting
        ? "확인 중..."
        : _plan.Mode switch
        {
            InstallMode.Update => "업데이트",
            InstallMode.UpToDate => "실행",
            InstallMode.Downgrade => "실행",
            _ => "지금 설치",
        };

    /// <summary>준비 상태 1행 안내 — 감지 결과 반영.</summary>
    public string ReadyPrimaryText => IsDetecting
        ? "설치 상태를 확인하는 중입니다..."
        : _plan.Mode switch
        {
            InstallMode.Update =>
                $"설치된 v{CleanVersion(_plan.InstalledVersion)} → v{_plan.TargetVersion} 으로 업데이트합니다.",
            InstallMode.UpToDate => $"이미 최신 버전(v{_plan.TargetVersion})이 설치되어 있습니다.",
            InstallMode.Downgrade => $"더 최신 버전(v{CleanVersion(_plan.InstalledVersion)})이 이미 설치되어 있습니다.",
            _ => "Claude Code 사용량을 작업표시줄에서 실시간으로.",
        };

    /// <summary>준비 상태 2행(mono) 보조 안내.</summary>
    public string ReadySecondaryText => _plan.Mode switch
    {
        InstallMode.UpToDate => "[실행]을 눌러 시작하거나 창을 닫으세요.",
        InstallMode.Downgrade => "다운그레이드는 지원하지 않습니다. [실행]으로 현재 버전을 시작하세요.",
        _ => InstallLocationText,
    };

    /// <summary>완료 상태 헤드라인 — 설치/업데이트 구분.</summary>
    public string DoneHeadlineText => $"{Verb}가 끝났습니다";

    public string InstallLocationText =>
        @"%LOCALAPPDATA%\AgentUsageMonitor · 약 70 MB · 설치 후 자동 실행";

    /// <summary>버전 표시용 — "v" 접두 제거 + 빌드메타(+) 절단 (프리릴리스는 유지).</summary>
    private static string CleanVersion(string? v)
    {
        if (string.IsNullOrEmpty(v))
        {
            return "?";
        }

        var s = v.TrimStart('v', 'V');
        var cut = s.IndexOf('+');
        return cut >= 0 ? s[..cut] : s;
    }

    /// <summary>준비 상태 주 버튼: 설치/업데이트는 Setup 실행, 최신/다운그레이드는 실행만.</summary>
    [RelayCommand]
    private async Task PrimaryAction()
    {
        if (IsDetecting)
        {
            return;
        }

        if (_plan.Mode is InstallMode.UpToDate or InstallMode.Downgrade)
        {
            Launch();
            return;
        }

        await InstallAsync();
    }

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
        string? downloadedSetup = null;
        try
        {
            var setupPath = SetupLocator.Locate(_setupArgPath, AppContext.BaseDirectory, File.Exists);
            if (setupPath is null)
            {
                setupPath = await DownloadSetupAsync(cancellationToken);
                downloadedSetup = setupPath;
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
            // OperationCanceledException 여기 도달 = 사용자 취소가 아닌 HTTP 스톨/타임아웃
            ShowFailure(InstallDiagnostics.FromDownloadError(ex));
        }
        catch (Exception ex)
        {
            // 마지막 방어선 — 어떤 예외도 크래시 대신 오류 상태로 (예: 프록시 간섭의 JsonException 등)
            ShowFailure(new InstallFailure(
                InstallFailureClass.Unknown,
                ex.GetType().Name + ": " + ex.Message,
                InstallDiagnostics.LogAdvice));
        }
        finally
        {
            if (downloadedSetup is not null)
            {
                try
                {
                    File.Delete(downloadedSetup); // 실행 중이면 잠겨서 실패 — 무시 (임시 폴더 잔존만)
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

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
        // 고유 임시 이름 — 동시 실행/이전 잔존 파일과의 충돌 방지 (사용 후 finally에서 삭제)
        var destination = Path.Combine(
            Path.GetTempPath(), $"AgentUsageMonitor-Setup-{Guid.NewGuid():N}.exe");
        await client.DownloadAsync(url, destination, (pct, bytes) =>
        {
            if (pct is { } value)
            {
                GaugePct = value * 0.55; // 게이지 = 전 흐름 단일 밴드 (다운로드 0~55 구간)
                ProgressValueText = $"{value:0}%";
            }
            else
            {
                // Content-Length 없음 — 멎은 "0%" 대신 수신량 표기, 게이지는 자산 크기(~70MB) 근사
                GaugePct = Math.Min(50, bytes / (70.0 * 1024 * 1024) * 55);
                ProgressValueText = $"{bytes / 1048576.0:0.0} MB";
            }
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
            InstallStage.Install => ($"{Verb} 중입니다", "3/4 단계"),
            _ => ($"{Verb}가 끝났습니다", "4/4 단계"),
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
        // 실행 성공 시에만 창을 닫는다 — 설치본이 사라졌거나 실행 불가면 피드백 없이 닫히면 안 됨
        if (TryShellStart(SetupRunner.DefaultInstalledExePath))
        {
            CloseRequested?.Invoke();
        }
        else
        {
            ShowFailure(new InstallFailure(
                InstallFailureClass.Unknown,
                "설치된 앱을 실행할 수 없습니다.",
                "설치가 손상되었을 수 있습니다 — 다시 설치해 주세요."));
        }
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

    private static bool TryShellStart(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // 셸 실행 실패 — 설치 흐름 자체는 계속 (버튼 재시도 가능)
            return false;
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
