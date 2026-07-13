using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.GBFR;

namespace ReloadedDropIn.Tests;

public class GbfrDataMirrorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = _temp.Path,
        ModsDirectory = Path.Combine(_temp.Path, "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "reloaded-dropin"),
    };

    private string DataPath(string relative) => Path.Combine(_temp.Path, "data", relative);

    private GbfrDataMirror.MirrorSource Source(string modId) =>
        new(modId, Path.Combine(_temp.Path, "mods", modId));

    private void WriteModFile(string modId, string gamePath, string content)
    {
        var path = Path.Combine(_temp.Path, "mods", modId, "GBFR", "data", gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteTempFile(string modId, string gamePath, string content)
    {
        var path = Path.Combine(_temp.Path, "mods", "gbfrelink.utility.manager", "temp", modId, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void MirrorsModFilesIntoData()
    {
        WriteModFile("some.mod", "model/pl/pl0001/pl0001.minfo", "modded-minfo");

        var log = new GbfrDataMirror(Context).Reconcile([Source("some.mod")]);

        Assert.Equal("modded-minfo", File.ReadAllText(DataPath("model/pl/pl0001/pl0001.minfo")));
        Assert.Contains(log, l => l.Contains("mirrored 1 file(s)"));
    }

    [Fact]
    public void ConvertedTempOutputOverridesRawFile()
    {
        WriteModFile("some.mod", "model/x.minfo", "raw");
        WriteTempFile("some.mod", "model/x.minfo", "upgraded");

        new GbfrDataMirror(Context).Reconcile([Source("some.mod")]);

        Assert.Equal("upgraded", File.ReadAllText(DataPath("model/x.minfo")));
    }

    [Fact]
    public void LaterModWinsConflicts()
    {
        WriteModFile("mod.a", "texture/t.texture", "from-a");
        WriteModFile("mod.b", "texture/t.texture", "from-b");

        new GbfrDataMirror(Context).Reconcile([Source("mod.a"), Source("mod.b")]);

        Assert.Equal("from-b", File.ReadAllText(DataPath("texture/t.texture")));
    }

    [Fact]
    public void RemovedModFilesAreCleanedUp()
    {
        WriteModFile("some.mod", "model/gone.minfo", "content");
        var mirror = new GbfrDataMirror(Context);
        mirror.Reconcile([Source("some.mod")]);
        Assert.True(File.Exists(DataPath("model/gone.minfo")));

        Directory.Delete(Path.Combine(_temp.Path, "mods", "some.mod"), recursive: true);
        var log = new GbfrDataMirror(Context).Reconcile(Array.Empty<GbfrDataMirror.MirrorSource>());

        Assert.False(File.Exists(DataPath("model/gone.minfo")));
        Assert.Contains(log, l => l.Contains("0 copied, 1 removed"));
    }

    [Fact]
    public void DisplacedStockFileIsBackedUpAndRestored()
    {
        // Stock loose file already in data/ (like the game's sound files).
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath("sound/core.bnk"))!);
        File.WriteAllText(DataPath("sound/core.bnk"), "stock");
        WriteModFile("sound.mod", "sound/core.bnk", "modded");

        var mirror = new GbfrDataMirror(Context);
        var log = mirror.Reconcile([Source("sound.mod")]);
        Assert.Equal("modded", File.ReadAllText(DataPath("sound/core.bnk")));
        Assert.Contains(log, l => l.Contains("backed up stock file"));

        Directory.Delete(Path.Combine(_temp.Path, "mods", "sound.mod"), recursive: true);
        var restoreLog = new GbfrDataMirror(Context).Reconcile(Array.Empty<GbfrDataMirror.MirrorSource>());

        Assert.Equal("stock", File.ReadAllText(DataPath("sound/core.bnk")));
        Assert.Contains(restoreLog, l => l.Contains("restored stock file"));
    }

    [Fact]
    public void ReconcileIsIdempotent()
    {
        WriteModFile("some.mod", "model/a.minfo", "content");
        var mirror = new GbfrDataMirror(Context);
        mirror.Reconcile([Source("some.mod")]);

        var log = new GbfrDataMirror(Context).Reconcile([Source("some.mod")]);

        Assert.Contains(log, l => l.Contains("(0 copied, 0 removed)"));
    }

    [Fact]
    public void FreshConversionsOverwriteExistingMirroredFiles()
    {
        WriteModFile("some.mod", "model/x.minfo", "raw");
        var mirror = new GbfrDataMirror(Context);
        mirror.Reconcile([Source("some.mod")]);
        Assert.Equal("raw", File.ReadAllText(DataPath("model/x.minfo")));

        // Utility manager regenerates an upgraded version during mod load.
        WriteTempFile("some.mod", "model/x.minfo", "upgraded-this-run");
        var log = new GbfrDataMirror(Context).MirrorFreshConversions(["some.mod"]);

        Assert.Equal("upgraded-this-run", File.ReadAllText(DataPath("model/x.minfo")));
        Assert.Contains(log, l => l.Contains("refreshed 1 converted file(s)"));
        Assert.False(File.Exists(DataPath("model/x.minfo.dropin-staging")));
    }

    [Fact]
    public void FreshConversionFilesAreTrackedInManifestForCleanup()
    {
        WriteTempFile("some.mod", "model/converted.msg", "converted");
        new GbfrDataMirror(Context).MirrorFreshConversions(["some.mod"]);
        Assert.True(File.Exists(DataPath("model/converted.msg")));

        // Mod (and its temp outputs) removed: next reconcile cleans data/.
        Directory.Delete(Path.Combine(_temp.Path, "mods", "gbfrelink.utility.manager"), recursive: true);
        new GbfrDataMirror(Context).Reconcile(Array.Empty<GbfrDataMirror.MirrorSource>());

        Assert.False(File.Exists(DataPath("model/converted.msg")));
    }

    [Fact]
    public void ModFolderNamedDifferentlyFromModIdIsMirrored()
    {
        // Regression: gbfrelink.yorha.captain folder contained ModId
        // gbfrelink.nines.gran; the mirror must use the directory, not the ID.
        var folder = Path.Combine(_temp.Path, "mods", "gbfrelink.yorha.captain", "GBFR", "data", "model");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "np0100.mmesh"), "pod-mesh");

        var source = new GbfrDataMirror.MirrorSource(
            "gbfrelink.nines.gran", Path.Combine(_temp.Path, "mods", "gbfrelink.yorha.captain"));
        var log = new GbfrDataMirror(Context).Reconcile([source]);

        Assert.Equal("pod-mesh", File.ReadAllText(DataPath("model/np0100.mmesh")));
        Assert.Contains(log, l => l.Contains("mirrored 1 file(s)"));
    }

    [Fact]
    public void StockFilesNotTouchedByModsAreLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath("sound/untouched.bnk"))!);
        File.WriteAllText(DataPath("sound/untouched.bnk"), "stock");

        new GbfrDataMirror(Context).Reconcile(Array.Empty<GbfrDataMirror.MirrorSource>());

        Assert.Equal("stock", File.ReadAllText(DataPath("sound/untouched.bnk")));
    }
}
