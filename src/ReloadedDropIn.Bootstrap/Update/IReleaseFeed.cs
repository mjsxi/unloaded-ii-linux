namespace ReloadedDropIn.Bootstrap.Update;

/// <summary>One downloadable file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl);

/// <summary>
/// Source of "latest release" information for bundled mods. Abstracted so the
/// update logic is testable without network access.
/// </summary>
public interface IReleaseFeed
{
    /// <summary>Assets of the latest release of "owner/name", or null when the
    /// feed is unreachable (offline, rate-limited, repo gone).</summary>
    Task<IReadOnlyList<ReleaseAsset>?> GetLatestReleaseAssetsAsync(string repo, CancellationToken cancellationToken);

    /// <summary>Downloads one asset, or null on failure.</summary>
    Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken);
}
