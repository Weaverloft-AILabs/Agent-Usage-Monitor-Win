using System.IO;
using System.Net.Http;

namespace ClaudeUsageMonitor.Installer.Install;

/// <summary>
/// 인스톨러 백엔드 흐름 — InstallerViewModel.InstallAsync에서 이동(옵션 A 공용화).
/// Setup 탐색(① --setup 인자 ② 동반 파일 ③ GitHub 최신 다운로드) → SetupRunner --silent 실행.
/// 디스크 신호 기반 단계 전환·워치독은 SetupRunner(무변경, E2E 검증본)가 담당한다.
/// </summary>
public sealed class SetupInstallFlow(string? setupArgPath) : IUpdateFlow
{
    public async Task<UpdateFlowResult> RunAsync(
        IProgress<UpdateFlowProgress> progress, CancellationToken cancellationToken)
    {
        string? downloadedSetup = null;
        try
        {
            var setupPath = SetupLocator.Locate(setupArgPath, AppContext.BaseDirectory, File.Exists);
            if (setupPath is null)
            {
                setupPath = await DownloadSetupAsync(progress, cancellationToken);
                downloadedSetup = setupPath;
            }

            // 로컬 Setup 동반이면 다운로드 단계는 건너뜀 — SetupRunner의 첫 보고(SecurityScan)가
            // 단계를 1로 올리면서 다운로드 슬롯이 Done으로 마킹된다.
            var runner = new SetupRunner();
            var result = await runner.RunAsync(setupPath, new StageRelay(progress), cancellationToken);
            return result.Success
                ? new UpdateFlowResult(true)
                : new UpdateFlowResult(false, result.Failure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 사용자 취소 — VM이 준비 상태로 복귀
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Setup 프로세스 자체가 시작되지 못함 — AV 차단 가능성이 가장 높음
            return new UpdateFlowResult(false, new InstallFailure(
                InstallFailureClass.AntivirusHold,
                "Setup could not start: " + ex.Message,
                InstallDiagnostics.AntivirusAdvice));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // OperationCanceledException 여기 도달 = 사용자 취소가 아닌 HTTP 스톨/타임아웃
            return new UpdateFlowResult(false, InstallDiagnostics.FromDownloadError(ex));
        }
        finally
        {
            if (downloadedSetup is not null)
            {
                try
                {
                    File.Delete(downloadedSetup); // 실행 중이면 잠겨서 실패 — 무시 (임시 폴더 잔존만)
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static async Task<string> DownloadSetupAsync(
        IProgress<UpdateFlowProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new UpdateFlowProgress(0, 0));
        using var client = new GitHubReleaseClient();
        var url = await client.FindLatestSetupUrlAsync(cancellationToken)
            ?? throw new HttpRequestException("최신 릴리스에서 Setup 자산을 찾지 못했습니다");
        // 고유 임시 이름 — 동시 실행/이전 잔존 파일과의 충돌 방지 (사용 후 finally에서 삭제)
        var destination = Path.Combine(
            Path.GetTempPath(), $"AgentUsageMonitor-Setup-{Guid.NewGuid():N}.exe");
        await client.DownloadAsync(url, destination, (pct, bytes) =>
            progress.Report(new UpdateFlowProgress(0, pct, bytes)), cancellationToken);
        return destination;
    }

    /// <summary>SetupRunner의 StageProgress → 공용 UpdateFlowProgress 릴레이 (동기 — 컨텍스트 홉 없음).</summary>
    private sealed class StageRelay(IProgress<UpdateFlowProgress> inner) : IProgress<StageProgress>
    {
        public void Report(StageProgress value) => inner.Report(new UpdateFlowProgress((int)value.Stage));
    }
}
