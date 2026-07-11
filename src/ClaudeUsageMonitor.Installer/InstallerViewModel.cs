using System.IO;
using System.Net.Http;
using System.Reflection;
using ClaudeUsageMonitor.Installer.Install;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeUsageMonitor.Installer;

/// <summary>
/// 인스톨러 고유 상태 — 설치본 감지(DetectAsync)·설치/업데이트/최신/다운그레이드 모드·설치본 실행.
/// 진행 상태머신·창 표현은 공용 UpdateFlowViewModel(UpdateUi)로 이동했고(옵션 A),
/// 백엔드(Setup 탐색→--silent 실행→디스크 신호)는 SetupInstallFlow가 담당한다.
/// </summary>
public partial class InstallerViewModel : UpdateFlowViewModel
{
    private readonly string _bakedVersion;
    private InstallPlan _plan;

    /// <summary>설치본·최신 릴리스 감지 완료 전(주 버튼 비활성).</summary>
    [ObservableProperty]
    private bool _isDetecting = true;

    public InstallerViewModel(string? setupArgPath)
        : base(
            BuildTexts("설치"),
            versionText: ResolveVersionText(),
            logPath: SetupRunner.DefaultVelopackLogPath,
            repoUrl: InstallerUrls.RepoUrl,
            releasesPageUrl: InstallerUrls.ReleasesPageUrl)
    {
        _bakedVersion = ResolveVersionText().TrimStart('v');
        // 잠정값 — DetectAsync가 설치본 버전과 "실제 설치할 대상(최신 릴리스)"을 읽어 확정한다.
        _plan = new InstallPlan(InstallMode.NotInstalled, null, _bakedVersion);
        FlowFactory = () => new SetupInstallFlow(setupArgPath);
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
            if (_plan.Mode == InstallMode.Update)
            {
                Texts = BuildTexts("업데이트"); // 진행/완료 문구의 동사 전환
            }
        }
        finally
        {
            IsDetecting = false;
            OnPropertyChanged(nameof(Mode));
            RaiseReadyProps();
        }
    }

    /// <summary>감지된 설치 상태(신규/업데이트/최신/다운그레이드) — E2E·진단용.</summary>
    public InstallMode Mode => _plan.Mode;

    /// <summary>감지 완료 전에는 주 버튼 비활성.</summary>
    public override bool CanAct => !IsDetecting;

    partial void OnIsDetectingChanged(bool value) => RaiseReadyProps();

    /// <summary>준비 상태 주 버튼 라벨.</summary>
    public override string PrimaryActionText => IsDetecting
        ? "확인 중..."
        : _plan.Mode switch
        {
            InstallMode.Update => "업데이트",
            InstallMode.UpToDate => "실행",
            InstallMode.Downgrade => "실행",
            _ => "지금 설치",
        };

    /// <summary>준비 상태 1행 안내 — 감지 결과 반영.</summary>
    public override string ReadyPrimaryText => IsDetecting
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
    public override string ReadySecondaryText => _plan.Mode switch
    {
        InstallMode.UpToDate => "[실행]을 눌러 시작하거나 창을 닫으세요.",
        InstallMode.Downgrade => "다운그레이드는 지원하지 않습니다. [실행]으로 현재 버전을 시작하세요.",
        _ => InstallLocationText,
    };

    public string InstallLocationText =>
        @"%LOCALAPPDATA%\AgentUsageMonitor · 약 70 MB · 설치 후 자동 실행";

    /// <summary>준비 상태 주 버튼: 설치/업데이트는 Setup 실행, 최신/다운그레이드는 실행만.</summary>
    protected override async Task OnPrimaryActionAsync()
    {
        if (IsDetecting)
        {
            return;
        }

        if (_plan.Mode is InstallMode.UpToDate or InstallMode.Downgrade)
        {
            OnDonePrimary();
            return;
        }

        await StartFlowAsync();
    }

    /// <summary>완료 카드 [시작하기] — 실행 성공 시에만 창을 닫는다
    /// (설치본이 사라졌거나 실행 불가면 피드백 없이 닫히면 안 됨).</summary>
    protected override void OnDonePrimary()
    {
        if (TryShellStartTarget(SetupRunner.DefaultInstalledExePath))
        {
            RequestClose();
        }
        else
        {
            ShowFailure(new InstallFailure(
                InstallFailureClass.Unknown,
                "설치된 앱을 실행할 수 없습니다.",
                "설치가 손상되었을 수 있습니다 — 다시 설치해 주세요."));
        }
    }

    private static UpdateFlowTexts BuildTexts(string verb) => new()
    {
        WindowTitle = "Agent Usage Monitor 설치",
        StepLabels = ["다운로드", "보안 검사", "설치", "완료"],
        ProgressHeadlines = ["다운로드 중입니다", "보안 검사 중입니다", $"{verb} 중입니다", $"{verb}가 끝났습니다"],
        DoneHeadline = $"{verb}가 끝났습니다",
        DoneSecondary = "위젯이 작업표시줄에 표시됩니다. 우클릭으로 메뉴를 열 수 있습니다.",
        DoneButton = "시작하기",
        ErrorHeadline = $"{verb}를 완료하지 못했습니다",
        SlowHintText = "보안 소프트웨어 검사로 1분 이상 걸릴 수 있습니다 — 창을 닫지 말고 기다려 주세요.",
        SlowHintStepIndex = 1,          // 보안 검사(AV 홀드 관찰 구간)
        LastCancellableStepIndex = 1,   // 디스크 쓰기 전(다운로드·보안 검사)만 취소 허용
    };

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
