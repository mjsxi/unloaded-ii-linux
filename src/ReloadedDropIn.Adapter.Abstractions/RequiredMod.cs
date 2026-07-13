namespace ReloadedDropIn.Adapter.Abstractions;

/// <summary>
/// A mod the adapter needs present and enabled for the game to be moddable at all
/// (e.g. a game-specific file-loader utility mod and its library dependencies).
/// </summary>
public sealed record RequiredMod
{
    /// <summary>Reloaded mod ID (ModConfig.json "ModId").</summary>
    public required string Id { get; init; }

    /// <summary>True when the mod must appear in the enabled-mods list (not just be present).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>GitHub repository ("owner/name") that publishes this mod's releases,
    /// or null when the mod has no auto-update source.</summary>
    public string? UpdateRepo { get; init; }

    /// <summary>Release-asset filename prefix identifying the mod archive; Reloaded
    /// release tooling names assets "&lt;prefix&gt;&lt;version&gt;.7z".</summary>
    public string? UpdateAssetPrefix { get; init; }
}
