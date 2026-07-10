using System.IO;

namespace ClaudeUsageMonitor.Installer.Install;

/// <summary>Velopack Setup.exe 탐색: ① --setup 인자 → ② 자기 폴더 동반 파일 → ③ null(= GitHub 다운로드).</summary>
public static class SetupLocator
{
    /// <summary>vpk pack이 생성하는 Setup 자산 이름 (릴리스 자산과 동일).</summary>
    public const string SetupFileName = "AgentUsageMonitor-win-Setup.exe";

    public static string? Locate(string? cliArgPath, string baseDirectory, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(cliArgPath) && fileExists(cliArgPath))
        {
            return cliArgPath;
        }

        var sideBySide = Path.Combine(baseDirectory, SetupFileName);
        return fileExists(sideBySide) ? sideBySide : null;
    }
}
