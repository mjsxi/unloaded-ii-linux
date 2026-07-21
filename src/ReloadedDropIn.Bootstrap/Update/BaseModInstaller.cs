using System.Text.Json;
using System.Text.RegularExpressions;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Core.Filesystem;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ReloadedDropIn.Bootstrap.Update;

/// <summary>
/// Installs and updates the required base mods (utility manager + its
/// libraries) from their authors' official GitHub releases. The package does
/// NOT redistribute these mods — per their maintainers' wishes — so the first
/// online launch downloads them, and later launches keep them current. Their
/// releases stay the single source of truth.
///
/// Constraints: runs pre-load inside the frozen game process, so it must be
/// fast and failure-proof — any error (offline, rate limit, bad archive)
/// leaves the installed state untouched and never blocks the launch. UPDATE
/// checks are throttled via reloaded-dropin/update.json (default: once per
/// week) and can be disabled there ("AutoUpdate": false); INSTALLING a missing
/// base mod ignores both, since nothing works until it exists.
/// </summary>
public sealed class BaseModInstaller(AdapterContext context, IReleaseFeed feed)
{
    /// <summary>User-editable knobs + check timestamp, at reloaded-dropin/update.json.</summary>
    public sealed record UpdateSettings
    {
        public bool AutoUpdate { get; init; } = true;
        // Weekly: base mods change rarely, and a stale check only delays an
        // update, never blocks a missing-mod install (those bypass the
        // throttle entirely).
        public int CheckIntervalHours { get; init; } = 168;
        public DateTime LastCheckedUtc { get; init; } = DateTime.MinValue;

        public static string PathFor(string dropInDirectory) => Path.Combine(dropInDirectory, "update.json");

