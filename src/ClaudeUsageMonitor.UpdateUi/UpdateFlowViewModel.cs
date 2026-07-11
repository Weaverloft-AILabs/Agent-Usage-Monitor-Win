using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeUsageMonitor.UpdateUi;

/// <summary>진행 창 표시 상태 (디자인 카드의 4상태와 1:1). XAML DataTrigger는 멤버 이름 문자열로 매칭.</summary>
public enum UpdateFlowState
{
    Ready,
    Progress,
    Done,
    Error,
}

/// <summary>
/// 브랜드 진행 창(UpdateProgressWindow)의 공용 상태머신 — 인스톨러 InstallerViewModel에서 추출(옵션 A).
/// 4상태 전환·단계 타임라인·단일밴드 게이지·크리프·지연 힌트·취소 게이팅·오류 카드를 담당하고,
/// 백엔드 의미(델타 vs Setup 실행)는 IUpdateFlow 뒤로 격리한다.
/// 인스톨러는 이 클래스를 상속해 준비(감지) 상태를 재정의하고, 앱은 준비 상태 없이 즉시 흐름을 시작한다.
/// </summary>
public partial class UpdateFlowViewModel : ObservableObject
{
    /// <summary>본문 컬럼 폭(px) — 게이지 실폭 계산 기준 (560 − 36×2).</summary>
    private const double BodyWidth = 488;

    /// <summary>게이지 = 전 흐름 단일 밴드 — 다운로드(0단계)는 0~55 구간을 실측 %로 채운다.</summary>
    private const double DownloadBandMax = 55;

    private CancellationTokenSource? _cancelSource;
    private int _stageToken; // 단계 전환 세대 — 지연 힌트/게이지 크리프의 늦은 콜백 무효화 (UI 스레드 전용)
    private int _currentStep = -1;

    [ObservableProperty]
    private UpdateFlowState _state = UpdateFlowState.Ready;

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

    [ObservableProperty]
    private UpdateFlowTexts _texts;

    /// <summary>완료 후 주 버튼 또는 오류 정리 시 창 닫기 요청.</summary>
    public event Action? CloseRequested;

    /// <summary>흐름이 재시작을 예약함 — 호스트는 이 이벤트에서 앱을 graceful 종료해야 한다
    /// (Update.exe가 이 프로세스의 종료를 최대 60초 기다렸다가 적용·재시작한다).</summary>
    public event Action? PendingRestartRequested;

    public UpdateFlowViewModel(
        UpdateFlowTexts texts, string versionText, string logPath, string repoUrl, string releasesPageUrl)
    {
        _texts = texts;
        VersionText = versionText;
        LogPath = logPath;
        RepoUrl = repoUrl;
        ReleasesPageUrl = releasesPageUrl;
    }

    /// <summary>실행할 흐름 팩토리 — 매 시도(첫 실행·오류 재시도)마다 새로 생성해 캡처 스냅샷을 갱신한다.
    /// null 반환 = 시작 불가(게이트 차단/업데이트 소멸) — 상태를 바꾸지 않는다.</summary>
    public Func<IUpdateFlow?>? FlowFactory { get; set; }

    /// <summary>헤더 버전 칩 (인스톨러: 자체 버전, 앱: 현재 앱 버전).</summary>
    public string VersionText { get; }

    public string LogPath { get; }

    public string RepoUrl { get; }

    public string ReleasesPageUrl { get; }

    /// <summary>PendingRestart 수신 후 true — 진행 중 닫기 가드를 해제한다(앱 Shutdown이 정당한 닫기).
    /// 참고: WPF Application.Shutdown()은 Window.Closing의 e.Cancel을 무시하므로 이 플래그가 없어도
    /// 종료 자체는 막히지 않는다 — 가드의 실효 범위는 사용자 ✕/Esc/Alt+F4에 한정된다(문서화된 한계).</summary>
    public bool SuppressCloseGuard { get; private set; }

    // ── 준비 상태 (파생이 재정의 — 앱 흐름은 준비 상태를 건너뛰므로 기본값은 빈 문자열) ──

    public virtual string ReadyPrimaryText => "";

    public virtual string ReadySecondaryText => "";

    public virtual string PrimaryActionText => "";

    public virtual bool CanAct => true;

