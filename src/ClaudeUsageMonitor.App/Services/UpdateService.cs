using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.Core.Updates;
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

    /// <summary>메이저 점프 시 수동 다운로드 안내에 쓰는 릴리스 페이지 주소.</summary>
    public const string ReleasesPageUrl = RepoUrl + "/releases/latest";
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

    /// <summary>발견된 업데이트가 메이저 버전 점프(예: 1.x→2.x)인지 — 인앱 설치 차단 대상.
    /// 메이저 업그레이드는 수동 설치 필요: UI는 설치 대신 릴리스 페이지 링크로 안내한다.</summary>
    public bool AvailableIsMajorJump =>
        Available is { } update
        && UpdateGate.IsMajorJump(CurrentVersionText, update.TargetFullRelease.Version.ToString());

    /// <summary>버전 문자열과 메이저 점프 여부를 Available 1회 읽기에서 계산한 원자 쌍 (UI 표시용).
    /// AvailableVersionText/AvailableIsMajorJump를 따로 읽으면 동시 CheckAsync로 쌍이 찢어질 수 있다.</summary>
    public (string Version, bool MajorJump)? AvailableSnapshot
    {
        get
        {
            if (Available is not { } update)
            {
                return null;
            }

            var target = update.TargetFullRelease.Version.ToString();
            return (target, UpdateGate.IsMajorJump(CurrentVersionText, target));
        }
    }

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
                // 버전 문자열과 메이저 점프 여부를 같은 UpdateInfo에서 계산해 원자 쌍으로 발행
                var target = info.TargetFullRelease.Version.ToString();
                WeakReferenceMessenger.Default.Send(new UpdateAvailableMessage(
                    target, UpdateGate.IsMajorJump(CurrentVersionText, target)));
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
        if (Available is not { } update
            || UpdateGate.IsMajorJump(CurrentVersionText, update.TargetFullRelease.Version.ToString()))
        {
            // 메이저 점프는 인앱 설치 금지. 판정은 캡처한 update 기준 —
            // Available 재독취는 동시 CheckAsync와의 TOCTOU로 다른 UpdateInfo를 설치할 수 있음
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
