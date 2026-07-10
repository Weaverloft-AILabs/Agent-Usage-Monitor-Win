using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeUsageMonitor.Installer.Install;

/// <summary>GitHub 최신 릴리스에서 Velopack Setup 자산을 찾고 스트리밍 다운로드한다.</summary>
public sealed class GitHubReleaseClient : IDisposable
{
    private readonly HttpClient _http;

    public GitHubReleaseClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentUsageMonitor-Installer/2.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>최신 릴리스에서 "-Setup.exe"로 끝나는 자산의 다운로드 URL. 없으면 null.</summary>
    public async Task<string?> FindLatestSetupUrlAsync(CancellationToken cancellationToken)
    {
        var json = await _http.GetStringAsync(InstallerUrls.LatestReleaseApi, cancellationToken)
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is not null
                && name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var url))
            {
                return url.GetString();
            }
        }

        return null;
    }

    /// <summary>스트리밍 다운로드. progress = 0~100 (Content-Length 없으면 보고 생략).</summary>
    public async Task DownloadAsync(
        string url, string destinationPath, Action<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (destination.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    if (total is > 0)
                    {
                        progress?.Invoke(written * 100.0 / total.Value);
                    }
                }
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
