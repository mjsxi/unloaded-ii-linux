using ReloadedDropIn.Adapter.Abstractions;

namespace ReloadedDropIn.Adapter.DSTS;

/// <summary>
/// Digimon Story: Time Stranger (PC, Steam). P5R-shaped: the game reads its
/// MVGL archives through hookable file APIs, so DSTS.ModLoader (a thin shim
/// that registers a probing path with MVGL.FileLoader's runtime redirection)
/// just works — no on-disk rewrite, no generated output, no backups, and
/// therefore no disposable state to clear.
///
/// The game's Steam requirements say "DirectX 12", but it actually renders
/// with Direct3D 11 (PCGamingWiki, verified via Special K 2025-12-18), so the
/// overlay's stock D3D11 swapchain hook applies — no Faith-style bridge.
///
/// The required set below is the full transitive dependency closure of
/// DSTS.ModLoader 1.2.1 (verified against each repo's ModConfig.json on
/// 2026-07-19): MVGL.FileLoader.Reloaded + sharedlib.hooks + sigscan.
/// The Ryo stack (DSTS.RyoFramework -> Ryo.Reloaded -> SharedScans.Reloaded)
/// is included on top: the loader doesn't need it, but RyoTune's DSTS mod
/// template declares it as a dependency, so community mods routinely require
/// it (observed 2026-07-19: DSTS.bettershopcheat), and a missing dependency
/// means the drop-in must leave those mods disabled.
/// </summary>
public sealed class DstsAdapter : IGameAdapter
{
    public const string ModLoaderModId = "DSTS.ModLoader";

    public string Id => "dsts";
    public string DisplayName => "Digimon Story: Time Stranger";

    public IReadOnlyList<string> ExecutableNames { get; } = ["Digimon Story Time Stranger.exe"];
    public IReadOnlyList<uint> SteamAppIds { get; } = [1984270u];

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
        // The Time Stranger loader mod itself…
        new RequiredMod
        {
            Id = ModLoaderModId, Enabled = true,
            UpdateRepo = "RyoTune/DSTS.ModLoader",
            UpdateAssetPrefix = "DSTS.ModLoader",
        },
        // …and its transitive dependency closure. Present-but-not-enabled:
        // Reloaded pulls dependencies in automatically once they exist.
        new RequiredMod
        {
            // Runtime MVGL-archive redirection shared by Media.Vision games;
            // DSTS.ModLoader only registers its probing path with this.
            Id = "MVGL.FileLoader.Reloaded", Enabled = false,
            UpdateRepo = "RyoTune/MVGL.FileLoader",
            UpdateAssetPrefix = "MVGL.FileLoader.Reloaded",
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
        // The Ryo stack: not needed by the loader, but declared as a
        // dependency by RyoTune's DSTS mod template and therefore required
        // by most community mods.
        new RequiredMod
        {
            Id = "DSTS.RyoFramework", Enabled = false,
            UpdateRepo = "RyoTune/DSTS.RyoFramework",
            UpdateAssetPrefix = "DSTS.RyoFramework",
        },
        new RequiredMod
        {
            Id = "Ryo.Reloaded", Enabled = false,
            UpdateRepo = "RyoTune/Ryo",
            UpdateAssetPrefix = "Ryo.Reloaded",
        },
        new RequiredMod
        {
            Id = "SharedScans.Reloaded", Enabled = false,
            UpdateRepo = "RyoTune/SharedScans",
            UpdateAssetPrefix = "SharedScans.Reloaded",
        },
    ];

    public InjectionConfiguration GetInjectionConfiguration(AdapterContext context) => new()
    {
        // The same uniform proxy/launch option as the other supported games;
        // fall back to PROXY_NAME=dinput8.dll if the exe turns out not to
        // import winmm on hardware.
        PreferredProxyDll = "winmm.dll",
        WineDllOverride = "winmm=n,b",
    };

    /// <summary>All redirection happens at runtime; nothing persists on disk.</summary>
    public IReadOnlyList<string> GetDisposableModStatePaths(AdapterContext context) => [];

    public Task BeforeGenerateConfigurationAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> AfterGenerateConfigurationAsync(
        AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Nothing was backed up, so nothing to restore.</summary>
    public Task<IReadOnlyList<string>> RestoreAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["dsts: no on-disk game files are modified; nothing to restore"]);

    /// <summary>No game files need backing up: nothing touches the MVGL archives on disk.</summary>
    public Task<IReadOnlyList<string>> CreateBackupsAsync(AdapterContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> AfterModsLoadedAsync(
        AdapterContext context, DateTime launchStartedUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
