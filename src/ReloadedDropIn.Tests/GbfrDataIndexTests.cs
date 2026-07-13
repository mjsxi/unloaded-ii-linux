using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.GBFR;

namespace ReloadedDropIn.Tests;

public class GbfrDataIndexTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = _temp.Path,
        ModsDirectory = Path.Combine(_temp.Path, "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "reloaded-dropin"),
    };

    private string GameIndex => Path.Combine(_temp.Path, "data.i");
    private string Backup => Path.Combine(_temp.Path, "reloaded-dropin", "backups", "gbfr", "data.i.orig");

    private GbfrDataIndex NewIndex() => new(Context);

    private void WriteModdedTempIndex(string content = "modded-index")
    {
        var index = NewIndex();
        Directory.CreateDirectory(Path.GetDirectoryName(index.ModdedIndexPath)!);
        File.WriteAllText(index.ModdedIndexPath, content);
    }

    [Fact]
    public void FirstBaselineCreatesBackup()
    {
        File.WriteAllText(GameIndex, "original");

        var log = NewIndex().EnsureBaseline();

        Assert.True(File.Exists(Backup));
        Assert.Equal("original", File.ReadAllText(Backup));
        Assert.Contains(log, l => l.Contains("created baseline backup"));
    }

    [Fact]
    public void ApplyThenBaselineRestoresPristine()
    {
        File.WriteAllText(GameIndex, "original");
        var index = NewIndex();
        index.EnsureBaseline();
        WriteModdedTempIndex();

        var applyLog = index.ApplyModdedIndex(DateTime.UtcNow.AddMinutes(-1));
        Assert.Contains(applyLog, l => l.Contains("applied modded index"));
        Assert.Equal("modded-index", File.ReadAllText(GameIndex));

        // Next launch: baseline puts the original back before mods load.
        var baselineLog = NewIndex().EnsureBaseline();
        Assert.Contains(baselineLog, l => l.Contains("restored pristine"));
        Assert.Equal("original", File.ReadAllText(GameIndex));
    }

    [Fact]
    public void StaleModdedIndexIsNotApplied()
    {
        File.WriteAllText(GameIndex, "original");
        var index = NewIndex();
        index.EnsureBaseline();
        WriteModdedTempIndex();

        var log = index.ApplyModdedIndex(notBefore: DateTime.UtcNow.AddMinutes(5));

        Assert.Contains(log, l => l.Contains("stale"));
        Assert.Equal("original", File.ReadAllText(GameIndex));
    }

    [Fact]
    public void MissingModdedIndexLeavesPristine()
    {
        File.WriteAllText(GameIndex, "original");
        var index = NewIndex();
        index.EnsureBaseline();

        var log = index.ApplyModdedIndex(DateTime.MinValue);

        Assert.Contains(log, l => l.Contains("no modded index"));
        Assert.Equal("original", File.ReadAllText(GameIndex));
    }

    [Fact]
    public void GameUpdateRefreshesBaselineInsteadOfClobbering()
    {
        File.WriteAllText(GameIndex, "original-v1");
        var index = NewIndex();
        index.EnsureBaseline();
        WriteModdedTempIndex();
        index.ApplyModdedIndex(DateTime.UtcNow.AddMinutes(-1));

        // Steam update replaces data.i with new content (neither original nor modded hash).
        File.WriteAllText(GameIndex, "original-v2");

        var log = NewIndex().EnsureBaseline();

        Assert.Contains(log, l => l.Contains("game update"));
        Assert.Equal("original-v2", File.ReadAllText(GameIndex));
        Assert.Equal("original-v2", File.ReadAllText(Backup));
    }

    [Fact]
    public void RefusesToApplyWithoutBackup()
    {
        File.WriteAllText(GameIndex, "original");
        WriteModdedTempIndex();

        var log = NewIndex().ApplyModdedIndex(DateTime.MinValue);

        Assert.Contains(log, l => l.Contains("refusing"));
        Assert.Equal("original", File.ReadAllText(GameIndex));
    }

    [Fact]
    public void RestorePutsOriginalBack()
    {
        File.WriteAllText(GameIndex, "original");
        var index = NewIndex();
        index.EnsureBaseline();
        WriteModdedTempIndex();
        index.ApplyModdedIndex(DateTime.UtcNow.AddMinutes(-1));

        var log = index.Restore();

        Assert.Contains(log, l => l.Contains("restored pristine"));
        Assert.Equal("original", File.ReadAllText(GameIndex));
    }
}
