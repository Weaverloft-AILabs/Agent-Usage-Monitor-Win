using ClaudeUsageMonitor.Installer.Install;
using Xunit;

namespace ClaudeUsageMonitor.Installer.Tests;

public class InstallProbeTests
{
    [Fact]
    public void Not_Installed_Is_NotInstalled()
        => Assert.Equal(InstallMode.NotInstalled, InstallProbe.Decide(null, "2.1.0").Mode);

    [Theory]
    [InlineData("2.0.2", "2.1.0")]          // patch/minor 낮음 → 업데이트
    [InlineData("2.0.2+abc123", "2.1.0")]   // 빌드메타 무시
    [InlineData("1.2.0", "2.0.0")]          // 메이저 낮음
    [InlineData("2.0.9", "2.1.0")]          // 숫자 비교(문자열 아님)
    [InlineData("v2.0.0", "v2.1.0")]        // v 접두
    public void Older_Installed_Switches_To_Update(string installed, string target)
    {
        var plan = InstallProbe.Decide(installed, target);
        Assert.Equal(InstallMode.Update, plan.Mode);
        Assert.Equal(installed, plan.InstalledVersion);
        Assert.Equal(target, plan.TargetVersion);
    }

    /// <summary>알려진 한계(문서화): 숫자만 비교하므로 프리릴리스는 동일 버전 정식과 UpToDate로 취급된다.
    /// 이 프로젝트는 정식 vX.Y.Z만 릴리스하므로 실무 영향 없음.</summary>
    [Fact]
    public void Prerelease_Install_Is_Not_Detected_As_Update_KnownLimitation()
        => Assert.Equal(InstallMode.UpToDate, InstallProbe.Decide("2.1.0-beta.1", "2.1.0").Mode);

    [Theory]
    [InlineData("2.1.0", "2.1.0")]
    [InlineData("2.1.0+hash", "2.1.0")]
    public void Same_Version_Is_UpToDate(string installed, string target)
        => Assert.Equal(InstallMode.UpToDate, InstallProbe.Decide(installed, target).Mode);

    [Theory]
    [InlineData("2.2.0", "2.1.0")]
    [InlineData("3.0.0", "2.1.0")]
    [InlineData("2.1.1", "2.1.0")]
    public void Newer_Installed_Is_Downgrade(string installed, string target)
        => Assert.Equal(InstallMode.Downgrade, InstallProbe.Decide(installed, target).Mode);

    [Fact]
    public void Installed_But_Unparseable_Version_Falls_To_Update()
        => Assert.Equal(InstallMode.Update, InstallProbe.Decide("garbage", "2.1.0").Mode);

    [Theory]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("2.1.0", "2.0.9", 1)]
    [InlineData("2.10.0", "2.9.0", 1)]      // 숫자 비교: 10 > 9
    [InlineData("v2.0.0+x", "2.0.0-beta", 0)] // 메타/프리릴리스 무시 후 동일
    public void CompareVersions_Numeric(string a, string b, int expected)
        => Assert.Equal(expected, InstallProbe.CompareVersions(a, b));
}
