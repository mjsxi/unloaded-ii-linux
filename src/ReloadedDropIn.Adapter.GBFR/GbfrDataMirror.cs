using System.Text.Json;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Core.Filesystem;

namespace ReloadedDropIn.Adapter.GBFR;

/// <summary>
/// Physically mirrors mod files into the game's data/ folder.
///
/// Why: Relink reads index-registered external files through the same
/// unhookable I/O path as its archives (verified via file monitor: the game
/// opened the modded data.i and still never requested data\model\... through
/// NT APIs). Redirects can't serve those reads, so the bytes must be on disk
/// at data/&lt;gamePath&gt; — the same approach Relink Mod Manager uses.
///
/// Sources, in priority order (later wins, matching the utility manager's
/// last-registration-wins conflict rule):
///   1. each enabled mod's GBFR/data/** (raw files), in load order
///   2. the utility manager's temp/&lt;modId&gt;/** (its converted/upgraded
///      outputs: .json→.msg, upgraded .minfo/.mmat), same order
///
/// Safety: a manifest records every file we placed; stock files we overwrite
/// are backed up first and restored when no mod claims the path anymore.
///
/// Timing: <see cref="Reconcile"/> (copies AND deletions) must run PRE-load,
/// before the redirector's hooks exist — in-process, a registered
/// data\&lt;path&gt; open gets rewritten to the mod's own file, turning our copy
/// into copy-onto-itself (observed: sharing-violation crash) and our delete
/// into deleting the mod's source. <see cref="MirrorFreshConversions"/> runs
/// POST-load for outputs the utility manager regenerated during mod load; it
/// writes via an unregistered temp name + rename, which bypasses redirects.
/// </summary>
public sealed class GbfrDataMirror(AdapterContext context, string? utilityManagerDirectory = null)
{
    private sealed record MirrorManifest(List<string> OwnedPaths, List<string> DisplacedStockPaths);

    private string DataDirectory => Path.Combine(context.GameDirectory, "data");
    private string TempDirectory => Path.Combine(
        utilityManagerDirectory ?? Path.Combine(context.ModsDirectory, "gbfrelink.utility.manager"),
        "temp");
    private string BackupDirectory => Path.Combine(context.BackupsDirectory, "gbfr");
    private string StockBackupDirectory => Path.Combine(BackupDirectory, "displaced-stock");
    private string ManifestPath => Path.Combine(BackupDirectory, "mirror-manifest.json");

    /// <summary>A mod to mirror: its ID (keys temp conversions) and its actual
    /// directory (folder names routinely differ from ModIds).</summary>
    public readonly record struct MirrorSource(string ModId, string Directory);

