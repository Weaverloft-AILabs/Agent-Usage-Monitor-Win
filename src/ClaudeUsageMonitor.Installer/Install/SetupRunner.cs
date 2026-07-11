using System.Diagnostics;
using System.IO;

namespace ClaudeUsageMonitor.Installer.Install;

/// <summary>설치 진행 단계 (디자인 카드의 4점 타임라인과 1:1).</summary>
public enum InstallStage
{
    Download,
    SecurityScan,
    Install,
    Complete,
}

public sealed record StageProgress(InstallStage Stage);

public sealed record InstallResult(bool Success, InstallFailure? Failure);

/// <summary>
/// Velopack Setup을 --silent로 실행하고 관찰 가능한 디스크 신호로 단계를 전환한다.
///
/// 신호 설계 (2026-07-10 적대적 리뷰 반영):
/// · velopack.log는 머신 전역 파일(다른 Velopack 앱/실행 중인 본 앱의 UpdateService도 씀) —
///   "길이 증가"를 핸들 열람으로 읽어(속성 캐시 지연 회피) 보조 신호로만 쓴다.
/// · 주 신호 = 설치 루트(%LOCALAPPDATA%\AgentUsageMonitor)의 생성/수정 — 우리 설치의 디스크 쓰기.
/// · 두 신호 중 하나라도 관측되면 "설치" 단계로 판정하고 취소를 회수한다(오탐 방향 = 취소가 일찍
///   막힐 뿐 안전; 미탐 방향이 반쯤 설치를 만든다). 신호 평가가 취소 처리보다 항상 먼저다.
/// · 워치독: 설치 신호 전 15분 → kill 후 실패 보고(디스크 쓰기 전이라 안전).
///   설치 신호 후 60분 → kill 없이 보고만(절전 복귀/야간 설치에서 쓰기 중 kill 금지).
/// </summary>
public sealed class SetupRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PreInstallWatchdog = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PostInstallWatchdog = TimeSpan.FromMinutes(60);

    public static string DefaultVelopackLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "velopack", "velopack.log");

    public static string DefaultInstalledExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentUsageMonitor", "current", "Agent Usage Monitor.exe");

    private readonly string _velopackLogPath;
    private readonly string _installedExePath;
    private readonly string _installRootPath;

    public SetupRunner(string? velopackLogPath = null, string? installedExePath = null)
    {
        _velopackLogPath = velopackLogPath ?? DefaultVelopackLogPath;
        _installedExePath = installedExePath ?? DefaultInstalledExePath;
        _installRootPath = Path.GetDirectoryName(Path.GetDirectoryName(_installedExePath)!)
            ?? _installedExePath;
    }

    public async Task<InstallResult> RunAsync(
        string setupPath, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new StageProgress(InstallStage.SecurityScan));
        var startLogLength = ReadLogLength();
        var rootBaseline = GetInstallRootWriteTimeUtc();
        var clock = Stopwatch.StartNew();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "--silent",
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();

        var installStageSeen = false;
        while (!process.HasExited)
        {
            // 1) 디스크 활동 신호를 취소 처리보다 먼저 평가 — 신호 이후 도착한 취소를 실행하지 않기 위한 순서
            if (!installStageSeen && DiskActivityObserved(startLogLength, rootBaseline))
            {
                installStageSeen = true;
                progress.Report(new StageProgress(InstallStage.Install));
            }

            if (!installStageSeen)
            {
                // 2) 취소·워치독은 디스크 쓰기 전에만 kill 허용
                if (cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    process.WaitForExit(5000); // 죽는 프로세스와 재시도 충돌 방지
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (clock.Elapsed > PreInstallWatchdog)
                {
                    TryKill(process);
                    process.WaitForExit(5000);
                    return new InstallResult(false, InstallDiagnostics.FromTimeout(installStageSeen: false));
                }
            }
            else if (clock.Elapsed > PostInstallWatchdog)
            {
                // 설치 신호 후에는 절대 kill하지 않는다 — 보고만 하고 Setup은 끝까지 진행하게 둔다
                return new InstallResult(false, InstallDiagnostics.FromTimeout(installStageSeen: true));
            }

            await Task.Delay(PollInterval, CancellationToken.None).ConfigureAwait(false);
        }

        if (process.ExitCode == 0 && File.Exists(_installedExePath))
        {
            progress.Report(new StageProgress(InstallStage.Complete));
            return new InstallResult(true, null);
        }

        if (process.ExitCode == 0)
        {
            return new InstallResult(false, InstallDiagnostics.FromMissingArtifact(_installedExePath));
        }

        // 분류는 "우리 설치 루트가 실제로 건드려졌는가" 기준 — 전역 velopack.log 성장만으로는
        // 다른 Velopack 프로세스의 로그 꼬리를 우리 실패 원인으로 오표기할 수 있다
        var rootTouched = InstallRootTouched(rootBaseline);
        var logGrew = ReadLogLength() > startLogLength;
        return new InstallResult(false, InstallDiagnostics.FromSetupExit(
            process.ExitCode,
            installActivitySeen: rootTouched,
            logTail: logGrew ? InstallDiagnostics.ReadLogTail(_velopackLogPath) : null));
    }

    /// <summary>설치 루트 수정(주 신호) 또는 velopack.log 길이 증가(보조 신호).</summary>
    private bool DiskActivityObserved(long startLogLength, DateTime? rootBaseline) =>
        InstallRootTouched(rootBaseline) || ReadLogLength() > startLogLength;

    private bool InstallRootTouched(DateTime? baseline)
    {
        var current = GetInstallRootWriteTimeUtc();
        if (current is null)
        {
            return false;
        }

        return baseline is null || current > baseline;
    }

    private DateTime? GetInstallRootWriteTimeUtc()
    {
        try
        {
            return Directory.Exists(_installRootPath)
                ? Directory.GetLastWriteTimeUtc(_installRootPath)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>핸들을 직접 열어 길이를 읽는다 — 속성 캐시(LastWriteTime 지연 갱신)를 우회.
    /// 읽기 실패는 0 (성장 판정이 보수적으로 기울음 = 취소가 일찍 회수되는 안전한 방향).</summary>
    private long ReadLogLength()
    {
        try
        {
            if (!File.Exists(_velopackLogPath))
            {
                return 0;
            }

            using var stream = new FileStream(
                _velopackLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 이미 종료됨
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 권한/상태 문제 — WaitForExit가 처리
        }
    }
}
