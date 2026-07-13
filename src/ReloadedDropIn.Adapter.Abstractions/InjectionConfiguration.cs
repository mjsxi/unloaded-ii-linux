namespace ReloadedDropIn.Adapter.Abstractions;

/// <summary>
/// How Reloaded gets into the game process for this title.
/// </summary>
public sealed record InjectionConfiguration
{
    /// <summary>Proxy DLL filename deployed next to the game exe (e.g. "winmm.dll").</summary>
    public required string PreferredProxyDll { get; init; }

    /// <summary>
    /// Wine DLL override entry required under Proton, e.g. "winmm=n,b".
    /// Appended into WINEDLLOVERRIDES by the launch wrapper.
    /// </summary>
    public required string WineDllOverride { get; init; }
}
