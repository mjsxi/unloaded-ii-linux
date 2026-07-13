using ReloadedDropIn.Core.Configuration;

namespace ReloadedDropIn.Tests;

public class ModStateHealerTests
{
    [Fact]
    public void FirstRunClearsExistingDisposableStateAndRecordsBaseline()
    {
        using var temp = new TempDirectory();
        var dropIn = Path.Combine(temp.Path, "reloaded-dropin");
        var cacheDirectory = Path.Combine(temp.Path, "mods", "loader", "Cache");
        var cacheFile = Path.Combine(temp.Path, "mods", "loader", "cached_files.txt");
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllText(Path.Combine(cacheDirectory, "merged.bin"), "old");
        File.WriteAllText(cacheFile, "old");

        var log = new ModStateHealer().Reconcile(
            temp.Path, dropIn, "test", ["loader", "user.mod"], [cacheDirectory, cacheFile]);

        Assert.False(Directory.Exists(cacheDirectory));
        Assert.False(File.Exists(cacheFile));
        Assert.True(File.Exists(Path.Combine(dropIn, "state", "test-active-mods.json")));
        Assert.Contains(log, line => line.Contains("clean active-mod baseline"));
    }

    [Fact]
    public void UnchangedModSetKeepsCache()
    {
        using var temp = new TempDirectory();
        var dropIn = Path.Combine(temp.Path, "reloaded-dropin");
        var cacheDirectory = Path.Combine(temp.Path, "mods", "loader", "Cache");
        var healer = new ModStateHealer();
        healer.Reconcile(temp.Path, dropIn, "test", ["loader"], [cacheDirectory]);
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllText(Path.Combine(cacheDirectory, "merged.bin"), "current");

        var log = healer.Reconcile(temp.Path, dropIn, "test", ["LOADER"], [cacheDirectory]);

        Assert.True(File.Exists(Path.Combine(cacheDirectory, "merged.bin")));
        Assert.Empty(log);
    }

    [Fact]
    public void RemovedModClearsCache()
    {
        using var temp = new TempDirectory();
        var dropIn = Path.Combine(temp.Path, "reloaded-dropin");
        var cacheDirectory = Path.Combine(temp.Path, "mods", "loader", "Cache");
        var healer = new ModStateHealer();
        healer.Reconcile(temp.Path, dropIn, "test", ["loader", "removed.mod"], [cacheDirectory]);
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllText(Path.Combine(cacheDirectory, "merged.bin"), "stale");

        var log = healer.Reconcile(temp.Path, dropIn, "test", ["loader"], [cacheDirectory]);

        Assert.False(Directory.Exists(cacheDirectory));
        Assert.Contains(log, line => line.Contains("active mod set changed"));
    }

    [Fact]
    public void RefusesPathsOutsideGameDirectory()
    {
        using var game = new TempDirectory();
        using var outside = new TempDirectory();
        var outsideFile = Path.Combine(outside.Path, "keep.txt");
        File.WriteAllText(outsideFile, "keep");

        var log = new ModStateHealer().Reconcile(
            game.Path, Path.Combine(game.Path, "reloaded-dropin"), "test", ["loader"], [outsideFile]);

        Assert.True(File.Exists(outsideFile));
        Assert.Contains(log, line => line.Contains("refused unsafe"));
    }
}
