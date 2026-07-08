using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Pricing;

/// <summary>
/// 오프라인 폴백 가격표 (USD per MTok, 2026-07 LiteLLM/Anthropic 공식 페이지 교차 검증).
/// 주의: claude-sonnet-5는 2026-08-31까지 인트로 가격($2/$10). 이후 $3/$15 —
/// 원격(LiteLLM) 소스가 우선이므로 원격 갱신으로 자동 반영된다.
/// </summary>
public static class EmbeddedPricing
{
    public static readonly IReadOnlyDictionary<string, ModelPricing> Table =
        new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-fable-5"] = new(10m, 50m, 12.5m, 20m, 1m),
            ["claude-opus-4-8"] = new(5m, 25m, 6.25m, 10m, 0.5m),
            ["claude-opus-4-7"] = new(5m, 25m, 6.25m, 10m, 0.5m),
            ["claude-sonnet-5"] = new(2m, 10m, 2.5m, 4m, 0.2m),
            ["claude-sonnet-4-6"] = new(3m, 15m, 3.75m, 6m, 0.3m),
            ["claude-haiku-4-5"] = new(1m, 5m, 1.25m, 2m, 0.1m),
        };
}
