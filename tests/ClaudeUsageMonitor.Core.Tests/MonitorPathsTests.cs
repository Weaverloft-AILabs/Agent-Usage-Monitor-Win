using ClaudeUsageMonitor.Core;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class MonitorPathsTests
{
    [Fact]
    public void Default_UsesUserProfileClaudeDir()
    {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            var paths = MonitorPaths.Default();

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(Path.Combine(profile, ".claude", ".credentials.json"), paths.CredentialsPath);
            Assert.Equal(Path.Combine(profile, ".claude", "projects"), paths.ProjectsRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }

    [Fact]
    public void Default_HonorsClaudeConfigDir()
    {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            var custom = Path.Combine(Path.GetTempPath(), "aum-config-test");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", custom);
            var paths = MonitorPaths.Default();

            Assert.Equal(Path.Combine(custom, ".credentials.json"), paths.CredentialsPath);
            Assert.Equal(Path.Combine(custom, "projects"), paths.ProjectsRoot);
            Assert.Equal(Path.Combine(custom, "sessions"), paths.SessionsDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
        }
    }
}
