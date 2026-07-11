namespace ClaudeUsageMonitor.App.Messaging;

/// <summary>인앱 업데이트 진행 창 열기 요청 — 트레이 메뉴/설정 페이지 어느 쪽에서 눌러도
/// App이 단일 창 소유로 처리한다 (이미 열려 있으면 Activate — 수정 필수 ③).</summary>
public sealed record OpenUpdateWindowMessage;
