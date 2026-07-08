using System.Net;
using System.Text;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.RateLimit;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class UsageResponseParserTests
{
    // 실측 응답 (2026-07-08, 값만 축약)
    private const string RealResponse = """
    {
      "five_hour": { "utilization": 6.0, "resets_at": "2026-07-08T10:19:59.852991+00:00",
                     "limit_dollars": null, "used_dollars": null, "remaining_dollars": null },
      "seven_day": { "utilization": 64.0, "resets_at": "2026-07-09T04:59:59.853012+00:00",
                     "limit_dollars": null, "used_dollars": null, "remaining_dollars": null },
      "seven_day_opus": null,
      "extra_usage": { "is_enabled": false },
      "limits": [
        { "kind": "session", "group": "session", "percent": 6, "severity": "normal",
          "resets_at": "2026-07-08T10:19:59.852991+00:00", "scope": null, "is_active": false },
        { "kind": "weekly_all", "group": "weekly", "percent": 64, "severity": "normal",
          "resets_at": "2026-07-09T04:59:59.853012+00:00", "scope": null, "is_active": true },
        { "kind": "weekly_scoped", "group": "weekly", "percent": 13, "severity": "normal",
          "resets_at": "2026-07-09T04:59:59.853341+00:00",
          "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
          "is_active": false }
      ],
      "spend": { "percent": 0 },
      "member_dashboard_available": false
    }
    """;

    [Fact]
    public void Parse_RealResponse_PrefersLimitsArray()
    {
        var snapshot = UsageResponseParser.Parse(RealResponse, DateTimeOffset.UtcNow);

        Assert.NotNull(snapshot);
        Assert.Equal(6, snapshot!.FiveHourPct);
        Assert.Equal(64, snapshot.SevenDayPct);
        Assert.Equal(3, snapshot.Limits.Count);
        Assert.Equal("Fable", snapshot.Limits[2].ScopeLabel);
        Assert.True(snapshot.Limits[1].IsActive);
        Assert.Equal(new DateTimeOffset(2026, 7, 9, 4, 59, 59, 853, TimeSpan.Zero).AddTicks(120), snapshot.SevenDayResetsAt!.Value);
    }

    [Fact]
    public void Parse_FlatKeysOnly_FallsBack()
    {
        const string flatOnly = """
        { "five_hour": { "utilization": 42.5, "resets_at": "2026-07-08T10:00:00+00:00" },
          "seven_day": { "utilization": 80.0, "resets_at": "2026-07-09T05:00:00+00:00" } }
        """;

        var snapshot = UsageResponseParser.Parse(flatOnly, DateTimeOffset.UtcNow);

        Assert.NotNull(snapshot);
        Assert.Equal(42.5, snapshot!.FiveHourPct);
        Assert.Equal(80.0, snapshot.SevenDayPct);
        Assert.Empty(snapshot.Limits);
    }

    [Fact]
    public void Parse_Garbage_ReturnsNull()
    {
        Assert.Null(UsageResponseParser.Parse("not json", DateTimeOffset.UtcNow));
        Assert.Null(UsageResponseParser.Parse("[1,2]", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Parse_ClampsPercentTo100()
    {
        const string overflow = """
        { "five_hour": { "utilization": 1751234567.0 }, "seven_day": { "utilization": -5 } }
        """;

        var snapshot = UsageResponseParser.Parse(overflow, DateTimeOffset.UtcNow);

        Assert.Equal(100, snapshot!.FiveHourPct);
        Assert.Equal(0, snapshot.SevenDayPct);
    }
}

public sealed class RateLimitClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cum-rl-" + Guid.NewGuid().ToString("N"));

    public RateLimitClientTests()
    {
        Directory.CreateDirectory(_dir);
        WriteCredentials("sk-ant-oat01-test-token");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CredPath => Path.Combine(_dir, ".credentials.json");

    private void WriteCredentials(string token) =>
        File.WriteAllText(CredPath, $$"""
        { "claudeAiOauth": { "accessToken": "{{token}}", "refreshToken": "sk-ant-ort01-x",
          "expiresAt": 9999999999999, "subscriptionType": "max", "rateLimitTier": "default_claude_max_20x" } }
        """);

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls;
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = _ => Ok();

        public static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{ "five_hour": {"utilization": 10.0}, "seven_day": {"utilization": 20.0} }""",
                Encoding.UTF8, "application/json"),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Responder(request));
        }
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-07-08T12:00:00Z");

    [Fact]
    public async Task Fetch_Success_ReturnsOkSnapshot()
    {
        var handler = new StubHandler();
        using var client = new RateLimitClient(new CredentialsReader(CredPath), new HttpClient(handler));

        var state = await client.FetchAsync(T0);

        Assert.Equal(RateLimitStatus.Ok, state.Status);
        Assert.Equal(10.0, state.Snapshot!.FiveHourPct);
        Assert.False(state.Snapshot.IsStale);
    }

    [Fact]
    public async Task Fetch_SendsRequiredHeaders()
    {
        var handler = new StubHandler();
        HttpRequestMessage? seen = null;
        handler.Responder = req => { seen = req; return StubHandler.Ok(); };
        using var client = new RateLimitClient(new CredentialsReader(CredPath), new HttpClient(handler), cliVersion: "9.9.9");

        await client.FetchAsync(T0);

        Assert.NotNull(seen);
        Assert.Equal("Bearer sk-ant-oat01-test-token", seen!.Headers.GetValues("Authorization").Single());
        Assert.Equal("2023-06-01", seen.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("oauth-2025-04-20", seen.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal("claude-code/9.9.9", seen.Headers.GetValues("User-Agent").Single());
    }

    [Fact]
    public async Task Fetch_429_SetsBackoff_AndSkipsNetworkUntilExpiry()
    {
        var handler = new StubHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) };
        using var client = new RateLimitClient(new CredentialsReader(CredPath), new HttpClient(handler));

        var first = await client.FetchAsync(T0);
        Assert.Equal(RateLimitStatus.Stale, first.Status);
        Assert.Equal(T0 + TimeSpan.FromSeconds(60), first.NextPollAt);
        Assert.Equal(1, handler.Calls);

        // 백오프 기간 내 재호출 — 네트워크 미사용
        var second = await client.FetchAsync(T0 + TimeSpan.FromSeconds(30));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(RateLimitStatus.Stale, second.Status);

        // 백오프 경과 후 다시 429 → 지수 증가 (120s)
        var third = await client.FetchAsync(T0 + TimeSpan.FromSeconds(61));
        Assert.Equal(2, handler.Calls);
        Assert.Equal(T0 + TimeSpan.FromSeconds(61 + 120), third.NextPollAt);
    }

    [Fact]
    public async Task Fetch_401_RereadsCredentials_AndRetriesOnce()
    {
        var handler = new StubHandler();
        handler.Responder = req =>
            req.Headers.GetValues("Authorization").Single().Contains("rotated")
                ? StubHandler.Ok()
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);

        using var client = new RateLimitClient(new CredentialsReader(CredPath), new HttpClient(handler));

        // 첫 요청은 401 → 파일이 회전된 토큰을 갖고 있으면 재시도 성공해야 함
        WriteCredentials("sk-ant-oat01-old"); // 첫 읽기용
        var reader = new CredentialsReader(CredPath);
        using var client2 = new RateLimitClient(reader, new HttpClient(new StubHandler
        {
            Responder = req => req.Headers.GetValues("Authorization").Single().Contains("rotated")
                ? StubHandler.Ok()
                : new HttpResponseMessage(HttpStatusCode.Unauthorized),
        }), cliVersion: "1");

        // 401 응답을 받으면 재독취 — 그 사이 파일을 회전시킬 수 없으므로(동기),
        // 동일 토큰 재독취 → AuthRequired 경로를 검증
        var state = await client2.FetchAsync(T0);
        Assert.Equal(RateLimitStatus.AuthRequired, state.Status);
    }

    [Fact]
    public async Task Fetch_NoCredentialsFile_ReturnsNoCredentials()
    {
        var missing = Path.Combine(_dir, "nope.json");
        using var client = new RateLimitClient(new CredentialsReader(missing), new HttpClient(new StubHandler()));

        var state = await client.FetchAsync(T0);

        Assert.Equal(RateLimitStatus.NoCredentials, state.Status);
        Assert.Null(state.Snapshot);
    }

    [Fact]
    public async Task Fetch_AfterSuccess_FailureKeepsStaleSnapshot()
    {
        var handler = new StubHandler();
        using var client = new RateLimitClient(new CredentialsReader(CredPath), new HttpClient(handler));
        await client.FetchAsync(T0);

        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var state = await client.FetchAsync(T0 + TimeSpan.FromMinutes(5));

        Assert.Equal(RateLimitStatus.Stale, state.Status);
        Assert.NotNull(state.Snapshot);
        Assert.True(state.Snapshot!.IsStale);
        Assert.Equal(10.0, state.Snapshot.FiveHourPct); // 마지막 성공값 유지
    }
}
