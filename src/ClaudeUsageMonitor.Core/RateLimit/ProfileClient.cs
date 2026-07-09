using System.Net;
using System.Text.Json;

namespace ClaudeUsageMonitor.Core.RateLimit;

/// <summary>/api/oauth/profile 응답에서 계정 uuid만 추출 (PII는 파싱하지 않음).</summary>
public static class ProfileResponseParser
{
    public static string? ParseAccountUuid(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("account", out var account) ||
                account.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return account.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String
                ? uuid.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 계정 uuid 변경 감지. 조회 실패(null)는 무시하고 마지막 확인값을 유지한다 —
/// 일시적 네트워크 오류가 계정 변경으로 오인되지 않게.
/// </summary>
public sealed class AccountTracker
{
    private string? _lastUuid;

    /// <returns>계정이 실제로 바뀌었으면 true (최초 확인·동일 계정·조회 실패는 false).</returns>
    public bool Update(string? uuid)
    {
        if (string.IsNullOrEmpty(uuid))
        {
            return false;
        }

        if (_lastUuid is null)
        {
            _lastUuid = uuid;
            return false;
        }

        if (_lastUuid == uuid)
        {
            return false;
        }

        _lastUuid = uuid;
        return true;
    }
}

/// <summary>
/// /api/oauth/profile 클라이언트 (DESIGN §4.1). 계정 uuid만 취득 — 이메일 등 PII는
/// 메모리에도 올리지 않는다. 401은 credentials 재독취 후 1회 재시도(usage 클라이언트와 동일),
/// refresh는 절대 수행하지 않는다.
/// </summary>
public sealed class ProfileClient : IDisposable
{
    public const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";

    private readonly HttpClient _http;
    private readonly CredentialsReader _credentials;
    private readonly string _cliVersion;

    public ProfileClient(CredentialsReader credentials, HttpClient? http = null, string cliVersion = "2.1.204")
    {
        _credentials = credentials;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _cliVersion = cliVersion;
    }

    /// <summary>현재 로그인 계정의 uuid. 자격 없음/네트워크 오류/파싱 실패 시 null.</summary>
    public async Task<string?> FetchAccountUuidAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var creds = _credentials.TryRead();
            if (creds is null)
            {
                return null;
            }

            var response = await SendAsync(creds.AccessToken, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                var fresh = _credentials.TryRead();
                if (fresh is null || fresh.AccessToken == creds.AccessToken)
                {
                    return null;
                }
                response = await SendAsync(fresh.AccessToken, cancellationToken).ConfigureAwait(false);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ProfileResponseParser.ParseAccountUuid(json);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/" + _cliVersion);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
