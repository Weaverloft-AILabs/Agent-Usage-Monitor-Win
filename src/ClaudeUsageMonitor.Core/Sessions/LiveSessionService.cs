using System.Diagnostics;
using System.Text.Json;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Sessions;

/// <summary>
/// ~/.claude/sessions/{pid}.json 라이브 세션 레지스트리를 읽어
/// 현재 실행 중인 Claude Code 세션(=현재 사용 중인 프로젝트)을 나열한다.
/// PID 생존 확인으로 stale 항목을 걸러낸다.
/// </summary>
public sealed class LiveSessionService
{
    private readonly string _sessionsDirectory;
    private readonly Func<int, bool> _isProcessAlive;

    public LiveSessionService(string sessionsDirectory, Func<int, bool>? isProcessAlive = null)
    {
        _sessionsDirectory = sessionsDirectory;
        _isProcessAlive = isProcessAlive ?? DefaultIsAlive;
    }

    public IReadOnlyList<LiveSession> GetLive()
    {
        var result = new List<LiveSession>();
        if (!Directory.Exists(_sessionsDirectory))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(_sessionsDirectory, "*.json"))
        {
            var session = TryParse(file);
            if (session is not null && _isProcessAlive(session.Pid))
            {
                result.Add(session);
            }
        }

        return result
            .OrderByDescending(s => s.StartedAt)
            .ToList();
    }

    private static LiveSession? TryParse(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var pid = root.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            var sessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
            var cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() : null;
            if (pid <= 0 || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(cwd))
            {
                return null;
            }

            var startedAtMs = root.TryGetProperty("startedAt", out var sa) && sa.ValueKind == JsonValueKind.Number
                ? sa.GetInt64()
                : 0L;

            return new LiveSession(
                pid,
                sessionId,
                cwd,
                root.TryGetProperty("status", out var st) ? st.GetString() : null,
                root.TryGetProperty("version", out var v) ? v.GetString() : null,
                DateTimeOffset.FromUnixTimeMilliseconds(startedAtMs));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool DefaultIsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
