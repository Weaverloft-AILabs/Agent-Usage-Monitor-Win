using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.Ingest;

/// <summary>
/// Claude Code 세션 JSONL 라인 파서. 스키마는 비공식이므로 방어적으로 파싱한다:
/// 알 수 없는 필드 무시, 파싱 실패는 예외 없이 false.
/// </summary>
public static class JsonlParser
{
    private const string SyntheticModel = "<synthetic>";

    public static bool TryParseLine(string line, [NotNullWhen(true)] out RawUsageLine? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // 토큰 usage는 type=assistant 라인에만 존재
            if (!TryGetString(root, "type", out var type) || type != "assistant")
            {
                return false;
            }

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(message, "model", out var model) || model == SyntheticModel)
            {
                return false;
            }

            if (!TryGetString(message, "id", out var messageId))
            {
                return false;
            }

            if (!TryGetString(root, "timestamp", out var timestampRaw) ||
                !DateTimeOffset.TryParse(timestampRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            {
                return false;
            }

            TryGetString(root, "sessionId", out var sessionId);
            TryGetString(root, "cwd", out var cwd);

            parsed = new RawUsageLine(
                messageId,
                model,
                timestamp,
                sessionId ?? string.Empty,
                cwd ?? string.Empty,
                ReadTokens(usage));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TokenCounts ReadTokens(JsonElement usage)
    {
        var input = GetLong(usage, "input_tokens");
        var output = GetLong(usage, "output_tokens");
        var cacheRead = GetLong(usage, "cache_read_input_tokens");

        long cache5m;
        long cache1h;
        if (usage.TryGetProperty("cache_creation", out var breakdown) && breakdown.ValueKind == JsonValueKind.Object)
        {
            // 1h 캐시 쓰기는 5m의 1.6배 요율이므로 분해값을 우선한다
            cache5m = GetLong(breakdown, "ephemeral_5m_input_tokens");
            cache1h = GetLong(breakdown, "ephemeral_1h_input_tokens");
        }
        else
        {
            // 구버전/축약 스키마: 전체를 5m으로 간주
            cache5m = GetLong(usage, "cache_creation_input_tokens");
            cache1h = 0;
        }

        return new TokenCounts(input, output, cache5m, cache1h, cacheRead);
    }

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt64()
            : 0;

    private static bool TryGetString(JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        value = element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
        return !string.IsNullOrEmpty(value);
    }
}
