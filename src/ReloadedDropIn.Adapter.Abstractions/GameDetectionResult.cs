namespace ReloadedDropIn.Adapter.Abstractions;

public sealed record GameDetectionResult
{
    public required bool IsDetected { get; init; }

    /// <summary>Executable name that matched, when detected.</summary>
    public string? MatchedExecutable { get; init; }

    /// <summary>Absolute path to the matched executable, when detected.</summary>
    public string? ExecutablePath { get; init; }

    public static GameDetectionResult NotDetected { get; } = new() { IsDetected = false };

    public static GameDetectionResult Detected(string executableName, string executablePath) =>
        new() { IsDetected = true, MatchedExecutable = executableName, ExecutablePath = executablePath };
}
