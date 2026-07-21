using System.Text.Json;
using System.Text.Json.Nodes;
using ReloadedDropIn.Core.Configuration;
using ReloadedDropIn.Core.Discovery;

namespace ReloadedDropIn.Overlay;

public sealed record CatalogMod
{
    public required string ModId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Directory { get; init; }

    /// <summary>Base mods (shipped under mods/_base-mods/) can't be toggled off.</summary>
    public required bool IsBaseMod { get; init; }

    public required bool Enabled { get; set; }

    /// <summary>
    /// Editable user config (generated/User/Mods/&lt;id&gt;/Config.json), if any.
    /// Before the mod's first run that file doesn't exist yet, so it is seeded
    /// from the Config.json the mod ships in its own folder, if present.
    /// </summary>
    public JsonObject? UserConfig { get; set; }
    public string? UserConfigPath { get; init; }
    public bool ConfigExpanded { get; set; }
}

/// <summary>
/// Disk-backed model behind the overlay: discovered mods, their toggle state
/// (via the drop-in's overrides file), and their user configs.
/// </summary>
public sealed class ModCatalog(string gameDirectory)
{
    private string ModsDirectory => Path.Combine(gameDirectory, "mods");
    private string DropInDirectory => Path.Combine(gameDirectory, "reloaded-dropin");
    private string UserConfigRoot => Path.Combine(DropInDirectory, "generated", "User", "Mods");

    public List<CatalogMod> Mods { get; } = [];

    public bool HideWatermark { get; set; }

    /// <summary>Drop-in version from reloaded-dropin/version.json, for display.</summary>
    public string DropInVersion { get; private set; } = "dev";

    /// <summary>Update info from reloaded-dropin/update-check.json (written by sync).</summary>
    public bool UpdateAvailable { get; private set; }
    public string? LatestVersion { get; private set; }
    public string? UpdateDownloadUrl { get; private set; }

    public void Reload()
    {
        Mods.Clear();
        var overrides = OverlayOverrides.Load(DropInDirectory);
        HideWatermark = overrides.HideWatermark;
        DropInVersion = ReadDropInVersion();
        ReadUpdateCheck();
        var scan = new ModScanner().Scan(ModsDirectory);

        foreach (var mod in scan.Mods)
        {
            // Library mods aren't user-facing; hide them like the Reloaded launcher does.
            if (mod.Manifest.IsLibrary)
                continue;

            var configPath = Path.Combine(UserConfigRoot, mod.ModId, "Config.json");
            var separator = Path.DirectorySeparatorChar;
            Mods.Add(new CatalogMod
            {
                ModId = mod.ModId,
                Name = string.IsNullOrWhiteSpace(mod.Manifest.ModName) ? mod.ModId : mod.Manifest.ModName,
                Version = mod.Manifest.ModVersion,
                Directory = mod.Directory,
                IsBaseMod = mod.Directory.Contains($"{separator}_base-mods{separator}", StringComparison.OrdinalIgnoreCase),
                Enabled = !overrides.IsDisabled(mod.ModId),
                UserConfigPath = configPath,
                UserConfig = TryLoadConfig(configPath)
                    ?? TryLoadConfig(Path.Combine(mod.Directory, "Config.json")),
            });
        }

        // User mods first, base mods at the bottom.
        Mods.Sort((a, b) => a.IsBaseMod != b.IsBaseMod
            ? a.IsBaseMod.CompareTo(b.IsBaseMod)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Persists current toggle states to the overrides file sync reads.</summary>
    public void SaveToggles()
    {
        new OverlayOverrides
        {
            DisabledMods = [.. Mods.Where(m => !m.Enabled && !m.IsBaseMod).Select(m => m.ModId)],
            HideWatermark = HideWatermark,
        }.Save(DropInDirectory);
    }

    private string ReadDropInVersion()
    {
        try
        {
            var versionPath = Path.Combine(DropInDirectory, "version.json");
            if (!File.Exists(versionPath))
                return "dev";
            using var doc = JsonDocument.Parse(File.ReadAllText(versionPath));
            return doc.RootElement.GetProperty("dropin").GetString() ?? "dev";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException)
        {
            return "dev";
        }
    }

    /// <summary>Writes one mod's edited user config back to disk.</summary>
    public void SaveConfig(CatalogMod mod)
    {
        if (mod.UserConfig is null || mod.UserConfigPath is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(mod.UserConfigPath)!);
        File.WriteAllText(mod.UserConfigPath,
            mod.UserConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject? TryLoadConfig(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ReadUpdateCheck()
    {
        UpdateAvailable = false;
        LatestVersion = null;
        UpdateDownloadUrl = null;
        try
        {
            var path = Path.Combine(DropInDirectory, "update-check.json");
            if (!File.Exists(path))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            UpdateAvailable = doc.RootElement.GetProperty("UpdateAvailable").GetBoolean();
            LatestVersion = doc.RootElement.GetProperty("LatestVersion").GetString();
            UpdateDownloadUrl = doc.RootElement.GetProperty("DownloadUrl").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException)
        {
            // Stale or corrupt file; no update banner.
        }
    }
}
