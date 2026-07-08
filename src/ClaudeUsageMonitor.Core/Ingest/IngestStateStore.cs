using ClaudeUsageMonitor.Core.Storage;

namespace ClaudeUsageMonitor.Core.Ingest;

/// <summary>파일 경로 → FileIngestState 맵의 영속화 (ingest-state.json).</summary>
public sealed class IngestStateStore
{
    private readonly string _path;

    public IngestStateStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "ingest-state.json");
    }

    public Dictionary<string, FileIngestState> Load() =>
        AtomicJsonFile.Load<Dictionary<string, FileIngestState>>(_path)
        ?? new Dictionary<string, FileIngestState>(StringComparer.OrdinalIgnoreCase);

    public void Save(Dictionary<string, FileIngestState> state) =>
        AtomicJsonFile.Save(_path, state);
}
