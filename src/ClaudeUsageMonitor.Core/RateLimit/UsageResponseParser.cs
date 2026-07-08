using System.Globalization;
using System.Text.Json;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.RateLimit;

/// <summary>
/// /api/oauth/usage 응답 파서. 비공식 엔드포인트이므로 방어적으로:
/// limits[] 배열(자기서술형)을 1차 소스로, 플랫 five_hour/seven_day 키를 폴백으로 사용한다.
/// 알 수 없는 키/null은 모두 허용.
/// </summary>
public static class UsageResponseParser
{
    public static RateLimitSnapshot? Parse(string json, DateTimeOffset fetchedAt)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var limits = ParseLimits(root);

            double fiveHourPct = 0;
            DateTimeOffset? fiveHourResets = null;
            double sevenDayPct = 0;
            DateTimeOffset? sevenDayResets = null;

            var session = limits.FirstOrDefault(l => l.Kind == "session");
            var weeklyAll = limits.FirstOrDefault(l => l.Kind == "weekly_all");

            if (session is not null)
            {
                fiveHourPct = session.Percent;
                fiveHourResets = session.ResetsAt;
            }
            else if (TryReadWindow(root, "five_hour", out var fh))
            {
                (fiveHourPct, fiveHourResets) = fh;
            }

            if (weeklyAll is not null)
            {
                sevenDayPct = weeklyAll.Percent;
                sevenDayResets = weeklyAll.ResetsAt;
            }
            else if (TryReadWindow(root, "seven_day", out var sd))
            {
                (sevenDayPct, sevenDayResets) = sd;
            }

            return new RateLimitSnapshot(
                Math.Clamp(fiveHourPct, 0, 100),
                fiveHourResets,
                Math.Clamp(sevenDayPct, 0, 100),
                sevenDayResets,
                limits,
                fetchedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<LimitEntry> ParseLimits(JsonElement root)
    {
        var result = new List<LimitEntry>();
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var kind = GetString(entry, "kind");
            if (kind is null)
            {
                continue;
            }

            string? scopeLabel = null;
            if (entry.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
                scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
            {
                scopeLabel = GetString(model, "display_name");
            }

            result.Add(new LimitEntry(
                kind,
                GetInt(entry, "percent"),
                GetString(entry, "severity") ?? "normal",
                GetDate(entry, "resets_at"),
                entry.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.True,
                scopeLabel));
        }
        return result;
    }

    private static bool TryReadWindow(JsonElement root, string name, out (double Pct, DateTimeOffset? ResetsAt) window)
    {
        window = default;
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var pct = el.TryGetProperty("utilization", out var util) && util.ValueKind == JsonValueKind.Number
            ? util.GetDouble()
            : 0;
        window = (pct, GetDate(el, "resets_at"));
        return true;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;

    private static DateTimeOffset? GetDate(JsonElement el, string name)
    {
        var raw = GetString(el, name);
        return raw is not null &&
               DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}
