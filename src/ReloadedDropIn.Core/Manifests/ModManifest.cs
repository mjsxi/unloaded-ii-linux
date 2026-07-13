using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReloadedDropIn.Core.Manifests;

/// <summary>
/// The subset of Reloaded-II's ModConfig.json we need for discovery and
/// dependency validation. Field names mirror Reloaded's
/// Reloaded.Mod.Loader.IO.Config.ModConfig exactly (PascalCase in the JSON).
/// </summary>
public sealed record ModManifest
{
    public const string FileName = "ModConfig.json";

    public required string ModId { get; init; }
    public string ModName { get; init; } = string.Empty;
    public string ModAuthor { get; init; } = string.Empty;
    public string ModVersion { get; init; } = string.Empty;
    public string ModDescription { get; init; } = string.Empty;
    public string ModDll { get; init; } = string.Empty;
    public string ModR2RManagedDll32 { get; init; } = string.Empty;
    public string ModR2RManagedDll64 { get; init; } = string.Empty;
    public string ModNativeDll32 { get; init; } = string.Empty;
    public string ModNativeDll64 { get; init; } = string.Empty;
    public bool IsLibrary { get; init; }
    public bool IsUniversalMod { get; init; }
    public string[] ModDependencies { get; init; } = [];
    public string[] OptionalDependencies { get; init; } = [];
    public string[] SupportedAppId { get; init; } = [];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Parses a manifest. Returns null (with a reason) instead of throwing on bad input,
    /// so a broken mod folder never aborts a whole scan.
    /// </summary>
    public static ModManifest? TryParse(string json, out string? error)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ModManifest>(json, ReadOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.ModId))
            {
                error = "manifest has no ModId";
                return null;
            }

            error = null;
            return manifest;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return null;
        }
    }
}
