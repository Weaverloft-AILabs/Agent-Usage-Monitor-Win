using System.Text;
using ClaudeUsageMonitor.Core.Ingest;
using ClaudeUsageMonitor.Core.Rollup;
using ClaudeUsageMonitor.Core.Storage;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

/// <summary>
/// 데이터 무결성 내구성 (리스크 감사 Batch 1):
/// 원자적 저장의 세대 백업/손상 폴백, 동일 경로 파일 교체 감지, 커버리지 갭 판정.
/// </summary>
public sealed class DataDurabilityTests : IDisposable
{
    private sealed record Box(string Value);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cum-dur-" + Guid.NewGuid().ToString("N"));

    public DataDurabilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Save_KeepsPreviousGoodCopy_AsBackup()
    {
        var path = P("data.json");
        AtomicJsonFile.Save(path, new Box("v1"));
        AtomicJsonFile.Save(path, new Box("v2"));

        Assert.True(File.Exists(path + ".bak"), "이전 세대가 .bak으로 보존되어야 함");
        var bak = AtomicJsonFile.Load<Box>(path + ".bak");
        Assert.Equal("v1", bak!.Value);
        Assert.Equal("v2", AtomicJsonFile.Load<Box>(path)!.Value);
    }

    [Fact]
    public void Load_FallsBackToBackup_WhenPrimaryCorrupt()
    {
        var path = P("data.json");
        AtomicJsonFile.Save(path, new Box("good1"));
        AtomicJsonFile.Save(path, new Box("good2")); // path=good2, .bak=good1

        // 주 파일 손상 (부분 기록 시뮬레이션)
        File.WriteAllText(path, "{ this is not valid json ");

        var loaded = AtomicJsonFile.Load<Box>(path);
        Assert.NotNull(loaded);
        Assert.Equal("good1", loaded!.Value); // 침묵 리셋 대신 백업 복구
    }

    [Fact]
    public void Load_ReturnsNull_WhenPrimaryAndBackupBothCorrupt()
    {
        var path = P("data.json");
        AtomicJsonFile.Save(path, new Box("x"));
        AtomicJsonFile.Save(path, new Box("y"));
        File.WriteAllText(path, "corrupt");
        File.WriteAllText(path + ".bak", "corrupt");

        Assert.Null(AtomicJsonFile.Load<Box>(path));
    }

    [Fact]
    public void Load_ReturnsNull_WhenNothingExists()
    {
        Assert.Null(AtomicJsonFile.Load<Box>(P("missing.json")));
    }

    [Fact]
    public void RollupStore_RecoversFromBackup_WhenPrimaryCorrupt()
    {
        var store = new RollupStore(_dir);
        var data = new RollupData { CoverageStart = "2026-07-01" };
        store.Save(data);
        store.Save(new RollupData { CoverageStart = "2026-07-02" }); // primary=07-02, bak=07-01

        File.WriteAllText(Path.Combine(_dir, "rollups.json"), "{ broken");

        var recovered = store.Load();
        Assert.Equal("2026-07-01", recovered.CoverageStart); // 빈 롤업 리셋 아님
    }

    [Fact]
    public void Reader_DetectsSamePathReplacement_EvenWhenLonger()
    {
        var path = Path.Combine(_dir, "live.jsonl");
        var reader = new IncrementalFileReader();
        var state = new FileIngestState();

        File.WriteAllText(path, "alpha\nbeta\n", new UTF8Encoding(false));
        var first = reader.ReadNewLines(path, state);
        Assert.Equal(new[] { "alpha", "beta" }, first);

        // 같은 경로를 완전히 다른(그리고 더 긴) 내용으로 교체 — 길이만 보면 성장으로 오인
        File.WriteAllText(path, "gamma\ndelta\nepsilon\n", new UTF8Encoding(false));
        var second = reader.ReadNewLines(path, state);

        // 교체를 감지해 처음부터 다시 읽어야 함 (append로 오인해 뒷부분만 읽으면 안 됨)
        Assert.Equal(new[] { "gamma", "delta", "epsilon" }, second);
    }

    [Fact]
    public void Reader_PlainAppend_DoesNotReset()
    {
        var path = Path.Combine(_dir, "live.jsonl");
        var reader = new IncrementalFileReader();
        var state = new FileIngestState();

        File.WriteAllText(path, "one\ntwo\n", new UTF8Encoding(false));
        reader.ReadNewLines(path, state);

        File.AppendAllText(path, "three\n");
        var appended = reader.ReadNewLines(path, state);

        Assert.Equal(new[] { "three" }, appended); // 교체 오탐 없이 증분만
    }

    [Theory]
    [InlineData(null, 30, false)]        // 최초 실행 — 이전 커버리지 없음
    [InlineData(5, 30, false)]           // 5일 오프라인 — 보존창 내, 손실 없음
    [InlineData(29, 30, false)]          // 경계 직전
    [InlineData(30, 30, true)]           // 보존창 = 오프라인 → 잠재 손실
    [InlineData(45, 30, true)]           // 장기 오프라인
    public void CoverageGap_FlagsOnlyLongDowntime(int? offlineDays, int retention, bool expected)
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? last = offlineDays is null ? null : now.AddDays(-offlineDays.Value);

        Assert.Equal(expected, CoverageGap.HasPotentialGap(last, now, retention));
    }
}
