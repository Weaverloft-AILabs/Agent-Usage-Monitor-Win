using System.IO;

namespace ClaudeUsageMonitor.Installer.Install;

public enum InstallFailureClass
{
    /// <summary>다운로드/네트워크 실패.</summary>
    Network,

    /// <summary>Setup이 실행됐지만 velopack.log가 갱신되지 않음 — AV/EDR 진입 전 홀드 추정.</summary>
    AntivirusHold,

    /// <summary>Setup이 0이 아닌 코드로 종료 (로그에 원인 있음).</summary>
    SetupError,

    /// <summary>그 외 (exit 0인데 설치 산출물 없음 등).</summary>
    Unknown,
}

/// <summary>실패 원인(원문)과 사용자 조치 안내 — 디자인 카드 오류 상태의 실패 클래스 매핑.</summary>
public sealed record InstallFailure(InstallFailureClass Class, string Detail, string Advice);

public static class InstallDiagnostics
{
    public const string NetworkAdvice = "네트워크 상태를 확인한 뒤 다시 시도해 주세요.";
    public const string AntivirusAdvice = "보안 소프트웨어가 실행을 보류했을 수 있습니다.";
    public const string LogAdvice = "로그를 열어 원인을 확인해 주세요.";

    /// <summary>다운로드 단계 실패 → 네트워크 안내.</summary>
    public static InstallFailure FromDownloadError(Exception exception) =>
        new(InstallFailureClass.Network, "download failed: " + exception.Message, NetworkAdvice);

    /// <summary>Setup 종료 후 판정. logWrittenAfterStart = 프로세스 시작 이후 velopack.log 갱신 여부
    /// (파일 존재만으로는 판정 불가 — 이전 설치의 잔존 로그가 있을 수 있음).</summary>
    public static InstallFailure FromSetupExit(int exitCode, bool logWrittenAfterStart, string? logTail)
    {
        if (!logWrittenAfterStart)
        {
            return new(
                InstallFailureClass.AntivirusHold,
                $"Setup exited with code {exitCode} — velopack.log not updated",
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
