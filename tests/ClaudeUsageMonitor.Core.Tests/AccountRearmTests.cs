using System.Net;
using ClaudeUsageMonitor.Core.RateLimit;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

/// <summary>
/// 경고 알림 재무장 요구사항: 한 번 발사 후 재발사 금지,
/// 단 리셋 윈도우 변경(기존)·앱 재시작(인메모리)·계정 변경(Rearm) 시 다시 발사.
/// </summary>
public class AccountRearmTests
{
    private static readonly DateTimeOffset Reset1 = DateTimeOffset.Parse("2026-07-09T10:00:00Z");

    // --- ThresholdArm.Rearm ---

    [Fact]
    public void Rearm_AllowsRefire_WithinSameResetWindow()
    {
        var arm = new ThresholdArm();
        Assert.True(arm.ShouldFire(85, 80, Reset1));
        Assert.False(arm.ShouldFire(90, 80, Reset1));

        arm.Rearm();

        Assert.True(arm.ShouldFire(90, 80, Reset1));
        Assert.False(arm.ShouldFire(95, 80, Reset1));
    }

    [Fact]
    public void Rearm_AllowsRefire_ForNullResetWindow()
    {
        var arm = new ThresholdArm();
        Assert.True(arm.ShouldFire(85, 80, null));
        Assert.False(arm.ShouldFire(90, 80, null));

        arm.Rearm();

        Assert.True(arm.ShouldFire(90, 80, null));
    }

    // --- ProfileResponseParser ---

    [Fact]
    public void Parse_RealShape_ReturnsUuid()
    {
        const string json = """
        { "account": { "uuid": "11111111-2222-3333-4444-555555555555",
                       "full_name": "x", "display_name": "x", "email": "x@x.co",
                       "has_claude_max": true, "has_claude_pro": false },
          "organization": { "uuid": "o", "rate_limit_tier": "default_claude_max_20x" },
          "application": { "uuid": "a" }, "enabled_plugins": [] }
        """;
        Assert.Equal("11111111-2222-3333-4444-555555555555", ProfileResponseParser.ParseAccountUuid(json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "account": null }""")]
    [InlineData("""{ "account": { "email": "x@x.co" } }""")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void Parse_MissingOrGarbage_ReturnsNull(string json)
        => Assert.Null(ProfileResponseParser.ParseAccountUuid(json));

    // --- AccountTracker ---

    [Fact]
    public void Tracker_FirstAndSameUuid_DoNotSignal()
    {
        var t = new AccountTracker();
        Assert.False(t.Update("A"));
        Assert.False(t.Update("A"));
    }

    [Fact]
    public void Tracker_DifferentUuid_Signals()
    {
        var t = new AccountTracker();
        t.Update("A");
        Assert.True(t.Update("B"));
        Assert.False(t.Update("B"));
    }

    [Fact]
    public void Tracker_NullFetch_IsIgnored()
    {
        var t = new AccountTracker();
        t.Update("A");
        Assert.False(t.Update(null));   // 일시 조회 실패 — 계정 변경 아님
        Assert.False(t.Update("A"));    // 같은 계정 복귀 — 신호 없음
        Assert.False(t.Update(null));
        Assert.True(t.Update("B"));     // 실패를 사이에 두고 실제 변경
    }

    // --- ProfileClient (HTTP는 목 핸들러) ---

    [Fact]
    public async Task Client_ParsesUuid_AndSendsOauthHeaders()
    {
        using var dir = new TempCredentials("tok-1");
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(req =>
        {
            seen = req;
            return Json("""{ "account": { "uuid": "abc" } }""");
        });
        using var client = new ProfileClient(dir.Reader, new HttpClient(handler), cliVersion: "9.9.9");

        var uuid = await client.FetchAccountUuidAsync();

        Assert.Equal("abc", uuid);
        Assert.NotNull(seen);
        Assert.Equal("Bearer tok-1", seen!.Headers.GetValues("Authorization").Single());
        Assert.Equal("oauth-2025-04-20", seen.Headers.GetValues("anthropic-beta").Single());
        Assert.Contains("claude-code/9.9.9", seen.Headers.GetValues("User-Agent").Single());
    }

    [Fact]
    public async Task Client_NoCredentials_ReturnsNull()
    {
        using var dir = new TempCredentials(token: null);
        using var client = new ProfileClient(dir.Reader, new HttpClient(new StubHandler(_ => Json("{}"))));

        Assert.Null(await client.FetchAccountUuidAsync());
    }

    [Fact]
    public async Task Client_401_RereadsCredentialsOnce()
    {
        using var dir = new TempCredentials("tok-old");
        var calls = 0;
        var handler = new StubHandler(req =>
        {
            calls++;
            if (req.Headers.GetValues("Authorization").Single() == "Bearer tok-old")
            {
                dir.WriteToken("tok-new"); // CLI가 토큰을 회전한 상황 재현
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            return Json("""{ "account": { "uuid": "after-rotate" } }""");
        });
        using var client = new ProfileClient(dir.Reader, new HttpClient(handler));

        Assert.Equal("after-rotate", await client.FetchAccountUuidAsync());
        Assert.Equal(2, calls);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responder(request));
    }

    private sealed class TempCredentials : IDisposable
    {
        private readonly string _dir;
        public CredentialsReader Reader { get; }

        public TempCredentials(string? token)
        {
            _dir = Path.Combine(Path.GetTempPath(), "aum-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Reader = new CredentialsReader(Path.Combine(_dir, ".credentials.json"));
            if (token is not null)
            {
                WriteToken(token);
            }
        }

        public void WriteToken(string token) =>
            File.WriteAllText(Path.Combine(_dir, ".credentials.json"),
                $$"""{ "claudeAiOauth": { "accessToken": "{{token}}", "expiresAt": 9999999999999 } }""");

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
