namespace ClaudeUsageMonitor.Core.Models;

/// <summary>~/.claude/sessions/{pid}.json 의 라이브 세션 항목.</summary>
public sealed record LiveSession(
    int Pid,
    string SessionId,
    string Cwd,
    string? Status,
    string? Version,
    DateTimeOffset StartedAt);
