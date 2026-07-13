using System.Text.Json;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.GBFR;

namespace ReloadedDropIn.Tests;

public class GbfrTextTableTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private AdapterContext Context => new()
    {
        GameDirectory = _temp.Path,
        ModsDirectory = Path.Combine(_temp.Path, "mods"),
        DropInDirectory = Path.Combine(_temp.Path, "reloaded-dropin"),
    };

    private string UserConfigPath => Path.Combine(
        _temp.Path, "reloaded-dropin", "generated", "User", "Mods",
        GbfrAdapter.UtilityManagerModId, "Config.json");

    [Fact]
    public async Task BackupStepDisablesModLoaderInfoAndClearsGeneratedText()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "data.i"), "index");
        var staleTextDir = Path.Combine(
            _temp.Path, "mods", GbfrAdapter.UtilityManagerModId, "GBFR", "data", "system", "table", "text", "en");
        Directory.CreateDirectory(staleTextDir);
        File.WriteAllText(Path.Combine(staleTextDir, "text_ui.msg"), "stale");

        var log = await new GbfrAdapter().CreateBackupsAsync(Context, CancellationToken.None);

        using var config = JsonDocument.Parse(File.ReadAllText(UserConfigPath));
        Assert.False(config.RootElement.GetProperty("ShowModLoaderInfo").GetBoolean());
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(staleTextDir))));
        Assert.Contains(log, l => l.Contains("disabled utility-manager title-screen text"));
    }

    [Fact]
    public async Task ExistingUserConfigKeysArePreserved()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "data.i"), "index");
        Directory.CreateDirectory(Path.GetDirectoryName(UserConfigPath)!);
        File.WriteAllText(UserConfigPath, """{ "VerboseLogging": false, "ShowModLoaderInfo": true }""");

        await new GbfrAdapter().CreateBackupsAsync(Context, CancellationToken.None);

        using var config = JsonDocument.Parse(File.ReadAllText(UserConfigPath));
        Assert.False(config.RootElement.GetProperty("ShowModLoaderInfo").GetBoolean());
        Assert.False(config.RootElement.GetProperty("VerboseLogging").GetBoolean());
    }

    [Fact]
    public async Task SecondRunIsQuietWhenAlreadyDisabled()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "data.i"), "index");
        var adapter = new GbfrAdapter();
        await adapter.CreateBackupsAsync(Context, CancellationToken.None);

        var log = await adapter.CreateBackupsAsync(Context, CancellationToken.None);

        Assert.DoesNotContain(log, l => l.Contains("disabled utility-manager"));
    }
}
