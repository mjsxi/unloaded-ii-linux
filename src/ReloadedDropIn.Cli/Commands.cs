using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Core.Configuration;
using ReloadedDropIn.Core.Dependencies;
using ReloadedDropIn.Core.Discovery;

namespace ReloadedDropIn.Cli;

public sealed class Commands(
    IReadOnlyList<IGameAdapter> adapters,
    AdapterContext context,
    TextWriter output)
{
    public int Detect()
    {
        var (adapter, detection) = DetectAdapter();
        if (adapter is null)
        {
            output.WriteLine($"No supported game detected in {context.GameDirectory}");
            return 1;
        }

        output.WriteLine($"Game: {adapter.DisplayName}");
        output.WriteLine($"Adapter: {adapter.Id}");
        output.WriteLine($"Executable: {detection!.MatchedExecutable}");
        return 0;
    }

    public int ListMods()
    {
        var scan = new ModScanner().Scan(context.ModsDirectory);

        foreach (var mod in scan.Mods)
            output.WriteLine($"{mod.ModId} {mod.Manifest.ModVersion} ({mod.Manifest.ModName})");

        foreach (var issue in scan.Issues)
            output.WriteLine($"ignored: {issue.Path} — {issue.Reason}");

        output.WriteLine($"{scan.Mods.Count} mod(s) discovered");
        return 0;
    }

    public int Validate()
    {
        var (adapter, _) = DetectAdapter();
        if (adapter is null)
        {
            output.WriteLine($"No supported game detected in {context.GameDirectory}");
            return 1;
        }

        var result = adapter.ValidateInstallation(context);
        foreach (var message in result.Messages)
            output.WriteLine($"[{message.Severity.ToString().ToLowerInvariant()}] {message.Check}: {message.Message}");

        output.WriteLine(result.IsValid ? "Validation passed" : "Validation failed");
        return result.IsValid ? 0 : 1;
    }

    public int Doctor()
    {
        var (adapter, detection) = DetectAdapter();
        if (adapter is null)
        {
            output.WriteLine($"Game: not detected in {context.GameDirectory}");
            output.WriteLine("Status: not ready");
            return 1;
        }

        var scan = new ModScanner().Scan(context.ModsDirectory);
        var resolution = new DependencyResolver().Resolve(scan.Mods);
        var validation = adapter.ValidateInstallation(context);
        var injection = adapter.GetInjectionConfiguration(context);
        var requiredMods = adapter.GetRequiredMods();
        var discoveredIds = scan.Mods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        output.WriteLine($"Game: {adapter.DisplayName}");
        output.WriteLine($"Adapter: {adapter.Id}");
        output.WriteLine($"Steam App ID: {string.Join(", ", adapter.SteamAppIds)}");
        output.WriteLine($"Executable: {(detection!.IsDetected ? "found" : "missing")}");
        output.WriteLine($"Proxy DLL: {injection.PreferredProxyDll}");
        output.WriteLine($"Wine override: {injection.WineDllOverride}");

        var missingRequired = new List<string>();
        foreach (var required in requiredMods)
        {
            var found = discoveredIds.Contains(required.Id);
            if (!found)
                missingRequired.Add(required.Id);
            output.WriteLine($"Required mod: {required.Id} {(found ? "found" : "MISSING")}");
        }

        output.WriteLine($"Discovered mods: {scan.Mods.Count}");
        output.WriteLine($"Missing dependencies: {resolution.MissingDependencies.Count}");
        foreach (var missing in resolution.MissingDependencies)
            output.WriteLine($"  {missing.ModId} requires {missing.MissingDependencyId}");

        foreach (var message in validation.Messages.Where(m => m.Severity != ValidationSeverity.Info))
            output.WriteLine($"[{message.Severity.ToString().ToLowerInvariant()}] {message.Check}: {message.Message}");

        var ready = validation.IsValid && missingRequired.Count == 0 && resolution.IsComplete;
        output.WriteLine($"Status: {(ready ? "ready" : "not ready")}");
        return ready ? 0 : 1;
    }

    public int Restore()
    {
        var (adapter, _) = DetectAdapter();
        if (adapter is null)
        {
            output.WriteLine($"No supported game detected in {context.GameDirectory}");
            return 1;
        }

        var lines = adapter.RestoreAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var line in lines)
            output.WriteLine(line);
        output.WriteLine("Restore complete — game files are back to their pre-mod state.");
        return 0;
    }

    public int Sync(bool dryRun)
    {
        var (adapter, detection) = DetectAdapter();
        if (adapter is null)
        {
            output.WriteLine($"No supported game detected in {context.GameDirectory}");
            return 1;
        }

        var validation = adapter.ValidateInstallation(context);
        if (!validation.IsValid)
        {
            foreach (var message in validation.Messages.Where(m => m.Severity == ValidationSeverity.Error))
                output.WriteLine($"[error] {message.Check}: {message.Message}");
            output.WriteLine("Sync aborted: installation validation failed");
            return 1;
        }

        var scan = new ModScanner().Scan(context.ModsDirectory);
        var resolution = new DependencyResolver().Resolve(scan.Mods);

        foreach (var missing in resolution.MissingDependencies)
            output.WriteLine($"[warning] {missing.ModId} requires missing dependency {missing.MissingDependencyId}");

        var requiredEnabled = adapter.GetRequiredMods()
            .Where(m => m.Enabled)
            .Select(m => m.Id)
            .ToList();

        var plan = new ConfigGenerator().Plan(
            context.GeneratedDirectory,
            appId: detection!.MatchedExecutable!,
            appName: adapter.DisplayName,
            appLocation: detection.ExecutablePath!,
            orderedMods: resolution.OrderedMods,
            requiredEnabledModIds: requiredEnabled);

        output.WriteLine($"Enabled mods ({plan.EnabledMods.Length}): {string.Join(", ", plan.EnabledMods)}");
        output.WriteLine($"Target: {plan.TargetPath}");

        if (dryRun)
        {
            output.WriteLine(plan.WouldChange ? "Dry run: config would change" : "Dry run: no changes");
            return 0;
        }

        var changed = new ConfigGenerator().Apply(plan);
        output.WriteLine(changed ? "Config written" : "Config already up to date");
        return 0;
    }

    private (IGameAdapter? Adapter, GameDetectionResult? Detection) DetectAdapter()
    {
        foreach (var adapter in adapters)
        {
            var detection = adapter.Detect(context);
            if (detection.IsDetected)
                return (adapter, detection);
        }

        return (null, null);
    }
}
