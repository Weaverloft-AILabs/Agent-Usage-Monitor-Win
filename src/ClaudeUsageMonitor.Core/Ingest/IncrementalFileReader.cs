using System.Text;

namespace ClaudeUsageMonitor.Core.Ingest;

/// <summary>파일별 인제스트 상태 (ingest-state.json으로 영속).</summary>
public sealed class FileIngestState
{
    public long ByteOffset { get; set; }
    public long LastLength { get; set; }
    public DateTime LastWriteUtc { get; set; }
}

/// <summary>
/// append-only JSONL 파일의 증분 리더.
/// - Claude Code가 append 핸들을 쥐고 있으므로 FileShare.ReadWrite|Delete로 연다.
/// - 마지막 미완성 라인(개행 없음)은 반환하지 않고 오프셋도 전진시키지 않는다.
/// - 파일이 잘렸으면(Length &lt; offset) 0부터 다시 읽는다.
/// </summary>
public sealed class IncrementalFileReader
{
    public IReadOnlyList<string> ReadNewLines(string path, FileIngestState state)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < state.ByteOffset)
        {
            // truncate/재생성 감지 → 전체 재읽기
            state.ByteOffset = 0;
        }

        var remaining = stream.Length - state.ByteOffset;
        if (remaining <= 0)
        {
            return Array.Empty<string>();
        }

        stream.Seek(state.ByteOffset, SeekOrigin.Begin);
        var buffer = new byte[remaining];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                break;
            }
            read += n;
        }

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
        if (lastNewline < 0)
        {
            // 완결된 라인이 아직 없음 — 다음 패스에서 처리
            return Array.Empty<string>();
        }

        var completeLength = lastNewline + 1;
        var text = Encoding.UTF8.GetString(buffer, 0, completeLength);

        state.ByteOffset += completeLength;
        state.LastLength = stream.Length;
        state.LastWriteUtc = File.GetLastWriteTimeUtc(path);

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }
        return lines;
    }
}
