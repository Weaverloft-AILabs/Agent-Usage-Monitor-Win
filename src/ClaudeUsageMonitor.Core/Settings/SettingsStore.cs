using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Storage;

namespace ClaudeUsageMonitor.Core.Settings;

/// <summary>settings.json 로드/저장 (원자적).</summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "settings.json");
    }

    public MonitorSettings Load() => AtomicJsonFile.Load<MonitorSettings>(_path) ?? new MonitorSettings();

    public void Save(MonitorSettings settings) => AtomicJsonFile.Save(_path, settings);
}
