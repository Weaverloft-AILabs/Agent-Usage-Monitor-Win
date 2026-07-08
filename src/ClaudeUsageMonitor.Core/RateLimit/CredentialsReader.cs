using System.Text.Json;

namespace ClaudeUsageMonitor.Core.RateLimit;

/// <summary>
/// ~/.claude/.credentials.json 의 OAuth 자격. 토큰은 메모리에서만 취급 — 로깅/영속 절대 금지.
/// expiresAt/refreshTokenExpiresAt 는 epoch 밀리초.
/// </summary>
public sealed record Credentials(
    string AccessToken,
    long ExpiresAtMs,
    long? RefreshExpiresAtMs,
    string? SubscriptionType,
    string? RateLimitTier)
{
    public bool IsAccessTokenExpired(DateTimeOffset now) =>
        now.ToUnixTimeMilliseconds() >= ExpiresAtMs;

    public bool IsRefreshTokenExpired(DateTimeOffset now) =>
        RefreshExpiresAtMs is { } ms && now.ToUnixTimeMilliseconds() >= ms;
}

/// <summary>
/// credentials 파일을 매 호출 시 새로 읽는다 — CLI가 토큰을 회전하면 즉시 반영.
/// 이 앱은 절대 토큰 refresh를 수행하지 않는다(회전 충돌로 CLI 로그인이 파괴됨).
/// </summary>
public sealed class CredentialsReader
{
    private readonly string _path;

    public CredentialsReader(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".credentials.json");
    }

    public string CredentialsPath => _path;

    public Credentials? TryRead()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var accessToken = oauth.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(accessToken))
            {
                return null;
            }

            var expiresAt = oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number
                ? exp.GetInt64()
                : 0L;
            long? refreshExpiresAt = oauth.TryGetProperty("refreshTokenExpiresAt", out var rexp) && rexp.ValueKind == JsonValueKind.Number
                ? rexp.GetInt64()
                : null;
            var subscription = oauth.TryGetProperty("subscriptionType", out var sub) ? sub.GetString() : null;
            var tier = oauth.TryGetProperty("rateLimitTier", out var t) ? t.GetString() : null;

            return new Credentials(accessToken, expiresAt, refreshExpiresAt, subscription, tier);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
