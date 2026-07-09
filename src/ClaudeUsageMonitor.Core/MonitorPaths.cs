namespace ClaudeUsageMonitor.Core;

/// <summary>모니터가 사용하는 경로 집합. 테스트에서 재정의 가능.</summary>
public sealed class MonitorPaths
{
    public required string DataDirectory { get; init; }
    public required string ProjectsRoot { get; init; }
    public required string SessionsDirectory { get; init; }
    public string? CredentialsPath { get; init; }

    public static MonitorPaths Default()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // CLAUDE_CONFIG_DIR 지원 (DESIGN §4.2) — CLI와 동일하게 설정 디렉터리 재지정 가능
        var claudeDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(profile, ".claude");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 설치 루트(%LOCALAPPDATA%\AgentUsageMonitor)와 겹치지 않도록 .Data 접미사 사용
        var dataDirectory = Path.Combine(localAppData, "AgentUsageMonitor.Data");
        MigrateLegacyDataDirectory(Path.Combine(localAppData, "ClaudeUsageMonitor"), dataDirectory);

        return new MonitorPaths
        {
            DataDirectory = dataDirectory,
            ProjectsRoot = Path.Combine(claudeDir, "projects"),
            SessionsDirectory = Path.Combine(claudeDir, "sessions"),
            CredentialsPath = Path.Combine(claudeDir, ".credentials.json"),
        };
    }

    /// <summary>구 데이터 폴더(ClaudeUsageMonitor)를 새 위치로 1회 이전 — 롤업(월간 통계)·설정 보존.</summary>
    private static void MigrateLegacyDataDirectory(string legacyDirectory, string newDirectory)
    {
        try
        {
            if (Directory.Exists(newDirectory) || !Directory.Exists(legacyDirectory))
            {
                return;
            }
            Directory.Move(legacyDirectory, newDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 이동 실패(다른 프로세스가 잠금 등) — 새 폴더로 새로 시작. 치명적 아님
        }
    }
}
