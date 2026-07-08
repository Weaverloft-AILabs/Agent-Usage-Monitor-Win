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
        var claudeDir = Path.Combine(profile, ".claude");
        return new MonitorPaths
        {
            DataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeUsageMonitor"),
            ProjectsRoot = Path.Combine(claudeDir, "projects"),
            SessionsDirectory = Path.Combine(claudeDir, "sessions"),
            CredentialsPath = Path.Combine(claudeDir, ".credentials.json"),
        };
    }
}
