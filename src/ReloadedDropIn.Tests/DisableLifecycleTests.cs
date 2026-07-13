using System.Text.Json;
using ReloadedDropIn.Bootstrap;
using ReloadedDropIn.Core.Configuration;

namespace ReloadedDropIn.Tests;

/// <summary>
/// End-to-end lifecycle: a mod is fully active (mirrored files + modded index),
/// the user disables it in the overlay, and the next launch must leave zero
/// trace of it — pristine data.i, loose files gone, AppConfig without it.
/// </summary>
[Collection("appdata-environment")]
public class DisableLifecycleTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public DisableLifecycleTests()
    {
        Environment.SetEnvironmentVariable("RELOADED_DROPIN_APPDATA", Path.Combine(_temp.Path, "appdata"));
        Environment.SetEnvironmentVariable("RELOADED_DROPIN_DISABLE_UPDATE", "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("RELOADED_DROPIN_APPDATA", null);
        Environment.SetEnvironmentVariable("RELOADED_DROPIN_DISABLE_UPDATE", null);
        _temp.Dispose();
    }

    private string GameDir => Path.Combine(_temp.Path, "game");
    private string UtilityDir => Path.Combine(GameDir, "mods", "_base-mods", "gbfrelink.utility.manager");
    private string AppConfigPath => Path.Combine(
        GameDir, "reloaded-dropin", "generated", "Apps", "granblue_fantasy_relink.exe", "AppConfig.json");

    private List<string> ReadEnabledMods()
    {
        using var appConfig = JsonDocument.Parse(File.ReadAllText(AppConfigPath));
        return [.. appConfig.RootElement.GetProperty("EnabledMods").EnumerateArray().Select(e => e.GetString()!)];
    }

    [Fact]
    public void DisablingModRemovesItsFilesAndIndexOnNextLaunch()
    {
        // Install: game + utility manager + one user mod with a raw data file.
        Directory.CreateDirectory(GameDir);
        File.WriteAllText(Path.Combine(GameDir, "granblue_fantasy_relink.exe"), "MZ");
        File.WriteAllText(Path.Combine(GameDir, "data.i"), "pristine-index");
        _temp.CreateMod("game/mods/_base-mods/gbfrelink.utility.manager", "gbfrelink.utility.manager");
        var userModDir = _temp.CreateMod("game/mods/CoolSwap", "cool.swap");
        var rawFile = Path.Combine(userModDir, "GBFR", "data", "model", "swap.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(rawFile)!);
        File.WriteAllText(rawFile, "modded-model");

        // ---- Launch 1: mod enabled and fully applied. ----
        Assert.Equal(0, SyncRunner.Run(GameDir));

        // The utility manager "rebuilds" its index during mod load (fresh mtime).
        var moddedIndex = Path.Combine(UtilityDir, "temp", "data.i");
        Directory.CreateDirectory(Path.GetDirectoryName(moddedIndex)!);
        File.WriteAllText(moddedIndex, "modded-index-with-cool-swap");
        // And emits a converted output for the mod.
        var conversion = Path.Combine(UtilityDir, "temp", "cool.swap", "model", "swap.minfo");
        Directory.CreateDirectory(Path.GetDirectoryName(conversion)!);
        File.WriteAllText(conversion, "converted");

        Assert.Equal(0, SyncRunner.PostLoad(GameDir));

        Assert.Contains("cool.swap", ReadEnabledMods());
        Assert.Equal("modded-index-with-cool-swap", File.ReadAllText(Path.Combine(GameDir, "data.i")));
        Assert.Equal("modded-model", File.ReadAllText(Path.Combine(GameDir, "data", "model", "swap.bin")));
        Assert.Equal("converted", File.ReadAllText(Path.Combine(GameDir, "data", "model", "swap.minfo")));

        // ---- User unchecks the mod in the overlay. ----
        new OverlayOverrides { DisabledMods = ["cool.swap"] }
            .Save(Path.Combine(GameDir, "reloaded-dropin"));

        // ---- Launch 2: no trace of the mod may survive. ----
        Assert.Equal(0, SyncRunner.Run(GameDir));
        // Utility manager loads with no gbfr mods: temp/data.i is NOT rebuilt
        // this launch (stale file from launch 1 still present).
        Assert.Equal(0, SyncRunner.PostLoad(GameDir));

        Assert.DoesNotContain("cool.swap", ReadEnabledMods());
        Assert.Equal("pristine-index", File.ReadAllText(Path.Combine(GameDir, "data.i")));
        Assert.False(File.Exists(Path.Combine(GameDir, "data", "model", "swap.bin")),
            "raw mirrored file must be removed once the mod is disabled");
        Assert.False(File.Exists(Path.Combine(GameDir, "data", "model", "swap.minfo")),
            "converted mirrored file must be removed once the mod is disabled");
    }

    [Fact]
    public void ReenablingModBringsItBack()
    {
        Directory.CreateDirectory(GameDir);
        File.WriteAllText(Path.Combine(GameDir, "granblue_fantasy_relink.exe"), "MZ");
        File.WriteAllText(Path.Combine(GameDir, "data.i"), "pristine-index");
        _temp.CreateMod("game/mods/_base-mods/gbfrelink.utility.manager", "gbfrelink.utility.manager");
        var userModDir = _temp.CreateMod("game/mods/CoolSwap", "cool.swap");
        var rawFile = Path.Combine(userModDir, "GBFR", "data", "model", "swap.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(rawFile)!);
        File.WriteAllText(rawFile, "modded-model");

        // Disabled from the start…
        new OverlayOverrides { DisabledMods = ["cool.swap"] }
            .Save(Path.Combine(GameDir, "reloaded-dropin"));
        Assert.Equal(0, SyncRunner.Run(GameDir));
        Assert.Equal(0, SyncRunner.PostLoad(GameDir));
        Assert.DoesNotContain("cool.swap", ReadEnabledMods());
        Assert.False(File.Exists(Path.Combine(GameDir, "data", "model", "swap.bin")));

        // …then re-enabled.
        new OverlayOverrides { DisabledMods = [] }
            .Save(Path.Combine(GameDir, "reloaded-dropin"));
        Assert.Equal(0, SyncRunner.Run(GameDir));

        Assert.Contains("cool.swap", ReadEnabledMods());
        Assert.Equal("modded-model", File.ReadAllText(Path.Combine(GameDir, "data", "model", "swap.bin")));
    }
}
