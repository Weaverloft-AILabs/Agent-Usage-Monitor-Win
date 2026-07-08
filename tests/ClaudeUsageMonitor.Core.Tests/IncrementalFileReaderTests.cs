using System.Text;
using ClaudeUsageMonitor.Core.Ingest;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public sealed class IncrementalFileReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cum-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IncrementalFileReader _reader = new();

    public IncrementalFileReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewFile(string content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void ReadsAllCompleteLines_OnFirstPass()
    {
        var path = NewFile("line1\nline2\n");
        var state = new FileIngestState();

        var lines = _reader.ReadNewLines(path, state);

        Assert.Equal(new[] { "line1", "line2" }, lines);
    }

    [Fact]
    public void ReturnsOnlyAppendedLines_OnSecondPass()
    {
        var path = NewFile("line1\n");
        var state = new FileIngestState();
        _reader.ReadNewLines(path, state);

        File.AppendAllText(path, "line2\nline3\n");
        var lines = _reader.ReadNewLines(path, state);

        Assert.Equal(new[] { "line2", "line3" }, lines);
    }

    [Fact]
    public void DoesNotConsume_TrailingPartialLine()
    {
        var path = NewFile("line1\npartial-without-newline");
        var state = new FileIngestState();

        var first = _reader.ReadNewLines(path, state);
        Assert.Equal(new[] { "line1" }, first);

        // 부분 라인이 완성되면 다음 패스에서 반환되어야 한다
        File.AppendAllText(path, "-done\n");
        var second = _reader.ReadNewLines(path, state);
        Assert.Equal(new[] { "partial-without-newline-done" }, second);
    }

    [Fact]
    public void ResetsToZero_WhenFileTruncated()
    {
        var path = NewFile("aaaaaaaaaaaaaaaaaaaa\nbbbbbbbbbbbbbbbbbbbb\n");
        var state = new FileIngestState();
        _reader.ReadNewLines(path, state);

        File.WriteAllText(path, "new\n"); // 더 짧게 재작성 (truncate)
        var lines = _reader.ReadNewLines(path, state);

        Assert.Equal(new[] { "new" }, lines);
    }

    [Fact]
    public void ReadsWhileAnotherProcessHoldsAppendHandle()
    {
        var path = Path.Combine(_dir, "live.jsonl");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = Encoding.UTF8.GetBytes("live-line\n");
        writer.Write(bytes);
        writer.Flush();

        var state = new FileIngestState();
        var lines = _reader.ReadNewLines(path, state); // writer 핸들이 열려 있는 동안 읽기

        Assert.Equal(new[] { "live-line" }, lines);
    }

    [Fact]
    public void HandlesCrlfLineEndings()
    {
        var path = NewFile("line1\r\nline2\r\n");
        var state = new FileIngestState();

        var lines = _reader.ReadNewLines(path, state);

        Assert.Equal(new[] { "line1", "line2" }, lines);
    }

    [Fact]
    public void StateStore_RoundTrips()
    {
        var store = new IngestStateStore(_dir);
        var state = new Dictionary<string, FileIngestState>(StringComparer.OrdinalIgnoreCase)
        {
            ["c:\\a.jsonl"] = new() { ByteOffset = 42, LastLength = 100 },
        };

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal(42, loaded["c:\\a.jsonl"].ByteOffset);
        Assert.Equal(100, loaded["c:\\a.jsonl"].LastLength);
    }
}
