namespace ClaudeUsageMonitor.Installer.Install;

/// <summary>설치 원본/안내 링크 상수 (단일 소스 — XAML/VM 어디서든 이 값만 사용).</summary>
public static class InstallerUrls
{
    public const string RepoUrl = "https://github.com/Weaverloft-AILabs/Agent-Usage-Monitor-Win";

    public const string ReleasesPageUrl = RepoUrl + "/releases/latest";

    public const string LatestReleaseApi =
        "https://api.github.com/repos/Weaverloft-AILabs/Agent-Usage-Monitor-Win/releases/latest";
}
