namespace ReloadedDropIn.Tests;

/// <summary>A throwaway directory tree for filesystem-facing tests.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } =
        Directory.CreateTempSubdirectory("reloaded-dropin-test-").FullName;

    public string CreateMod(string relativeDirectory, string modId, string[]? dependencies = null, bool isLibrary = false)
    {
        var modDirectory = System.IO.Path.Combine(Path, relativeDirectory);
        Directory.CreateDirectory(modDirectory);
        var dependencyJson = string.Join(", ", (dependencies ?? []).Select(d => $"\"{d}\""));
        File.WriteAllText(System.IO.Path.Combine(modDirectory, "ModConfig.json"),
            $$"""
            {
              "ModId": "{{modId}}",
              "ModName": "{{modId}} name",
              "ModVersion": "1.0.0",
              "IsLibrary": {{(isLibrary ? "true" : "false")}},
              "ModDependencies": [{{dependencyJson}}]
            }
            """);
        return modDirectory;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup of temp data
        }
    }
}
