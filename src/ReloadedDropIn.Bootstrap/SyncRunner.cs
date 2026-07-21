using System.Text;
using System.Text.Json;
using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.GBFR;
using ReloadedDropIn.Core.Configuration;
using ReloadedDropIn.Core.Dependencies;
using ReloadedDropIn.Core.Discovery;

namespace ReloadedDropIn.Bootstrap;

/// <summary>
/// The in-process sync pipeline: runs inside the game (under the proxy/.asi)
/// before Reloaded's loader starts, so configuration is always fresh.
/// </summary>
public sealed class SyncRunner
{
    /// <summary>When this launch's sync started; freshness gate for the post-load step.</summary>
    private DateTime _launchStartedUtc = DateTime.MinValue;

    public int Run(string gameDirectory)
    {
        _launchStartedUtc = DateTime.UtcNow;
        var context = new AdapterContext
        {
            GameDirectory = gameDirectory,
            ModsDirectory = Path.Combine(gameDirectory, "mods"),
            DropInDirectory = Path.Combine(gameDirectory, "reloaded-dropin"),
        };

        Directory.CreateDirectory(context.LogsDirectory);
        RotateSyncLogs(context.LogsDirectory);
        var log = new StringBuilder();
        void Log(string message) => log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        IGameAdapter? detectedAdapter = null;
        string[] lastEnabledMods = [];
        var exitCode = 1;

        try
        {
            Log($"sync starting in {gameDirectory}");

            var adapters = new IGameAdapter[]
                { new GbfrAdapter(), new Adapter.P5R.P5rAdapter(), new Adapter.FFXVI.FfxviAdapter(), new Adapter.DSTS.DstsAdapter() };
            var (adapter, detection) = DetectAdapter(adapters, context);
            if (adapter is null || detection?.ExecutablePath is null)
            {
                Log("no supported game detected; aborting (game will run vanilla)");
                return 2;
            }
            Log($"adapter: {adapter.Id} ({detection.MatchedExecutable})");
            detectedAdapter = adapter;

            var validation = adapter.ValidateInstallation(context);
            foreach (var message in validation.Messages)
                Log($"[{message.Severity}] {message.Check}: {message.Message}");
            if (!validation.IsValid)
            {
                Log("validation failed; aborting (game will run vanilla)");
                return 3;
            }

            // Base mods aren't shipped in the package: install them from their
            // authors' official releases on first launch and keep them current
            // after that. Tests opt out via env var (mirrors the
            // RELOADED_DROPIN_APPDATA override).
            //
            // All network work shares a single deadline: an offline box with
            // multiple required mods would otherwise pay N × DNS timeout while
            // the game sits frozen at its entry point.
            var updatesEnabled = Environment.GetEnvironmentVariable("RELOADED_DROPIN_DISABLE_UPDATE") is null;
            var faithDx12Ready = true;
            if (updatesEnabled)
            {
                using var networkDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    using var feed = new Update.GitHubReleaseFeed(
                        Path.Combine(context.DropInDirectory, "state"));
                    var updater = new Update.BaseModInstaller(context, feed);
                    foreach (var line in updater.RunAsync(adapter.GetRequiredMods(), networkDeadline.Token).GetAwaiter().GetResult())
                        Log($"update: {line}");

                    if (adapter is Adapter.FFXVI.FfxviAdapter)
                    {
                        var patch = new Update.FaithDx12PatchInstaller(context, feed)
                            .RunAsync(allowNetwork: true, networkDeadline.Token).GetAwaiter().GetResult();
                        faithDx12Ready = patch.Ready;
                        foreach (var line in patch.Log)
                            Log($"ffxvi-patch: {line}");
                    }

                    // Check for a newer drop-in release so the overlay can
                    // show an update banner + download button.
                    using var updateHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    updateHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Unloaded-II-DropIn");
                    foreach (var line in new Update.SelfUpdateChecker(context.DropInDirectory)
                        .CheckAsync(updateHttp, networkDeadline.Token).GetAwaiter().GetResult())
                        Log(line);
                }
                catch (OperationCanceledException)
                {
                    Log("[Warning] network deadline exceeded; continuing with installed versions (missing mods retry next launch)");
                }
            }
            else if (adapter is Adapter.FFXVI.FfxviAdapter)
            {
                var patch = new Update.FaithDx12PatchInstaller(context, feed: null)
                    .RunAsync(allowNetwork: false, CancellationToken.None).GetAwaiter().GetResult();
                faithDx12Ready = patch.Ready;
                foreach (var line in patch.Log)
                    Log($"ffxvi-patch: {line}");
            }

            foreach (var line in adapter.CreateBackupsAsync(context, CancellationToken.None).GetAwaiter().GetResult())
                Log($"backup: {line}");

            // Archives dropped into mods/ become mod folders before the scan,
            // so they load on this very launch.
            foreach (var line in new ModArchiveImporter(context).ImportAll())
                Log($"import: {line}");

            var scan = new ModScanner().Scan(context.ModsDirectory);
            foreach (var issue in scan.Issues)
                Log($"ignored: {issue.Path} — {issue.Reason}");
            Log($"discovered {scan.Mods.Count} mod(s): {string.Join(", ", scan.Mods.Select(m => m.ModId))}");

            // Cache the scan so adapters and installers don't re-enumerate mods/.
            context.ModDirectories = scan.Mods
                .ToDictionary(m => m.ModId, m => m.Directory, StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<DiscoveredMod> usableMods = scan.Mods;
            if (!faithDx12Ready)
            {
                usableMods = scan.Mods
                    .Where(mod => !mod.ModId.Equals(
                        Adapter.FFXVI.FfxviAdapter.FaithFrameworkModId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Log("[Warning] Faith Framework was removed from this launch plan because its safe DX12 replacement is unavailable");
            }

            var resolution = new DependencyResolver().Resolve(usableMods);
            foreach (var missing in resolution.MissingDependencies)
                Log($"[Warning] {missing.ModId} requires missing dependency {missing.MissingDependencyId}");

            // Reloaded's loader reads its config from a fixed AppData path; since we
            // are already inside the game process, %APPDATA% resolves to the correct
            // Wine prefix (or the real AppData on Windows) with no path mapping.
            var loaderConfigPath = ReloadedLoaderConfigWriter.Write(context);
            Log($"wrote {loaderConfigPath}");

            // Only required mods that exist AND have their full dependency
            // chain go in the enabled list. Enabling a mod with any missing
            // (even transitive) dependency makes Reloaded abort the whole load
            // with an error dialog — a missing base mod (first launch offline)
            // must instead mean a quiet vanilla run that heals next launch.
            var presentIds = usableMods.Select(m => m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Loadable(string id) => presentIds.Contains(id) && !resolution.UnloadableModIds.Contains(id);
            var requiredEnabled = adapter.GetRequiredMods()
                .Where(m => m.Enabled && Loadable(m.Id))
                .Select(m => m.Id)
                .ToList();
            foreach (var absent in adapter.GetRequiredMods().Where(m => m.Enabled && !Loadable(m.Id)))
                Log($"[Warning] required mod {absent.Id} is missing or incomplete — mods won't affect the game " +
                    "until a launch with internet access downloads it");

            // Overlay toggles: user-disabled mods are excluded from the enabled
            // list (required mods cannot be disabled — the stack needs them).
            var overridesPath = OverlayOverrides.PathFor(context.DropInDirectory);
            var overrides = OverlayOverrides.Load(context.DropInDirectory);
            Log(File.Exists(overridesPath)
                ? $"overlay overrides: {overrides.DisabledMods.Length} disabled [{string.Join(", ", overrides.DisabledMods)}]"
                : $"overlay overrides: no file at {overridesPath} (nothing disabled)");
            var requiredIds = adapter.GetRequiredMods().Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activeMods = resolution.OrderedMods
                .Where(m => requiredIds.Contains(m.ModId) || !overrides.IsDisabled(m.ModId))
                .Where(m => !resolution.UnloadableModIds.Contains(m.ModId))
                .ToList();
            foreach (var disabled in resolution.OrderedMods.Except(activeMods))
                Log(resolution.UnloadableModIds.Contains(disabled.ModId)
                    ? $"[Warning] {disabled.ModId} left disabled: a dependency is missing (Reloaded would abort the whole load)"
                    : $"disabled via overlay: {disabled.ModId}");

            var plan = new ConfigGenerator().Plan(
                context.GeneratedDirectory,
                appId: detection.MatchedExecutable!.ToLowerInvariant(),
                appName: adapter.DisplayName,
                appLocation: detection.ExecutablePath,
                orderedMods: activeMods,
                requiredEnabledModIds: requiredEnabled);

            var changed = new ConfigGenerator().Apply(plan);
            Log($"{(changed ? "wrote" : "unchanged")} {plan.TargetPath}");
            Log($"enabled mods ({plan.EnabledMods.Length}): {string.Join(", ", plan.EnabledMods)}");
            lastEnabledMods = plan.EnabledMods;

            // A removed/toggled mod can leave loader-owned merged or converted
            // files behind even though AppConfig is now correct. Invalidate only
            // adapter-declared disposable state when the ordered mod set changes.
            foreach (var line in new ModStateHealer().Reconcile(
                         context.GameDirectory,
                         context.DropInDirectory,
                         adapter.Id,
                         plan.EnabledMods,
                         adapter.GetDisposableModStatePaths(context)))
                Log($"heal: {line}");

            foreach (var line in adapter.AfterGenerateConfigurationAsync(context, CancellationToken.None).GetAwaiter().GetResult())
                Log($"mirror: {line}");

            Log("sync complete");
            exitCode = 0;
            return 0;
        }
        catch (Exception ex)
        {
            Log($"crash: {ex}");

            // The .asi won't load Reloaded after a failed sync, but the previous
            // launch's modded index and mirrored files may still be on disk —
            // without this, "vanilla" would actually mean "last launch's mods".
            if (detectedAdapter is not null)
            {
                try
                {
                    foreach (var line in detectedAdapter.RestoreAsync(context, CancellationToken.None).GetAwaiter().GetResult())
                        Log($"restore-after-crash: {line}");
                }
                catch (Exception restoreEx)
                {
                    Log($"restore-after-crash failed: {restoreEx.Message}");
                }
            }

            return 1;
        }
        finally
        {
            File.WriteAllText(Path.Combine(context.LogsDirectory, "sync.log"), log.ToString());
            WriteLastSyncStatus(context, detectedAdapter?.Id, lastEnabledMods, exitCode);
        }
    }

    /// <summary>
    /// Post-load step: invoked by the .asi after Reloaded initialized, while the
    /// game is still frozen at its entry point. Appends to sync.log.
    /// </summary>
    public int PostLoad(string gameDirectory)
    {
        var context = new AdapterContext
        {
            GameDirectory = gameDirectory,
            ModsDirectory = Path.Combine(gameDirectory, "mods"),
            DropInDirectory = Path.Combine(gameDirectory, "reloaded-dropin"),
        };

        var log = new StringBuilder();
        void Log(string message) => log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] post-load: {message}");

        try
        {
            var adapters = new IGameAdapter[]
                { new GbfrAdapter(), new Adapter.P5R.P5rAdapter(), new Adapter.FFXVI.FfxviAdapter(), new Adapter.DSTS.DstsAdapter() };
            var (adapter, _) = DetectAdapter(adapters, context);
            if (adapter is null)
            {
                Log("no adapter; nothing to do");
                return 0;
            }

            var lines = adapter
                .AfterModsLoadedAsync(context, _launchStartedUtc, CancellationToken.None)
                .GetAwaiter().GetResult();
            foreach (var line in lines)
                Log(line);
            return 0;
        }
        catch (Exception ex)
        {
            Log($"crash: {ex}");
            return 1;
        }
        finally
        {
            File.AppendAllText(Path.Combine(context.LogsDirectory, "sync.log"), log.ToString());
        }
    }

    /// <summary>
    /// Machine-readable status for the diagnostics collector and overlay.
    /// Written on every exit path (success, failure, or early abort).
    /// </summary>
    private static void WriteLastSyncStatus(
        AdapterContext context, string? adapterId, string[] enabledMods, int exitCode)
    {
        try
        {
            var status = new
            {
                timestampUtc = DateTime.UtcNow.ToString("O"),
                adapter = adapterId,
                exitCode,
                success = exitCode == 0,
                modCount = enabledMods.Length,
                enabledMods,
            };
            Core.Filesystem.AtomicFile.WriteAllText(
                Path.Combine(context.DropInDirectory, "last-sync.json"),
                JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        catch
        {
            // Status file is best-effort; never cost us the launch.
        }
    }

    /// <summary>
    /// Keeps the previous two launches' sync logs as sync-prev1/2.log.
    /// Cross-launch bugs (toggles, baselines, mirrors) are undiagnosable from
    /// a single launch's log, and diagnostics are collected after the fact.
    /// </summary>
    private static void RotateSyncLogs(string logsDirectory)
    {
        try
        {
            var current = Path.Combine(logsDirectory, "sync.log");
            if (!File.Exists(current))
                return;
            var previous1 = Path.Combine(logsDirectory, "sync-prev1.log");
            var previous2 = Path.Combine(logsDirectory, "sync-prev2.log");
            if (File.Exists(previous1))
                File.Copy(previous1, previous2, overwrite: true);
            File.Copy(current, previous1, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log rotation must never cost us the launch.
        }
    }

    private static (IGameAdapter? Adapter, GameDetectionResult? Detection) DetectAdapter(
        IReadOnlyList<IGameAdapter> adapters, AdapterContext context)
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
