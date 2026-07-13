using ReloadedDropIn.Core.Discovery;

namespace ReloadedDropIn.Core.Dependencies;

public sealed record MissingDependency(string ModId, string MissingDependencyId);

public sealed record ResolutionResult
{
    /// <summary>Mods in a deterministic, dependency-respecting enable order.</summary>
    public required IReadOnlyList<DiscoveredMod> OrderedMods { get; init; }

    public required IReadOnlyList<MissingDependency> MissingDependencies { get; init; }

    /// <summary>
    /// ModIds whose dependency closure contains something missing — directly or
    /// through another mod. Enabling any of these makes Reloaded's loader abort
    /// the ENTIRE load with an error dialog, so they must be left disabled
    /// until the dependency exists.
    /// </summary>
    public required IReadOnlySet<string> UnloadableModIds { get; init; }

    public bool IsComplete => MissingDependencies.Count == 0;
}

/// <summary>
/// Validates hard dependencies (ModConfig "ModDependencies") across a set of
/// discovered mods and produces a deterministic order (dependencies before
/// dependents; ties broken by ModId).
///
/// Note: Reloaded's own loader re-sorts by dependency at runtime, so this order
/// is for stable config output and human review, not a correctness requirement.
/// </summary>
public sealed class DependencyResolver
{
    public ResolutionResult Resolve(IReadOnlyList<DiscoveredMod> mods)
    {
        var byId = mods.ToDictionary(m => m.ModId, StringComparer.OrdinalIgnoreCase);
        var missing = new List<MissingDependency>();

        foreach (var mod in mods.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dependency in mod.Manifest.ModDependencies.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!byId.ContainsKey(dependency))
                    missing.Add(new MissingDependency(mod.ModId, dependency));
            }
        }

        return new ResolutionResult
        {
            OrderedMods = TopologicalSort(mods, byId),
            MissingDependencies = missing,
            UnloadableModIds = ComputeUnloadable(mods, byId),
        };
    }

    /// <summary>Transitive closure of missing dependencies: a mod is unloadable
    /// when any dependency is absent, or present but itself unloadable.</summary>
    private static HashSet<string> ComputeUnloadable(
        IReadOnlyList<DiscoveredMod> mods, Dictionary<string, DiscoveredMod> byId)
    {
        var unloadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            if (IsUnloadable(mod))
                unloadable.Add(mod.ModId);
        }

        return unloadable;

        bool IsUnloadable(DiscoveredMod mod)
        {
            if (verdicts.TryGetValue(mod.ModId, out var known))
                return known;

            verdicts[mod.ModId] = false; // break cycles optimistically; the loader tolerates cycles
            var result = mod.Manifest.ModDependencies.Any(dependency =>
                !byId.TryGetValue(dependency, out var dependencyMod) || IsUnloadable(dependencyMod));
            verdicts[mod.ModId] = result;
            return result;
        }
    }

    private static List<DiscoveredMod> TopologicalSort(
        IReadOnlyList<DiscoveredMod> mods,
        Dictionary<string, DiscoveredMod> byId)
    {
        var ordered = new List<DiscoveredMod>(mods.Count);
        var state = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase))
            Visit(mod);

        return ordered;

        void Visit(DiscoveredMod mod)
        {
            var visitState = state.GetValueOrDefault(mod.ModId);
            if (visitState != VisitState.Unvisited)
                return; // done, or a cycle — break it and let Reloaded's loader deal with it

            state[mod.ModId] = VisitState.Visiting;
            foreach (var dependency in mod.Manifest.ModDependencies.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (byId.TryGetValue(dependency, out var dependencyMod) &&
                    state.GetValueOrDefault(dependencyMod.ModId) == VisitState.Unvisited)
                {
                    Visit(dependencyMod);
                }
            }

            state[mod.ModId] = VisitState.Visited;
            ordered.Add(mod);
        }
    }

    private enum VisitState
    {
        Unvisited = 0,
        Visiting,
        Visited,
    }
}
