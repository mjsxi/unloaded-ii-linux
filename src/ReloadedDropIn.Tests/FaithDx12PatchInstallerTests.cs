using System.Text.Json;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Bootstrap.Update;

namespace ReloadedDropIn.Tests;

public class FaithDx12PatchInstallerTests : IDisposable
{
    private const string CurrentCacheDirectoryName = "ffxvi-faith-dx12-v2";
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = Path.Combine(_temp.Path, "game"),
        ModsDirectory = Path.Combine(_temp.Path, "game", "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "game", "reloaded-dropin"),
    };

    private string FaithDirectory
    {
        get
        {
            var directory = Path.Combine(Context.ModsDirectory, "_base-mods", "faith");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "ModConfig.json"),
                """{ "ModId": "ff16.utility.framework", "ModName": "Faith", "ModVersion": "2.2.1" }""");
            return directory;
        }
    }

    private string CachePath => Path.Combine(
        Context.DropInDirectory, "cache", CurrentCacheDirectoryName,
        FaithDx12PatchInstaller.AssetName);

    private sealed class FakeFeed : IReleaseFeed
    {
        public IReadOnlyList<ReleaseAsset>? Assets { get; set; }
        public Dictionary<string, byte[]> Downloads { get; } = [];
        public List<string> RequestedRepos { get; } = [];

        public Task<IReadOnlyList<ReleaseAsset>?> GetLatestReleaseAssetsAsync(
            string repo, CancellationToken cancellationToken)
        {
            RequestedRepos.Add(repo);
            return Task.FromResult(Assets);
        }

        public Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(Downloads.TryGetValue(url, out var bytes) ? bytes : null);
    }

    [Fact]
    public async Task DownloadsCachesAndAppliesLatestReleaseAsset()
    {
        var faithDirectory = FaithDirectory;
        var official = PortableExecutable(1);
        File.WriteAllBytes(Path.Combine(faithDirectory, FaithDx12PatchInstaller.AssetName), official);
        var replacement = PortableExecutable(2);
        var feed = new FakeFeed
        {
            Assets = [new ReleaseAsset(FaithDx12PatchInstaller.AssetName, "https://example.test/patch.dll")],
        };
        feed.Downloads["https://example.test/patch.dll"] = replacement;

        var result = await new FaithDx12PatchInstaller(Context, feed)
            .RunAsync(allowNetwork: true, CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal([FaithDx12PatchInstaller.Repository], feed.RequestedRepos);
        Assert.Equal(replacement, File.ReadAllBytes(CachePath));
        Assert.Equal(replacement, File.ReadAllBytes(
            Path.Combine(faithDirectory, FaithDx12PatchInstaller.AssetName)));
        Assert.Contains(result.Log, line => line.Contains("downloaded the latest"));
    }

    [Fact]
    public async Task CachedPatchIsReappliedOfflineAfterFaithUpdate()
    {
        var faithDirectory = FaithDirectory;
        var replacement = PortableExecutable(3);
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllBytes(CachePath, replacement);
        File.WriteAllBytes(
            Path.Combine(faithDirectory, FaithDx12PatchInstaller.AssetName), PortableExecutable(4));

        var result = await new FaithDx12PatchInstaller(Context, feed: null)
            .RunAsync(allowNetwork: false, CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(replacement, File.ReadAllBytes(
            Path.Combine(faithDirectory, FaithDx12PatchInstaller.AssetName)));
        Assert.Contains(result.Log, line => line.Contains("installed the cached"));
    }

    [Fact]
    public async Task RecentSuccessfulCheckDoesNotQueryGitHubAgain()
    {
        var faithDirectory = FaithDirectory;
        var cached = PortableExecutable(10);
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllBytes(CachePath, cached);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(CachePath)!, "update.json"),
            JsonSerializer.Serialize(new { LastCheckedUtc = DateTime.UtcNow }));
        var feed = new FakeFeed { Assets = null };

        var result = await new FaithDx12PatchInstaller(Context, feed)
            .RunAsync(allowNetwork: true, CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Empty(feed.RequestedRepos);
        Assert.Equal(cached, File.ReadAllBytes(
            Path.Combine(faithDirectory, FaithDx12PatchInstaller.AssetName)));
    }

    [Fact]
    public async Task MissingDownloadReportsFaithUnsafeWithoutChangingOfficialDll()
    {
        var faithDirectory = FaithDirectory;
        var official = PortableExecutable(5);
        var destination = Path.Combine(faithDirectory, FaithDx12PatchInstaller.AssetName);
        File.WriteAllBytes(destination, official);
        var feed = new FakeFeed { Assets = null };

        var result = await new FaithDx12PatchInstaller(Context, feed)
            .RunAsync(allowNetwork: true, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(official, File.ReadAllBytes(destination));
        Assert.Contains(result.Log, line => line.Contains("stay disabled"));
    }

    [Fact]
    public async Task InvalidDownloadCannotReplaceAWorkingCache()
    {
        _ = FaithDirectory;
        var cached = PortableExecutable(6);
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllBytes(CachePath, cached);
        var feed = new FakeFeed
        {
            Assets = [new ReleaseAsset(FaithDx12PatchInstaller.AssetName, "https://example.test/bad.dll")],
        };
        feed.Downloads["https://example.test/bad.dll"] = [1, 2, 3];

        var result = await new FaithDx12PatchInstaller(Context, feed)
            .RunAsync(allowNetwork: true, CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(cached, File.ReadAllBytes(CachePath));
        Assert.Contains(result.Log, line => line.Contains("not a valid Windows DLL"));
    }

    private static byte[] PortableExecutable(byte marker)
    {
        var bytes = new byte[128];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3c);
        bytes[0x40] = (byte)'P';
        bytes[0x41] = (byte)'E';
        bytes[0x42] = 0;
        bytes[0x43] = 0;
        bytes[0x50] = marker;
        return bytes;
    }
}
