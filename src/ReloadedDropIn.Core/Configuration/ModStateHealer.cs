using System.Text.Json;
using ReloadedDropIn.Core.Filesystem;

namespace ReloadedDropIn.Core.Configuration;

/// <summary>
/// Invalidates game-loader caches when the ordered active-mod set changes.
/// The first run also invalidates them so an installation upgraded from a
/// version without state tracking starts from a known-clean baseline.
/// </summary>
public sealed class ModStateHealer
{
    private sealed record State
    {
        public int SchemaVersion { get; init; } = 1;
        public string[] ActiveMods { get; init; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<string> Reconcile(
        string gameDirectory,
        string dropInDirectory,
        string gameId,
        IReadOnlyList<string> activeMods,
        IReadOnlyList<string> disposablePaths)
    {
        var log = new List<string>();
        var canonicalMods = activeMods.Select(id => id.ToLowerInvariant()).ToArray();
        var statePath = Path.Combine(dropInDirectory, "state", $"{gameId}-active-mods.json");
        var previous = Load(statePath);

        if (previous is not null &&
            previous.SchemaVersion == 1 &&
            previous.ActiveMods.SequenceEqual(canonicalMods, StringComparer.Ordinal))
        {
            return log;
        }

        var failed = false;
        foreach (var path in disposablePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSafeDescendant(gameDirectory, path))
            {
                failed = true;
                log.Add($"refused unsafe disposable-state path: {path}");
                continue;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    log.Add($"removed stale loader directory: {Path.GetRelativePath(gameDirectory, path)}");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    log.Add($"removed stale loader file: {Path.GetRelativePath(gameDirectory, path)}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed = true;
                log.Add($"could not remove stale loader state {path}: {ex.Message}");
            }
        }

        if (failed)
        {
            log.Add("loader-state cleanup incomplete; it will be retried next launch");
            return log;
        }

        try
        {
            AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(
                new State { ActiveMods = canonicalMods }, JsonOptions) + Environment.NewLine);
            log.Add(previous is null
                ? "established clean active-mod baseline"
                : "active mod set changed; rebuilt loader-state baseline");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Add($"could not record loader-state baseline; cleanup will repeat next launch: {ex.Message}");
        }

        return log;
    }

    private static State? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSafeDescendant(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
        return relative != "." &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}
