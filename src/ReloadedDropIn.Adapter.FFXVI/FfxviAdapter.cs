using ReloadedDropIn.Adapter.Abstractions;

namespace ReloadedDropIn.Adapter.FFXVI;

/// <summary>
/// Final Fantasy XVI (PC, Steam/Epic). P5R-shaped: detect the game and name
/// the required mod stack; no game file is ever modified in place.
///
/// ff16.utility.modloader works differently from runtime redirection, though:
/// on each modded launch it deletes its previous output and rebuilds mod
/// content as additive `*.diff.pac` archives inside the game's data/
/// directory (via FF16Tools). Original .pac files are never touched. Because
/// that output persists on disk and stays active even when Reloaded doesn't
/// run, the adapter declares the .diff.pac files as disposable state (cleared
/// when the active mod set changes and the loader might not run to clean up
/// after itself) and removes them on restore/uninstall.
///
/// The required set is the full dependency closure of ff16.utility.modloader
/// 1.2.1 (ModConfig.json verified 2026-07-16): just sharedlib.hooks + sigscan.
/// Faith Framework (ff16.utility.framework) is included on top: the loader
/// doesn't need it, but FFXVI gameplay/code mods routinely depend on it, and
/// a missing dependency means the drop-in must leave those mods disabled.
/// </summary>
public sealed class FfxviAdapter : IGameAdapter
{
    public const string ModLoaderModId = "ff16.utility.modloader";
    public const string FaithFrameworkModId = "ff16.utility.framework";

    public string Id => "ffxvi";
    public string DisplayName => "Final Fantasy XVI";

    /// <summary>The loader supports the demo executable too; same app id family.</summary>
    public IReadOnlyList<string> ExecutableNames { get; } = ["ffxvi.exe", "ffxvi_demo.exe"];
    public IReadOnlyList<uint> SteamAppIds { get; } = [2515020u];

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

        if (!Detect(context).IsDetected)
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Error, "executable",
                $"none of [{string.Join(", ", ExecutableNames)}] found in {context.GameDirectory}"));
        }

        if (!Directory.Exists(Path.Combine(context.GameDirectory, "data")))
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Error, "data",
                $"data directory not found in {context.GameDirectory}; the mod loader writes its .diff.pac output there"));
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
        new RequiredMod
        {
            Id = ModLoaderModId, Enabled = true,
            UpdateRepo = "Nenkai/ff16.utility.modloader",
            UpdateAssetPrefix = "ff16.utility.modloader",
        },
        // Present-but-not-enabled: Reloaded pulls dependencies in
        // automatically once they exist.
        new RequiredMod
        {
            // De-facto standard framework for FF16 code mods (hooks into the
            // game's Nex/imgui internals). Repo renamed from FF16Framework.
            Id = FaithFrameworkModId, Enabled = false,
            UpdateRepo = "Nenkai/FaithFramework",
            UpdateAssetPrefix = "FaithFramework",
        },
        new RequiredMod
        {
            Id = "reloaded.sharedlib.hooks", Enabled = false,
            UpdateRepo = "Sewer56/Reloaded.SharedLib.Hooks.ReloadedII",
            UpdateAssetPrefix = "Reloaded.Hooks.ReloadedII",
        },
        new RequiredMod
        {
            Id = "Reloaded.Memory.SigScan.ReloadedII", Enabled = false,
            UpdateRepo = "Reloaded-Project/Reloaded.Memory.SigScan",
            UpdateAssetPrefix = "Reloaded.Memory.SigScan.ReloadedII",
        },
    ];

    public InjectionConfiguration GetInjectionConfiguration(AdapterContext context) => new()
    {
        // Verified under Proton with the same uniform proxy/launch option as
        // the other supported games.
        PreferredProxyDll = "winmm.dll",
        WineDllOverride = "winmm=n,b",
    };

    /// <summary>
    /// The loader's generated .diff.pac archives are additive and rebuilt from
    /// the active mod set on every modded launch. Clearing them when the mod
    /// set changes guarantees a removed mod's output can't stay live on a
    /// launch where the loader itself fails to run (missing download, crash).
    /// </summary>
    public IReadOnlyList<string> GetDisposableModStatePaths(AdapterContext context) =>
        FindDiffPacs(context);

    public Task BeforeGenerateConfigurationAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> AfterGenerateConfigurationAsync(
        AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Vanilla = no .diff.pac files. Originals are never modified, so restore
    /// is just deleting the loader's generated archives.
    /// </summary>
    public Task<IReadOnlyList<string>> RestoreAsync(AdapterContext context, CancellationToken cancellationToken)
    {
        var log = new List<string>();
        foreach (var path in FindDiffPacs(context))
        {
            File.Delete(path);
            log.Add($"ffxvi: removed generated {Path.GetRelativePath(context.GameDirectory, path)}");
        }

        if (log.Count == 0)
            log.Add("ffxvi: no generated .diff.pac files present; nothing to restore");
        return Task.FromResult<IReadOnlyList<string>>(log);
    }

    /// <summary>No game files need backing up: originals are never modified.</summary>
    public Task<IReadOnlyList<string>> CreateBackupsAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> AfterModsLoadedAsync(
        AdapterContext context, DateTime launchStartedUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    private static string[] FindDiffPacs(AdapterContext context)
    {
        var dataDirectory = Path.Combine(context.GameDirectory, "data");
        return Directory.Exists(dataDirectory)
            ? Directory.GetFiles(dataDirectory, "*.diff.pac", SearchOption.AllDirectories)
            : [];
    }
}
