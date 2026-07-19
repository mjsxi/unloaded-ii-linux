using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.DSTS;

namespace ReloadedDropIn.Tests;

public class DstsAdapterTests : IDisposable
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
    public void DetectsTheGameExecutable()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "Digimon Story Time Stranger.exe"), "MZ");

        var detection = new DstsAdapter().Detect(Context);

        Assert.True(detection.IsDetected);
        Assert.Equal("Digimon Story Time Stranger.exe", detection.MatchedExecutable);
    }

    [Fact]
    public void DoesNotDetectOtherGames()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "ffxvi.exe"), "MZ");

        Assert.False(new DstsAdapter().Detect(Context).IsDetected);
    }

    [Fact]
    public void EveryRequiredModHasAnInstallSource()
    {
        foreach (var mod in new DstsAdapter().GetRequiredMods())
        {
            Assert.False(string.IsNullOrWhiteSpace(mod.UpdateRepo), $"{mod.Id} has no UpdateRepo");
            Assert.False(string.IsNullOrWhiteSpace(mod.UpdateAssetPrefix), $"{mod.Id} has no UpdateAssetPrefix");
        }
    }

    [Fact]
    public void OnlyTheModLoaderIsEnabled()
    {
        var required = new DstsAdapter().GetRequiredMods();

        var enabled = Assert.Single(required, m => m.Enabled);
        Assert.Equal(DstsAdapter.ModLoaderModId, enabled.Id);
        Assert.Equal(7, required.Count);
    }

    [Fact]
    public void UsesSharedWinmmProxy()
    {
        var injection = new DstsAdapter().GetInjectionConfiguration(Context);

        Assert.Equal("winmm.dll", injection.PreferredProxyDll);
        Assert.Equal("winmm=n,b", injection.WineDllOverride);
    }

    [Fact]
    public void ValidationWarnsWhenModsDirectoryIsMissing()
    {
        Directory.CreateDirectory(Context.GameDirectory);
        File.WriteAllText(Path.Combine(Context.GameDirectory, "Digimon Story Time Stranger.exe"), "MZ");

        var result = new DstsAdapter().ValidateInstallation(Context);

        Assert.Contains(result.Messages,
            m => m.Severity == ValidationSeverity.Warning && m.Check == "mods");
        Assert.DoesNotContain(result.Messages, m => m.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void DeclaresNoDisposableState()
    {
        Directory.CreateDirectory(Context.GameDirectory);

        Assert.Empty(new DstsAdapter().GetDisposableModStatePaths(Context));
    }

    [Fact]
    public async Task RestoreReportsNothingToDo()
    {
        Directory.CreateDirectory(Context.GameDirectory);

        var log = await new DstsAdapter().RestoreAsync(Context, CancellationToken.None);

        Assert.Contains(log, line => line.Contains("nothing to restore"));
    }
}
