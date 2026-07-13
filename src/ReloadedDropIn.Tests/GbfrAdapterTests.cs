using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.GBFR;

namespace ReloadedDropIn.Tests;

public class GbfrAdapterTests
{
    private static AdapterContext Context(string gameDirectory) => new()
    {
        GameDirectory = gameDirectory,
        ModsDirectory = Path.Combine(gameDirectory, "mods"),
        DropInDirectory = Path.Combine(gameDirectory, "reloaded-dropin"),
    };

    private static void CreateFakeRelinkInstall(TempDirectory temp)
    {
        File.WriteAllText(Path.Combine(temp.Path, "granblue_fantasy_relink.exe"), "MZ");
        File.WriteAllText(Path.Combine(temp.Path, "data.i"), "index");
        Directory.CreateDirectory(Path.Combine(temp.Path, "mods"));
    }

    [Fact]
    public void DetectsRelinkByExecutable()
    {
        using var temp = new TempDirectory();
        CreateFakeRelinkInstall(temp);

        var detection = new GbfrAdapter().Detect(Context(temp.Path));

        Assert.True(detection.IsDetected);
        Assert.Equal("granblue_fantasy_relink.exe", detection.MatchedExecutable);
    }

    [Fact]
    public void DoesNotDetectEmptyDirectory()
    {
        using var temp = new TempDirectory();

        Assert.False(new GbfrAdapter().Detect(Context(temp.Path)).IsDetected);
    }

    [Fact]
    public void ValidInstallPassesValidation()
    {
        using var temp = new TempDirectory();
        CreateFakeRelinkInstall(temp);

        Assert.True(new GbfrAdapter().ValidateInstallation(Context(temp.Path)).IsValid);
    }

    [Fact]
    public void MissingDataIndexFailsValidation()
    {
        using var temp = new TempDirectory();
        CreateFakeRelinkInstall(temp);
        File.Delete(Path.Combine(temp.Path, "data.i"));

        var result = new GbfrAdapter().ValidateInstallation(Context(temp.Path));

        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, m => m.Check == "data.i" && m.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void RequiredModsIncludeUtilityManagerAndItsDependencies()
    {
        var required = new GbfrAdapter().GetRequiredMods();

        var manager = Assert.Single(required, m => m.Id == GbfrAdapter.UtilityManagerModId);
        Assert.True(manager.Enabled);
        // The three hard ModDependencies of gbfrelink.utility.manager (see research/gbfr-notes.md).
        Assert.Contains(required, m => m.Id == "Reloaded.Memory.SigScan.ReloadedII");
        Assert.Contains(required, m => m.Id == "reloaded.sharedlib.hooks");
        Assert.Contains(required, m => m.Id == "reloaded.universal.redirector");
    }

    [Fact]
    public void DeclaresOnlyUtilityManagerGeneratedStateAsDisposable()
    {
        using var temp = new TempDirectory();
        CreateFakeRelinkInstall(temp);
        var utility = temp.CreateMod("mods/_base-mods/utility", GbfrAdapter.UtilityManagerModId);

        var paths = new GbfrAdapter().GetDisposableModStatePaths(Context(temp.Path));

        Assert.Equal(
            [Path.Combine(utility, "temp"), Path.Combine(utility, "cached_files.txt")],
            paths);
    }
}
