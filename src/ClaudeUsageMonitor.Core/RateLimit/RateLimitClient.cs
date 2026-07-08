using System.Net;
using ClaudeUsageMonitor.Core.Models;

namespace ClaudeUsageMonitor.Core.RateLimit;

/// <summary>
/// /api/oauth/usage 폴링 클라이언트.
/// - 단일 in-flight 요청 보장.
/// - 429: 지수 백오프(60s×2^n, 상한 30분). 엔드포인트는 Retry-After 없이 sticky 429를 주는 것으로 알려짐.
/// - 401: credentials 파일 재독취 후 1회 재시도. 그래도 401이면 AuthRequired — 절대 refresh하지 않음.
/// - 마지막 성공 스냅샷을 stale로 유지해 UI가 빈 화면이 되지 않게 한다.
/// </summary>
public sealed class RateLimitClient : IDisposable
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly CredentialsReader _credentials;
    private readonly string _cliVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RateLimitSnapshot? _lastSnapshot;
    private int _consecutive429;
    private DateTimeOffset? _backoffUntil;

    public RateLimitClient(CredentialsReader credentials, HttpClient? http = null, string cliVersion = "2.1.204")
    {
        _credentials = credentials;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _cliVersion = cliVersion;
    }

    public RateLimitSnapshot? LastSnapshot => _lastSnapshot;

    public async Task<RateLimitState> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (_backoffUntil is { } until && now < until)
        {
            return Stale(until);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var creds = _credentials.TryRead();
            if (creds is null)
            {
                return new RateLimitState(MarkStale(), RateLimitStatus.NoCredentials, null);
            }

            var response = await SendAsync(creds.AccessToken, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // CLI가 토큰을 회전했을 수 있음 — 파일 재독취 후 1회 재시도
                response.Dispose();
                var fresh = _credentials.TryRead();
                if (fresh is null || fresh.AccessToken == creds.AccessToken)
                {
                    return new RateLimitState(MarkStale(), RateLimitStatus.AuthRequired, null);
                }
                response = await SendAsync(fresh.AccessToken, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    return new RateLimitState(MarkStale(), RateLimitStatus.AuthRequired, null);
                }
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _consecutive429++;
                    var delayTicks = InitialBackoff.Ticks << Math.Min(_consecutive429 - 1, 5);
                    var delay = TimeSpan.FromTicks(Math.Min(delayTicks, MaxBackoff.Ticks));
                    _backoffUntil = now + delay;
                    return Stale(_backoffUntil);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Stale(null);
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var snapshot = UsageResponseParser.Parse(json, now);
                if (snapshot is null)
                {
                    return Stale(null);
                }

                _consecutive429 = 0;
                _backoffUntil = null;
                _lastSnapshot = snapshot;
                return new RateLimitState(snapshot, RateLimitStatus.Ok, null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Stale(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        // 관대한 레이트리밋 버킷은 claude-code UA 기준으로 알려져 있음
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/" + _cliVersion);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private RateLimitSnapshot? MarkStale() =>
        _lastSnapshot = _lastSnapshot is null ? null : _lastSnapshot with { IsStale = true };

    private RateLimitState Stale(DateTimeOffset? nextPollAt) =>
        new(MarkStale(), _lastSnapshot is null ? RateLimitStatus.Stale : RateLimitStatus.Stale, nextPollAt);

    public void Dispose()
    {
        _gate.Dispose();
        _http.Dispose();
    }
}
