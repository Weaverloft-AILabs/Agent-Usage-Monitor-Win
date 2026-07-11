namespace ClaudeUsageMonitor.UpdateUi;

/// <summary>진행 보고 — StepIndex는 4점 타임라인 슬롯(0..3). Percent/Bytes는 다운로드(0단계)에서만 의미.</summary>
public sealed record UpdateFlowProgress(int StepIndex, double? Percent = null, long? Bytes = null);

/// <summary>흐름 결과. PendingRestart = 흐름이 프로세스 재시작을 예약했고 호스트가 곧 스스로 종료해야 함
/// (앱 인앱 델타 경로 — 진행 창은 여기서 프로세스와 함께 소멸하고, 재시작 후 Done 카드가 이어받는다).</summary>
public sealed record UpdateFlowResult(bool Success, InstallFailure? Failure = null, bool PendingRestart = false);

/// <summary>업데이트/설치 백엔드 흐름 — 앱=Velopack 델타, 인스톨러=Setup.exe 실행.
/// 표현(진행 창·상태머신)은 UpdateFlowViewModel 한 벌이 담당하고, 의미가 다른 백엔드만 이 뒤에서 갈린다.</summary>
public interface IUpdateFlow
{
    /// <summary>흐름 실행. 사용자 취소는 OperationCanceledException으로 전파(VM이 준비 상태로 복귀),
    /// 그 외 실패는 InstallFailure를 담은 결과로 반환한다.</summary>
    Task<UpdateFlowResult> RunAsync(IProgress<UpdateFlowProgress> progress, CancellationToken cancellationToken);
}
