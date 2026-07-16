using System.Text;

namespace ClaudeUsageMonitor.Core.Ingest;

/// <summary>파일별 인제스트 상태 (ingest-state.json으로 영속).</summary>
public sealed class FileIngestState
{
    public long ByteOffset { get; set; }
    public long LastLength { get; set; }
    public DateTime LastWriteUtc { get; set; }

    /// <summary>파일 선두 바이트의 지문(hex). 같은 경로가 다른 파일로 교체됐는지 판별.</summary>
    public string? HeadSignature { get; set; }
}

/// <summary>
/// append-only JSONL 파일의 증분 리더.
/// - Claude Code가 append 핸들을 쥐고 있으므로 FileShare.ReadWrite|Delete로 연다.
/// - 마지막 미완성 라인(개행 없음)은 반환하지 않고 오프셋도 전진시키지 않는다.
/// - 파일이 잘렸으면(Length &lt; offset) 0부터 다시 읽는다.
/// </summary>
public sealed class IncrementalFileReader
{
    // 선두 지문 = 첫 라인(개행 포함) 바이트의 hex. append-only JSONL의 첫 라인은 세션/타임스탬프로 사실상
    // 유일하고 append에도 불변이라, 같은 경로가 다른 파일로 교체됐는지 판별하는 안정적 신호다.
    // 첫 라인이 이 상한을 넘으면 앞 상한만큼으로 지문화(그래도 append 불변).
    private const int HeadSignatureCap = 512;

    // 한 번에 메모리로 읽는 상한. long 길이가 int를 넘는 초대형 파일에서 new byte[remaining] 오버플로 방지
    // (남은 부분은 다음 패스에서 이어 읽음 — 미완결 라인 처리 로직이 경계를 자연히 흡수).
    private const int MaxBytesPerRead = 64 * 1024 * 1024;

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
        else if (state.ByteOffset > 0 && state.HeadSignature is { Length: > 0 })
        {
            var current = ReadHeadSignature(stream);
            if (current is not null && current != state.HeadSignature)
            {
                // 같은 경로가 다른 파일로 교체됨(길이가 오프셋보다 커서 truncate 검사엔 안 걸림) → 처음부터 재읽기
                state.ByteOffset = 0;
            }
        }

        var readFromStart = state.ByteOffset == 0;

        var remaining = stream.Length - state.ByteOffset;
        if (remaining <= 0)
        {
            return Array.Empty<string>();
        }

        stream.Seek(state.ByteOffset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(remaining, MaxBytesPerRead)];
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

        if (read == 0)
        {
            // 길이 측정 직후 파일이 오프셋까지 잘려 즉시 EOF — Array.LastIndexOf(-1,0) 예외 방지.
            return Array.Empty<string>();
        }

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);
        if (lastNewline < 0)
        {
            // 버퍼(64MB)를 꽉 채웠는데 개행이 없고 그 뒤로 데이터가 더 있으면 = 단일 라인이 상한을 초과.
            // 그대로 두면 오프셋이 전진하지 않아 매 스캔 같은 창을 무한 재독(영구 정체 + 이후 라인 미수집)한다.
            // → 이 병리적 창을 건너뛰어 전진시킨다(초대형 라인 1건은 미수집, 나머지는 정상 진행).
            if (read == buffer.Length && remaining > read)
            {
                state.ByteOffset += read;
                state.LastLength = stream.Length;
                return Array.Empty<string>();
            }
            // 완결된 라인이 아직 없음(파일 끝의 부분 라인) — 다음 패스에서 처리
            return Array.Empty<string>();
        }

        var completeLength = lastNewline + 1;
        var text = Encoding.UTF8.GetString(buffer, 0, completeLength);

        state.ByteOffset += completeLength;
        state.LastLength = stream.Length;
        state.LastWriteUtc = File.GetLastWriteTimeUtc(path);

        if (readFromStart)
        {
            // 처음부터 읽은 경우에만 선두 지문 갱신(append는 첫 라인 불변).
            var sig = FirstLineSignature(buffer, read);
            if (sig is not null)
            {
                state.HeadSignature = sig;
            }
        }

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

    /// <summary>스트림 선두를 읽어 첫 라인 지문을 계산. 첫 라인이 아직 미완성이면 null. 호출 후 스트림 위치는 호출부가 재-Seek.</summary>
    private static string? ReadHeadSignature(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var head = new byte[HeadSignatureCap];
        var got = 0;
        while (got < head.Length)
        {
            var n = stream.Read(head, got, head.Length - got);
            if (n == 0)
            {
                break;
            }
            got += n;
        }
        return FirstLineSignature(head, got);
    }

    /// <summary>버퍼 선두의 첫 라인(개행 포함, 상한 <see cref="HeadSignatureCap"/>)을 hex로. 개행이 없고 상한 미만이면 첫 라인 미완성 → null.</summary>
    private static string? FirstLineSignature(byte[] buffer, int length)
    {
        // write(전체 버퍼)/read(선두 512B) 양 경로가 동일 지문을 내도록 항상 선두 HeadSignatureCap만 본다.
        var len = Math.Min(Math.Min(length, buffer.Length), HeadSignatureCap);
        var nl = Array.IndexOf(buffer, (byte)'\n', 0, len);
        int sigLen;
        if (nl >= 0)
        {
            sigLen = nl + 1; // 첫 라인 완성
        }
        else if (len >= HeadSignatureCap)
        {
            sigLen = HeadSignatureCap; // 첫 라인이 상한 초과 — 앞부분만으로 지문(그래도 append 불변)
        }
        else
        {
            return null; // 첫 라인 미완성 — 지문화 보류
        }
        return Convert.ToHexString(buffer, 0, sigLen);
    }
}