    protected void RaiseReadyProps()
    {
        OnPropertyChanged(nameof(ReadyPrimaryText));
        OnPropertyChanged(nameof(ReadySecondaryText));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(CanAct));
        RaiseTextProps();
    }

    // ── Texts 파생 표시 프로퍼티 ──

    public string WindowTitle => Texts.WindowTitle;

    public string Step1Label => Texts.StepLabels[0];

    public string Step2Label => Texts.StepLabels[1];

    public string Step3Label => Texts.StepLabels[2];

    public string Step4Label => Texts.StepLabels[3];

    public string SlowHintText => Texts.SlowHintText;

    public string DoneHeadlineText => Texts.DoneHeadline;

    public string DoneSecondaryText => Texts.DoneSecondary;

    public string DoneButtonText => Texts.DoneButton;

    public string ErrorHeadlineText => Texts.ErrorHeadline;

    partial void OnTextsChanged(UpdateFlowTexts value) => RaiseTextProps();

    private void RaiseTextProps()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(Step1Label));
        OnPropertyChanged(nameof(Step2Label));
        OnPropertyChanged(nameof(Step3Label));
        OnPropertyChanged(nameof(Step4Label));
        OnPropertyChanged(nameof(SlowHintText));
        OnPropertyChanged(nameof(DoneHeadlineText));
        OnPropertyChanged(nameof(DoneSecondaryText));
        OnPropertyChanged(nameof(DoneButtonText));
        OnPropertyChanged(nameof(ErrorHeadlineText));
    }

    /// <summary>게이지 필 실폭(px).</summary>
    public double GaugeWidth => Math.Clamp(GaugePct, 0, 100) / 100.0 * BodyWidth;

    partial void OnGaugePctChanged(double value) => OnPropertyChanged(nameof(GaugeWidth));

    // ── 명령 ──

    /// <summary>준비 상태 주 버튼 — 파생이 감지 결과에 따라 분기(설치/업데이트/실행).</summary>
    [RelayCommand]
    private Task PrimaryActionAsync() => OnPrimaryActionAsync();

    protected virtual Task OnPrimaryActionAsync() => StartFlowAsync();

    /// <summary>오류 카드 [다시 시도] — 팩토리로 흐름을 새로 만들어 재실행 (캡처 스냅샷 갱신).</summary>
    [RelayCommand]
    private Task InstallAsync() => StartFlowAsync();

    /// <summary>완료 카드 주 버튼 (인스톨러: 설치본 실행, 앱: 닫기).</summary>
    [RelayCommand]
    private void Launch() => OnDonePrimary();

    protected virtual void OnDonePrimary() => CloseRequested?.Invoke();

    [RelayCommand]
    private void Cancel() => _cancelSource?.Cancel();

    [RelayCommand]
    private void OpenLog() => TryShellStart(File.Exists(LogPath) ? LogPath : Path.GetDirectoryName(LogPath) ?? LogPath);

    [RelayCommand]
    private void OpenRepo() => TryShellStart(RepoUrl);

    [RelayCommand]
    private void OpenReleases() => TryShellStart(ReleasesPageUrl);

    protected void RequestClose() => CloseRequested?.Invoke();

    // ── 흐름 실행 ──

    public async Task StartFlowAsync()
    {
        if (State == UpdateFlowState.Progress || FlowFactory is null)
        {
            return;
        }

        if (FlowFactory() is not { } flow)
        {
            return; // 게이트 차단/업데이트 소멸 — 상태 유지 (호스트가 별도 안내)
        }

        await RunFlowAsync(flow);
    }

    private async Task RunFlowAsync(IUpdateFlow flow)
    {
        ResetProgressUi();
        State = UpdateFlowState.Progress;
        _cancelSource = new CancellationTokenSource();
        var cancellationToken = _cancelSource.Token;
        try
        {
            var progress = new Progress<UpdateFlowProgress>(ApplyProgress);
            var result = await flow.RunAsync(progress, cancellationToken);
            if (result.Success && result.PendingRestart)
            {
                // 이 프로세스가 곧 내려간다 — 진행 창은 '설치' 헤드라인인 채로 프로세스와 함께 소멸하고,
                // 재시작된 프로세스가 --update-done/마커로 Done 카드를 이어받는다 (완료 연속성).
                SuppressCloseGuard = true;
                PendingRestartRequested?.Invoke();
            }
            else if (result.Success)
            {
                _stageToken++; // 크리프 중지
                GaugePct = 100;
                State = UpdateFlowState.Done;
            }
            else
            {
                ShowFailure(result.Failure!);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 사용자 취소 — 준비 상태로 복귀
            State = UpdateFlowState.Ready;
            ResetProgressUi();
        }
        catch (Exception ex)
        {
            // 마지막 방어선 — flow가 분류하지 못한 예외도 크래시 대신 오류 상태로
            ShowFailure(new InstallFailure(
                InstallFailureClass.Unknown,
                ex.GetType().Name + ": " + ex.Message,
                "로그를 열어 원인을 확인해 주세요."));
        }
        finally
        {
            _cancelSource?.Dispose();
            _cancelSource = null;
            CanCancel = false;
        }
    }

    private void ApplyProgress(UpdateFlowProgress progress)
    {
        if (progress.StepIndex != _currentStep)
        {
            ApplyStep(progress.StepIndex);
        }

        if (progress.StepIndex == 0)
        {
            if (progress.Percent is { } pct)
            {
                GaugePct = pct * (DownloadBandMax / 100.0);
                ProgressValueText = $"{pct:0}%";
            }
            else if (progress.Bytes is { } bytes)
            {
                // Content-Length 없음 — 멎은 "0%" 대신 수신량 표기, 게이지는 자산 크기(~70MB) 근사
                GaugePct = Math.Min(50, bytes / (70.0 * 1024 * 1024) * DownloadBandMax);
                ProgressValueText = $"{bytes / 1048576.0:0.0} MB";
            }
        }
    }

    /// <summary>단계 전환 — 헤드라인/수치/타임라인/취소 가능 여부를 한 번에 갱신.</summary>
    private void ApplyStep(int step)
    {
        _currentStep = step;
        var token = ++_stageToken;
        HeadlineText = Texts.ProgressHeadlines[Math.Clamp(step, 0, Texts.ProgressHeadlines.Count - 1)];
        ProgressValueText = step == 0 ? "0%" : $"{step + 1}/4 단계";
        DownloadStepState = StepState(0, step);
        ScanStepState = StepState(1, step);
        InstallStepState = StepState(2, step);
        CompleteStepState = StepState(3, step);
        CanCancel = step <= Texts.LastCancellableStepIndex;
        ShowSlowHint = false;

        if (step == Texts.SlowHintStepIndex)
        {
            _ = ShowSlowHintAfterDelayAsync(token);
        }

        if (step >= 0 && step < Texts.CreepTargets.Count && Texts.CreepTargets[step] > 0)
        {
            _ = CreepGaugeAsync(Texts.CreepTargets[step], token);
        }
    }

    private static string StepState(int step, int current) =>
        step < current ? "Done" : step == current ? "Active" : "Todo";

    /// <summary>지연 힌트 — 지정 단계가 15초를 넘기면 ⚠ 노출 (카드 스펙).</summary>
    private async Task ShowSlowHintAfterDelayAsync(int token)
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (token == _stageToken && State == UpdateFlowState.Progress)
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

        while (token == _stageToken && State == UpdateFlowState.Progress && GaugePct < target)
        {
            GaugePct = Math.Min(target, GaugePct + 0.4);
            await Task.Delay(300);
        }
    }

    // ── 상태 직접 주입 (재시작 연속성: --update-done/마커 조정이 결과 카드만 표시할 때) ──

    /// <summary>완료 카드로 직행 (재시작 후 연속성 경로).</summary>
    public void ShowDone()
    {
        _stageToken++;
        GaugePct = 100;
        State = UpdateFlowState.Done;
    }

    /// <summary>오류 카드로 직행.</summary>
    public void ShowFailure(InstallFailure failure)
    {
        _stageToken++;
        ErrorDetail = failure.Detail;
        ErrorAdvice = failure.Advice;
        State = UpdateFlowState.Error;
    }

    private void ResetProgressUi()
    {
        _stageToken++;
        _currentStep = -1;
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
            // 셸 실행 실패 — 흐름 자체는 계속 (버튼 재시도 가능)
            return false;
        }
    }

    protected static bool TryShellStartTarget(string target) => TryShellStart(target);
}
