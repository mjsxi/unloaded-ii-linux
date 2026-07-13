using ReloadedDropIn.Core.Manifests;

namespace ReloadedDropIn.Core.Discovery;

/// <summary>A mod directory found under mods/ with a valid manifest.</summary>
public sealed record DiscoveredMod
{
    public required ModManifest Manifest { get; init; }

    /// <summary>Absolute path to the directory containing ModConfig.json.</summary>
    public required string Directory { get; init; }

    public string ModId => Manifest.ModId;
}
