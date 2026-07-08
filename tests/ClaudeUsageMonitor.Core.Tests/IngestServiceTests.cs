using ClaudeUsageMonitor.Core.Ingest;
using ClaudeUsageMonitor.Core.Sessions;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public sealed class IngestServiceTests : IDisposable
{
    private static readonly TimeZoneInfo Kst = TimeZoneInfo.CreateCustomTimeZone(
        "Test-KST", TimeSpan.FromHours(9), "Test-KST", "Test-KST");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cum-ingest-" + Guid.NewGuid().ToString("N"));
    private string ProjectsDir => Path.Combine(_root, "projects");
    private string DataDir => Path.Combine(_root, "data");

    public IngestServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(ProjectsDir, "proj-a"));
        Directory.CreateDirectory(DataDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private IngestService NewService() => new(ProjectsDir, DataDir, Kst);

    [Fact]
    public async Task Backfill_AggregatesFixture_WithDedup()
    {
        File.WriteAllText(Path.Combine(ProjectsDir, "proj-a", "session.jsonl"), Fixture("main-session.jsonl"));
        using var service = NewService();

        await service.ScanAllAsync();

        // main-session: msg_A(305) + msg_B(129) — 중복 라인 제외
        var day = service.CurrentRollup.Days["2026-07-08"];
        Assert.Equal(434, day.TotalTokens.Output);
        Assert.Equal(2, day.TotalRequests);
    }

    [Fact]
    public async Task IncrementalAppend_UpdatesWithoutDoubleCount()
    {
        var path = Path.Combine(ProjectsDir, "proj-a", "live.jsonl");
        var allLines = Fixture("subagent-partial.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // msg_C의 부분 스트리밍 2줄만 먼저
        File.WriteAllText(path, string.Join('\n', allLines.Take(2)) + "\n");
        using var service = NewService();
        await service.ScanAllAsync();
        Assert.Equal(9, service.CurrentRollup.Days["2026-07-08"].TotalTokens.Output);

        // 최종 라인 append → 486으로 갱신 (합산 아님)
        File.AppendAllText(path, string.Join('\n', allLines.Skip(2).Take(1)) + "\n");
        await service.ScanAllAsync();
        var day = service.CurrentRollup.Days["2026-07-08"];
        Assert.Equal(486, day.TotalTokens.Output);
        Assert.Equal(1, day.TotalRequests);
    }

    [Fact]
    public async Task Restart_IsIdempotent_AcrossProcesses()
    {
        File.WriteAllText(Path.Combine(ProjectsDir, "proj-a", "session.jsonl"), Fixture("main-session.jsonl"));

        using (var first = NewService())
        {
            await first.ScanAllAsync();
        }

        // 새 인스턴스(재시작 시뮬레이션): 영속 상태 로드 후 재스캔해도 수치 불변
        using var second = NewService();
        await second.ScanAllAsync();

        var day = second.CurrentRollup.Days["2026-07-08"];
        Assert.Equal(434, day.TotalTokens.Output);
        Assert.Equal(2, day.TotalRequests);
    }

    [Fact]
    public async Task DeletedFile_KeepsRollup_RemovesFileState()
    {
        var path = Path.Combine(ProjectsDir, "proj-a", "session.jsonl");
        File.WriteAllText(path, Fixture("main-session.jsonl"));
        using var service = NewService();
        await service.ScanAllAsync();

        File.Delete(path); // 보존기간 청소 시뮬레이션
        await service.ScanAllAsync();

        Assert.Equal(434, service.CurrentRollup.Days["2026-07-08"].TotalTokens.Output);
    }

    [Fact]
    public async Task RollupUpdated_FiresOnChange_NotOnNoop()
    {
        File.WriteAllText(Path.Combine(ProjectsDir, "proj-a", "session.jsonl"), Fixture("main-session.jsonl"));
        using var service = NewService();
        var fired = 0;
        service.RollupUpdated += _ => fired++;

        await service.ScanAllAsync();
        Assert.Equal(1, fired);

        await service.ScanAllAsync(); // 변경 없음
        Assert.Equal(1, fired);
    }
}

public sealed class LiveSessionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cum-live-" + Guid.NewGuid().ToString("N"));

    public LiveSessionServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteSession(int pid, string sessionId, string cwd) =>
        File.WriteAllText(Path.Combine(_dir, pid + ".json"), $$"""
        { "pid": {{pid}}, "sessionId": "{{sessionId}}", "cwd": "{{cwd.Replace("\\", "\\\\")}}",
          "startedAt": 1783500000000, "version": "2.1.204", "kind": "interactive", "status": "idle" }
        """);

    [Fact]
    public void GetLive_ReturnsOnlyAliveProcesses()
    {
        WriteSession(111, "sess-alive", @"d:\proj\alpha");
        WriteSession(222, "sess-dead", @"d:\proj\beta");
        var service = new LiveSessionService(_dir, isProcessAlive: pid => pid == 111);

        var live = service.GetLive();

        Assert.Single(live);
        Assert.Equal("sess-alive", live[0].SessionId);
        Assert.Equal(@"d:\proj\alpha", live[0].Cwd);
    }

    [Fact]
    public void GetLive_MissingDirectory_ReturnsEmpty()
    {
        var service = new LiveSessionService(Path.Combine(_dir, "nope"), isProcessAlive: _ => true);

        Assert.Empty(service.GetLive());
    }
}
