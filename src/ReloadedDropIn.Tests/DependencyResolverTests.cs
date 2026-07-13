using ReloadedDropIn.Core.Dependencies;
using ReloadedDropIn.Core.Discovery;
using ReloadedDropIn.Core.Manifests;

namespace ReloadedDropIn.Tests;

public class DependencyResolverTests
{
    private static DiscoveredMod Mod(string id, params string[] dependencies) => new()
    {
        Manifest = new ModManifest { ModId = id, ModDependencies = dependencies },
        Directory = $"/fake/{id}",
    };

    [Fact]
    public void ReportsMissingDependencies()
    {
        var result = new DependencyResolver().Resolve([Mod("a", "lib.missing")]);

        var missing = Assert.Single(result.MissingDependencies);
        Assert.Equal("a", missing.ModId);
        Assert.Equal("lib.missing", missing.MissingDependencyId);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void DependenciesComeBeforeDependents()
    {
        var result = new DependencyResolver().Resolve([Mod("app", "zlib"), Mod("zlib")]);

        Assert.Equal(["zlib", "app"], result.OrderedMods.Select(m => m.ModId));
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void OrderIsDeterministicRegardlessOfInputOrder()
    {
        var forward = new DependencyResolver().Resolve([Mod("a"), Mod("b"), Mod("c", "a")]);
        var backward = new DependencyResolver().Resolve([Mod("c", "a"), Mod("b"), Mod("a")]);

        Assert.Equal(
            forward.OrderedMods.Select(m => m.ModId),
            backward.OrderedMods.Select(m => m.ModId));
    }

    [Fact]
    public void DependencyCycleDoesNotHangOrThrow()
    {
        var result = new DependencyResolver().Resolve([Mod("a", "b"), Mod("b", "a")]);

        Assert.Equal(2, result.OrderedMods.Count);
    }

    [Fact]
    public void DependencyIdsAreCaseInsensitive()
    {
        var result = new DependencyResolver().Resolve([Mod("app", "ZLib"), Mod("zlib")]);

        Assert.True(result.IsComplete);
    }

    [Fact]
    public void MissingDependencyPoisonsDependentsTransitively()
    {
        // a -> missing.lib, b -> a, c standalone: a and b are unloadable
        // (enabling either would make Reloaded abort the whole load), c is fine.
        var result = new DependencyResolver().Resolve([Mod("a", "missing.lib"), Mod("b", "a"), Mod("c")]);

        Assert.Contains("a", result.UnloadableModIds);
        Assert.Contains("b", result.UnloadableModIds);
        Assert.DoesNotContain("c", result.UnloadableModIds);
    }

    [Fact]
    public void CycleWithSatisfiedDependenciesIsNotUnloadable()
    {
        var result = new DependencyResolver().Resolve([Mod("a", "b"), Mod("b", "a")]);

        Assert.Empty(result.UnloadableModIds);
    }
}
