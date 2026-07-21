using ReloadedDropIn.Adapter.Abstractions;

namespace ReloadedDropIn.Adapter.GBFR;

/// <summary>
/// Granblue Fantasy: Relink. Facts sourced from research/gbfr-notes.md —
/// notably the Reloaded app ID is the executable name, and
/// gbfrelink.utility.manager brings three hard library dependencies. None of
/// these are redistributed in the package (their maintainers asked us not
/// to); BaseModInstaller fetches them from their official releases instead.
/// </summary>
public sealed class GbfrAdapter : IGameAdapter
{
    public const string UtilityManagerModId = "gbfrelink.utility.manager";

    public string Id => "gbfr";
    public string DisplayName => "Granblue Fantasy: Relink";

    public IReadOnlyList<string> ExecutableNames { get; } = ["granblue_fantasy_relink.exe"];
    public IReadOnlyList<uint> SteamAppIds { get; } = [881020u];

    public GameDetectionResult Detect(AdapterContext context)
    {
        foreach (var executable in ExecutableNames)
        {
            var path = Path.Combine(context.GameDirectory, executable);
            if (File.Exists(path))
                return GameDetectionResult.Detected(executable, path);
        }

        return GameDetectionResult.NotDetected;
    }

    public ValidationResult ValidateInstallation(AdapterContext context)
    {
        var messages = new List<ValidationMessage>();

        var detection = Detect(context);
        if (!detection.IsDetected)
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Error, "executable",
                $"none of [{string.Join(", ", ExecutableNames)}] found in {context.GameDirectory}"));
        }

        var dataIndex = Path.Combine(context.GameDirectory, "data.i");
        if (!File.Exists(dataIndex))
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Error, "data.i",
                "data.i not found next to the game executable — is this a complete Relink install?"));
        }

        if (!Directory.Exists(context.ModsDirectory))
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Warning, "mods",
                $"mods directory does not exist yet: {context.ModsDirectory}"));
        }

        return ValidationResult.From(messages);
    }

    public IReadOnlyList<RequiredMod> GetRequiredMods() =>
    [
        // The Relink loader mod itself…
        new RequiredMod
        {
            Id = UtilityManagerModId, Enabled = true,
            UpdateRepo = "WistfulHopes/gbfrelink.utility.manager",
            UpdateAssetPrefix = "gbfrelink.utility.manager",
        },
        // …and its hard ModDependencies. Present-but-not-enabled: Reloaded pulls
        // libraries in as dependencies automatically.
        new RequiredMod
        {
            Id = "Reloaded.Memory.SigScan.ReloadedII", Enabled = false,
            UpdateRepo = "Reloaded-Project/Reloaded.Memory.SigScan",
            UpdateAssetPrefix = "Reloaded.Memory.SigScan.ReloadedII",
        },
        new RequiredMod
        {
            Id = "reloaded.sharedlib.hooks", Enabled = false,
            UpdateRepo = "Sewer56/Reloaded.SharedLib.Hooks.ReloadedII",
            UpdateAssetPrefix = "Reloaded.Hooks.ReloadedII",
        },
        new RequiredMod
        {
            Id = "reloaded.universal.redirector", Enabled = false,
            UpdateRepo = "Reloaded-Project/reloaded.universal.redirector",
            UpdateAssetPrefix = "Reloaded.Universal.Redirector",
        },
    ];

    public InjectionConfiguration GetInjectionConfiguration(AdapterContext context) => new()
    {
        PreferredProxyDll = "winmm.dll",
        WineDllOverride = "winmm=n,b",
    };

    /// <summary>
    /// Utility Manager recreates its converted files and cache registry. Purging
    /// both on a mod-set transition prevents removed mods from leaving converted
    /// output behind while preserving all user configuration.
    /// </summary>
    public IReadOnlyList<string> GetDisposableModStatePaths(AdapterContext context)
    {
        var utilityDirectory = FindUtilityManagerDirectory(context);
        return
        [
            Path.Combine(utilityDirectory, "temp"),
            Path.Combine(utilityDirectory, "cached_files.txt"),
        ];
    }

    /// <summary>
    /// Pre-load, after the enabled-mod config is written: full mirror reconcile
    /// (copies and deletions) while no file-redirect hooks exist yet.
    /// </summary>
    public Task<IReadOnlyList<string>> AfterGenerateConfigurationAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new GbfrDataMirror(context, FindUtilityManagerDirectory(context)).Reconcile(ResolveMirrorSources(context)));

    /// <summary>
    /// Full undo: clear every mirrored file from data/ (restoring displaced
    /// stock files) and put the pristine data.i back.
    /// </summary>
    public Task<IReadOnlyList<string>> RestoreAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var log = new List<string>();
        var utilityDir = FindUtilityManagerDirectory(context);
        log.AddRange(new GbfrDataMirror(context, utilityDir).Reconcile([]));
        log.AddRange(new GbfrDataIndex(context, utilityDir).Restore());
        return Task.FromResult<IReadOnlyList<string>>(log);
    }

    /// <summary>
    /// Resolves enabled mod IDs to their actual directories via the cached scan —
    /// mod folder names routinely differ from their ModConfig ModId.
    /// </summary>
    private IReadOnlyList<GbfrDataMirror.MirrorSource> ResolveMirrorSources(AdapterContext context)
    {
        var directoryByModId = context.ModDirectories
            ?? new Core.Discovery.ModScanner()
                .Scan(context.ModsDirectory).Mods
                .ToDictionary(m => m.ModId, m => m.Directory, StringComparer.OrdinalIgnoreCase);

        return ReadEnabledMods(context)
            .Where(directoryByModId.ContainsKey)
            .Select(id => new GbfrDataMirror.MirrorSource(id, directoryByModId[id]))
            .ToList();
    }

    /// <summary>Pre-load: back up data.i, ensure the on-disk copy is pristine, and
    /// disable the utility manager's title-screen text injection.</summary>
    public Task<IReadOnlyList<string>> CreateBackupsAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var log = new List<string>();
        log.AddRange(new GbfrDataIndex(context).EnsureBaseline());
        log.AddRange(DisableModLoaderInfoText(context, FindUtilityManagerDirectory(context)));
        return Task.FromResult<IReadOnlyList<string>>(log);
    }

    /// <summary>
    /// The utility manager's ShowModLoaderInfo feature re-serializes the game's whole
    /// UI text table to inject a version banner. The rewritten table renders as blank
    /// text on current game builds, so force the feature off via the mod's user config
    /// and delete any text tables it already generated.
    /// </summary>
    private static IReadOnlyList<string> DisableModLoaderInfoText(AdapterContext context, string utilityManagerDirectory)
    {
        var log = new List<string>();

        var configDirectory = Path.Combine(context.GeneratedDirectory, "User", "Mods", UtilityManagerModId);
        var configPath = Path.Combine(configDirectory, "Config.json");
        var config = File.Exists(configPath)
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath)) as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject()
            : new System.Text.Json.Nodes.JsonObject();

        if (config["ShowModLoaderInfo"]?.GetValue<bool>() != false)
        {
            config["ShowModLoaderInfo"] = false;
            Directory.CreateDirectory(configDirectory);
            Core.Filesystem.AtomicFile.WriteAllText(configPath, config.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            log.Add("disabled utility-manager title-screen text (its rewritten text tables render blank)");
        }

        var generatedTextTables = Path.Combine(
            utilityManagerDirectory, "GBFR", "data", "system", "table");
        if (Directory.Exists(generatedTextTables))
        {
            Directory.Delete(generatedTextTables, recursive: true);
            log.Add("removed previously generated text tables");
        }

        return log;
    }

    /// <summary>
    /// Post-load, game still frozen at entry: refresh the utility manager's
    /// just-regenerated conversion outputs in data/ (redirect-proof writes), then
    /// apply its rebuilt index to disk (see GbfrDataIndex).
    /// </summary>
    public Task<IReadOnlyList<string>> AfterModsLoadedAsync(
        AdapterContext context, DateTime launchStartedUtc, CancellationToken cancellationToken)
    {
        var log = new List<string>();
        var utilityDir = FindUtilityManagerDirectory(context);
        try
        {
            log.AddRange(new GbfrDataMirror(context, utilityDir).MirrorFreshConversions(ReadEnabledMods(context)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A refresh failure must never cost us the index apply below.
            log.Add($"conversion refresh failed (continuing): {ex.Message}");
        }

        log.AddRange(new GbfrDataIndex(context, utilityDir).ApplyModdedIndex(launchStartedUtc));
        return Task.FromResult<IReadOnlyList<string>>(log);
    }

    /// <summary>
    /// Locates the utility manager's actual folder under mods/ (it may live at
    /// the top level or under mods/_base-mods/). Falls back to the legacy
    /// top-level path when not discovered.
    /// </summary>
    private static string FindUtilityManagerDirectory(AdapterContext context)
    {
        if (context.ModDirectories is not null &&
            context.ModDirectories.TryGetValue(UtilityManagerModId, out var dir))
            return dir;

        var mod = new Core.Discovery.ModScanner().Scan(context.ModsDirectory).Mods
            .FirstOrDefault(m => m.ModId.Equals(UtilityManagerModId, StringComparison.OrdinalIgnoreCase));
        return mod?.Directory ?? Path.Combine(context.ModsDirectory, UtilityManagerModId);
    }

    /// <summary>Enabled mods, in the order sync wrote them (later mods override earlier).</summary>
    private IReadOnlyList<string> ReadEnabledMods(AdapterContext context)
    {
        var detection = Detect(context);
        if (detection.MatchedExecutable is null)
            return [];

        var appConfigPath = Path.Combine(
            context.GeneratedDirectory, "Apps", detection.MatchedExecutable.ToLowerInvariant(), "AppConfig.json");
        if (!File.Exists(appConfigPath))
            return [];

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appConfigPath));
            return [.. doc.RootElement.GetProperty("EnabledMods").EnumerateArray().Select(e => e.GetString()!)];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException)
        {
            return [];
        }
    }
}
