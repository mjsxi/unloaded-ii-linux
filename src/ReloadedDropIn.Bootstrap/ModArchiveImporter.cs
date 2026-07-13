using System.Text.Json;
using ReloadedDropIn.Adapter.Abstractions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ReloadedDropIn.Bootstrap;

/// <summary>
/// Lets users drop downloaded mod archives (.zip/.7z/.rar) straight into mods/
/// — sync extracts them into proper mod folders before the scan, so a dropped
/// archive loads on that same launch.
///
/// Robust against the usual packaging mess: the ModConfig.json may sit at the
/// archive root or nested inside one or more folders (each detected root is
/// installed, so multi-mod archives work). Folders are named after the ModId,
/// and re-dropping a newer archive of an installed mod replaces it in place.
/// The archive is deleted only after every mod in it installed successfully;
/// anything unrecognized is left where it is and logged.
/// </summary>
public sealed class ModArchiveImporter(AdapterContext context)
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];

    /// <summary>Imports every archive at the top level of mods/. Returns log lines; never throws.</summary>
    public IReadOnlyList<string> ImportAll()
    {
        var log = new List<string>();
        if (!Directory.Exists(context.ModsDirectory))
            return log;

        foreach (var archivePath in Directory.EnumerateFiles(context.ModsDirectory)
                     .Where(f => ArchiveExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                log.AddRange(Import(archivePath));
            }
            catch (Exception ex)
            {
                log.Add($"{Path.GetFileName(archivePath)}: import failed ({ex.Message}); archive left in place");
            }
        }

        return log;
    }

    private IReadOnlyList<string> Import(string archivePath)
    {
        var log = new List<string>();
        var name = Path.GetFileName(archivePath);
        var allInstalled = true;

        using (var archive = ArchiveFactory.Open(archivePath))
        {
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            var roots = FindModRoots(entries.Select(e => Canonical(e.Key!)));
            if (roots.Count == 0)
            {
                log.Add($"{name}: no ModConfig.json found inside; not a Reloaded mod archive, leaving it in place");
                return log;
            }

            foreach (var root in roots)
            {
                var result = InstallModRoot(archive, entries, root);
                log.Add($"{name}: {result.Message}");
                allInstalled &= result.Success;
            }
        }

        if (allInstalled)
        {
            File.Delete(archivePath);
            log.Add($"{name}: archive removed (contents installed)");
        }

        return log;
    }

    private (bool Success, string Message) InstallModRoot(
        IArchive archive, List<IArchiveEntry> entries, string root)
    {
        // Read the ModId from the archive before touching the disk.
        var configKey = root.Length == 0 ? "ModConfig.json" : $"{root}/ModConfig.json";
        var configEntry = entries.First(e => Canonical(e.Key!).Equals(configKey, StringComparison.OrdinalIgnoreCase));
        string? modId;
        using (var stream = configEntry.OpenEntryStream())
        using (var doc = JsonDocument.Parse(stream))
        {
            modId = doc.RootElement.TryGetProperty("ModId", out var idProperty) ? idProperty.GetString() : null;
        }

        if (string.IsNullOrWhiteSpace(modId))
            return (false, "ModConfig.json has no ModId; skipped");

        // Where does this mod live? Replace an existing install in place; never
        // touch base mods (those are managed by the package/auto-updater).
        var existing = new Core.Discovery.ModScanner().Scan(context.ModsDirectory).Mods
            .FirstOrDefault(m => m.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase));
        var separator = Path.DirectorySeparatorChar;
        if (existing is not null &&
            existing.Directory.Contains($"{separator}_base-mods{separator}", StringComparison.OrdinalIgnoreCase))
            return (false, $"'{modId}' is a managed base mod; not overwriting it");

        var target = existing?.Directory ?? Path.Combine(context.ModsDirectory, Sanitize(modId));
        var staging = target + ".import-staging";
        var retired = target + ".import-old";
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            var prefix = root.Length == 0 ? "" : root + "/";
            foreach (var entry in entries)
            {
                var key = Canonical(entry.Key!);
                if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = key[prefix.Length..];
                if (relative.Length == 0 || relative.Contains(".."))
                    continue;

                var destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.WriteToFile(destination, new ExtractionOptions { Overwrite = true });
            }

            if (Directory.Exists(retired))
                Directory.Delete(retired, recursive: true);
            if (Directory.Exists(target))
                Directory.Move(target, retired);
            Directory.Move(staging, target);
            if (Directory.Exists(retired))
                Directory.Delete(retired, recursive: true);

            return (true, existing is not null
                ? $"replaced installed mod '{modId}'"
                : $"installed mod '{modId}'");
        }
        catch (Exception ex)
        {
            try
            {
                if (!Directory.Exists(target) && Directory.Exists(retired))
                    Directory.Move(retired, target);
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (IOException) { /* best effort */ }

            return (false, $"installing '{modId}' failed ({ex.Message})");
        }
    }

    /// <summary>
    /// Directories inside the archive that directly contain a ModConfig.json,
    /// excluding any nested inside another mod root (mods ship their own deps
    /// folders sometimes; the outermost config wins).
    /// </summary>
    public static List<string> FindModRoots(IEnumerable<string> canonicalKeys)
    {
        var roots = canonicalKeys
            .Where(k => k.Equals("ModConfig.json", StringComparison.OrdinalIgnoreCase) ||
                        k.EndsWith("/ModConfig.json", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Length == "ModConfig.json".Length ? "" : k[..^"/ModConfig.json".Length])
            .OrderBy(r => r.Length)
            .ToList();

        var result = new List<string>();
        foreach (var root in roots)
        {
            // Shortest-first ordering means any earlier kept root that prefixes
            // this one makes it a nested copy (e.g. a bundled dependency).
            var nested = result.Any(kept =>
                kept.Length == 0 || root.StartsWith(kept + "/", StringComparison.OrdinalIgnoreCase));
            if (!nested)
                result.Add(root);
        }

        return result;
    }

    private static string Canonical(string key) => key.Replace('\\', '/').TrimStart('/');

    private static string Sanitize(string modId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(modId.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
