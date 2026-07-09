using System.IO;
using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.Core.RateLimit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;

namespace ClaudeUsageMonitor.App.Services;

/// <summary>
/// .credentials.json 변경을 감시해 로그인 계정(uuid)이 바뀌면 AccountChangedMessage를 발행하고
/// 즉시 재폴링을 트리거한다. 토큰 회전(같은 계정)은 uuid가 동일하므로 무시된다.
/// </summary>
public sealed class AccountWatchService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);

    private readonly ProfileClient _profile;
    private readonly CredentialsReader _credentials;
    private readonly RateLimitPollingService _polling;
    private readonly AccountTracker _tracker = new();
    private readonly SemaphoreSlim _changed = new(0);
    private FileSystemWatcher? _watcher;

    public AccountWatchService(ProfileClient profile, CredentialsReader credentials, RateLimitPollingService polling)
    {
        _profile = profile;
        _credentials = credentials;
        _polling = polling;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); // 시작 직후 폴링과 경합 방지
            _tracker.Update(await _profile.FetchAccountUuidAsync(stoppingToken).ConfigureAwait(false));
            StartWatcher();

            while (!stoppingToken.IsCancellationRequested)
            {
                await _changed.WaitAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Debounce, stoppingToken).ConfigureAwait(false);
                while (_changed.Wait(0))
                {
                    // 디바운스 동안 몰린 이벤트 드레인
                }

                var uuid = await _profile.FetchAccountUuidAsync(stoppingToken).ConfigureAwait(false);
                if (_tracker.Update(uuid))
                {
                    WeakReferenceMessenger.Default.Send(new AccountChangedMessage());
                    _polling.TriggerNow(); // 새 계정 사용량 즉시 반영
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    private void StartWatcher()
    {
        var path = _credentials.CredentialsPath;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => _changed.Release();
        _watcher.Created += (_, _) => _changed.Release();
        _watcher.Renamed += (_, _) => _changed.Release();
        _watcher.EnableRaisingEvents = true;
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _changed.Dispose();
        base.Dispose();
    }
}
