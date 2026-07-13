using ClaudeUsageMonitor.Installer.Install;
using Xunit;

namespace ClaudeUsageMonitor.Installer.Tests;

public class SetupLocatorTests
{
    [Fact]
    public void Cli_Arg_Wins_When_File_Exists()
        => Assert.Equal(
            @"c:\bundle\setup.exe",
            SetupLocator.Locate(@"c:\bundle\setup.exe", _ => true));

    // 옛 "같은 폴더의 Setup.exe 자동 사용"은 제거됨 — 스테일 파일이 엉뚱한 구버전을 설치하는 히잡이었다.
    [Fact]
    public void Missing_Cli_Arg_File_Returns_Null()
        => Assert.Null(SetupLocator.Locate(@"c:\bundle\missing.exe", _ => false));

    [Fact]
    public void No_Arg_Returns_Null_For_Embed_Or_Download()
        => Assert.Null(SetupLocator.Locate(null, _ => false));

    [Fact]
    public void Blank_Arg_Is_Ignored()
        => Assert.Null(SetupLocator.Locate("   ", _ => false));
}
