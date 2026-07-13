using System.IO.Compression;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Bootstrap.Update;

namespace ReloadedDropIn.Tests;

public class BaseModInstallerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = Path.Combine(_temp.Path, "game"),
        ModsDirectory = Path.Combine(_temp.Path, "game", "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "game", "reloaded-dropin"),
    };

    private sealed class FakeFeed : IReleaseFeed
    {
        public IReadOnlyList<ReleaseAsset>? Assets { get; set; }
        public Dictionary<string, byte[]> Downloads { get; } = [];
        public int FeedCalls { get; private set; }

        public Task<IReadOnlyList<ReleaseAsset>?> GetLatestReleaseAssetsAsync(string repo, CancellationToken ct)
        {
            FeedCalls++;
            return Task.FromResult(Assets);
        }

        public Task<byte[]?> DownloadAsync(string url, CancellationToken ct) =>
            Task.FromResult(Downloads.TryGetValue(url, out var bytes) ? bytes : null);
    }

    private static RequiredMod UpdatableMod(string id = "test.mod", string prefix = "TestMod") => new()
    {
        Id = id, UpdateRepo = "owner/repo", UpdateAssetPrefix = prefix,
    };

    private static byte[] ZipWithModConfig(string modId, string version, params (string Path, string Content)[] extras)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("ModConfig.json").Open()))
                writer.Write($$"""{ "ModId": "{{modId}}", "ModName": "x", "ModVersion": "{{version}}" }""");
            foreach (var (path, content) in extras)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }

    [Theory]
    [InlineData("gbfrelink.utility.manager2.0.1.7z", "gbfrelink.utility.manager", "2.0.1")]
    [InlineData("Reloaded.Memory.SigScan.ReloadedII1.2.14.7z", "Reloaded.Memory.SigScan.ReloadedII", "1.2.14")]
    [InlineData("Reloaded.Hooks.ReloadedII1.16.3.7z", "reloaded.hooks.reloadedii", "1.16.3")]
    [InlineData("TestMod2.0.zip", "TestMod", "2.0")]
    public void ParsesVersionFromAssetName(string assetName, string prefix, string expected)
    {
        var version = BaseModInstaller.ParseAssetVersion(assetName, prefix);
        Assert.NotNull(version);
        Assert.Equal(Version.Parse(expected).Major, version!.Major);
        Assert.Equal(Version.Parse(expected).Minor, version.Minor);
    }

    [Theory]
    [InlineData("SomeOtherMod2.0.1.7z", "TestMod")]      // wrong prefix
    [InlineData("TestMod.7z", "TestMod")]                 // no version
    [InlineData("TestMod2.0.1.txt", "TestMod")]           // wrong extension
    // Real hazards seen in the Reloaded release feeds:
    [InlineData("Reloaded.Hooks.ReloadedII1.16.1_to_1.16.3.7z", "Reloaded.Hooks.ReloadedII")]           // delta archive
    [InlineData("Reloaded.Universal.RedirectorMonitor1.0.11.7z", "Reloaded.Universal.Redirector")]      // sibling mod
    [InlineData("Reloaded.Universal.Monitor1.0.12.7z", "Reloaded.Universal.Redirector")]                // sibling mod
    [InlineData("Reloaded.Memory.Sigscan.3.1.9.nupkg", "Reloaded.Memory.SigScan.ReloadedII")]           // nupkg
    public void RejectsNonMatchingAssetNames(string assetName, string prefix) =>
        Assert.Null(BaseModInstaller.ParseAssetVersion(assetName, prefix));

    [Fact]
    public async Task UpdatesOutdatedModAndPreservesNothingStale()
    {
        var modDir = _temp.CreateMod("game/mods/_base-mods/test.mod", "test.mod"); // ModVersion 1.0.0
        File.WriteAllText(Path.Combine(modDir, "stale-file.dll"), "old");

        var feed = new FakeFeed
        {
            Assets = [new ReleaseAsset("TestMod2.0.0.zip", "https://x/TestMod2.0.0.zip")],
        };
        feed.Downloads["https://x/TestMod2.0.0.zip"] =
            ZipWithModConfig("test.mod", "2.0.0", ("code/new.dll", "new"));

        var log = await new BaseModInstaller(Context, feed).RunAsync([UpdatableMod()], CancellationToken.None);

        Assert.Contains(log, l => l.Contains("updated 1.0.0 -> 2.0.0"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(modDir, "code", "new.dll")));
        Assert.False(File.Exists(Path.Combine(modDir, "stale-file.dll")),
            "update must replace the directory, not merge into it");
        Assert.Contains("\"ModVersion\": \"2.0.0\"", File.ReadAllText(Path.Combine(modDir, "ModConfig.json")));
    }

    [Fact]
    public async Task LeavesUpToDateModAlone()
    {
        _temp.CreateMod("game/mods/_base-mods/test.mod", "test.mod"); // 1.0.0
        var feed = new FakeFeed { Assets = [new ReleaseAsset("TestMod1.0.0.7z", "https://x/a.7z")] };

        var log = await new BaseModInstaller(Context, feed).RunAsync([UpdatableMod()], CancellationToken.None);

        Assert.Contains(log, l => l.Contains("up to date"));
        Assert.Empty(feed.Downloads); // nothing was fetched
    }

    [Fact]
    public async Task WrongModIdInArchiveIsRejectedAndInstallKept()
    {
        var modDir = _temp.CreateMod("game/mods/_base-mods/test.mod", "test.mod");
        var feed = new FakeFeed { Assets = [new ReleaseAsset("TestMod2.0.0.zip", "https://x/a.zip")] };
        feed.Downloads["https://x/a.zip"] = ZipWithModConfig("evil.other.mod", "2.0.0");

        var log = await new BaseModInstaller(Context, feed).RunAsync([UpdatableMod()], CancellationToken.None);

        Assert.Contains(log, l => l.Contains("failed") && l.Contains("keeping installed version"));
        Assert.Contains("\"ModVersion\": \"1.0.0\"", File.ReadAllText(Path.Combine(modDir, "ModConfig.json")));
    }

    [Fact]
    public async Task CorruptArchiveIsRejectedAndInstallKept()
    {
        var modDir = _temp.CreateMod("game/mods/_base-mods/test.mod", "test.mod");
        var feed = new FakeFeed { Assets = [new ReleaseAsset("TestMod2.0.0.zip", "https://x/a.zip")] };
        feed.Downloads["https://x/a.zip"] = [0xDE, 0xAD, 0xBE, 0xEF];

        var log = await new BaseModInstaller(Context, feed).RunAsync([UpdatableMod()], CancellationToken.None);

        Assert.Contains(log, l => l.Contains("keeping installed version"));
        Assert.True(File.Exists(Path.Combine(modDir, "ModConfig.json")));
    }

    [Fact]
    public async Task OfflineFeedSkipsRemainingComponents()
    {
        _temp.CreateMod("game/mods/_base-mods/a.mod", "a.mod");
        _temp.CreateMod("game/mods/_base-mods/b.mod", "b.mod");
        var feed = new FakeFeed { Assets = null }; // unreachable

        var log = await new BaseModInstaller(Context, feed).RunAsync(
            [UpdatableMod("a.mod", "A"), UpdatableMod("b.mod", "B")], CancellationToken.None);

        Assert.Equal(1, feed.FeedCalls);
        Assert.Contains(log, l => l.Contains("skipping remaining checks"));
    }

    [Fact]
    public async Task ChecksAreThrottledByInterval()
    {
        _temp.CreateMod("game/mods/_base-mods/test.mod", "test.mod");
        var feed = new FakeFeed { Assets = [] };
        var updater = new BaseModInstaller(Context, feed);

        await updater.RunAsync([UpdatableMod()], CancellationToken.None);
        Assert.Equal(1, feed.FeedCalls);

        var log = await updater.RunAsync([UpdatableMod()], CancellationToken.None);
        Assert.Equal(1, feed.FeedCalls); // no second network hit
        Assert.Contains(log, l => l.Contains("skipping until the interval elapses"));
    }

    [Fact]
    public async Task AutoUpdateFalseDisablesUpdateChecks()
    {
        _temp.CreateMod("game/mods/_base-mods/test.mod", "test.mod"); // installed
        Directory.CreateDirectory(Context.DropInDirectory);
        File.WriteAllText(BaseModInstaller.UpdateSettings.PathFor(Context.DropInDirectory),
            """{ "AutoUpdate": false }""");
        var feed = new FakeFeed { Assets = [] };

        var log = await new BaseModInstaller(Context, feed).RunAsync([UpdatableMod()], CancellationToken.None);

        Assert.Equal(0, feed.FeedCalls);
        Assert.Contains(log, l => l.Contains("auto-update disabled"));
    }

    [Fact]
    public async Task MissingBaseModIsInstalledFresh()
    {
        // Nothing on disk: the package doesn't redistribute base mods.
        var feed = new FakeFeed
        {
            Assets = [new ReleaseAsset("TestMod2.0.0.zip", "https://x/a.zip")],
        };
        feed.Downloads["https://x/a.zip"] = ZipWithModConfig("test.mod", "2.0.0", ("code/x.dll", "bin"));

        var log = await new BaseModInstaller(Context, feed).RunAsync([UpdatableMod()], CancellationToken.None);

        Assert.Contains(log, l => l.Contains("fetching from official releases"));
        Assert.Contains(log, l => l.Contains("installed 2.0.0"));
        Assert.Equal("bin", File.ReadAllText(Path.Combine(
            Context.ModsDirectory, "_base-mods", "test.mod", "code", "x.dll")));
    }

    [Fact]
    public async Task MissingBaseModIgnoresThrottleAndOptOut()
    {
        // Throttled AND opted out — a missing base mod must still install,
        // since nothing works until it exists.
        Directory.CreateDirectory(Context.DropInDirectory);
        new BaseModInstaller.UpdateSettings { AutoUpdate = false, LastCheckedUtc = DateTime.UtcNow }
            .Save(Context.DropInDirectory);
        var feed = new FakeFeed
        {
            Assets = [new ReleaseAsset("TestMod2.0.0.zip", "https://x/a.zip")],
        };
        feed.Downloads["https://x/a.zip"] = ZipWithModConfig("test.mod", "2.0.0");

        var log = await new BaseModInstaller(Context, feed).RunAsync([UpdatableMod()], CancellationToken.None);

        Assert.Contains(log, l => l.Contains("installed 2.0.0"));
        Assert.True(File.Exists(Path.Combine(
            Context.ModsDirectory, "_base-mods", "test.mod", "ModConfig.json")));
    }
}
