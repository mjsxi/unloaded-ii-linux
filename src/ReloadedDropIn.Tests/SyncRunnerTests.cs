using System.Text.Json;
using ReloadedDropIn.Bootstrap;

namespace ReloadedDropIn.Tests;

// RELOADED_DROPIN_APPDATA is process-wide; classes touching it must not run in parallel.
[Collection("appdata-environment")]
public class SyncRunnerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public SyncRunnerTests()
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

    private void CreateFakeRelinkInstall()
    {
        Directory.CreateDirectory(GameDir);
        File.WriteAllText(Path.Combine(GameDir, "granblue_fantasy_relink.exe"), "MZ");
        File.WriteAllText(Path.Combine(GameDir, "data.i"), "index");
        _temp.CreateMod("game/mods/gbfrelink.utility.manager", "gbfrelink.utility.manager");
        _temp.CreateMod("game/mods/UserMod", "user.mod");
    }

    [Fact]
    public void FullSyncWritesLoaderConfigAndAppConfig()
    {
        CreateFakeRelinkInstall();

        var result = SyncRunner.Run(GameDir);

        Assert.Equal(0, result);

        // ReloadedII.json in (overridden) AppData, directories pointing into the game dir.
        var loaderConfigPath = Path.Combine(_temp.Path, "appdata", "Reloaded-Mod-Loader-II", "ReloadedII.json");
        Assert.True(File.Exists(loaderConfigPath));
        using var loaderConfig = JsonDocument.Parse(File.ReadAllText(loaderConfigPath));
        Assert.Equal(
            Path.Combine(GameDir, "mods"),
            loaderConfig.RootElement.GetProperty("ModConfigDirectory").GetString());
        Assert.EndsWith(
            Path.Combine("reloaded-dropin", "loader", "Reloaded.Mod.Loader.dll"),
            loaderConfig.RootElement.GetProperty("LoaderPath64").GetString());

        // AppConfig.json in generated/Apps/<appid>/.
        var appConfigPath = Path.Combine(
            GameDir, "reloaded-dropin", "generated", "Apps", "granblue_fantasy_relink.exe", "AppConfig.json");
        Assert.True(File.Exists(appConfigPath));
        using var appConfig = JsonDocument.Parse(File.ReadAllText(appConfigPath));
        var enabled = appConfig.RootElement.GetProperty("EnabledMods").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("gbfrelink.utility.manager", enabled);
        Assert.Contains("user.mod", enabled);

        // Directories the loader scans must exist.
        Assert.True(Directory.Exists(Path.Combine(GameDir, "reloaded-dropin", "generated", "User", "Mods")));

        // Sync log written.
        Assert.True(File.Exists(Path.Combine(GameDir, "reloaded-dropin", "logs", "sync.log")));
    }

    [Fact]
    public void BaseModsNestedUnderBaseModsFolderAreDiscoveredAndUtilityTempResolved()
    {
        // Package layout: bundled mods live under mods/_base-mods/, user mods at top level.
        Directory.CreateDirectory(GameDir);
        File.WriteAllText(Path.Combine(GameDir, "granblue_fantasy_relink.exe"), "MZ");
        File.WriteAllText(Path.Combine(GameDir, "data.i"), "index");
        _temp.CreateMod("game/mods/_base-mods/gbfrelink.utility.manager", "gbfrelink.utility.manager");
        _temp.CreateMod("game/mods/UserMod", "user.mod");

        Assert.Equal(0, SyncRunner.Run(GameDir));

        // Reloaded creates conversion output after pre-load sync, in the
        // *nested* utility manager's temp dir, before PostLoad mirrors it.
        var tempOut = Path.Combine(
            GameDir, "mods", "_base-mods", "gbfrelink.utility.manager", "temp", "user.mod", "model");
        Directory.CreateDirectory(tempOut);
        File.WriteAllText(Path.Combine(tempOut, "x.msg"), "converted");

        Assert.Equal(0, SyncRunner.PostLoad(GameDir));

        var appConfigPath = Path.Combine(
            GameDir, "reloaded-dropin", "generated", "Apps", "granblue_fantasy_relink.exe", "AppConfig.json");
        using var appConfig = JsonDocument.Parse(File.ReadAllText(appConfigPath));
        var enabled = appConfig.RootElement.GetProperty("EnabledMods").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("gbfrelink.utility.manager", enabled);
        Assert.Contains("user.mod", enabled);

        // Conversion refresh found the nested temp dir and mirrored its output.
        Assert.Equal("converted", File.ReadAllText(Path.Combine(GameDir, "data", "model", "x.msg")));
    }

    [Fact]
    public void OverlayDisabledModsAreExcludedFromEnabledList()
    {
        CreateFakeRelinkInstall();
        _temp.CreateMod("game/mods/SecondMod", "second.mod");
        new ReloadedDropIn.Core.Configuration.OverlayOverrides { DisabledMods = ["user.mod"] }
            .Save(Path.Combine(GameDir, "reloaded-dropin"));

        Assert.Equal(0, SyncRunner.Run(GameDir));

        var appConfigPath = Path.Combine(
            GameDir, "reloaded-dropin", "generated", "Apps", "granblue_fantasy_relink.exe", "AppConfig.json");
        using var appConfig = JsonDocument.Parse(File.ReadAllText(appConfigPath));
        var enabled = appConfig.RootElement.GetProperty("EnabledMods").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("user.mod", enabled);
        Assert.Contains("second.mod", enabled);
        // Required mods can't be disabled via overlay.
        Assert.Contains("gbfrelink.utility.manager", enabled);
    }

    [Fact]
    public void MissingUtilityManagerDisablesDependentModsInsteadOfBreakingReloaded()
    {
        // Regression: first launch where the base-mod download failed. User
        // mods declare gbfrelink.utility.manager as a ModDependency; enabling
        // them while it's absent makes Reloaded abort the ENTIRE load with an
        // error dialog. The sync must enable nothing that references it.
        Directory.CreateDirectory(GameDir);
        File.WriteAllText(Path.Combine(GameDir, "granblue_fantasy_relink.exe"), "MZ");
        File.WriteAllText(Path.Combine(GameDir, "data.i"), "index");
        _temp.CreateMod("game/mods/SwapMod", "swap.mod", dependencies: ["gbfrelink.utility.manager"]);
        _temp.CreateMod("game/mods/Standalone", "standalone.mod");

        Assert.Equal(0, SyncRunner.Run(GameDir));

        var appConfigPath = Path.Combine(
            GameDir, "reloaded-dropin", "generated", "Apps", "granblue_fantasy_relink.exe", "AppConfig.json");
        using var appConfig = JsonDocument.Parse(File.ReadAllText(appConfigPath));
        var enabled = appConfig.RootElement.GetProperty("EnabledMods").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("gbfrelink.utility.manager", enabled);
        Assert.DoesNotContain("swap.mod", enabled);
        Assert.Contains("standalone.mod", enabled);

        var syncLog = File.ReadAllText(Path.Combine(GameDir, "reloaded-dropin", "logs", "sync.log"));
        Assert.Contains("required mod gbfrelink.utility.manager is missing", syncLog);
        Assert.Contains("swap.mod left disabled", syncLog);
    }

    [Fact]
    public void P5rInstallIsDetectedAndConfigured()
    {
        Directory.CreateDirectory(GameDir);
        File.WriteAllText(Path.Combine(GameDir, "P5R.exe"), "MZ");
        _temp.CreateMod("game/mods/_base-mods/p5rpc.modloader", "p5rpc.modloader");
        _temp.CreateMod("game/mods/CostumeMod", "user.costume", dependencies: ["p5rpc.modloader"]);

        Assert.Equal(0, SyncRunner.Run(GameDir));
        Assert.Equal(0, SyncRunner.PostLoad(GameDir));

        var appConfigPath = Path.Combine(
            GameDir, "reloaded-dropin", "generated", "Apps", "p5r.exe", "AppConfig.json");
        Assert.True(File.Exists(appConfigPath));
        using var appConfig = JsonDocument.Parse(File.ReadAllText(appConfigPath));
        var enabled = appConfig.RootElement.GetProperty("EnabledMods").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("p5rpc.modloader", enabled);
        Assert.Contains("user.costume", enabled);

        var syncLog = File.ReadAllText(Path.Combine(GameDir, "reloaded-dropin", "logs", "sync.log"));
        Assert.Contains("adapter: p5r", syncLog);
    }

    [Fact]
    public void UndetectedGameFailsWithoutWritingLoaderConfig()
    {
        Directory.CreateDirectory(GameDir);

        var result = SyncRunner.Run(GameDir);

        Assert.Equal(2, result);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "appdata", "Reloaded-Mod-Loader-II", "ReloadedII.json")));
    }

    [Fact]
    public void BrokenInstallFailsValidationWithoutWritingLoaderConfig()
    {
        CreateFakeRelinkInstall();
        File.Delete(Path.Combine(GameDir, "data.i"));

        var result = SyncRunner.Run(GameDir);

        Assert.Equal(3, result);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "appdata", "Reloaded-Mod-Loader-II", "ReloadedII.json")));
    }
}
