using System.Net.Http;
using ClaudeUsageMonitor.Installer.Install;
using Xunit;

namespace ClaudeUsageMonitor.Installer.Tests;

public class GitHubReleaseClientTests
{
    [Fact]
    public void Exact_Asset_Name_Wins_Over_Earlier_Suffix_Match()
    {
        // arm64 자산이 먼저 나열돼도 정확한 이름을 선택
        const string json = """
            {"assets":[
              {"name":"AgentUsageMonitor-win-arm64-Setup.exe","browser_download_url":"https://x/arm64"},
              {"name":"AgentUsageMonitor-win-Setup.exe","browser_download_url":"https://x/win"}
            ]}
            """;
        Assert.Equal("https://x/win", GitHubReleaseClient.PickSetupUrl(json));
    }

    [Fact]
    public void Suffix_Fallback_When_No_Exact_Match()
    {
        const string json = """
            {"assets":[
              {"name":"Other-Setup.exe","browser_download_url":"https://x/other"},
              {"name":"AgentUsageMonitor-1.0.0-full.nupkg","browser_download_url":"https://x/nupkg"}
            ]}
            """;
        Assert.Equal("https://x/other", GitHubReleaseClient.PickSetupUrl(json));
    }

    [Fact]
    public void Rate_Limit_Error_Payload_Returns_Null()
    {
        // GitHub 오류 본문에는 assets가 없음 — null (호출부가 "자산 없음" 메시지로 승격)
        Assert.Null(GitHubReleaseClient.PickSetupUrl("""{"message":"API rate limit exceeded"}"""));
    }

    [Fact]
    public void Non_Json_Body_Throws_HttpRequestException()
        => Assert.Throws<HttpRequestException>(
            () => GitHubReleaseClient.PickSetupUrl("<html>proxy block page</html>"));
}
