using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Reloaded.Hooks.Definitions;
using Reloaded.Imgui.Hook;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// Reloaded Drop-In's in-game overlay: press INSERT to open a REFramework-style
/// panel (rendered inside the game window via a swapchain hook) that lists mods,
/// toggles them on/off, and edits their user configs. Toggle/config changes are
/// written to disk and applied by the drop-in's sync on the next launch.
/// </summary>
public class Program : IModV2, IExports
{
    public Action Disposing { get; } = () => { };

    /// <summary>
    /// FFXVI's ImGui components render through Faith Framework's patched DX12
    /// backend. Starting this overlay's independent renderer as well would put
    /// two hook/render stacks on the same swapchain.
    /// </summary>
    private static readonly string[] FaithRendererExecutables =
        ["ffxvi.exe", "ffxvi_demo.exe"];

    private OverlayUi? _ui;
    private CursorLiberator? _cursorLiberator;
    private bool _hooked;
    private Action<bool>? _setFaithComponentEnabled;

    public void StartEx(IModLoaderV1 loader, IModConfigV1 config)
    {
        var loaderV2 = (IModLoader)loader;
        var logger = (ILogger)loaderV2.GetLogger();

        try
        {
            var executablePath = Process.GetCurrentProcess().MainModule!.FileName;
            var executableName = Path.GetFileName(executablePath);
            var gameDirectory = Path.GetDirectoryName(executablePath)!;
            if (FaithRendererExecutables.Contains(executableName, StringComparer.OrdinalIgnoreCase))
            {
                StartFaithOverlay(loaderV2, logger, gameDirectory);
                return;
            }

            var hooksController = loaderV2.GetController<IReloadedHooks>();
            if (hooksController is null || !hooksController.TryGetTarget(out var hooks))
            {
                logger.WriteLine("[dropin.overlay] reloaded.sharedlib.hooks controller unavailable; overlay disabled.");
                return;
            }

            // Route the hook library's own diagnostics (window binds, present
            // discards) into the Reloaded log, deduplicated: they repeat per
            // frame and each distinct message only matters once.
            var loggedHookMessages = new HashSet<string>();
            SDK.Init(hooks, message =>
            {
                lock (loggedHookMessages)
                {
                    if (loggedHookMessages.Count < 256 && loggedHookMessages.Add(message))
                        logger.WriteLine($"[dropin.overlay/hook] {message}");
                }
            });

            _ui = new OverlayUi(
                gameDirectory,
                new DearOverlayImGui(),
                message => logger.WriteLine($"[dropin.overlay] {message}"));

            try
            {
                _cursorLiberator = new CursorLiberator();
                _cursorLiberator.Install(hooks);
            }
            catch (Exception ex)
            {
                logger.WriteLine($"[dropin.overlay] cursor hooks failed (panel mouse may fight the game): {ex.Message}");
            }

            Task.Run(async () =>
            {
                try
                {
                    // Hardened replacement for the stock ImguiHookDx11: it
                    // survives splash-window -> game-window handoffs and
                    // swapchain recreation (see ResilientImguiHookDx11).
                    await ImguiHook.Create(_ui.Render, new ImguiHookOptions
                    {
                        Implementations =
                            [new ResilientImguiHookDx11(message => logger.WriteLine($"[dropin.overlay/dx11] {message}"))],
                    }).ConfigureAwait(false);
                    _hooked = true;
                    logger.WriteLine("[dropin.overlay] overlay ready — press INSERT in-game.");
                }
                catch (Exception ex)
                {
                    logger.WriteLine($"[dropin.overlay] failed to hook renderer: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            logger.WriteLine($"[dropin.overlay] init failed: {ex.Message}");
        }
    }

    private void StartFaithOverlay(IModLoader loader, ILogger logger, string gameDirectory)
    {
        try
        {
            // Keep Faith's interface types out of the universal overlay
            // assembly. Reloaded reflects every type in a mod at startup; a
            // direct reference here would make GBFR/P5R require Faith too.
            var modDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
            var bridgePath = Path.Combine(modDirectory, "ReloadedDropIn.Overlay.Faith.dll");
            if (!File.Exists(bridgePath))
                throw new FileNotFoundException("Faith overlay bridge is missing", bridgePath);

            var loadContext = AssemblyLoadContext.GetLoadContext(typeof(Program).Assembly)
                              ?? AssemblyLoadContext.Default;
            var bridgeAssembly = loadContext.LoadFromAssemblyPath(bridgePath);
            var bridgeType = bridgeAssembly.GetType("ReloadedDropIn.Overlay.Faith.FaithBridge", throwOnError: true)!;
            var register = bridgeType.GetMethod("Register", BindingFlags.Public | BindingFlags.Static)
                           ?? throw new MissingMethodException(bridgeType.FullName, "Register");
            var log = new Action<string>(message => logger.WriteLine($"[dropin.overlay] {message}"));
            _setFaithComponentEnabled = register.Invoke(null, [loader, gameDirectory, log]) as Action<bool>
                ?? throw new InvalidOperationException("Faith overlay bridge returned no component controller");
            logger.WriteLine("[dropin.overlay] registered with Faith's patched DX12 renderer — press INSERT in-game.");
        }
        catch (Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: not null } ? ex.InnerException : ex;
            logger.WriteLine($"[dropin.overlay] Faith bridge failed; FFXVI panel disabled: {actual.Message}");
        }
    }

    public void Suspend()
    {
        _setFaithComponentEnabled?.Invoke(false);
        if (_hooked)
            ImguiHook.Disable();
    }

    public void Resume()
    {
        _setFaithComponentEnabled?.Invoke(true);
        if (_hooked)
            ImguiHook.Enable();
    }

    public void Unload() => Suspend();
    public bool CanUnload() => false;
    public bool CanSuspend() => true;
    public Type[] GetTypes() => [];
}
