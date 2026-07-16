using ClaudeUsageMonitor.App.Interop;
using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.Core;
using ClaudeUsageMonitor.Core.Models;
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
    /// <summary>저장소 페이지 (업데이트 창 푸터 링크 등 — URL 단일 소스).</summary>
    public const string RepoUrl = "https://github.com/Weaverloft-AILabs/Agent-Usage-Monitor-Win";

    /// <summary>메이저 점프 시 수동 다운로드 안내에 쓰는 릴리스 페이지 주소.</summary>
    public const string ReleasesPageUrl = RepoUrl + "/releases/latest";
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(2);
    /// <summary>유휴 자동 업데이트 발화 하한 — 마지막 입력 후 이 시간 이상이면 유휴로 본다.</summary>
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(30);

    /// <summary>안정 릴리스 채널. 인앱 업데이트는 이 채널의 releases.win.json만 확인한다 —
    /// 베타/프리릴리스는 별도 채널(beta)로 발행되어 안정 설치본엔 뜨지 않는다.
    /// ExplicitChannel로 고정해 (혹시 베타 설치본을 깐 경우의) 채널 stickiness도 차단.</summary>
    private const string StableChannel = "win";
    private const string BetaChannel = "beta";

    private readonly UpdateManager _manager = new(
        new GithubSource(RepoUrl, null, prerelease: false),
        new UpdateOptions { ExplicitChannel = StableChannel });

    /// <summary>베타 채널 매니저 — 설정 ReceiveBetaUpdates가 켜졌을 때만 사용. prerelease:true로 GitHub prerelease
    /// 릴리스도 읽고 ExplicitChannel="beta"로 releases.beta.json을 본다. 안정(win)과 함께 확인해 더 높은 버전을 제공.</summary>
    private readonly UpdateManager _betaManager = new(
        new GithubSource(RepoUrl, null, prerelease: true),
        new UpdateOptions { ExplicitChannel = BetaChannel });

    /// <summary>발견된 업데이트와 그 채널 매니저의 원자 쌍 — 참조 1개로 함께 교체돼 TOCTOU로 찢어지지 않는다.</summary>
    private sealed record AvailableUpdate(UpdateInfo Info, UpdateManager Manager);

    /// <summary>마지막 CheckAsync 결과(업데이트+채널 매니저). null이면 업데이트 없음.</summary>
    private AvailableUpdate? _available;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MonitorSettings _settings;

    /// <summary>적용 직전 기록되는 재시작 연속성 마커 (무창 구간의 결과를 재시작이 이어받음).</summary>
    public UpdatePendingMarker PendingMarker { get; }

    public UpdateService(MonitorPaths paths, MonitorSettings settings)
    {
        PendingMarker = new UpdatePendingMarker(paths.DataDirectory);
        _settings = settings;
    }

    /// <summary>설치판에서 실행 중인지 (포터블/개발 실행이면 업데이트 불가).</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>현재 실행 중인 버전 텍스트.</summary>
    public string CurrentVersionText =>
        _manager.IsInstalled && _manager.CurrentVersion is { } v
            ? v.ToString()
            : System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>마지막 확인에서 발견된 업데이트 (없으면 null).</summary>
    public UpdateInfo? Available => _available?.Info;

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
            if (_available is not { } snapshot)
            {
                return null;
            }

            var target = snapshot.Info.TargetFullRelease.Version.ToString();
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
                var found = await CheckAsync(stoppingToken).ConfigureAwait(false);
                MaybeAutoApplyWhenIdle(found);
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
            var winInfo = await _manager.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            var chosen = winInfo;
            var chosenManager = _manager;

            // 베타 옵트인: beta 채널도 확인해 정식+베타 중 더 높은 버전을 제공(승격 후 정식판을 못 받는 구간 방지).
            // 베타 채널 일시 오류는 무시하고 안정 결과를 유지.
            if (_settings.ReceiveBetaUpdates)
            {
                try
                {
                    var betaInfo = await _betaManager.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
                    if (betaInfo is not null
                        && (winInfo is null
                            || betaInfo.TargetFullRelease.Version > winInfo.TargetFullRelease.Version))
                    {
                        chosen = betaInfo;
                        chosenManager = _betaManager;
                    }
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.IO.IOException)
                {
                    // 베타 채널 네트워크 오류 — 안정 결과만 사용
                }
            }

            _available = chosen is not null ? new AvailableUpdate(chosen, chosenManager) : null;
            if (chosen is not null)
            {
                // 버전 문자열과 메이저 점프 여부를 같은 UpdateInfo에서 계산해 원자 쌍으로 발행
                var target = chosen.TargetFullRelease.Version.ToString();
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
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 종료 중 Dispose와 경합 — 무시(앱이 이미 내려가는 중).
            }
        }
    }

    /// <summary>주기 체크 완료 시 유휴 자동 업데이트 판정. 설정 ON + 업데이트 존재 + 메이저 점프 아님 +
    /// 유휴(마지막 입력 후 30분)이면 기존 수동 설치와 동일한 공용 진행 창 흐름을 발화한다
    /// (App 핸들러가 TryBeginInstall→즉시 적용·재시작; UpdatePendingMarker가 재시작 연속성 처리).
    /// 설정 OFF(기본)면 유휴 syscall조차 하지 않는다.</summary>
    private void MaybeAutoApplyWhenIdle(bool updateFound)
    {
        if (!_settings.AutoUpdateWhenIdle)
        {
            return;
        }

        var isIdle = SystemIdle.GetIdleDuration() >= IdleThreshold;
        if (AutoUpdateGate.ShouldAutoApply(_settings.AutoUpdateWhenIdle, updateFound, AvailableIsMajorJump, isIdle))
        {
            // App가 Dispatcher로 마샬해 ShowUpdateWindow 실행 — TryBeginInstall이 null이면(경합) no-op
            WeakReferenceMessenger.Default.Send(new OpenUpdateWindowMessage());
        }
    }

    /// <summary>인앱 설치 흐름 시작 시도 — 공용 진행 창(UpdateProgressWindow)에 넘길 흐름을 만든다.
    /// 메이저 점프/업데이트 없음이면 null (창을 열지 않고 기존 수동 다운로드 안내 유지).
    /// 판정은 캡처한 update 기준 — Available 재독취는 동시 CheckAsync와의 TOCTOU로
    /// 다른 UpdateInfo를 설치할 수 있음 (구 DownloadAndApplyAsync의 가드 불변식을 그대로 계승).</summary>
    public VelopackUpdateFlow? TryBeginInstall()
    {
        // 업데이트와 채널 매니저를 원자 쌍으로 1회 캡처 — 동시 CheckAsync가 쌍을 교체해도
        // 서로 다른 UpdateInfo/매니저 조합으로 설치하는 일이 없다.
        var snapshot = _available;
        if (snapshot is null
            || UpdateGate.IsMajorJump(CurrentVersionText, snapshot.Info.TargetFullRelease.Version.ToString()))
        {
            return null;
        }

        return new VelopackUpdateFlow(snapshot.Manager, _gate, snapshot.Info, PendingMarker);
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
