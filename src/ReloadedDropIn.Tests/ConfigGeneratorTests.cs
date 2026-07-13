using System.Text.Json;
using ReloadedDropIn.Core.Configuration;
using ReloadedDropIn.Core.Discovery;
using ReloadedDropIn.Core.Manifests;

namespace ReloadedDropIn.Tests;

public class ConfigGeneratorTests
{
    private static DiscoveredMod Mod(string id, bool isLibrary = false) => new()
    {
        Manifest = new ModManifest { ModId = id, IsLibrary = isLibrary },
        Directory = $"/fake/{id}",
    };

    private static GeneratePlan MakePlan(TempDirectory temp, params DiscoveredMod[] mods) =>
        new ConfigGenerator().Plan(
            Path.Combine(temp.Path, "generated"),
            appId: "game.exe",
            appName: "Test Game",
            appLocation: "/games/game.exe",
            orderedMods: mods,
            requiredEnabledModIds: ["required.loader"]);

    [Fact]
    public void RequiredModsComeFirstAndLibrariesAreExcluded()
    {
        using var temp = new TempDirectory();
        var plan = MakePlan(temp, Mod("user.mod"), Mod("some.library", isLibrary: true));

        Assert.Equal(["required.loader", "user.mod"], plan.EnabledMods);
    }

    [Fact]
    public void OutputIsValidReloadedAppConfigShape()
    {
        using var temp = new TempDirectory();
        var plan = MakePlan(temp, Mod("user.mod"));

        using var doc = JsonDocument.Parse(plan.Content);
        var root = doc.RootElement;
        Assert.Equal("game.exe", root.GetProperty("AppId").GetString());
        Assert.Equal("/games/game.exe", root.GetProperty("AppLocation").GetString());
        Assert.Equal(2, root.GetProperty("EnabledMods").GetArrayLength());
        Assert.True(root.GetProperty("PreserveDisabledModOrder").GetBoolean());
    }

    [Fact]
    public void ApplyThenReplanIsIdempotent()
    {
        using var temp = new TempDirectory();
        var generator = new ConfigGenerator();

        var first = MakePlan(temp, Mod("user.mod"));
        Assert.True(first.WouldChange);
        Assert.True(generator.Apply(first));

        var second = MakePlan(temp, Mod("user.mod"));
        Assert.False(second.WouldChange);
        Assert.False(generator.Apply(second));
        Assert.Equal(first.Content, File.ReadAllText(first.TargetPath));
    }

    [Fact]
    public void DryRunPlanWritesNothing()
    {
        using var temp = new TempDirectory();
        var plan = MakePlan(temp, Mod("user.mod"));

        Assert.False(File.Exists(plan.TargetPath));
    }

    [Fact]
    public void DuplicateRequiredAndDiscoveredIdIsEnabledOnce()
    {
        using var temp = new TempDirectory();
        var plan = MakePlan(temp, Mod("required.loader"), Mod("user.mod"));

        Assert.Equal(["required.loader", "user.mod"], plan.EnabledMods);
    }
}
