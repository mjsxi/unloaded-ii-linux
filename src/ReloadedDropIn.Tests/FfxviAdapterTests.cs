using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.FFXVI;

namespace ReloadedDropIn.Tests;

public class FfxviAdapterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = Path.Combine(_temp.Path, "game"),
        ModsDirectory = Path.Combine(_temp.Path, "game", "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "game", "reloaded-dropin"),
    };

    private string DataDirectory => Path.Combine(Context.GameDirectory, "data");

    [Fact]
    public void DetectsFfxviExecutable()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "ffxvi.exe"), "MZ");

        var detection = new FfxviAdapter().Detect(Context);

        Assert.True(detection.IsDetected);
        Assert.Equal("ffxvi.exe", detection.MatchedExecutable);
    }

    [Fact]
    public void DetectsTheDemoExecutable()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "ffxvi_demo.exe"), "MZ");

        var detection = new FfxviAdapter().Detect(Context);

        Assert.True(detection.IsDetected);
        Assert.Equal("ffxvi_demo.exe", detection.MatchedExecutable);
    }

    [Fact]
    public void DoesNotDetectOtherGames()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "P5R.exe"), "MZ");

        Assert.False(new FfxviAdapter().Detect(Context).IsDetected);
    }

    [Fact]
    public void EveryRequiredModHasAnInstallSource()
    {
        foreach (var mod in new FfxviAdapter().GetRequiredMods())
        {
            Assert.False(string.IsNullOrWhiteSpace(mod.UpdateRepo), $"{mod.Id} has no UpdateRepo");
            Assert.False(string.IsNullOrWhiteSpace(mod.UpdateAssetPrefix), $"{mod.Id} has no UpdateAssetPrefix");
        }
    }

    [Fact]
    public void OnlyTheModLoaderIsEnabled()
    {
        var required = new FfxviAdapter().GetRequiredMods();

        var enabled = Assert.Single(required, m => m.Enabled);
        Assert.Equal(FfxviAdapter.ModLoaderModId, enabled.Id);
        Assert.Equal(3, required.Count);
    }

    [Fact]
    public void UsesSharedWinmmProxy()
    {
        var injection = new FfxviAdapter().GetInjectionConfiguration(Context);

        Assert.Equal("winmm.dll", injection.PreferredProxyDll);
        Assert.Equal("winmm=n,b", injection.WineDllOverride);
    }

    [Fact]
    public void ValidationRequiresTheDataDirectory()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "ffxvi.exe"), "MZ");

        var withoutData = new FfxviAdapter().ValidateInstallation(Context);
        Assert.Contains(withoutData.Messages,
            m => m.Severity == ValidationSeverity.Error && m.Check == "data");

        Directory.CreateDirectory(DataDirectory);
        var withData = new FfxviAdapter().ValidateInstallation(Context);
        Assert.DoesNotContain(withData.Messages, m => m.Check == "data");
    }

    [Fact]
    public void DeclaresGeneratedDiffPacsAsDisposable()
    {
        Directory.CreateDirectory(DataDirectory);
        var diffPac = Path.Combine(DataDirectory, "0001.diff.pac");
        File.WriteAllText(diffPac, "pac");
        File.WriteAllText(Path.Combine(DataDirectory, "0001.pac"), "pac");

        var paths = new FfxviAdapter().GetDisposableModStatePaths(Context);

        Assert.Equal([diffPac], paths);
    }

    [Fact]
    public async Task RestoreDeletesOnlyGeneratedDiffPacs()
    {
        Directory.CreateDirectory(DataDirectory);
        var diffPac = Path.Combine(DataDirectory, "0001.diff.pac");
        var stockPac = Path.Combine(DataDirectory, "0001.pac");
        File.WriteAllText(diffPac, "pac");
        File.WriteAllText(stockPac, "pac");

        var log = await new FfxviAdapter().RestoreAsync(Context, CancellationToken.None);

        Assert.False(File.Exists(diffPac));
        Assert.True(File.Exists(stockPac));
        Assert.Contains(log, line => line.Contains("0001.diff.pac"));
    }

    [Fact]
    public async Task RestoreOnCleanInstallReportsNothingToDo()
    {
        Directory.CreateDirectory(Context.GameDirectory);

        var log = await new FfxviAdapter().RestoreAsync(Context, CancellationToken.None);

        Assert.Contains(log, line => line.Contains("nothing to restore"));
    }
}
