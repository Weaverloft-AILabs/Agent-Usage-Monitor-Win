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

    private readonly object _loadLock = new();

    /// <summary>_rollup 구조 변경(Apply/Prune)과 UI 스레드 읽기 스냅샷을 상호배제. 스캔 전체가 아니라
    /// 이벤트/스냅샷 단위로만 잡아 UI를 오래 막지 않는다(_scanGate와 별개, 중첩 안 함).</summary>
    private readonly object _rollupLock = new();

    /// <summary>스캔 전이라도 디스크의 기존 롤업을 즉시 제공 (대시보드 초기 표시용).</summary>
    public RollupData CurrentRollup
    {
        get
        {
            EnsureLoaded();
            return SnapshotRollup();
        }
    }

    /// <summary>_rollupLock 하에 읽기용 깊은 복사 — UI가 인제스트의 in-place 변경과 충돌 없이 열거하도록.</summary>
    private RollupData SnapshotRollup()
    {
        lock (_rollupLock)
        {
            return _rollup.SnapshotForRead();
        }
    }

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
                // 결정적 순서로 처리 — 파일 간 동일 message.id가 상이하게 존재하면 last-wins의 승자가
                // OS별 열거 순서에 좌우돼 총계가 비결정적이 된다. 경로 정렬로 재현성을 보장.
                var files = Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                foreach (var path in files)
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
                var now = DateTimeOffset.UtcNow;
                lock (_rollupLock)
                {
                    _aggregator!.PruneApplied(now);
                }
                try
                {
                    // rollup을 먼저 저장 — state만 실패해도 재시작 시 Applied 맵이 재적용을 멱등 처리.
                    _rollupStore.Save(_rollup);
                    _stateStore.Save(_fileStates);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 디스크 잠김/권한 오류: 이번 사이클 영속화만 건너뛰고 인메모리 상태 유지(다음 스캔 재시도).
                    // 서비스를 폴트시키지 않는다(기본 StopHost로 백그라운드 전체 정지 방지).
                }
                RollupUpdated?.Invoke(SnapshotRollup());
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SafeScanAsync(stoppingToken).ConfigureAwait(false); // 백필
        RollupUpdated?.Invoke(SnapshotRollup()); // 변경 유무와 무관하게 초기 1회 발행 (UI 초기 표시)
        StartWatcher();

        using var timer = new PeriodicTimer(_rescanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SafeScanAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    /// <summary>한 사이클 스캔 실패가 서비스를 폴트(기본 StopHost)시키지 않도록 격리. EnsureLoaded 실패도 다음 틱에 재시도.</summary>
    private async Task SafeScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ScanAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 종료 신호는 전파
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 이번 사이클만 실패 처리 — 다음 재스캔 틱이 복구를 시도.
        }
    }

    private void EnsureLoaded()
    {
        if (_aggregator is not null)
        {
            return;
        }
        lock (_loadLock)
        {
            if (_aggregator is not null)
            {
                return;
            }
            _fileStates = _stateStore.Load();
            _rollup = _rollupStore.Load();
            _aggregator = new RollupAggregator(_rollup, _timeZone);
        }
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
            // 잠긴/사라진 파일은 이 파일만 건너뛰고 다음 패스에서 재시도.
            // 그 외 예기치 못한 예외는 삼키지 않고 상위(SafeScanAsync)의 사이클 단위 격리에 맡긴다
            // — 체계적 결함이 파일별로 조용히 영구 무시되는 것을 피한다.
            return false;
        }

        var changed = false;
        foreach (var line in lines)
        {
            if (JsonlParser.TryParseLine(line, out var parsed))
            {
                lock (_rollupLock)
                {
                    changed |= _aggregator!.Apply(parsed!.ToUsageEvent());
                }
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 디바운스 스캔 실패는 주기 재스캔이 흡수 — 미관측 Task 예외 방지.
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
