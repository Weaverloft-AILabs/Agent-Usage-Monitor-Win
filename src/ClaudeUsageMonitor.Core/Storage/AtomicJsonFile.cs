using System.Text;
using System.Text.Json;

namespace ClaudeUsageMonitor.Core.Storage;

/// <summary>
/// tmp 파일에 쓴 뒤 교체하는 원자적 JSON 저장 유틸.
/// 내구성: tmp를 fsync(Flush(true))한 뒤 교체하고, 직전 정상본을 <c>.bak</c>으로 보존한다.
/// 로드 시 주 파일이 손상되면 <c>.bak</c>으로 폴백해 침묵 리셋(전 이력 소실)을 방지한다.
/// </summary>
public static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = path + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options));

        // 이름 교체(rename) 전에 데이터 블록을 디스크로 강제 커밋 — 전원손실 시 0바이트/토막 노출 방지.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            // 현재 primary가 정상(well-formed)일 때만 .bak으로 강등한다. 이미 손상된 primary를 .bak으로 옮기면
            // 직전에 남아 있던 '정상' .bak을 덮어써 이중손상 창이 생기므로, 손상 시엔 .bak을 건드리지 않는다.
            var backup = IsWellFormedJson(path) ? path + ".bak" : null;
            File.Replace(tmp, path, destinationBackupFileName: backup);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static bool IsWellFormedJson(string path)
    {
        try
        {
            using var _ = JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false; // 손상/접근불가 → 좋은 .bak 보존
        }
    }

    public static T? Load<T>(string path) where T : class
    {
        // 주 파일이 정상이면 그대로, 손상/부재면 세대 백업으로 폴백.
        if (TryDeserialize<T>(path, out var value))
        {
            return value;
        }

        return TryDeserialize<T>(path + ".bak", out var backup) ? backup : null;
    }

    private static bool TryDeserialize<T>(string path, out T? value) where T : class
    {
        value = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            return value is not null; // "null" 리터럴/빈 역직렬화는 실패로 취급 → 백업 폴백
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 손상/접근불가(락 등): 백업 폴백 유도 — Save의 IsWellFormedJson과 대칭.
            // (JsonException만 잡으면 primary 락 시 IOException이 폴백을 우회해 전파됐다.)
            return false;
        }
    }
}
