namespace ReloadedDropIn.Core.Discovery;

public enum ScanIssueKind
{
    InvalidManifest,
    DuplicateModId,
    IgnoredEntry,
}

public sealed record ScanIssue(ScanIssueKind Kind, string Path, string Reason);

public sealed record ScanResult
{
    /// <summary>Discovered mods, sorted by ModId (ordinal) for determinism.</summary>
    public required IReadOnlyList<DiscoveredMod> Mods { get; init; }

    public required IReadOnlyList<ScanIssue> Issues { get; init; }
}
