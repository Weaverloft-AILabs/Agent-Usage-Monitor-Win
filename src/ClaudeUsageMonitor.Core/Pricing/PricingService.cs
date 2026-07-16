using System.Text.Json;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Storage;

namespace ClaudeUsageMonitor.Core.Pricing;

/// <summary>
/// 모델 → 단가 해석. 1차: LiteLLM 원격 가격표(7일 캐시), 폴백: EmbeddedPricing.
/// 매칭: 정확 키 → 대소문자 무시 부분 일치(가장 긴 키 우선) → null(미지 모델 = $0).
/// </summary>
public sealed class PricingService
{
    public const string LiteLlmUrl =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);

    private readonly string _cachePath;
    private readonly HttpClient _http;
    private Dictionary<string, ModelPricing> _remote = new(StringComparer.OrdinalIgnoreCase);

    public PricingService(string dataDirectory, HttpClient? http = null)
    {
        _cachePath = Path.Combine(dataDirectory, "pricing-cache.json");
        _http = http ?? new HttpClient();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // 신선한 캐시가 있으면 네트워크 생략
        if (File.Exists(_cachePath) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath) < CacheMaxAge &&
            TryLoadCache())
        {
            return;
        }

        try
        {
            var json = await _http.GetStringAsync(LiteLlmUrl, cancellationToken).ConfigureAwait(false);
            var parsed = ParseLiteLlm(json);
            if (parsed.Count > 0)
            {
                _remote = parsed;
                AtomicJsonFile.Save(_cachePath, parsed);
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 오프라인/장애 — 오래된 캐시라도 사용
        }

        TryLoadCache();
    }

    public ModelPricing? Resolve(string model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return null;
        }

        if (_remote.TryGetValue(model, out var exact))
        {
            return exact;
        }

        var partial = PartialMatch(_remote, model);
        if (partial is not null)
        {
            return partial;
        }

        if (EmbeddedPricing.Table.TryGetValue(model, out var embedded))
        {
            return embedded;
        }

        return PartialMatch(EmbeddedPricing.Table, model);
    }

    // 부분 일치 최소 길이 — 실제 모델 키는 모두 이보다 길다. 짧은 조각이 무관 모델에 오매칭되는 것 방지.
    private const int MinPartialMatchLength = 8;

    private static ModelPricing? PartialMatch(IReadOnlyDictionary<string, ModelPricing> table, string model)
    {
        string? bestKey = null;
        foreach (var key in table.Keys)
        {
            // 오매칭 방지: 부분 일치는 두 문자열 모두 최소 길이 이상일 때만 인정(정확 일치는 상위 Resolve가 이미 처리).
            if (key.Length < MinPartialMatchLength || model.Length < MinPartialMatchLength)
            {
                continue;
            }
            if (key.Contains(model, StringComparison.OrdinalIgnoreCase) ||
                model.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                if (bestKey is null || key.Length > bestKey.Length)
                {
                    bestKey = key;
                }
            }
        }
        return bestKey is null ? null : table[bestKey];
    }

    private bool TryLoadCache()
    {
        var cached = AtomicJsonFile.Load<Dictionary<string, ModelPricing>>(_cachePath);
        if (cached is { Count: > 0 })
        {
            _remote = new Dictionary<string, ModelPricing>(cached, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        return false;
    }

    /// <summary>LiteLLM JSON에서 anthropic claude 모델의 per-token 단가를 USD/MTok로 변환해 추출.</summary>
    internal static Dictionary<string, ModelPricing> ParseLiteLlm(string json)
    {
        var result = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (!entry.Name.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ||
                entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var model = entry.Value;
            var provider = model.TryGetProperty("litellm_provider", out var p) ? p.GetString() : null;
            if (provider != "anthropic")
            {
                continue;
            }

            var input = PerMTok(model, "input_cost_per_token");
            var output = PerMTok(model, "output_cost_per_token");
            if (input is null || output is null)
            {
                continue;
            }

            var cache5m = PerMTok(model, "cache_creation_input_token_cost") ?? input.Value * 1.25m;
            var cache1h = PerMTok(model, "cache_creation_input_token_cost_above_1hr") ?? input.Value * 2m;
            var cacheRead = PerMTok(model, "cache_read_input_token_cost") ?? input.Value * 0.1m;

            result[entry.Name] = new ModelPricing(input.Value, output.Value, cache5m, cache1h, cacheRead);
        }

        return result;
    }

    private static decimal? PerMTok(JsonElement model, string property)
    {
        if (model.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            return (decimal)value.GetDouble() * 1_000_000m;
        }
        return null;
    }
}
