using System.Text.Json;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Core.Filesystem;

namespace ReloadedDropIn.Bootstrap;

/// <summary>
/// Writes ReloadedII.json — the loader config the (managed) Reloaded loader
/// reads from the fixed %APPDATA%\Reloaded-Mod-Loader-II path. All directory
/// fields point back into the game's drop-in folders; explicit values win over
/// Reloaded's portable/default resolution (LoaderConfig.ResetMissingDirectories
/// only fills empty fields).
/// </summary>
public static class ReloadedLoaderConfigWriter
{
    public static string Write(AdapterContext context)
    {
        var loaderDirectory = Path.Combine(context.DropInDirectory, "loader");
        var generated = context.GeneratedDirectory;

        var config = new Dictionary<string, object>
        {
            ["LoaderPath32"] = Path.Combine(loaderDirectory, "x86", "Reloaded.Mod.Loader.dll"),
            ["LoaderPath64"] = Path.Combine(loaderDirectory, "Reloaded.Mod.Loader.dll"),
            ["LauncherPath"] = Path.Combine(loaderDirectory, "Reloaded-II.exe"),
            ["Bootstrapper32Path"] = Path.Combine(context.GameDirectory, "reloaded-dropin.asi"),
            ["Bootstrapper64Path"] = Path.Combine(context.GameDirectory, "reloaded-dropin.asi"),
            ["ApplicationConfigDirectory"] = Path.Combine(generated, "Apps"),
            ["ModConfigDirectory"] = context.ModsDirectory,
            ["ModUserConfigDirectory"] = Path.Combine(generated, "User", "Mods"),
            ["MiscConfigDirectory"] = Path.Combine(generated, "User", "Misc"),
            ["PluginConfigDirectory"] = Path.Combine(generated, "Plugins"),
            ["EnabledPlugins"] = Array.Empty<string>(),
            ["FirstLaunch"] = false,
            ["ShowConsole"] = false,
        };

        // Loader-read directories must exist before the loader scans them.
        Directory.CreateDirectory(Path.Combine(generated, "Apps"));
        Directory.CreateDirectory(Path.Combine(generated, "User", "Mods"));
        Directory.CreateDirectory(Path.Combine(generated, "User", "Misc"));
        Directory.CreateDirectory(Path.Combine(generated, "Plugins"));
        Directory.CreateDirectory(context.ModsDirectory);

        // Test/debug override; in production %APPDATA% resolves inside the Wine
        // prefix because we run inside the game process.
        var appDataRoot = Environment.GetEnvironmentVariable("RELOADED_DROPIN_APPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDirectory = Path.Combine(appDataRoot, "Reloaded-Mod-Loader-II");
        var configPath = Path.Combine(configDirectory, "ReloadedII.json");

        AtomicFile.WriteAllText(configPath,
            JsonSerializer.Serialize(config, WriteOptions) + Environment.NewLine);
        return configPath;
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
}
