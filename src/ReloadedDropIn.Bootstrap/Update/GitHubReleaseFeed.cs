using System.Net;
using System.Text.Json;

namespace ReloadedDropIn.Bootstrap.Update;

/// <summary>
/// GitHub releases API client. Unauthenticated (60 requests/hour/IP), which the
/// once-per-week check throttle in <see cref="BaseModInstaller"/> stays far under.
/// Runs inside the game process while it's frozen at its entry point, so every
/// timeout is deliberately short — an offline box must not stall the launch.
///
/// When a <paramref name="cacheDirectory"/> is provided, ETags are cached between
/// launches: a 304 Not Modified costs zero rate-limit tokens and returns the
/// previously seen asset list.
/// </summary>
public sealed class GitHubReleaseFeed(string? cacheDirectory = null) : IReleaseFeed, IDisposable
{
    private readonly HttpClient _http = CreateClient();
    private readonly Dictionary<string, CachedRelease> _etagCache = LoadCache(cacheDirectory);

    private sealed record CachedRelease(string ETag, List<ReleaseAsset> Assets);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Unloaded-II-DropIn");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public async Task<IReadOnlyList<ReleaseAsset>?> GetLatestReleaseAssetsAsync(string repo, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_etagCache.TryGetValue(repo, out var cached))
                request.Headers.IfNoneMatch.ParseAdd(cached.ETag);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                return cached.Assets;

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var assets = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => new ReleaseAsset(
                    a.GetProperty("name").GetString()!,
                    a.GetProperty("browser_download_url").GetString()!))
                .ToList();

            var etag = response.Headers.ETag?.Tag;
            if (etag is not null && cacheDirectory is not null)
            {
                _etagCache[repo] = new CachedRelease(etag, assets);
                SaveCache();
            }

            return assets;
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

    private static string CachePath(string dir) => Path.Combine(dir, "github-etags.json");

    private static Dictionary<string, CachedRelease> LoadCache(string? dir)
    {
        if (dir is null)
            return [];
        try
        {
            var path = CachePath(dir);
            if (!File.Exists(path))
                return [];
            return JsonSerializer.Deserialize<Dictionary<string, CachedRelease>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void SaveCache()
    {
        if (cacheDirectory is null)
            return;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            Core.Filesystem.AtomicFile.WriteAllText(CachePath(cacheDirectory),
                JsonSerializer.Serialize(_etagCache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // Best effort; a failed cache write just means the next check uses a token.
        }
    }
}
