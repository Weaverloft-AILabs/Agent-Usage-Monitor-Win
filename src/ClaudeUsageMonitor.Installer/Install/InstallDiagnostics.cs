using System.IO;

namespace ClaudeUsageMonitor.Installer.Install;

// InstallFailureClass/InstallFailure 타입은 UpdateUi로 이동(옵션 A — 앱 인앱 업데이트와 오류 카드 공유).
// 분류 로직(이 클래스)은 인스톨러 고유(Setup exit code/velopack.log 해석)라 여기 남는다.
public static class InstallDiagnostics
{
    public const string NetworkAdvice = "네트워크 상태를 확인한 뒤 다시 시도해 주세요.";
    public const string AntivirusAdvice = "보안 소프트웨어가 실행을 보류했을 수 있습니다.";
    public const string LogAdvice = "로그를 열어 원인을 확인해 주세요.";

    /// <summary>다운로드 단계 실패 → 네트워크 안내.</summary>
    public static InstallFailure FromDownloadError(Exception exception) =>
        new(InstallFailureClass.Network, "download failed: " + exception.Message, NetworkAdvice);

    /// <summary>Setup 종료 후 판정. installActivitySeen = "우리 설치 루트"가 실제로 건드려졌는가
    /// (velopack.log는 머신 전역이라 다른 Velopack 프로세스의 기록일 수 있음 — 분류 기준으로 쓰지 않는다).</summary>
    public static InstallFailure FromSetupExit(int exitCode, bool installActivitySeen, string? logTail)
    {
        if (!installActivitySeen)
        {
            return new(
                InstallFailureClass.AntivirusHold,
                logTail is null
                    ? $"Setup exited with code {exitCode} — no install activity observed"
                    : $"Setup exited with code {exitCode} — no install activity; machine log tail (may be unrelated): \"{logTail}\"",
                AntivirusAdvice);
        }

        return new(
            InstallFailureClass.SetupError,
            $"Setup exited with code {exitCode} — velopack.log: \"{logTail ?? "(empty)"}\"",
            LogAdvice);
    }

    /// <summary>exit 0인데 설치된 exe가 없음.</summary>
    public static InstallFailure FromMissingArtifact(string expectedPath) =>
        new(InstallFailureClass.Unknown, "installed exe not found: " + expectedPath, LogAdvice);

    /// <summary>단계 감시 폴링이 시간 초과(비정상 장기 실행).</summary>
    public static InstallFailure FromTimeout(bool installStageSeen) =>
        installStageSeen
            ? new(InstallFailureClass.SetupError, "Setup did not finish within the watchdog window", LogAdvice)
            : new(InstallFailureClass.AntivirusHold, "Setup produced no install activity within the watchdog window", AntivirusAdvice);

    /// <summary>velopack.log의 마지막 비어있지 않은 줄 (파일 없음/잠김이면 null).</summary>
    public static string? ReadLogTail(string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return null;
            }

            using var stream = new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? last = null;
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    last = line;
                }
            }

            return last;
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
}
