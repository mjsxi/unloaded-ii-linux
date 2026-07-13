using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.GBFR;

namespace ReloadedDropIn.Tests;

public class RestoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = _temp.Path,
        ModsDirectory = Path.Combine(_temp.Path, "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "reloaded-dropin"),
    };

    [Fact]
    public async Task RestoreUndoesIndexAndMirroredFiles()
    {
        // Simulate a modded install: baseline, mirrored file, applied index.
        File.WriteAllText(Path.Combine(_temp.Path, "data.i"), "original");
        File.WriteAllText(Path.Combine(_temp.Path, "granblue_fantasy_relink.exe"), "MZ");
        var index = new GbfrDataIndex(Context);
        index.EnsureBaseline();

        var modDir = Path.Combine(_temp.Path, "mods", "SomeMod", "GBFR", "data", "model");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "x.minfo"), "modded");
        new GbfrDataMirror(Context).Reconcile(
            [new GbfrDataMirror.MirrorSource("some.mod", Path.Combine(_temp.Path, "mods", "SomeMod"))]);

        Directory.CreateDirectory(Path.GetDirectoryName(index.ModdedIndexPath)!);
        File.WriteAllText(index.ModdedIndexPath, "modded-index");
        index.ApplyModdedIndex(DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal("modded-index", File.ReadAllText(Path.Combine(_temp.Path, "data.i")));
        Assert.True(File.Exists(Path.Combine(_temp.Path, "data", "model", "x.minfo")));

        var log = await new GbfrAdapter().RestoreAsync(Context, CancellationToken.None);

        Assert.Equal("original", File.ReadAllText(Path.Combine(_temp.Path, "data.i")));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "data", "model", "x.minfo")));
        Assert.Contains(log, l => l.Contains("restored pristine data.i"));
    }

    [Fact]
    public async Task RestoreOnCleanInstallIsHarmless()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "data.i"), "original");
        File.WriteAllText(Path.Combine(_temp.Path, "granblue_fantasy_relink.exe"), "MZ");

        var log = await new GbfrAdapter().RestoreAsync(Context, CancellationToken.None);

        Assert.Equal("original", File.ReadAllText(Path.Combine(_temp.Path, "data.i")));
        Assert.Contains(log, l => l.Contains("no backup to restore from"));
    }
}
