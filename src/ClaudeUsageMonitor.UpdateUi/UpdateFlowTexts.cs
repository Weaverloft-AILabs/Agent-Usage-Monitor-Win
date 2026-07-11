namespace ClaudeUsageMonitor.UpdateUi;

/// <summary>진행 창의 문구·단계 구성 — 호스트(인스톨러/앱)가 흐름 의미에 맞게 주입한다.
/// 인스톨러는 감지(DetectAsync) 결과에 따라 동사가 바뀌므로 Texts를 통째로 교체할 수 있다.</summary>
public sealed record UpdateFlowTexts
{
    public required string WindowTitle { get; init; }

    /// <summary>4점 타임라인 라벨 (인스톨러: 다운로드/보안 검사/설치/완료, 앱: 다운로드/검증/설치/완료).</summary>
    public required IReadOnlyList<string> StepLabels { get; init; }

    /// <summary>단계별 진행 헤드라인 (StepLabels와 같은 인덱스).</summary>
    public required IReadOnlyList<string> ProgressHeadlines { get; init; }

    public required string DoneHeadline { get; init; }
    public required string DoneSecondary { get; init; }
    public required string DoneButton { get; init; }
    public required string ErrorHeadline { get; init; }

    /// <summary>지연 힌트 문구. SlowHintStepIndex 단계가 15초를 넘기면 노출 (-1 = 비활성).</summary>
    public string SlowHintText { get; init; } = "";
    public int SlowHintStepIndex { get; init; } = -1;

    /// <summary>이 단계 인덱스까지만 취소 허용 (인스톨러: 보안 검사(1)까지 — 디스크 쓰기 전).</summary>
    public int LastCancellableStepIndex { get; init; } = 1;

    /// <summary>실측이 없는 단계의 크리프 게이지 목표 (0 = 크리프 없음). 인덱스 = 단계.</summary>
    public IReadOnlyList<double> CreepTargets { get; init; } = [0, 74, 94, 0];
}
