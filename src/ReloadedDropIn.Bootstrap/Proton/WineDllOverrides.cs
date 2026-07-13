namespace ReloadedDropIn.Bootstrap.Proton;

/// <summary>
/// Merges a required DLL override (e.g. "winmm=n,b") into an existing
/// WINEDLLOVERRIDES value without clobbering the user's other overrides.
/// Format reference: "dll1=n,b;dll2=b" — entries separated by ';'.
/// </summary>
public static class WineDllOverrides
{
    public static string Merge(string? existing, string requiredOverride)
    {
        var requiredParts = requiredOverride.Split('=', 2);
        if (requiredParts.Length != 2 || requiredParts[0].Length == 0)
            throw new ArgumentException($"not a valid override entry: '{requiredOverride}'", nameof(requiredOverride));

        var requiredDll = requiredParts[0].Trim();

        var entries = (existing ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // If the same DLL already has an override, replace it with ours — the loader
        // cannot work without the native-then-builtin order we need.
        entries.RemoveAll(e =>
        {
            var dll = e.Split('=', 2)[0].Trim();
            return dll.Equals(requiredDll, StringComparison.OrdinalIgnoreCase);
        });

        entries.Add($"{requiredDll}={requiredParts[1].Trim()}");
        return string.Join(';', entries);
    }
}
