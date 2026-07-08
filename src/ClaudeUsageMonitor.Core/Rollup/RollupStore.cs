using ClaudeUsageMonitor.Core.Storage;

namespace ClaudeUsageMonitor.Core.Rollup;

/// <summary>rollups.json 로드/저장 (원자적 쓰기).</summary>
public sealed class RollupStore
{
    private readonly string _path;

    public RollupStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "rollups.json");
    }

    public RollupData Load() => AtomicJsonFile.Load<RollupData>(_path) ?? new RollupData();

    public void Save(RollupData data) => AtomicJsonFile.Save(_path, data);
}
