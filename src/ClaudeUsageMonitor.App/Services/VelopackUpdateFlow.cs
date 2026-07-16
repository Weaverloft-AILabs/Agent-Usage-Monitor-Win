using System.IO;
using System.Net.Http;
using ClaudeUsageMonitor.Core.Updates;
using ClaudeUsageMonitor.UpdateUi;
using Velopack;

namespace ClaudeUsageMonitor.App.Services;

/// <summary>
/// 인앱 델타 업데이트 백엔드 흐름 — 공용 진행 창(UpdateFlowViewModel) 뒤에서 Velopack을 구동한다.
/// 단계: 0 다운로드(실측 %) → 1 검증(DownloadUpdatesAsync 반환 = 체크섬/델타 스테이징 완료) →
/// 마커 기록 → 2 설치(WaitExitThenApplyUpdates 예약) → PendingRestart 반환.
/// 호스트는 PendingRestart 수신 시 Application.Shutdown()으로 graceful 종료해야 하며
/// (App.OnExit의 임베드/트레이/뮤텍스 해제가 정상 실행), Update.exe가 종료를 최대 60초
/// 기다렸다가 델타를 적용하고 --update-done 인자로 앱을 재시작한다.
/// </summary>
public sealed class VelopackUpdateFlow : IUpdateFlow
{
    private readonly UpdateManager _manager;
    private readonly SemaphoreSlim _gate;
    private readonly UpdateInfo _captured;
    private readonly UpdatePendingMarker _marker;

    /// <summary>이 흐름이 설치할 대상 버전 (캡처 시점 고정 — TOCTOU 면역).</summary>
    public string TargetVersion { get; }

    internal VelopackUpdateFlow(
        UpdateManager manager, SemaphoreSlim gate, UpdateInfo captured, UpdatePendingMarker marker)
    {
        _manager = manager;
        _gate = gate;
        _captured = captured;
        _marker = marker;
        TargetVersion = captured.TargetFullRelease.Version.ToString();
    }

    public async Task<UpdateFlowResult> RunAsync(
        IProgress<UpdateFlowProgress> progress, CancellationToken cancellationToken)
    {
        // CheckAsync와 같은 세마포어로 직렬화 — Velopack 글로벌 업데이트 락 경합을 인앱 내부에서 차단
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress.Report(new UpdateFlowProgress(0, 0));
            await _manager.DownloadUpdatesAsync(
                    _captured, p => progress.Report(new UpdateFlowProgress(0, p)), cancellationToken)
                .ConfigureAwait(false);

            // 다운로드 반환 = 체크섬 검증 + 델타 스테이징 완료 — 검증 단계는 즉시 통과
            progress.Report(new UpdateFlowProgress(1));

            // 다운로드 완료 후 적용 예약(WaitExitThenApplyUpdates) 직전까지는 취소를 존중한다 —
            // 그 지점 이후는 되돌릴 수 없으므로, 검증/설치 단계에서 누른 취소가 무시되지 않게 한 번 더 확인.
            cancellationToken.ThrowIfCancellationRequested();

            // 무창 구간(적용~재시작)의 결과를 외부화 — 재시작/다음 실행이 이어받는다.
            // 기록 실패는 업데이트를 막지 않는다 (연속성 표시만 저하).
            _marker.Write(TargetVersion);
            progress.Report(new UpdateFlowProgress(2));

            try
            {
                _manager.WaitExitThenApplyUpdates(
                    _captured.TargetFullRelease, silent: true, restart: true,
                    restartArgs: ["--update-done", TargetVersion]);
            }
            catch
            {
                _marker.Delete(); // 적용 예약 실패 — 스테일 마커의 오경보 차단 (수정 필수 ②)
                throw;
            }

            return new UpdateFlowResult(true, PendingRestart: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 사용자 취소 — VM이 준비 상태로 복귀 (마커는 아직 미기록 구간)
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new UpdateFlowResult(false, new InstallFailure(
                InstallFailureClass.Network,
                "download failed: " + ex.Message,
                "네트워크 상태를 확인한 뒤 다시 시도해 주세요."));
        }
        catch (Exception ex)
        {
            // Velopack 글로벌 업데이트 락 실패(다른 업데이트 작업 진행 중) 포함 — 타입명으로 분류
            var lockContention = ex.GetType().Name.Contains("Lock", StringComparison.OrdinalIgnoreCase);
            return new UpdateFlowResult(false, new InstallFailure(
                InstallFailureClass.Unknown,
                ex.GetType().Name + ": " + ex.Message,
                lockContention
                    ? "다른 업데이트 작업이 진행 중입니다 — 잠시 후 다시 시도해 주세요."
                    : "잠시 후 다시 시도하거나 로그를 확인해 주세요."));
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 종료 중 UpdateService.Dispose와 경합 — 무시.
            }
        }
    }
}
