using System.IO;

namespace ClaudeUsageMonitor.App.Widget.Native;

/// <summary>
/// 임베드 위젯 진단 로그 — SetParent 계열 실패는 조용히 일어나므로(반환값만 false)
/// 현장 트러블슈팅용 기록이 필수다. 데이터 폴더의 native-widget.log에 남긴다.
/// </summary>
internal static class NativeWidgetLog
{
    private static readonly object Sync = new();
    private static string? _path;

    /// <summary>App 시작 시 데이터 폴더 기준으로 초기화. 세션 간 무한 증식 방지를 위해 64KB 초과 시 리셋.</summary>
    public static void Initialize(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "native-widget.log");
            if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024)
            {
                File.Delete(path);
            }
            _path = path;
            Write($"=== session start pid={Environment.ProcessId} ===");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _path = null;
        }
    }

    public static void Write(string message)
    {
        var path = _path;
        if (path is null)
        {
            return;
        }
        try
        {
            lock (Sync)
            {
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 로그 실패가 기능을 방해하면 안 됨
        }
    }
}
