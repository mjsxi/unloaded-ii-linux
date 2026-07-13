namespace ReloadedDropIn.Core.Configuration;

/// <summary>
/// The fields of Reloaded-II's AppConfig.json we generate. Mirrors
/// Reloaded.Mod.Loader.IO.Config.ApplicationConfig (PascalCase JSON).
/// See research/reload-config-notes.md.
/// </summary>
public sealed record ReloadedAppConfig
{
    public const string FileName = "AppConfig.json";

    public required string AppId { get; init; }
    public required string AppName { get; init; }
    public required string AppLocation { get; init; }
    public string AppArguments { get; init; } = string.Empty;
    public string AppIcon { get; init; } = "Icon.png";
    public bool AutoInject { get; init; }
    public required string[] EnabledMods { get; init; }
    public string[] SortedMods { get; init; } = [];
    public bool PreserveDisabledModOrder { get; init; } = true;
    public bool DontInject { get; init; }
    public string WorkingDirectory { get; init; } = string.Empty;
}