        public static UpdateSettings Load(string dropInDirectory)
        {
            var path = PathFor(dropInDirectory);
            if (!File.Exists(path))
                return new UpdateSettings();
            try
            {
                return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path)) ?? new UpdateSettings();
            }
            catch (JsonException)
            {
                return new UpdateSettings();
            }
        }

        public void Save(string dropInDirectory) =>
            AtomicFile.WriteAllText(PathFor(dropInDirectory),
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Version embedded at the end of a Reloaded release-asset name,
    /// e.g. "gbfrelink.utility.manager2.0.1.7z" → 2.0.1. Null when the name
    /// doesn't match "&lt;prefix&gt;&lt;version&gt;.&lt;7z|zip&gt;".</summary>
    public static Version? ParseAssetVersion(string assetName, string expectedPrefix)
    {
        var match = Regex.Match(assetName, @"^(?<prefix>.+?)(?<version>\d+(?:\.\d+){1,3})\.(?:7z|zip)$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
        if (!match.Groups["prefix"].Value.Equals(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return Version.TryParse(match.Groups["version"].Value, out var version) ? Normalize(version) : null;
    }

    /// <summary>"2.0.1" and "2.0.1.0" must compare equal.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Human form of a normalized version: "2.0.1", not "2.0.1.0".</summary>
    private static string Fmt(Version v) => v.Revision > 0 ? v.ToString() : v.ToString(3);

    /// <summary>
    /// Installs missing base mods and (throttled) updates present ones.
    /// Returns log lines; never throws.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAsync(
        IReadOnlyList<RequiredMod> requiredMods, CancellationToken cancellationToken)
    {
        var log = new List<string>();
        try
        {
            // Scan once and build a lookup; FindInstalledDirectory used to
            // re-enumerate mods/ per required mod.
            var modDirectories = context.ModDirectories
                ?? new Core.Discovery.ModScanner().Scan(context.ModsDirectory).Mods
                    .ToDictionary(m => m.ModId, m => m.Directory, StringComparer.OrdinalIgnoreCase);

            var installable = requiredMods
                .Where(m => m.UpdateRepo is not null && m.UpdateAssetPrefix is not null)
                .ToList();
            var missing = installable
                .Where(m => !modDirectories.ContainsKey(m.Id))
                .ToList();
            var settings = UpdateSettings.Load(context.DropInDirectory);
            var updatesDue = settings.AutoUpdate &&
                DateTime.UtcNow - settings.LastCheckedUtc >= TimeSpan.FromHours(Math.Max(settings.CheckIntervalHours, 1));

            List<RequiredMod> toProcess;
            if (updatesDue)
            {
                toProcess = installable;
                // Record the attempt up front: an offline box pays the DNS
                // timeout once per interval, not on every launch.
                (settings with { LastCheckedUtc = DateTime.UtcNow }).Save(context.DropInDirectory);
            }
            else
            {
                // Missing mods are fetched regardless of throttle or opt-out:
                // nothing works until they exist.
                toProcess = missing;
                if (missing.Count == 0)
                {
                    log.Add(settings.AutoUpdate
                        ? $"last checked {settings.LastCheckedUtc:u}; skipping until the interval elapses"
                        : "auto-update disabled in update.json");
                    return log;
                }
            }

            if (missing.Count > 0)
                log.Add($"base mod(s) not installed yet: {string.Join(", ", missing.Select(m => m.Id))} — fetching from official releases");

            foreach (var mod in toProcess)
            {
                var line = await UpdateOneAsync(mod, modDirectories, cancellationToken).ConfigureAwait(false);
                log.Add(line);
                if (line.EndsWith("(offline?)", StringComparison.Ordinal))
                {
                    log.Add("feed unreachable; skipping remaining checks (missing mods retry next launch)");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            log.Add($"install/update check failed (continuing with installed versions): {ex.Message}");
        }

        return log;
    }

    private async Task<string> UpdateOneAsync(
        RequiredMod mod, IReadOnlyDictionary<string, string> modDirectories, CancellationToken cancellationToken)
    {
        var assets = await feed.GetLatestReleaseAssetsAsync(mod.UpdateRepo!, cancellationToken).ConfigureAwait(false);
        if (assets is null)
            return $"{mod.Id}: release feed unreachable (offline?)";

        var candidate = assets
            .Select(a => (Asset: a, Version: ParseAssetVersion(a.Name, mod.UpdateAssetPrefix!)))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();
        if (candidate.Asset is null)
            return $"{mod.Id}: latest release has no asset matching '{mod.UpdateAssetPrefix}<version>'";

        modDirectories.TryGetValue(mod.Id, out var installedDirectory);
        var installedVersion = ReadInstalledVersion(installedDirectory);
        if (installedVersion is not null && candidate.Version <= installedVersion)
            return $"{mod.Id}: up to date ({Fmt(installedVersion)})";

        var payload = await feed.DownloadAsync(candidate.Asset.DownloadUrl, cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return $"{mod.Id}: download of {candidate.Asset.Name} failed; keeping installed version";

        var target = installedDirectory ?? Path.Combine(context.ModsDirectory, "_base-mods", mod.Id);
        return InstallArchive(mod.Id, candidate.Asset.Name, payload, target, installedVersion, candidate.Version!);
    }

    /// <summary>Extract to a staging sibling, sanity-check, then swap directories —
    /// a bad or truncated archive can never destroy the working install.</summary>
    private static string InstallArchive(
        string modId, string assetName, byte[] payload, string targetDirectory, Version? fromVersion, Version toVersion)
    {
        var staging = targetDirectory + ".update-staging";
        var retired = targetDirectory + ".update-old";
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            using (var stream = new MemoryStream(payload))
            using (var archive = ArchiveFactory.Open(stream))
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    entry.WriteToDirectory(staging,
                        new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }

            var manifestPath = Path.Combine(staging, "ModConfig.json");
            if (!File.Exists(manifestPath))
                throw new InvalidDataException($"{assetName} has no ModConfig.json at its root");
            using (var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
                var actualId = doc.RootElement.GetProperty("ModId").GetString();
                if (!string.Equals(actualId, modId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{assetName} contains ModId '{actualId}', expected '{modId}'");
            }

            if (Directory.Exists(retired))
                Directory.Delete(retired, recursive: true);
            if (Directory.Exists(targetDirectory))
                Directory.Move(targetDirectory, retired);
            Directory.Move(staging, targetDirectory);
            if (Directory.Exists(retired))
                Directory.Delete(retired, recursive: true);

            return fromVersion is null
                ? $"{modId}: installed {Fmt(toVersion)}"
                : $"{modId}: updated {Fmt(fromVersion)} -> {Fmt(toVersion)}";
        }
        catch (Exception ex)
        {
            // Roll back: if the live directory went missing mid-swap, put the old one back.
            try
            {
                if (!Directory.Exists(targetDirectory) && Directory.Exists(retired))
                    Directory.Move(retired, targetDirectory);
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (IOException) { /* best effort; next launch re-checks */ }

            return $"{modId}: update to {Fmt(toVersion)} failed ({ex.Message}); keeping installed version";
        }
    }

    private static Version? ReadInstalledVersion(string? modDirectory)
    {
        if (modDirectory is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modDirectory, "ModConfig.json")));
            return Version.TryParse(doc.RootElement.GetProperty("ModVersion").GetString(), out var version)
                ? Normalize(version)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }
}
