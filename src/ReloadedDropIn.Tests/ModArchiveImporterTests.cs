using System.IO.Compression;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Bootstrap;

namespace ReloadedDropIn.Tests;

public class ModArchiveImporterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = Path.Combine(_temp.Path, "game"),
        ModsDirectory = Path.Combine(_temp.Path, "game", "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "game", "reloaded-dropin"),
    };

    private string WriteArchive(string fileName, params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(Context.ModsDirectory);
        var archivePath = Path.Combine(Context.ModsDirectory, fileName);
        using var stream = File.Create(archivePath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(path).Open());
            writer.Write(content);
        }

        return archivePath;
    }

    private static string ModConfig(string modId, string version = "1.0.0") =>
        $$"""{ "ModId": "{{modId}}", "ModName": "{{modId}}", "ModVersion": "{{version}}" }""";

    [Fact]
    public void RootLevelModIsExtractedAndArchiveRemoved()
    {
        var archive = WriteArchive("CoolMod.zip",
            ("ModConfig.json", ModConfig("cool.mod")),
            ("GBFR/data/model/x.bin", "bytes"));

        var log = new ModArchiveImporter(Context).ImportAll();

        Assert.Contains(log, l => l.Contains("installed mod 'cool.mod'"));
        Assert.Equal("bytes",
            File.ReadAllText(Path.Combine(Context.ModsDirectory, "cool.mod", "GBFR", "data", "model", "x.bin")));
        Assert.False(File.Exists(archive), "archive must be removed after successful install");
    }

    [Fact]
    public void NestedFolderIsStripped()
    {
        // The classic Nexus layout: everything wrapped in a folder inside the zip.
        WriteArchive("CoolMod-1-0.zip",
            ("Cool Mod v1/ModConfig.json", ModConfig("cool.mod")),
            ("Cool Mod v1/GBFR/data/x.bin", "bytes"));

        new ModArchiveImporter(Context).ImportAll();

        Assert.True(File.Exists(Path.Combine(Context.ModsDirectory, "cool.mod", "ModConfig.json")));
        Assert.True(File.Exists(Path.Combine(Context.ModsDirectory, "cool.mod", "GBFR", "data", "x.bin")));
        Assert.False(Directory.Exists(Path.Combine(Context.ModsDirectory, "cool.mod", "Cool Mod v1")));
    }

    [Fact]
    public void MultiModArchiveInstallsEveryMod()
    {
        WriteArchive("pack.zip",
            ("ModA/ModConfig.json", ModConfig("mod.a")),
            ("ModB/ModConfig.json", ModConfig("mod.b")));

        var log = new ModArchiveImporter(Context).ImportAll();

        Assert.Contains(log, l => l.Contains("installed mod 'mod.a'"));
        Assert.Contains(log, l => l.Contains("installed mod 'mod.b'"));
    }

    [Fact]
    public void ReDroppedArchiveReplacesExistingInstallInPlace()
    {
        // v1 installed under a folder name that differs from the ModId.
        var existing = _temp.CreateMod("game/mods/OldFolderName", "cool.mod");
        File.WriteAllText(Path.Combine(existing, "stale.dll"), "old");

        WriteArchive("CoolMod-2-0.zip", ("ModConfig.json", ModConfig("cool.mod", "2.0.0")));

        var log = new ModArchiveImporter(Context).ImportAll();

        Assert.Contains(log, l => l.Contains("replaced installed mod 'cool.mod'"));
        Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(existing, "ModConfig.json")));
        Assert.False(File.Exists(Path.Combine(existing, "stale.dll")));
        Assert.False(Directory.Exists(Path.Combine(Context.ModsDirectory, "cool.mod")),
            "must replace the existing folder, not create a duplicate mod");
    }

    [Fact]
    public void ArchiveWithoutModConfigIsLeftInPlace()
    {
        var archive = WriteArchive("random.zip", ("readme.txt", "hi"));

        var log = new ModArchiveImporter(Context).ImportAll();

        Assert.Contains(log, l => l.Contains("not a Reloaded mod archive"));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void BaseModsAreNeverOverwrittenAndArchiveIsKept()
    {
        _temp.CreateMod("game/mods/_base-mods/gbfrelink.utility.manager", "gbfrelink.utility.manager");
        var archive = WriteArchive("um.zip",
            ("ModConfig.json", ModConfig("gbfrelink.utility.manager", "9.9.9")));

        var log = new ModArchiveImporter(Context).ImportAll();

        Assert.Contains(log, l => l.Contains("managed base mod"));
        Assert.True(File.Exists(archive), "archive stays when nothing was installed from it");
        Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(
            Context.ModsDirectory, "_base-mods", "gbfrelink.utility.manager", "ModConfig.json")));
    }

    [Fact]
    public void CorruptArchiveIsLeftInPlaceWithLogLine()
    {
        Directory.CreateDirectory(Context.ModsDirectory);
        var archive = Path.Combine(Context.ModsDirectory, "broken.zip");
        File.WriteAllBytes(archive, [0xDE, 0xAD]);

        var log = new ModArchiveImporter(Context).ImportAll();

        Assert.Contains(log, l => l.Contains("archive left in place"));
        Assert.True(File.Exists(archive));
    }

    [Theory]
    [InlineData(new[] { "ModConfig.json", "sub/ModConfig.json" }, new[] { "" })]
    [InlineData(new[] { "A/ModConfig.json", "A/deps/B/ModConfig.json" }, new[] { "A" })]
    [InlineData(new[] { "A/ModConfig.json", "B/ModConfig.json" }, new[] { "A", "B" })]
    [InlineData(new[] { "wrap/A/ModConfig.json" }, new[] { "wrap/A" })]
    public void FindModRootsKeepsOutermostRootsOnly(string[] keys, string[] expected) =>
        Assert.Equal(expected.OrderBy(x => x), ModArchiveImporter.FindModRoots(keys).OrderBy(x => x));
}
