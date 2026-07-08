using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Pricing;

public static class CostCalculator
{
    private const decimal MTok = 1_000_000m;

    /// <summary>토큰 × 단가(USD/MTok). pricing이 null(미지 모델)이면 0.</summary>
    public static decimal Cost(TokenCounts tokens, ModelPricing? pricing)
    {
        if (pricing is null)
        {
            return 0m;
        }

        return (tokens.Input * pricing.InputPerMTok
              + tokens.Output * pricing.OutputPerMTok
              + tokens.Cache5m * pricing.Cache5mPerMTok
              + tokens.Cache1h * pricing.Cache1hPerMTok
              + tokens.CacheRead * pricing.CacheReadPerMTok) / MTok;
    }
}
