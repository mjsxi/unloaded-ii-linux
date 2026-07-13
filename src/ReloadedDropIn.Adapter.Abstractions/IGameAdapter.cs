namespace ReloadedDropIn.Adapter.Abstractions;

/// <summary>
/// Everything game-specific lives behind this contract. The core must not know
/// about any particular game's executables, archives, or required mods.
/// </summary>
public interface IGameAdapter
{
    string Id { get; }
    string DisplayName { get; }

    IReadOnlyList<string> ExecutableNames { get; }
    IReadOnlyList<uint> SteamAppIds { get; }

    GameDetectionResult Detect(AdapterContext context);

    ValidationResult ValidateInstallation(AdapterContext context);

    IReadOnlyList<RequiredMod> GetRequiredMods();

    InjectionConfiguration GetInjectionConfiguration(AdapterContext context);

    /// <summary>
    /// Loader-owned caches or generated outputs that must be rebuilt when the
    /// active mod set changes. Every returned path must be disposable and live
    /// below the game directory; user configuration must never be returned.
    /// </summary>
    IReadOnlyList<string> GetDisposableModStatePaths(AdapterContext context) => [];

    Task BeforeGenerateConfigurationAsync(AdapterContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Pre-load, after configuration generation: adapter work that needs the final
    /// enabled-mod list but must run before any mod hooks exist (e.g. mirroring
    /// files while file operations still hit the real filesystem). Returns log lines.
    /// </summary>
    Task<IReadOnlyList<string>> AfterGenerateConfigurationAsync(AdapterContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Pre-load: create/refresh backups of game files the modding stack will touch.
    /// Returns human-readable log lines.
    /// </summary>
    Task<IReadOnlyList<string>> CreateBackupsAsync(AdapterContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Runs after Reloaded's loader (and all mods) initialized but before the game
    /// executes its first instruction (the bootstrap holds the entry point).
    /// Adapters use this for work that depends on mod-load output, e.g. applying
    /// a rebuilt archive index to disk. <paramref name="launchStartedUtc"/> is when
    /// this launch's sync began — output older than it is stale. Returns log lines.
    /// </summary>
    Task<IReadOnlyList<string>> AfterModsLoadedAsync(
        AdapterContext context, DateTime launchStartedUtc, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Undoes every game-file change this adapter made: restores backed-up
    /// originals and removes files it placed. Run with the game closed
    /// (uninstall/repair path). Returns log lines.
    /// </summary>
    Task<IReadOnlyList<string>> RestoreAsync(AdapterContext context, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([]);
}
