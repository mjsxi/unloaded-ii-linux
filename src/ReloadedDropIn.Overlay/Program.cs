using System.Diagnostics;
using Reloaded.Hooks.Definitions;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.Direct3D11;
using Reloaded.Imgui.Hook.Implementations;
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

    private OverlayUi? _ui;
    private CursorLiberator? _cursorLiberator;
    private bool _hooked;

    public void StartEx(IModLoaderV1 loader, IModConfigV1 config)
    {
        var loaderV2 = (IModLoader)loader;
        var logger = (ILogger)loaderV2.GetLogger();

        try
        {
            var hooksController = loaderV2.GetController<IReloadedHooks>();
            if (hooksController is null || !hooksController.TryGetTarget(out var hooks))
            {
                logger.WriteLine("[dropin.overlay] reloaded.sharedlib.hooks controller unavailable; overlay disabled.");
                return;
            }

            SDK.Init(hooks);

            var gameDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
            _ui = new OverlayUi(gameDirectory, message => logger.WriteLine($"[dropin.overlay] {message}"));

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
                    await ImguiHook.Create(_ui.Render, new ImguiHookOptions
                    {
                        Implementations = [new ImguiHookDx11()],
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

    public void Suspend()
    {
        if (_hooked)
            ImguiHook.Disable();
    }

    public void Resume()
    {
        if (_hooked)
            ImguiHook.Enable();
    }

    public void Unload() => Suspend();
    public bool CanUnload() => false;
    public bool CanSuspend() => true;
    public Type[] GetTypes() => [];
}
