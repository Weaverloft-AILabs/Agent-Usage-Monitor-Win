using ClaudeUsageMonitor.Core.RateLimit;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

/// <summary>
/// usage API / 자격증명 파싱 방어성 (리스크 감사 Batch 2):
/// 비공식 엔드포인트 스키마 드리프트가 폴링 서비스를 폴트시키거나 0% 정상으로 오표시하지 않도록.
/// </summary>
public sealed class RateLimitResilienceTests
{
    [Fact]
    public void Parse_LimitsPercentDecimal_DoesNotThrow()
    {
        // percent가 정수 대신 소수로 오면 GetInt32는 FormatException을 던졌음 (폴링 서비스 폴트).
        const string json = """
        { "limits": [ { "kind": "session", "percent": 42.5, "severity": "normal",
                        "resets_at": "2026-07-08T10:00:00+00:00", "is_active": true } ] }
        """;

        var snapshot = UsageResponseParser.Parse(json, DateTimeOffset.UtcNow);

        Assert.NotNull(snapshot);
        Assert.Equal(42, snapshot!.Limits[0].Percent); // 반올림, 예외 없음
        Assert.Equal(42, snapshot.FiveHourPct);
    }

    [Fact]
    public void Parse_LimitsPercentHugeOrNegative_ClampsWithoutOverflow()
    {
        const string json = """
        { "limits": [ { "kind": "session", "percent": 1750000000000.0, "severity": "normal" },
                      { "kind": "weekly_all", "percent": -9, "severity": "normal" } ] }
        """;

        var snapshot = UsageResponseParser.Parse(json, DateTimeOffset.UtcNow);

        Assert.NotNull(snapshot);
        Assert.Equal(100, snapshot!.FiveHourPct); // 스냅샷 클램프
        Assert.Equal(0, snapshot.SevenDayPct);
    }

    [Fact]
    public void Parse_UnrecognizedSchema_ReturnsNull_NotFalseZero()
    {
        // 5h/7d 어느 창도 매칭되지 않는 응답: 0%/정상 오표시 대신 null(→ Stale) 이어야 함.
        const string json = """
        { "limits": [ { "kind": "renamed_session", "percent": 90, "severity": "normal" } ],
          "some_new_shape": { "x": 1 } }
        """;

        Assert.Null(UsageResponseParser.Parse(json, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Parse_OnlyWeeklyResolved_StillReturnsSnapshot()
    {
        const string json = """
        { "limits": [ { "kind": "weekly_all", "percent": 30, "severity": "normal", "is_active": true } ] }
        """;

        var snapshot = UsageResponseParser.Parse(json, DateTimeOffset.UtcNow);

        Assert.NotNull(snapshot); // 하나라도 인식되면 스냅샷 제공
        Assert.Equal(30, snapshot!.SevenDayPct);
    }

    [Fact]
    public void Credentials_NonStringAccessToken_ReturnsNull_WithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cum-cred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ".credentials.json");
            // accessToken이 문자열이 아니라 숫자 — GetString()이 InvalidOperationException을 던졌음.
            File.WriteAllText(path, """{ "claudeAiOauth": { "accessToken": 12345, "expiresAt": 9999 } }""");

            var creds = new CredentialsReader(path).TryRead();

            Assert.Null(creds); // 예외 없이 null
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Credentials_NonStringSubscription_StillReadsToken()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cum-cred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ".credentials.json");
            File.WriteAllText(path, """
            { "claudeAiOauth": { "accessToken": "sk-ant-oat01-ok", "expiresAt": 9999,
              "subscriptionType": 7, "rateLimitTier": null } }
            """);

            var creds = new CredentialsReader(path).TryRead();

            Assert.NotNull(creds);
            Assert.Equal("sk-ant-oat01-ok", creds!.AccessToken);
            Assert.Null(creds.SubscriptionType); // 비문자열은 무시
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
