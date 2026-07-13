using ReloadedDropIn.Core.Manifests;

namespace ReloadedDropIn.Core.Discovery;

/// <summary>
/// Finds Reloaded mods under a mods/ directory.
///
/// Rule (plan §12): a mod is a directory containing a valid ModConfig.json.
/// The scan is depth-limited, never executes mod code, rejects duplicate mod IDs
/// deterministically (lexicographically-first directory wins), and reports every
/// ignored entry with a reason.
/// </summary>
public sealed class ModScanner
{
    /// <summary>How many directory levels below mods/ may contain a manifest.</summary>
    public int MaxDepth { get; init; } = 3;

    public ScanResult Scan(string modsDirectory)
    {
        var mods = new List<DiscoveredMod>();
        var issues = new List<ScanIssue>();

        if (!Directory.Exists(modsDirectory))
            return new ScanResult { Mods = [], Issues = [] };

        foreach (var file in Directory.EnumerateFiles(modsDirectory))
        {
            if (!Path.GetFileName(file).Equals("PUT_MODS_HERE.txt", StringComparison.OrdinalIgnoreCase))
                issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, file, "loose file in mods/ (mods must be extracted folders)"));
        }

        ScanDirectory(modsDirectory, depth: 0, mods, issues);

        // Deterministic order: sort candidates by directory path, then keep the first
        // occurrence of each ModId so duplicate resolution never depends on OS enumeration order.
        var sorted = mods.OrderBy(m => m.Directory, StringComparer.Ordinal).ToList();
        var byId = new Dictionary<string, DiscoveredMod>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<DiscoveredMod>();
        foreach (var mod in sorted)
        {
            if (byId.TryGetValue(mod.ModId, out var existing))
            {
                issues.Add(new ScanIssue(
                    ScanIssueKind.DuplicateModId,
                    mod.Directory,
                    $"duplicate ModId '{mod.ModId}' (already provided by {existing.Directory})"));
                continue;
            }

            byId.Add(mod.ModId, mod);
            unique.Add(mod);
        }

        return new ScanResult
        {
            Mods = unique.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).ToList(),
            Issues = issues,
        };
    }

    private void ScanDirectory(string directory, int depth, List<DiscoveredMod> mods, List<ScanIssue> issues)
    {
        if (depth > MaxDepth)
            return;

        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory);
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new ScanIssue(ScanIssueKind.IgnoredEntry, directory, "permission denied"));
            return;
        }

        foreach (var subdirectory in subdirectories)
        {
            var manifestPath = Path.Combine(subdirectory, ModManifest.FileName);
            if (File.Exists(manifestPath))
            {
                var manifest = ModManifest.TryParse(File.ReadAllText(manifestPath), out var error);
                if (manifest is null)
                    issues.Add(new ScanIssue(ScanIssueKind.InvalidManifest, manifestPath, error!));
                else
                    mods.Add(new DiscoveredMod { Manifest = manifest, Directory = subdirectory });

                // A manifest root is a leaf: don't look for nested mods inside a mod.
                continue;
            }

            ScanDirectory(subdirectory, depth + 1, mods, issues);
        }
    }
}
