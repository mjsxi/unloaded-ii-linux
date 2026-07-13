using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.P5R;
using ReloadedDropIn.Bootstrap.Update;

namespace ReloadedDropIn.Tests;

public class P5rAdapterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = Path.Combine(_temp.Path, "game"),
        ModsDirectory = Path.Combine(_temp.Path, "game", "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "game", "reloaded-dropin"),
    };

    [Fact]
    public void DetectsP5rExecutable()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "P5R.exe"), "MZ");

        var detection = new P5rAdapter().Detect(Context);

        Assert.True(detection.IsDetected);
        Assert.Equal("P5R.exe", detection.MatchedExecutable);
    }

    [Fact]
    public void DoesNotDetectOtherGames()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "granblue_fantasy_relink.exe"), "MZ");

        Assert.False(new P5rAdapter().Detect(Context).IsDetected);
    }

    [Fact]
    public void EveryRequiredModHasAnInstallSource()
    {
        // No mods are bundled; if any required mod lacked an update source the
        // stack could never self-assemble and Reloaded would refuse to load.
        foreach (var mod in new P5rAdapter().GetRequiredMods())
        {
            Assert.False(string.IsNullOrWhiteSpace(mod.UpdateRepo), $"{mod.Id} has no UpdateRepo");
            Assert.False(string.IsNullOrWhiteSpace(mod.UpdateAssetPrefix), $"{mod.Id} has no UpdateAssetPrefix");
        }
    }

    [Fact]
    public void OnlyTheModLoaderIsEnabled()
    {
        var required = new P5rAdapter().GetRequiredMods();

        var enabled = Assert.Single(required, m => m.Enabled);
        Assert.Equal(P5rAdapter.ModLoaderModId, enabled.Id);
        Assert.Equal(14, required.Count);
    }

    [Fact]
    public void UsesSharedWinmmProxy()
    {
        var injection = new P5rAdapter().GetInjectionConfiguration(Context);

        Assert.Equal("winmm.dll", injection.PreferredProxyDll);
        Assert.Equal("winmm=n,b", injection.WineDllOverride);
    }

    [Fact]
    public void DeclaresPersonaEssentialsCacheAsDisposable()
    {
        var loader = _temp.CreateMod("game/mods/_base-mods/persona", P5rAdapter.ModLoaderModId);

        var paths = new P5rAdapter().GetDisposableModStatePaths(Context);

        Assert.Equal([Path.Combine(loader, "Cache")], paths);
    }

    [Fact]
    public void SiblingAssetsDoNotSatisfyEachOthersPrefixes()
    {
        // These repos publish multiple mods per release; a prefix mix-up would
        // install the wrong mod under the right ModId.
        Assert.NotNull(BaseModInstaller.ParseAssetVersion("CriFs.V2.Hook2.6.1.7z", "CriFs.V2.Hook"));
        Assert.Null(BaseModInstaller.ParseAssetVersion("CriFs.V2.Hook.Awb1.1.2.7z", "CriFs.V2.Hook"));
        Assert.NotNull(BaseModInstaller.ParseAssetVersion("CriFs.V2.Hook.Awb1.1.2.7z", "CriFs.V2.Hook.Awb"));
        Assert.Null(BaseModInstaller.ParseAssetVersion("CriFs.V2.Hook2.6.1.7z", "CriFs.V2.Hook.Awb"));
        Assert.NotNull(BaseModInstaller.ParseAssetVersion("FileEmulationFramework2.3.0.7z", "FileEmulationFramework"));
        Assert.Null(BaseModInstaller.ParseAssetVersion("FileEmulationFramework.Lib.1.1.2.nupkg", "FileEmulationFramework"));
        Assert.NotNull(BaseModInstaller.ParseAssetVersion(
            "Reloaded.Universal.Localisation.Provider.Steam1.0.1.7z", "Reloaded.Universal.Localisation.Provider.Steam"));
        Assert.Null(BaseModInstaller.ParseAssetVersion(
            "Reloaded.Universal.Localisation.Provider.Steam1.0.1.7z", "Reloaded.Universal.Localisation.Framework"));
    }
}
