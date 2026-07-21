using System.Text.Json;
using ReloadedDropIn.Core.Filesystem;

namespace ReloadedDropIn.Bootstrap.Update;

/// <summary>
/// Checks whether a newer Unloaded-II release exists on GitHub and writes the
/// result to reloaded-dropin/update-check.json for the in-game overlay to read.
/// Runs inside the existing network deadline — never blocks the launch.
/// </summary>
public sealed class SelfUpdateChecker(string dropInDirectory)
{
    private const string ReleaseRepo = "mjsxi/unloaded-ii-linux";

    public sealed record UpdateCheckResult
    {
        public string CurrentVersion { get; init; } = "";
        public string? LatestVersion { get; init; }
        public string? DownloadUrl { get; init; }
        public bool UpdateAvailable { get; init; }
        public DateTime CheckedUtc { get; init; } = DateTime.UtcNow;
    }

    public static string PathFor(string dropInDir) => Path.Combine(dropInDir, "update-check.json");

    /// <summary>Checks GitHub and writes update-check.json. Returns log lines.</summary>
    public async Task<IReadOnlyList<string>> CheckAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var log = new List<string>();
        try
        {
            var current = ReadCurrentVersion();
            log.Add($"self-update: current version {current}");

            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{ReleaseRepo}/releases/latest", cancellationToken)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = tag.TrimStart('v', 'V');

            var downloadUrl = doc.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                .GetProperty("browser_download_url").GetString();

            var updateAvailable = Version.TryParse(latest, out var latestVer) &&
                                  Version.TryParse(current, out var currentVer) &&
                                  latestVer > currentVer;

            var result = new UpdateCheckResult
            {
                CurrentVersion = current,
                LatestVersion = latest,
                DownloadUrl = updateAvailable ? downloadUrl : null,
                UpdateAvailable = updateAvailable,
            };

            AtomicFile.WriteAllText(PathFor(dropInDirectory),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");

            log.Add(updateAvailable
                ? $"self-update: v{latest} available (current: {current})"
                : $"self-update: up to date ({current})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            log.Add($"self-update: check failed ({ex.GetType().Name}); skipping");
        }

        return log;
    }

    private string ReadCurrentVersion()
    {
        try
        {
            var path = Path.Combine(dropInDirectory, "version.json");
            if (!File.Exists(path))
                return "0.0.0";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("dropin").GetString() ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }
}
