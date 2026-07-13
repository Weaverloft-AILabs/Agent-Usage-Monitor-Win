using System.IO;
using ClaudeUsageMonitor.Installer.Install;
using Xunit;

namespace ClaudeUsageMonitor.Installer.Tests;

public class EmbeddedSetupTests
{
    [Fact]
    public void No_Resource_Returns_Null()
        => Assert.Null(EmbeddedSetup.Extract(() => null, Path.GetTempPath()));

    [Fact]
    public void Present_Resource_Is_Extracted_With_Same_Bytes()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 42, 99, 0, 255 };
        var dir = Path.Combine(Path.GetTempPath(), "aum-embed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = EmbeddedSetup.Extract(() => new MemoryStream(payload), dir);

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Equal(payload, File.ReadAllBytes(path!));
            Assert.Equal(dir, Path.GetDirectoryName(path));
            Assert.EndsWith(".exe", path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Two_Extractions_Get_Distinct_Temp_Names()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aum-embed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = EmbeddedSetup.Extract(() => new MemoryStream([1]), dir);
            var b = EmbeddedSetup.Extract(() => new MemoryStream([1]), dir);
            Assert.NotEqual(a, b);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
