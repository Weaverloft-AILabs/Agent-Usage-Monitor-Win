using ClaudeUsageMonitor.Core.Rollup;
using Microsoft.Extensions.Hosting;

namespace ClaudeUsageMonitor.Core.Ingest;

/// <summary>
/// JSONL 인제스트 오케스트레이션:
/// 시작 시 전체 백필 → FileSystemWatcher(디바운스) + 60초 주기 재스캔(FSW 이벤트 유실 대비).
/// 파이프라인: IncrementalFileReader → JsonlParser → RollupAggregator(Applied 맵이 메시지 단위 멱등성 보장).
/// </summary>
public sealed class IngestService : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _projectsRoot;
    private readonly IncrementalFileReader _reader = new();
    private readonly IngestStateStore _stateStore;
    private readonly RollupStore _rollupStore;
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeSpan _rescanInterval;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private Dictionary<string, FileIngestState> _fileStates = new(StringComparer.OrdinalIgnoreCase);
    private RollupData _rollup = new();
    private RollupAggregator? _aggregator;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;

    public event Action<RollupData>? RollupUpdated;

    public RollupData CurrentRollup => _rollup;

    public IngestService(
        string projectsRoot,
        string dataDirectory,
        TimeZoneInfo? timeZone = null,
        TimeSpan? rescanInterval = null)
    {
        _projectsRoot = projectsRoot;
        _stateStore = new IngestStateStore(dataDirectory);
        _rollupStore = new RollupStore(dataDirectory);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _rescanInterval = rescanInterval ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>테스트/즉시 새로고침용. 상태 로드가 안 됐으면 로드부터 수행.</summary>
    public async Task ScanAllAsync(CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var changed = false;

            if (Directory.Exists(_projectsRoot))
            {
                foreach (var path in Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    changed |= IngestFile(path);
                }
            }

            // 삭제된 파일(보존기간 청소)의 상태 정리 — 롤업 수치는 유지
            var missing = _fileStates.Keys.Where(p => !File.Exists(p)).ToList();
            foreach (var path in missing)
            {
                _fileStates.Remove(path);
                changed = true;
            }

            if (changed)
            {
                _aggregator!.PruneApplied(DateTimeOffset.UtcNow);
                _rollupStore.Save(_rollup);
                _stateStore.Save(_fileStates);
                RollupUpdated?.Invoke(_rollup);
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ScanAllAsync(stoppingToken).ConfigureAwait(false); // 백필
        StartWatcher();

        using var timer = new PeriodicTimer(_rescanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await ScanAllAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    private void EnsureLoaded()
    {
        if (_aggregator is not null)
        {
            return;
        }
        _fileStates = _stateStore.Load();
        _rollup = _rollupStore.Load();
        _aggregator = new RollupAggregator(_rollup, _timeZone);
    }

    private bool IngestFile(string path)
    {
        if (!_fileStates.TryGetValue(path, out var state))
        {
            state = new FileIngestState();
            _fileStates[path] = state;
        }

        IReadOnlyList<string> lines;
        try
        {
            lines = _reader.ReadNewLines(path, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // 잠긴/사라진 파일은 다음 패스에서 재시도
        }

        var changed = false;
        foreach (var line in lines)
        {
            if (JsonlParser.TryParseLine(line, out var parsed))
            {
                changed |= _aggregator!.Apply(parsed!.ToUsageEvent());
            }
        }
        return changed;
    }

    private void StartWatcher()
    {
        if (!Directory.Exists(_projectsRoot))
        {
            return; // 재스캔 타이머가 디렉터리 생성을 감지
        }

        _watcher = new FileSystemWatcher(_projectsRoot, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => ScheduleDebouncedScan();
        _watcher.Created += (_, _) => ScheduleDebouncedScan();
        _watcher.Renamed += (_, _) => ScheduleDebouncedScan();
        _watcher.Error += (_, _) => ScheduleDebouncedScan(); // 버퍼 오버플로 → 전체 재스캔
        _watcher.EnableRaisingEvents = true;
    }

    private void ScheduleDebouncedScan()
    {
        _debounceTimer ??= new Timer(_ => _ = SafeScan(), null, Timeout.Infinite, Timeout.Infinite);
        _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);

        async Task SafeScan()
        {
            try
            {
                await ScanAllAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        _scanGate.Dispose();
        base.Dispose();
    }
}
