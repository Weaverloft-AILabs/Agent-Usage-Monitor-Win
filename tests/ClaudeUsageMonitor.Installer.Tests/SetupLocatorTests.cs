using System.IO;
using ClaudeUsageMonitor.Installer.Install;
using Xunit;

namespace ClaudeUsageMonitor.Installer.Tests;

public class SetupLocatorTests
{
    [Fact]
    public void Cli_Arg_Wins_When_File_Exists()
        => Assert.Equal(
            @"c:\bundle\setup.exe",
            SetupLocator.Locate(@"c:\bundle\setup.exe", @"c:\base", _ => true));

    [Fact]
    public void Missing_Cli_Arg_Falls_To_SideBySide()
    {
        var sideBySide = Path.Combine(@"c:\base", SetupLocator.SetupFileName);
        Assert.Equal(
            sideBySide,
            SetupLocator.Locate(@"c:\bundle\missing.exe", @"c:\base", p => p == sideBySide));
    }

    [Fact]
    public void No_Local_Setup_Returns_Null_For_Download()
        => Assert.Null(SetupLocator.Locate(null, @"c:\base", _ => false));

    [Fact]
    public void Blank_Arg_Is_Ignored()
        => Assert.Null(SetupLocator.Locate("   ", @"c:\base", _ => false));
}
