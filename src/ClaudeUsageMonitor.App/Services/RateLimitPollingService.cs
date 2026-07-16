using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.RateLimit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;

namespace ClaudeUsageMonitor.App.Services;

/// <summary>
/// usage API를 설정 주기(하한 20초, 기본 180초)로 폴링하고 결과를 메신저로 발행.
/// 429 백오프 시 클라이언트가 알려준 NextPollAt까지 대기한다.
/// </summary>
public sealed class RateLimitPollingService : BackgroundService
{
    private readonly RateLimitClient _client;
    private readonly MonitorSettings _settings;
    private readonly BurnRateEstimator _estimator;
    private readonly SemaphoreSlim _wake = new(0);

    public RateLimitState? Current { get; private set; }

    public RateLimitPollingService(RateLimitClient client, MonitorSettings settings, BurnRateEstimator estimator)
    {
        _client = client;
        _settings = settings;
        _estimator = estimator;
    }

    /// <summary>트레이 "새로고침" — 대기를 깨워 즉시 폴링.</summary>
    public void TriggerNow() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                RateLimitState state;
                try
                {
                    state = await _client.FetchAsync(now, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 예기치 못한 클라이언트/파서 예외가 폴링 서비스를 폴트(기본 StopHost로 백그라운드 전체 정지)
                    // 시키지 않도록 방어 — 이번 주기만 Stale로 강등하고 다음 주기에 재시도.
                    state = _client.LastSnapshot is { } last
                        ? new RateLimitState(last with { IsStale = true }, RateLimitStatus.Stale, null)
                        : new RateLimitState(null, RateLimitStatus.Stale, null);
                }

                Current = state;
                if (state.Snapshot is { IsStale: false } fresh)
                {
                    _estimator.Add(fresh.FetchedAt, fresh.FiveHourPct); // 소진 속도 표본 (stale 캐시는 제외)
                }
                WeakReferenceMessenger.Default.Send(new RateLimitUpdatedMessage(state));

                await WaitForNextAsync(ComputeDelay(state), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    private TimeSpan ComputeDelay(RateLimitState state)
    {
        var delay = TimeSpan.FromSeconds(_settings.PollIntervalSeconds);
        if (state.NextPollAt is { } next)
        {
            var backoff = next - DateTimeOffset.UtcNow;
            if (backoff > delay)
            {
                delay = backoff;
            }
        }
        return delay;
    }

    /// <summary>
    /// 다음 주기까지 대기하되 TriggerNow(_wake) 시 즉시 깨어난다.
    /// ★ 진 대기(Delay 또는 WaitAsync)를 linked CTS로 취소해 세마포어 큐/타이머에서 제거한다 —
    /// 이전 구현은 타임아웃 시 WaitAsync가 큐에 orphan으로 남아, 이후 Release(TriggerNow)가 FIFO로
    /// 죽은 대기자에게 전달돼 현재 대기가 안 깨어나고 대기자가 무한 누적됐다.
    /// </summary>
    private async Task WaitForNextAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var wakeTask = _wake.WaitAsync(waitCts.Token);
        var delayTask = Task.Delay(delay, waitCts.Token);

        await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);

        waitCts.Cancel(); // 진 쪽 취소 → 세마포어 큐 orphan/타이머 잔류 방지
        await ObserveCanceled(wakeTask).ConfigureAwait(false);   // 이긴 wake면 세마포어 1 소모 완료
        await ObserveCanceled(delayTask).ConfigureAwait(false);

        stoppingToken.ThrowIfCancellationRequested();
    }

    private static async Task ObserveCanceled(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 진 대기의 취소는 정상 — 관찰만 하고 무시(미관측 Task 예외 방지).
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
