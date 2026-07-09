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
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var state = await _client.FetchAsync(now, stoppingToken).ConfigureAwait(false);
            Current = state;
            if (state.Snapshot is { IsStale: false } fresh)
            {
                _estimator.Add(fresh.FetchedAt, fresh.FiveHourPct); // 소진 속도 표본 (stale 캐시는 제외)
            }
            WeakReferenceMessenger.Default.Send(new RateLimitUpdatedMessage(state));

            var delay = TimeSpan.FromSeconds(_settings.PollIntervalSeconds);
            if (state.NextPollAt is { } next)
            {
                var backoff = next - DateTimeOffset.UtcNow;
                if (backoff > delay)
                {
                    delay = backoff;
                }
            }

            try
            {
                var wakeTask = _wake.WaitAsync(stoppingToken);
                var completed = await Task.WhenAny(Task.Delay(delay, stoppingToken), wakeTask).ConfigureAwait(false);
                if (completed != wakeTask)
                {
                    // 타임아웃 경로 — wake 대기 취소 방지를 위해 세마포어를 소모하지 않음
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
