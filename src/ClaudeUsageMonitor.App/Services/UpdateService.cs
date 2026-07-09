using ClaudeUsageMonitor.App.Messaging;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Velopack;
using Velopack.Sources;

namespace ClaudeUsageMonitor.App.Services;

/// <summary>
/// GitHub Releases에서 주기적으로 최신 버전을 확인하고(시작 1분 후 + 4시간 간격),
/// 업데이트 발견 시 메신저로 알린다. 설치는 위젯 메뉴/설정 페이지에서 트리거.
/// 포터블/개발 실행(비설치)에서는 비활성.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    // 2026-07-09 저장소 이관: 구 weavernoia1223/agent_usage_monitor → 신 Weaverloft-AILabs 조직.
    // 구 저장소에는 v1.0.12 브리지 릴리스까지만 게시 — 이전 설치본이 그걸 타고 여기로 넘어온다.
    private const string RepoUrl = "https://github.com/Weaverloft-AILabs/Agent-Usage-Monitor-Win";
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, prerelease: false));
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>설치판에서 실행 중인지 (포터블/개발 실행이면 업데이트 불가).</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>현재 실행 중인 버전 텍스트.</summary>
    public string CurrentVersionText =>
        _manager.IsInstalled && _manager.CurrentVersion is { } v
            ? v.ToString()
            : System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>마지막 확인에서 발견된 업데이트 (없으면 null).</summary>
    public UpdateInfo? Available { get; private set; }

    public string? AvailableVersionText => Available?.TargetFullRelease.Version.ToString();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsInstalled)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    /// <summary>수동/주기 확인. 업데이트 발견 시 UpdateAvailableMessage 발행.</summary>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            Available = info;
            if (info is not null)
            {
                WeakReferenceMessenger.Default.Send(
                    new UpdateAvailableMessage(info.TargetFullRelease.Version.ToString()));
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException)
        {
            return false; // 네트워크 오류 — 다음 주기에 재시도
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>업데이트 다운로드 후 재시작하며 적용. progress는 0~100.</summary>
    public async Task DownloadAndApplyAsync(Action<int>? progress = null)
    {
        if (Available is not { } update)
        {
            return;
        }

        await _manager.DownloadUpdatesAsync(update, progress).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(update);
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
