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
/// Velopack Setup을 --silent로 실행하고 관찰 가능한 신호로 단계를 전환한다:
/// 보안 검사(프로세스 시작 ~ velopack.log 첫 갱신 — 무서명 exe의 AV 홀드가 이 구간에 보임)
/// → 설치(velopack.log 갱신 감지) → 완료(exit 0 + 설치 산출물 존재).
/// 취소는 설치 단계 진입 전(디스크 쓰기 전)에만 허용 — 반쯤 설치 방지.
/// </summary>
public sealed class SetupRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Watchdog = TimeSpan.FromMinutes(15);

    public static string DefaultVelopackLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "velopack", "velopack.log");

    public static string DefaultInstalledExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentUsageMonitor", "current", "Agent Usage Monitor.exe");

    private readonly string _velopackLogPath;
    private readonly string _installedExePath;

    public SetupRunner(string? velopackLogPath = null, string? installedExePath = null)
    {
        _velopackLogPath = velopackLogPath ?? DefaultVelopackLogPath;
        _installedExePath = installedExePath ?? DefaultInstalledExePath;
    }

    public async Task<InstallResult> RunAsync(
        string setupPath, IProgress<StageProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new StageProgress(InstallStage.SecurityScan));
        var startedAtUtc = DateTime.UtcNow;

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
            // 설치(디스크 쓰기) 진입 전까지만 취소 허용
            if (!installStageSeen && cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!installStageSeen && LogWrittenAfter(startedAtUtc))
            {
                installStageSeen = true;
                progress.Report(new StageProgress(InstallStage.Install));
            }

            if (DateTime.UtcNow - startedAtUtc > Watchdog)
            {
                TryKill(process);
                return new InstallResult(false, InstallDiagnostics.FromTimeout(installStageSeen));
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

        var logWritten = LogWrittenAfter(startedAtUtc);
        return new InstallResult(false, InstallDiagnostics.FromSetupExit(
            process.ExitCode, logWritten,
            logWritten ? InstallDiagnostics.ReadLogTail(_velopackLogPath) : null));
    }

    private bool LogWrittenAfter(DateTime utc)
    {
        try
        {
            return File.Exists(_velopackLogPath) && File.GetLastWriteTimeUtc(_velopackLogPath) > utc;
        }
        catch (IOException)
        {
            return false;
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
            // 권한/상태 문제 — 종료 대기 루프가 알아서 빠져나감
        }
    }
}
