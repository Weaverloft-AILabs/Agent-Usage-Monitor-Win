using System.IO;
using System.Net.Http;
using ClaudeUsageMonitor.Installer.Install;
using Xunit;

namespace ClaudeUsageMonitor.Installer.Tests;

public class InstallDiagnosticsTests
{
    [Fact]
    public void Setup_Exit_Without_Install_Activity_Is_Antivirus_Hold()
    {
        var failure = InstallDiagnostics.FromSetupExit(1, installActivitySeen: false, logTail: null);
        Assert.Equal(InstallFailureClass.AntivirusHold, failure.Class);
        Assert.Equal(InstallDiagnostics.AntivirusAdvice, failure.Advice);
    }

    [Fact]
    public void Unrelated_Machine_Log_Does_Not_Flip_Classification()
    {
        // 다른 Velopack 프로세스가 전역 로그를 썼어도 우리 설치 루트가 안 건드려졌으면 AV 홀드 유지
        var failure = InstallDiagnostics.FromSetupExit(1, installActivitySeen: false, logTail: "someone else's line");
        Assert.Equal(InstallFailureClass.AntivirusHold, failure.Class);
        Assert.Contains("may be unrelated", failure.Detail);
    }

    [Fact]
    public void Setup_Exit_With_Install_Activity_Is_Setup_Error_With_Tail()
    {
        var failure = InstallDiagnostics.FromSetupExit(1, installActivitySeen: true, logTail: "fatal: disk full");
        Assert.Equal(InstallFailureClass.SetupError, failure.Class);
        Assert.Contains("disk full", failure.Detail);
        Assert.Equal(InstallDiagnostics.LogAdvice, failure.Advice);
    }

    [Fact]
    public void Download_Error_Maps_To_Network_Advice()
    {
        var failure = InstallDiagnostics.FromDownloadError(new HttpRequestException("timeout"));
        Assert.Equal(InstallFailureClass.Network, failure.Class);
        Assert.Equal(InstallDiagnostics.NetworkAdvice, failure.Advice);
    }

    [Fact]
    public void Missing_Artifact_After_Exit_Zero_Advises_Log()
    {
        var failure = InstallDiagnostics.FromMissingArtifact(@"c:\x\app.exe");
        Assert.Equal(InstallFailureClass.Unknown, failure.Class);
        Assert.Equal(InstallDiagnostics.LogAdvice, failure.Advice);
    }

    [Fact]
    public void Timeout_Before_Install_Stage_Is_Antivirus_Hold()
        => Assert.Equal(InstallFailureClass.AntivirusHold, InstallDiagnostics.FromTimeout(installStageSeen: false).Class);

    [Fact]
    public void Timeout_After_Install_Stage_Is_Setup_Error()
        => Assert.Equal(InstallFailureClass.SetupError, InstallDiagnostics.FromTimeout(installStageSeen: true).Class);

    [Fact]
    public void Log_Tail_Skips_Trailing_Blank_Lines()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "first line\nlast line\n\n   \n");
            Assert.Equal("last line", InstallDiagnostics.ReadLogTail(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Log_Tail_Of_Missing_File_Is_Null()
        => Assert.Null(InstallDiagnostics.ReadLogTail(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));
}
