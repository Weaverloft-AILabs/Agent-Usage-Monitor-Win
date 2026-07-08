using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Pricing;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class CostCalculatorTests
{
    [Fact]
    public void Cost_SumsAllFiveComponents()
    {
        var pricing = EmbeddedPricing.Table["claude-sonnet-5"]; // 2 / 10 / 2.5 / 4 / 0.2
        var tokens = new TokenCounts(1_000_000, 1_000_000, 1_000_000, 1_000_000, 1_000_000);

        Assert.Equal(18.70m, CostCalculator.Cost(tokens, pricing));
    }

    [Fact]
    public void Cost_1hCacheWrite_UsesDoubleRate()
    {
        var pricing = EmbeddedPricing.Table["claude-sonnet-5"];

        var only5m = CostCalculator.Cost(new TokenCounts(0, 0, 1_000_000, 0, 0), pricing);
        var only1h = CostCalculator.Cost(new TokenCounts(0, 0, 0, 1_000_000, 0), pricing);

        Assert.Equal(2.5m, only5m);
        Assert.Equal(4m, only1h); // 1h 요율은 5m의 1.6배 (2x vs 1.25x input)
    }

    [Fact]
    public void Cost_UnknownModel_ReturnsZero()
    {
        Assert.Equal(0m, CostCalculator.Cost(new TokenCounts(1000, 1000, 0, 0, 0), null));
    }

    [Fact]
    public void Cost_RealWorldExample_Opus48()
    {
        // opus-4-8: in $5, out $25, cr $0.50 per MTok
        var pricing = EmbeddedPricing.Table["claude-opus-4-8"];
        var tokens = new TokenCounts(Input: 10_000, Output: 2_000, Cache5m: 0, Cache1h: 0, CacheRead: 100_000);

        // 10k×5/1M + 2k×25/1M + 100k×0.5/1M = 0.05 + 0.05 + 0.05
        Assert.Equal(0.15m, CostCalculator.Cost(tokens, pricing));
    }
}

public sealed class PricingServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cum-pricing-" + Guid.NewGuid().ToString("N"));

    public PricingServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string LiteLlmSample = """
    {
      "claude-sonnet-5": {
        "litellm_provider": "anthropic",
        "input_cost_per_token": 0.000002,
        "output_cost_per_token": 0.00001,
        "cache_creation_input_token_cost": 0.0000025,
        "cache_creation_input_token_cost_above_1hr": 0.000004,
        "cache_read_input_token_cost": 2e-7
      },
      "claude-sonnet-5-bedrock": {
        "litellm_provider": "bedrock",
        "input_cost_per_token": 0.000002,
        "output_cost_per_token": 0.00001
      },
      "gpt-x": { "litellm_provider": "openai", "input_cost_per_token": 0.000001, "output_cost_per_token": 0.000002 },
      "claude-no-costs": { "litellm_provider": "anthropic" }
    }
    """;

    [Fact]
    public void ParseLiteLlm_ExtractsAnthropicClaudeEntries_PerMTok()
    {
        var table = PricingService.ParseLiteLlm(LiteLlmSample);

        var sonnet = table["claude-sonnet-5"];
        Assert.Equal(2m, sonnet.InputPerMTok);
        Assert.Equal(10m, sonnet.OutputPerMTok);
        Assert.Equal(2.5m, sonnet.Cache5mPerMTok);
        Assert.Equal(4m, sonnet.Cache1hPerMTok);
        Assert.Equal(0.2m, sonnet.CacheReadPerMTok);

        Assert.False(table.ContainsKey("claude-sonnet-5-bedrock")); // provider != anthropic
        Assert.False(table.ContainsKey("gpt-x"));
        Assert.False(table.ContainsKey("claude-no-costs")); // 필수 단가 없음
    }

    [Fact]
    public void Resolve_FallsBackToEmbedded_WhenNoRemoteData()
    {
        var service = new PricingService(_dir);

        var pricing = service.Resolve("claude-opus-4-8");

        Assert.NotNull(pricing);
        Assert.Equal(5m, pricing!.InputPerMTok);
    }

    [Fact]
    public void Resolve_PartialMatch_ForDateSuffixedModel()
    {
        var service = new PricingService(_dir);

        var pricing = service.Resolve("claude-haiku-4-5-20251001");

        Assert.NotNull(pricing);
        Assert.Equal(1m, pricing!.InputPerMTok);
    }

    [Fact]
    public void Resolve_UnknownModel_ReturnsNull()
    {
        var service = new PricingService(_dir);

        Assert.Null(service.Resolve("gpt-4o"));
        Assert.Null(service.Resolve(""));
    }
}
