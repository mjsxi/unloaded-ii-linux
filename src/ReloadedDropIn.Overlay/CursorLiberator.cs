using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// Frees the mouse while the panel is open. Action games capture the mouse by
/// re-centering it every frame (SetCursorPos), reading it back (GetCursorPos)
/// to derive camera deltas, and confining it (ClipCursor). While the panel is
/// visible we:
///   - swallow the game's SetCursorPos (cursor stops snapping to center),
///   - answer GetCursorPos with the game's last requested position (camera
///     sees zero delta and stays still),
///   - lift ClipCursor confinement.
/// ImGui itself is unaffected: its input comes from window messages.
/// </summary>
public sealed class CursorLiberator
{
    /// <summary>Set by the UI each frame; hooks read it.</summary>
    public static volatile bool PanelOpen;

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    public delegate int SetCursorPosFn(int x, int y);

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    public delegate int GetCursorPosFn(IntPtr point);

    [Reloaded.Hooks.Definitions.X64.Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
    [Reloaded.Hooks.Definitions.X86.Function(Reloaded.Hooks.Definitions.X86.CallingConventions.Stdcall)]
    public delegate int ClipCursorFn(IntPtr rect);

    private IHook<SetCursorPosFn>? _setCursorPos;
    private IHook<GetCursorPosFn>? _getCursorPos;
    private IHook<ClipCursorFn>? _clipCursor;

    private volatile int _anchorX = -1;
    private volatile int _anchorY = -1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    public void Install(IReloadedHooks hooks)
    {
        var user32 = GetModuleHandleW("user32.dll");
        _setCursorPos = hooks
            .CreateHook<SetCursorPosFn>(SetCursorPosImpl, (long)GetProcAddress(user32, "SetCursorPos"))
            .Activate();
        _getCursorPos = hooks
            .CreateHook<GetCursorPosFn>(GetCursorPosImpl, (long)GetProcAddress(user32, "GetCursorPos"))
            .Activate();
        _clipCursor = hooks
            .CreateHook<ClipCursorFn>(ClipCursorImpl, (long)GetProcAddress(user32, "ClipCursor"))
            .Activate();
    }

    private int SetCursorPosImpl(int x, int y)
    {
        if (!PanelOpen)
            return _setCursorPos!.OriginalFunction(x, y);

        // Remember where the game wants the cursor so GetCursorPos can report
        // exactly that (zero camera delta), but don't actually move it.
        _anchorX = x;
        _anchorY = y;
        return 1;
    }

    private int GetCursorPosImpl(IntPtr point)
    {
        var result = _getCursorPos!.OriginalFunction(point);
        if (!PanelOpen || point == IntPtr.Zero || _anchorX < 0)
            return result;

        Marshal.WriteInt32(point, 0, _anchorX);
        Marshal.WriteInt32(point, 4, _anchorY);
        return result;
    }

    private int ClipCursorImpl(IntPtr rect)
    {
        if (!PanelOpen)
            return _clipCursor!.OriginalFunction(rect);

        // Refuse confinement while the panel is open.
        return _clipCursor!.OriginalFunction(IntPtr.Zero);
    }
}
