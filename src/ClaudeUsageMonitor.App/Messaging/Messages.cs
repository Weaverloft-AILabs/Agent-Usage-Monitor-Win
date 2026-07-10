using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Rollup;

namespace ClaudeUsageMonitor.App.Messaging;

/// <summary>usage API 폴링 결과 갱신 브로드캐스트.</summary>
public sealed record RateLimitUpdatedMessage(RateLimitState State);

/// <summary>롤업(로컬 통계) 갱신 브로드캐스트.</summary>
public sealed record RollupUpdatedMessage(RollupData Rollup);

/// <summary>위젯 표시 모드 변경.</summary>
public sealed record WidgetModeChangedMessage(WidgetMode Mode);

/// <summary>새 버전 발견 (GitHub Releases 주기 확인).
/// MajorJump = 메이저 버전 점프 — 인앱 설치 차단, 수동 설치 안내로 전환.
/// 버전과 점프 여부는 같은 UpdateInfo에서 계산된 원자 쌍이다 (수신 측에서 라이브 재독취 금지).</summary>
public sealed record UpdateAvailableMessage(string Version, bool MajorJump);

/// <summary>CLI 로그인 계정이 바뀜 (credentials 감시 + 프로필 uuid 대조).</summary>
public sealed record AccountChangedMessage;
