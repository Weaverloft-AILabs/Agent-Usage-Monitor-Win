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
            // 직전 정상본을 .bak으로 이동(세대 백업). 손상 시 Load가 여기서 복구한다.
            File.Replace(tmp, path, destinationBackupFileName: path + ".bak");
        }
        else
        {
            File.Move(tmp, path);
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
        catch (JsonException)
        {
            // 손상된 파일: 백업 폴백 유도 (JSONL 재스캔은 30일 초과분을 복구하지 못하므로 백업이 1차 방어)
            return false;
        }
    }
}
