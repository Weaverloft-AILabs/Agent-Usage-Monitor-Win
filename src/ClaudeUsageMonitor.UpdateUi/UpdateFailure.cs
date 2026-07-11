namespace ClaudeUsageMonitor.UpdateUi;

// 인스톨러 Install/InstallDiagnostics.cs에서 이동(2단계 A 공용화) — 앱 인앱 업데이트와 인스톨러의
// 오류 카드가 같은 실패 타입을 사용한다. 분류 로직(InstallDiagnostics)은 인스톨러에 남는다.
public enum InstallFailureClass
{
    /// <summary>다운로드/네트워크 실패.</summary>
    Network,

    /// <summary>Setup이 실행됐지만 velopack.log가 갱신되지 않음 — AV/EDR 진입 전 홀드 추정.</summary>
    AntivirusHold,

    /// <summary>Setup이 0이 아닌 코드로 종료 (로그에 원인 있음).</summary>
    SetupError,

    /// <summary>그 외 (exit 0인데 설치 산출물 없음 등).</summary>
    Unknown,
}

/// <summary>실패 원인(원문)과 사용자 조치 안내 — 디자인 카드 오류 상태의 실패 클래스 매핑.</summary>
public sealed record InstallFailure(InstallFailureClass Class, string Detail, string Advice);
