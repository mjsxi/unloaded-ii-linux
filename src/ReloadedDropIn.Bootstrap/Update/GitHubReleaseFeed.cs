using System.Text.Json;

namespace ReloadedDropIn.Bootstrap.Update;

/// <summary>
/// GitHub releases API client. Unauthenticated (60 requests/hour/IP), which the
/// once-per-day check throttle in <see cref="BaseModInstaller"/> stays far under.
/// Runs inside the game process while it's frozen at its entry point, so every
/// timeout is deliberately short — an offline box must not stall the launch.
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    private readonly HttpClient _http;

    public GitHubReleaseFeed()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Unloaded-II-DropIn");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<IReadOnlyList<ReleaseAsset>?> GetLatestReleaseAssetsAsync(string repo, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _http.GetStringAsync(
                $"https://api.github.com/repos/{repo}/releases/latest", cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => new ReleaseAsset(
                    a.GetProperty("name").GetString()!,
                    a.GetProperty("browser_download_url").GetString()!))];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    public async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
