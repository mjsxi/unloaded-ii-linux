namespace ReloadedDropIn.Adapter.Abstractions;

/// <summary>
/// Paths and environment handed to an adapter. All paths are absolute.
/// </summary>
public sealed record AdapterContext
{
    /// <summary>Directory containing the game executable.</summary>
    public required string GameDirectory { get; init; }

    /// <summary>The user-facing mods directory (usually GameDirectory/mods).</summary>
    public required string ModsDirectory { get; init; }

    /// <summary>Root of the drop-in installation (usually GameDirectory/reloaded-dropin).</summary>
    public required string DropInDirectory { get; init; }

    public string GeneratedDirectory => Path.Combine(DropInDirectory, "generated");
    public string BackupsDirectory => Path.Combine(DropInDirectory, "backups");
    public string LogsDirectory => Path.Combine(DropInDirectory, "logs");

    /// <summary>
    /// Cached modId → directory lookup for this sync pass (case-insensitive).
    /// Set by the bootstrap after scanning mods/; adapters and installers should
    /// prefer this over re-scanning the filesystem.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ModDirectories { get; set; }
}
