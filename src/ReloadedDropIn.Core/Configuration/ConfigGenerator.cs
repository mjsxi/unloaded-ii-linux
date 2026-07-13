using System.Text.Json;
using ReloadedDropIn.Core.Discovery;
using ReloadedDropIn.Core.Filesystem;

namespace ReloadedDropIn.Core.Configuration;

public sealed record GeneratePlan
{
    /// <summary>Absolute path the AppConfig.json would be written to.</summary>
    public required string TargetPath { get; init; }

    /// <summary>Serialized config content that would be written.</summary>
    public required string Content { get; init; }

    /// <summary>False when the file already has identical content (idempotent no-op).</summary>
    public required bool WouldChange { get; init; }

    public required string[] EnabledMods { get; init; }
}

/// <summary>
/// Turns discovered mods into a Reloaded AppConfig.json, deterministically.
/// Running twice with the same inputs must produce byte-identical output (plan §18).
/// </summary>
public sealed class ConfigGenerator
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Computes what would be written without touching the filesystem (dry-run).
    /// </summary>
    public GeneratePlan Plan(
        string generatedDirectory,
        string appId,
        string appName,
        string appLocation,
        IReadOnlyList<DiscoveredMod> orderedMods,
        IReadOnlyList<string> requiredEnabledModIds)
    {
        // Required mods first (adapter-mandated), then discovered mods in resolver order.
        // Libraries are loaded as dependencies by Reloaded itself; don't force-enable them.
        var enabled = new List<string>();
        foreach (var id in requiredEnabledModIds)
        {
            if (!enabled.Contains(id, StringComparer.OrdinalIgnoreCase))
                enabled.Add(id);
        }

        foreach (var mod in orderedMods)
        {
            if (mod.Manifest.IsLibrary)
                continue;
            if (!enabled.Contains(mod.ModId, StringComparer.OrdinalIgnoreCase))
                enabled.Add(mod.ModId);
        }

        var config = new ReloadedAppConfig
        {
            AppId = appId,
            AppName = appName,
            AppLocation = appLocation,
            EnabledMods = [.. enabled],
            SortedMods = [.. enabled],
        };

        var targetPath = Path.Combine(generatedDirectory, "Apps", appId, ReloadedAppConfig.FileName);
        var content = JsonSerializer.Serialize(config, WriteOptions) + Environment.NewLine;
        var wouldChange = !File.Exists(targetPath) || File.ReadAllText(targetPath) != content;

        return new GeneratePlan
        {
            TargetPath = targetPath,
            Content = content,
            WouldChange = wouldChange,
            EnabledMods = [.. enabled],
        };
    }

    /// <summary>Applies a plan with an atomic write. Returns true if the file changed.</summary>
    public bool Apply(GeneratePlan plan)
    {
        if (!plan.WouldChange)
            return false;

        AtomicFile.WriteAllText(plan.TargetPath, plan.Content);
        return true;
    }
}
