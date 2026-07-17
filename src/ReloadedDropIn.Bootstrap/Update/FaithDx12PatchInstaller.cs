using System.Text.Json;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.FFXVI;
using ReloadedDropIn.Core.Discovery;
using ReloadedDropIn.Core.Filesystem;

namespace ReloadedDropIn.Bootstrap.Update;

/// <summary>
/// Downloads and applies the FFXVI-only Faith Framework DX12 replacement.
/// The patch is deliberately not part of the universal package: its own
/// GitHub release is the source of truth, while a local cache keeps FFXVI
/// working offline and lets us reapply the patch after an official Faith
/// update replaces the DLL.
/// </summary>
public sealed class FaithDx12PatchInstaller(AdapterContext context, IReleaseFeed? feed)
{
    public const string Repository = "mjsxi/dxd12-patch-files";
    public const string AssetName = "NenTools.ImGui.Hooks.DirectX12.dll";
    private const string CacheDirectoryName = "ffxvi-faith-dx12-v2";

    public sealed record Result(bool Ready, IReadOnlyList<string> Log);

    private sealed record PatchState
    {
        public DateTime LastCheckedUtc { get; init; } = DateTime.MinValue;
    }

    public async Task<Result> RunAsync(bool allowNetwork, CancellationToken cancellationToken)
    {
        var log = new List<string>();
        try
        {
            var faith = new ModScanner().Scan(context.ModsDirectory).Mods
                .FirstOrDefault(mod => mod.ModId.Equals(
                    FfxviAdapter.FaithFrameworkModId, StringComparison.OrdinalIgnoreCase));
            if (faith is null)
            {
                log.Add("Faith Framework is not installed; DX12 patch will be fetched after Faith installs");
                return new Result(false, log);
            }

            var cachePath = CachePath();

            var state = LoadState();
            var updateSettings = BaseModInstaller.UpdateSettings.Load(context.DropInDirectory);
            var interval = TimeSpan.FromHours(Math.Max(updateSettings.CheckIntervalHours, 1));
            var cacheReady = IsValidPortableExecutable(cachePath);
            var checkDue = !cacheReady ||
                (updateSettings.AutoUpdate && DateTime.UtcNow - state.LastCheckedUtc >= interval);

            if (allowNetwork && feed is not null && checkDue)
            {
                SaveState(state with { LastCheckedUtc = DateTime.UtcNow });
                var assets = await feed.GetLatestReleaseAssetsAsync(Repository, cancellationToken)
                    .ConfigureAwait(false);
                if (assets is null)
                {
                    log.Add("patch release feed is unreachable; using the cached replacement if available");
                }
                else
                {
                    var asset = assets.FirstOrDefault(candidate =>
                        candidate.Name.Equals(AssetName, StringComparison.OrdinalIgnoreCase));
                    if (asset is null)
                    {
                        log.Add($"latest patch release has no asset named {AssetName}; using the cached replacement if available");
                    }
                    else
                    {
                        var payload = await feed.DownloadAsync(asset.DownloadUrl, cancellationToken)
                            .ConfigureAwait(false);
                        if (payload is null)
                        {
                            log.Add("patch download failed; using the cached replacement if available");
                        }
                        else if (!IsValidPortableExecutable(payload))
                        {
                            log.Add("downloaded patch is not a valid Windows DLL; cached replacement was left untouched");
                        }
                        else
                        {
                            var changed = !File.Exists(cachePath) ||
                                !File.ReadAllBytes(cachePath).AsSpan().SequenceEqual(payload);
                            if (changed)
                            {
                                AtomicFile.WriteAllBytes(cachePath, payload);
                                log.Add("downloaded the latest Faith DX12 replacement");
                            }
                            cacheReady = true;
                        }
                    }
                }
            }
            else if (!allowNetwork && !cacheReady)
            {
                log.Add("network updates are disabled and no cached Faith DX12 replacement is available");
            }

            cacheReady = IsValidPortableExecutable(cachePath);
            if (!cacheReady)
            {
                log.Add("Faith and mods that require it will stay disabled for this launch to avoid the known DX12 crash");
                return new Result(false, log);
            }

            var destination = Path.Combine(faith.Directory, AssetName);
            var replacement = File.ReadAllBytes(cachePath);
            if (!File.Exists(destination) ||
                !File.ReadAllBytes(destination).AsSpan().SequenceEqual(replacement))
            {
                AtomicFile.WriteAllBytes(destination, replacement);
                log.Add("installed the cached Faith Framework DX12 replacement");
            }
            else
            {
                log.Add("Faith Framework DX12 replacement is already applied");
            }

            return new Result(true, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Add($"Faith DX12 patch failed safely ({ex.Message}); Faith will stay disabled for this launch");
            return new Result(false, log);
        }
    }

    private string CachePath() => Path.Combine(
        context.DropInDirectory, "cache", CacheDirectoryName, AssetName);

    private string StatePath() => Path.Combine(
        context.DropInDirectory, "cache", CacheDirectoryName, "update.json");

    private PatchState LoadState()
    {
        var path = StatePath();
        if (!File.Exists(path))
            return new PatchState();
        try
        {
            return JsonSerializer.Deserialize<PatchState>(File.ReadAllText(path)) ?? new PatchState();
        }
        catch (JsonException)
        {
            return new PatchState();
        }
    }

    private void SaveState(PatchState state) => AtomicFile.WriteAllText(
        StatePath(), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

    private static bool IsValidPortableExecutable(string path) =>
        File.Exists(path) && IsValidPortableExecutable(File.ReadAllBytes(path));

    internal static bool IsValidPortableExecutable(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 0x40 || payload[0] != (byte)'M' || payload[1] != (byte)'Z')
            return false;

        var peOffset = BitConverter.ToInt32(payload.Slice(0x3c, sizeof(int)));
        return peOffset >= 0 && peOffset <= payload.Length - 4 &&
               payload[peOffset] == (byte)'P' && payload[peOffset + 1] == (byte)'E' &&
               payload[peOffset + 2] == 0 && payload[peOffset + 3] == 0;
    }
}
