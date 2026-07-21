using ReloadedDropIn.Adapter.Abstractions;

namespace ReloadedDropIn.Adapter.P5R;

/// <summary>
/// Persona 5 Royal (PC). The polar opposite of the GBFR adapter: P5R reads its
/// CPK archives through hookable file APIs, so p5rpc.modloader's runtime
/// CriFS redirection just works — no on-disk index rewrite, no file mirror, no
/// backups. This adapter only has to detect the game and name the required mod
/// stack, which BaseModInstaller fetches from the authors' official releases.
///
/// The required set below is the full transitive dependency closure of
/// p5rpc.modloader 2.9.5 (verified against each repo's ModConfig.json on
/// 2026-07-13); Reloaded aborts the whole load if any piece is absent, so
/// every mod here carries an update source.
/// </summary>
public sealed class P5rAdapter : IGameAdapter
{
    public const string ModLoaderModId = "p5rpc.modloader";

    public string Id => "p5r";
    public string DisplayName => "Persona 5 Royal";

    public IReadOnlyList<string> ExecutableNames { get; } = ["P5R.exe"];
    public IReadOnlyList<uint> SteamAppIds { get; } = [1687950u];

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

        if (!Directory.Exists(context.ModsDirectory))
        {
            messages.Add(new ValidationMessage(ValidationSeverity.Warning, "mods",
                $"mods directory does not exist yet: {context.ModsDirectory}"));
        }

        return ValidationResult.From(messages);
    }

    public IReadOnlyList<RequiredMod> GetRequiredMods() =>
    [
        // The P5R loader mod itself…
        new RequiredMod
        {
            Id = ModLoaderModId, Enabled = true,
            UpdateRepo = "Sewer56/p5rpc.modloader",
            UpdateAssetPrefix = "p5rpc.modloader",
        },
        // …and its transitive dependency closure. Present-but-not-enabled:
        // Reloaded pulls dependencies in automatically once they exist.
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
        new RequiredMod
        {
            Id = "crifs.v2.hook", Enabled = false,
            UpdateRepo = "Sewer56/CriFs.V2.Hook.ReloadedII",
            UpdateAssetPrefix = "CriFs.V2.Hook",
        },
        new RequiredMod
        {
            Id = "CriFs.V2.Hook.Awb", Enabled = false,
            UpdateRepo = "Sewer56/CriFs.V2.Hook.ReloadedII",
            UpdateAssetPrefix = "CriFs.V2.Hook.Awb",
        },
        new RequiredMod
        {
            Id = "reloaded.universal.fileemulationframework", Enabled = false,
            UpdateRepo = "Sewer56/FileEmulationFramework",
            UpdateAssetPrefix = "FileEmulationFramework",
        },
        new RequiredMod
        {
            // Pulled in by CriFs.V2.Hook.Awb, not by the modloader directly.
            Id = "reloaded.universal.fileemulationframework.awb", Enabled = false,
            UpdateRepo = "Sewer56/FileEmulationFramework",
            UpdateAssetPrefix = "AWB.Stream.Emulator",
        },
        new RequiredMod
        {
            Id = "reloaded.universal.fileemulationframework.pak", Enabled = false,
            UpdateRepo = "Sewer56/FileEmulationFramework",
            UpdateAssetPrefix = "PAK.Stream.Emulator",
        },
        new RequiredMod
        {
            Id = "reloaded.universal.fileemulationframework.bmd", Enabled = false,
            UpdateRepo = "Sewer56/FileEmulationFramework",
            UpdateAssetPrefix = "BMD.File.Emulator",
        },
        new RequiredMod
        {
            Id = "reloaded.universal.fileemulationframework.bf", Enabled = false,
            UpdateRepo = "Sewer56/FileEmulationFramework",
            UpdateAssetPrefix = "BF.File.Emulator",
        },
        new RequiredMod
        {
            Id = "reloaded.universal.fileemulationframework.spd", Enabled = false,
            UpdateRepo = "Sewer56/FileEmulationFramework",
            UpdateAssetPrefix = "SPD.File.Emulator",
        },
        new RequiredMod
        {
            Id = "Reloaded.Universal.Localisation.Framework", Enabled = false,
            UpdateRepo = "AnimatedSwine37/Reloaded.Universal.Localisation.Framework",
            UpdateAssetPrefix = "Reloaded.Universal.Localisation.Framework",
        },
        new RequiredMod
        {
            Id = "Reloaded.Universal.Localisation.Provider.Steam", Enabled = false,
            UpdateRepo = "AnimatedSwine37/Reloaded.Universal.Localisation.Framework",
            UpdateAssetPrefix = "Reloaded.Universal.Localisation.Provider.Steam",
        },
        new RequiredMod
        {
            Id = "Reloaded.Universal.Localisation.Provider.Windows", Enabled = false,
            UpdateRepo = "AnimatedSwine37/Reloaded.Universal.Localisation.Framework",
            UpdateAssetPrefix = "Reloaded.Universal.Localisation.Provider.Windows",
        },
    ];

    public InjectionConfiguration GetInjectionConfiguration(AdapterContext context) => new()
    {
        // Verified on Proton: the base stack and overlay initialize through
        // the same proxy and launch option used by the GBFR package.
        PreferredProxyDll = "winmm.dll",
        WineDllOverride = "winmm=n,b",
    };

    /// <summary>
    /// Persona Essentials stores merged PAK/BF/BMD/SPD/TBL outputs here. It is
    /// recreated automatically, so invalidate it whenever the active mod set
    /// changes rather than risk retaining output from a removed mod.
    /// </summary>
    public IReadOnlyList<string> GetDisposableModStatePaths(AdapterContext context)
    {
        var loader = new Core.Discovery.ModScanner().Scan(context.ModsDirectory).Mods
            .FirstOrDefault(m => m.ModId.Equals(ModLoaderModId, StringComparison.OrdinalIgnoreCase));
        return loader is null ? [] : [Path.Combine(loader.Directory, "Cache")];
    }

    public Task<IReadOnlyList<string>> AfterGenerateConfigurationAsync(
        AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Nothing was backed up, so nothing to restore.</summary>
    public Task<IReadOnlyList<string>> RestoreAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["p5r: no on-disk game files are modified; nothing to restore"]);

    /// <summary>No game files need backing up: nothing touches the CPKs on disk.</summary>
    public Task<IReadOnlyList<string>> CreateBackupsAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> AfterModsLoadedAsync(
        AdapterContext context, DateTime launchStartedUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
