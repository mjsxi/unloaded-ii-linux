using System.Security.Cryptography;
using System.Text.Json;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Core.Filesystem;

namespace ReloadedDropIn.Adapter.GBFR;

/// <summary>
/// Manages Relink's archive index (data.i) on disk.
///
/// Why: the game reads data.i (and the data.N archives) through an I/O path the
/// file redirector cannot intercept — verified with the Reloaded file monitor —
/// so gbfrelink.utility.manager's runtime index *redirect* is invisible to the
/// game. Loose external files ARE read through normal, hookable I/O. Therefore:
/// restore a pristine data.i before mods load (the utility manager builds its
/// modded index from the on-disk one), then copy its rebuilt index over data.i
/// while the game is still frozen at its entry point.
///
/// Safety per plan §8: hash-verified baseline backup, never overwrite the only
/// backup, detect game updates (unknown hash) and re-baseline.
/// </summary>
public sealed class GbfrDataIndex(AdapterContext context, string? utilityManagerDirectory = null)
{
    private sealed record IndexState(string? OriginalSha256, string? ModdedSha256);

    private string GameIndexPath => Path.Combine(context.GameDirectory, "data.i");
    private string BackupDirectory => Path.Combine(context.BackupsDirectory, "gbfr");
    private string BackupPath => Path.Combine(BackupDirectory, "data.i.orig");
    private string StatePath => Path.Combine(BackupDirectory, "state.json");

    /// <summary>Temp index the utility manager rebuilds on every mod load. The
    /// manager's folder may live anywhere under mods/ (e.g. mods/_base-mods/).</summary>
    public string ModdedIndexPath => Path.Combine(
        utilityManagerDirectory ?? Path.Combine(context.ModsDirectory, "gbfrelink.utility.manager"),
        "temp", "data.i");

    /// <summary>
    /// Pre-load step: ensure data.i on disk is the pristine original and a valid
    /// backup exists. Returns log lines.
    /// </summary>
    public IReadOnlyList<string> EnsureBaseline()
    {
        var log = new List<string>();
        if (!File.Exists(GameIndexPath))
        {
            log.Add($"data.i not found at {GameIndexPath}; skipping baseline");
            return log;
        }

        Directory.CreateDirectory(BackupDirectory);
        var currentHash = HashFile(GameIndexPath);
        var state = LoadState();

        if (state?.OriginalSha256 is null || !File.Exists(BackupPath))
        {
            File.Copy(GameIndexPath, BackupPath, overwrite: true);
            SaveState(new IndexState(currentHash, null));
            log.Add($"created baseline backup of data.i ({currentHash[..12]}…)");
        }
        else if (currentHash == state.OriginalSha256)
        {
            log.Add("data.i is pristine");
        }
        else if (currentHash == state.ModdedSha256)
        {
            File.Copy(BackupPath, GameIndexPath, overwrite: true);
            log.Add("restored pristine data.i from backup (was modded by previous run)");
        }
        else
        {
            // Unknown content: most likely a game update replaced data.i.
            // The old backup no longer matches the game; re-baseline.
            File.Copy(GameIndexPath, BackupPath, overwrite: true);
            SaveState(new IndexState(currentHash, null));
            log.Add($"data.i changed unexpectedly (game update?) — refreshed baseline backup ({currentHash[..12]}…)");
        }

        return log;
    }

    /// <summary>
    /// Post-load step: if the utility manager produced a fresh modded index,
    /// apply it over data.i. <paramref name="notBefore"/> guards against stale
    /// output from a previous run. Returns log lines.
    /// </summary>
    public IReadOnlyList<string> ApplyModdedIndex(DateTime notBefore)
    {
        var log = new List<string>();
        if (!File.Exists(GameIndexPath))
        {
            log.Add("data.i not found; nothing to apply");
            return log;
        }

        if (!File.Exists(ModdedIndexPath))
        {
            log.Add("no modded index produced (utility manager not active?); data.i left pristine");
            return log;
        }

        if (File.GetLastWriteTimeUtc(ModdedIndexPath) < notBefore)
        {
            log.Add("modded index is stale (not rebuilt this launch); data.i left pristine");
            return log;
        }

        if (LoadState()?.OriginalSha256 is null || !File.Exists(BackupPath))
        {
            log.Add("refusing to apply modded index: no baseline backup exists");
            return log;
        }

        // Copy-then-rename so an interrupted write can't truncate data.i.
        var stagingPath = GameIndexPath + ".dropin-staging";
        File.Copy(ModdedIndexPath, stagingPath, overwrite: true);
        File.Move(stagingPath, GameIndexPath, overwrite: true);

        var moddedHash = HashFile(GameIndexPath);
        SaveState(LoadState()! with { ModdedSha256 = moddedHash });
        log.Add($"applied modded index to data.i ({moddedHash[..12]}…)");
        return log;
    }

    /// <summary>Restores the pristine data.i (uninstall/repair path).</summary>
    public IReadOnlyList<string> Restore()
    {
        var log = new List<string>();
        if (!File.Exists(BackupPath))
        {
            log.Add("no backup to restore from");
            return log;
        }

        File.Copy(BackupPath, GameIndexPath, overwrite: true);
        var state = LoadState();
        if (state is not null)
            SaveState(state with { ModdedSha256 = null });
        log.Add("restored pristine data.i from backup");
        return log;
    }

    private IndexState? LoadState()
    {
        if (!File.Exists(StatePath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<IndexState>(File.ReadAllText(StatePath));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SaveState(IndexState state) =>
        AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(state));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