    /// <summary>
    /// Brings data/ in sync with the given enabled mods (load order, later wins).
    /// Returns log lines.
    /// </summary>
    public IReadOnlyList<string> Reconcile(IReadOnlyList<MirrorSource> enabledModsInLoadOrder)
    {
        var log = new List<string>();
        var desired = BuildDesiredFileMap(enabledModsInLoadOrder);
        var manifest = LoadManifest();
        var previouslyOwned = new HashSet<string>(manifest.OwnedPaths, StringComparer.OrdinalIgnoreCase);

        // Remove files we placed earlier that no mod provides anymore.
        var removed = 0;
        foreach (var stale in manifest.OwnedPaths.Where(p => !desired.ContainsKey(p)).ToList())
        {
            var target = Path.Combine(DataDirectory, stale);
            if (File.Exists(target))
                File.Delete(target);

            var stockBackup = Path.Combine(StockBackupDirectory, stale);
            if (manifest.DisplacedStockPaths.Contains(stale, StringComparer.OrdinalIgnoreCase) && File.Exists(stockBackup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(stockBackup, target);
                manifest.DisplacedStockPaths.RemoveAll(p => p.Equals(stale, StringComparison.OrdinalIgnoreCase));
                log.Add($"restored stock file {stale}");
            }

            manifest.OwnedPaths.RemoveAll(p => p.Equals(stale, StringComparison.OrdinalIgnoreCase));
            removed++;
        }

        // Copy new/changed files.
        var copied = 0;
        foreach (var (relativePath, sourcePath) in desired)
        {
            var target = Path.Combine(DataDirectory, relativePath);

            if (File.Exists(target) && !previouslyOwned.Contains(relativePath))
            {
                // A file we don't own is in the way: that's a stock loose file. Back it up once.
                var stockBackup = Path.Combine(StockBackupDirectory, relativePath);
                if (!File.Exists(stockBackup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(stockBackup)!);
                    File.Copy(target, stockBackup);
                    manifest.DisplacedStockPaths.Add(relativePath);
                    log.Add($"backed up stock file {relativePath}");
                }
            }

            if (!IsUpToDate(sourcePath, target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourcePath, target, overwrite: true);
                copied++;
            }

            if (!manifest.OwnedPaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
                manifest.OwnedPaths.Add(relativePath);
        }

        SaveManifest(manifest);
        log.Add($"mirrored {desired.Count} file(s) into data/ ({copied} copied, {removed} removed)");
        return log;
    }

    /// <summary>
    /// Post-load pass: the utility manager regenerates converted files
    /// (.json→.msg, upgraded .minfo/.mmat) in its temp folder during mod load,
    /// after the pre-load Reconcile already ran. Push them into data/ via
    /// temp-name + rename (redirect-proof, see class docs). Unconditional — the
    /// set is small and metadata reads of registered paths can't be trusted here.
    /// </summary>
    public IReadOnlyList<string> MirrorFreshConversions(IReadOnlyList<string> enabledModIdsInLoadOrder)
    {
        var log = new List<string>();
        var manifest = LoadManifest();
        var copied = 0;

        foreach (var modId in enabledModIdsInLoadOrder)
        {
            var root = Path.Combine(TempDirectory, modId);
            if (!Directory.Exists(root))
                continue;

            foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relativePath = Canonical(Path.GetRelativePath(root, source));
                var target = Path.Combine(DataDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                var staging = target + ".dropin-staging";
                File.Copy(source, staging, overwrite: true);
                File.Move(staging, target, overwrite: true);
                copied++;

                if (!manifest.OwnedPaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
                    manifest.OwnedPaths.Add(relativePath);
            }
        }

        SaveManifest(manifest);
        log.Add($"refreshed {copied} converted file(s) in data/");
        return log;
    }

    /// <summary>Maps relative game path → source file, later mods overriding earlier ones.</summary>
    private Dictionary<string, string> BuildDesiredFileMap(IReadOnlyList<MirrorSource> enabledModsInLoadOrder)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in enabledModsInLoadOrder)
            AddTree(map, Path.Combine(mod.Directory, "GBFR", "data"));

        // Converted/upgraded outputs override raw files. The utility manager keys
        // its temp folders by ModId, not by directory name.
        foreach (var mod in enabledModsInLoadOrder)
            AddTree(map, Path.Combine(TempDirectory, mod.ModId));

        return map;
    }

    /// <summary>
    /// Canonical relative-path form: forward slashes. The manifest is written
    /// in-process under Wine (backslashes) but read by Linux-side tooling
    /// (uninstall script, CLI restore); '/' works as a separator on both sides.
    /// </summary>
    private static string Canonical(string relativePath) => relativePath.Replace('\\', '/');

    private static void AddTree(Dictionary<string, string> map, string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Canonical(Path.GetRelativePath(root, file));
            map[relative] = file;
        }
    }

    private static bool IsUpToDate(string source, string target)
    {
        if (!File.Exists(target))
            return false;

        var sourceInfo = new FileInfo(source);
        var targetInfo = new FileInfo(target);
        return sourceInfo.Length == targetInfo.Length &&
               sourceInfo.LastWriteTimeUtc <= targetInfo.LastWriteTimeUtc;
    }

    private MirrorManifest LoadManifest()
    {
        if (!File.Exists(ManifestPath))
            return new MirrorManifest([], []);
        try
        {
            var manifest = JsonSerializer.Deserialize<MirrorManifest>(File.ReadAllText(ManifestPath))
                ?? new MirrorManifest([], []);
            // Canonicalize entries written by older versions (backslash separators).
            return new MirrorManifest(
                [.. manifest.OwnedPaths.Select(Canonical)],
                [.. manifest.DisplacedStockPaths.Select(Canonical)]);
        }
        catch (JsonException)
        {
            return new MirrorManifest([], []);
        }
    }

    private void SaveManifest(MirrorManifest manifest)
    {
        Directory.CreateDirectory(BackupDirectory);
        AtomicFile.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
    }
}
