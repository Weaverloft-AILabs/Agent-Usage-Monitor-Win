using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeUsageMonitor.Installer.Install;

/// <summary>GitHub 최신 릴리스에서 Velopack Setup 자산을 찾고 스트리밍 다운로드한다.
/// 전체 타임아웃 없음(저속 회선에서 70MB 다운로드가 잘리지 않도록) — 대신
/// 메타데이터 호출 30초 + 본문 스톨 60초(무수신) 감시로 대체.</summary>
public sealed class GitHubReleaseClient : IDisposable
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    public GitHubReleaseClient()
    {
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentUsageMonitor-Installer/2.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>최신 릴리스에서 Setup 자산의 다운로드 URL. 없으면 null.
    /// 4xx/비JSON 응답은 원인 메시지를 담은 HttpRequestException으로 승격 (크래시 대신 오류 상태).</summary>
    public async Task<string?> FindLatestSetupUrlAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(MetadataTimeout);

        using var response = await _http.GetAsync(InstallerUrls.LatestReleaseApi, cts.Token)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException(
                    "GitHub 요청 한도 초과 — 잠시 후 다시 시도하거나 릴리스 페이지에서 직접 받아 주세요");
            }

            throw new HttpRequestException(
                $"GitHub 응답 오류 ({(int)response.StatusCode}): {TryReadApiMessage(body) ?? response.ReasonPhrase}");
        }

        return PickSetupUrl(body);
    }

    /// <summary>릴리스 JSON에서 Setup 자산 URL 선택 — 정확한 자산명 우선, "-Setup.exe" 접미 폴백.
    /// (미래에 arm64 등 두 번째 Setup 자산이 생겨도 잘못 집지 않도록.) 비JSON이면 HttpRequestException.</summary>
    public static string? PickSetupUrl(string releaseJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(releaseJson);
        }
        catch (JsonException)
        {
            throw new HttpRequestException("GitHub 응답을 해석할 수 없습니다 (프록시/보안장비 간섭 가능)");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? fallback = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null
                    || !asset.TryGetProperty("browser_download_url", out var urlProperty)
                    || urlProperty.GetString() is not { } url)
                {
                    continue;
                }

                if (string.Equals(name, SetupLocator.SetupFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }

                if (fallback is null && name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    fallback = url;
                }
            }

            return fallback;
        }
    }

    /// <summary>스트리밍 다운로드. progress(pct, bytes): Content-Length 있으면 pct 0~100, 없으면 null.
    /// 완료 후 실제 기록 바이트가 Content-Length와 다르면 IOException (잘린 다운로드 실행 방지).</summary>
    public async Task DownloadAsync(
        string url, string destinationPath, Action<double?, long>? progress, CancellationToken cancellationToken)
    {
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stallCts.CancelAfter(StallTimeout);

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stallCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        var source = await response.Content.ReadAsStreamAsync(stallCts.Token).ConfigureAwait(false);
        long written = 0;
        await using (source.ConfigureAwait(false))
        {
            var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (destination.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false)) > 0)
                {
                    stallCts.CancelAfter(StallTimeout); // 수신이 이어지는 한 리셋 — 총량 제한 없음
                    await destination.WriteAsync(buffer.AsMemory(0, read), stallCts.Token).ConfigureAwait(false);
                    written += read;
                    progress?.Invoke(total is > 0 ? written * 100.0 / total.Value : null, written);
                }
            }
        }

        if (total is > 0 && written != total.Value)
        {
            throw new IOException($"download truncated: {written}/{total.Value} bytes");
        }
    }

    private static string? TryReadApiMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
