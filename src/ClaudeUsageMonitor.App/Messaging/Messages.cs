using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Rollup;

namespace ClaudeUsageMonitor.App.Messaging;

/// <summary>usage API 폴링 결과 갱신 브로드캐스트.</summary>
public sealed record RateLimitUpdatedMessage(RateLimitState State);

/// <summary>롤업(로컬 통계) 갱신 브로드캐스트.</summary>
public sealed record RollupUpdatedMessage(RollupData Rollup);

/// <summary>위젯 표시 모드 변경.</summary>
public sealed record WidgetModeChangedMessage(WidgetMode Mode);

/// <summary>새 버전 발견 (GitHub Releases 주기 확인).</summary>
public sealed record UpdateAvailableMessage(string Version);
