using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DearImguiSharp;
using Reloaded.Hooks.Definitions;
using Reloaded.Imgui.Hook;
using Reloaded.Imgui.Hook.DirectX.Hooks;
using Reloaded.Imgui.Hook.Implementations;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// A hardened variant of the library's ImguiHookDx11. The stock implementation
/// binds permanently to whichever window presents first and creates its render
/// target once from that first swapchain — on games that open a splash/video
/// window before the real one, or that recreate their swapchain during startup
/// (fullscreen transitions under DXVK), ImGui then renders forever into a
/// surface that is never shown (observed on Digimon Story: Time Stranger:
/// frames ran, imgui.ini was written, nothing was visible). This variant:
///
///  1. rebinds to the presenting window when the bound window has been
///     destroyed or hidden (splash → main window handoff), and
///  2. rebuilds the DX11 renderer whenever the presenting swapchain or device
///     changes under the bound window (swapchain recreation), and
///  3. reports every bind/discard decision through the drop-in's log so a
///     failed launch diagnoses itself.
///
/// Uses only public surface of Reloaded.Imgui.Hook (DX11Hook vtable, ImguiHook
/// window/lifecycle statics), so it slots into ImguiHookOptions.Implementations
/// in place of the stock hook. Intended to be upstreamed alongside the DX12
/// backend work.
/// </summary>
public sealed unsafe class ResilientImguiHookDx11 : IImguiHook
{
    public static ResilientImguiHookDx11? Instance { get; private set; }

    private readonly Action<string> _log;
    private IHook<DX11Hook.Present>? _presentHook;
    private IHook<DX11Hook.ResizeBuffers>? _resizeBuffersHook;
    private RenderTargetView? _renderTargetView;
    private bool _initialized;
    private IntPtr _boundSwapChain;
    private IntPtr _boundDevice;

    private bool _presentRecursionLock;
    private bool _resizeRecursionLock;

    // Discard decisions repeat every frame; log each distinct message once.
    private readonly HashSet<string> _loggedOnce = [];

    private static readonly string[] SupportedDlls =
        ["d3d11.dll", "d3d11_1.dll", "d3d11_2.dll", "d3d11_3.dll", "d3d11_4.dll"];

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    public ResilientImguiHookDx11(Action<string> log)
    {
        _log = log;
    }

    public bool IsApiSupported() =>
        SupportedDlls.Any(dll => GetModuleHandle(dll) != IntPtr.Zero);

    public void Initialize()
    {
        var presentPtr = (long)DX11Hook.DXGIVTable[(int)Reloaded.Imgui.Hook.DirectX.Definitions.IDXGISwapChain.Present].FunctionPointer;
        var resizeBuffersPtr = (long)DX11Hook.DXGIVTable[(int)Reloaded.Imgui.Hook.DirectX.Definitions.IDXGISwapChain.ResizeBuffers].FunctionPointer;
        Instance = this;
        _presentHook = SDK.Hooks.CreateHook<DX11Hook.Present>(typeof(ResilientImguiHookDx11), nameof(PresentImplStatic), presentPtr).Activate();
        _resizeBuffersHook = SDK.Hooks.CreateHook<DX11Hook.ResizeBuffers>(typeof(ResilientImguiHookDx11), nameof(ResizeBuffersImplStatic), resizeBuffersPtr).Activate();
    }

    private IntPtr PresentImpl(IntPtr swapChainPtr, int syncInterval, PresentFlags flags)
    {
        if (_presentRecursionLock)
            return _presentHook!.OriginalFunction.Value.Invoke(swapChainPtr, syncInterval, flags);

        _presentRecursionLock = true;
        try
        {
            RenderIfBindable(swapChainPtr);
        }
        catch (Exception ex)
        {
            LogOnce($"render failed; frame skipped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _presentRecursionLock = false;
        }

        return _presentHook!.OriginalFunction.Value.Invoke(swapChainPtr, syncInterval, flags);
    }

    private void RenderIfBindable(IntPtr swapChainPtr)
    {
        var swapChain = new SwapChain(swapChainPtr);
        var windowHandle = swapChain.Description.OutputHandle;

        if (!ImguiHook.CheckWindowHandle(windowHandle))
        {
            if (!ShouldRebind(windowHandle))
            {
                LogOnce($"discarding presents from window 0x{windowHandle:X} (bound to 0x{ImguiHook.WindowHandle:X})");
                return;
            }

            // The window we bound to is gone or hidden while another window is
            // presenting: splash/video window -> game window handoff. Shutdown
            // resets the library's Win32 side so the init below rebinds. (The
            // old window's WndProc hook stays behind; it is dead or dormant.)
            _log($"window 0x{ImguiHook.WindowHandle:X} is gone/hidden; rebinding to presenting window 0x{windowHandle:X}");
            TearDownRenderer();
            ImguiHook.Shutdown();
        }

        using var device = swapChain.GetDevice<Device>();

        // Same window, different swapchain/device: the game recreated its
        // swapchain (e.g. fullscreen switch). Our render target still points
        // at the old backbuffer, so rendering would be invisible - rebuild.
        if (_initialized && (swapChainPtr != _boundSwapChain || device.NativePointer != _boundDevice))
        {
            _log($"swapchain changed (0x{_boundSwapChain:X} -> 0x{swapChainPtr:X}); rebuilding renderer");
            TearDownRenderer();
        }

        if (!_initialized)
        {
            ImguiHook.InitializeWithHandle(windowHandle);
            ImGui.ImGuiImplDX11Init((void*)device.NativePointer, (void*)device.ImmediateContext.NativePointer);
            using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
            _renderTargetView = new RenderTargetView(device, backBuffer);
            _boundSwapChain = swapChainPtr;
            _boundDevice = device.NativePointer;
            _initialized = true;
            _log($"renderer bound: window 0x{windowHandle:X}, swapchain 0x{swapChainPtr:X}");
        }

        ImGui.ImGuiImplDX11NewFrame();
        ImguiHook.NewFrame();
        device.ImmediateContext.OutputMerger.SetRenderTargets(_renderTargetView);
        using var drawData = ImGui.GetDrawData();
        ImGui.ImGuiImplDX11RenderDrawData(drawData);
    }

    private static bool ShouldRebind(IntPtr candidate)
    {
        var bound = ImguiHook.WindowHandle;
        if (!ImguiHook.Initialized || bound == IntPtr.Zero || candidate == IntPtr.Zero)
            return false;
        if (!IsWindow(bound))
            return true;
        return !IsWindowVisible(bound) && IsWindowVisible(candidate);
    }

    private void TearDownRenderer()
    {
        if (!_initialized)
            return;

        _renderTargetView?.Dispose();
        _renderTargetView = null;
        ImGui.ImGuiImplDX11Shutdown();
        _initialized = false;
        _boundSwapChain = IntPtr.Zero;
        _boundDevice = IntPtr.Zero;
    }

    private IntPtr ResizeBuffersImpl(IntPtr swapchainPtr, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
    {
        if (_resizeRecursionLock)
            return _resizeBuffersHook!.OriginalFunction.Value.Invoke(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);

        _resizeRecursionLock = true;
        try
        {
            // Only the swapchain we render into needs its render target rebuilt.
            if (!_initialized || swapchainPtr != _boundSwapChain)
                return _resizeBuffersHook!.OriginalFunction.Value.Invoke(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);

            _renderTargetView?.Dispose();
            _renderTargetView = null;
            ImGui.ImGuiImplDX11InvalidateDeviceObjects();

            var result = _resizeBuffersHook!.OriginalFunction.Value.Invoke(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);

            ImGui.ImGuiImplDX11CreateDeviceObjects();
            var swapChain = new SwapChain(swapchainPtr);
            using var device = swapChain.GetDevice<Device>();
            using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
            _renderTargetView = new RenderTargetView(device, backBuffer);
            return result;
        }
        catch (Exception ex)
        {
            LogOnce($"resize handling failed: {ex.GetType().Name}: {ex.Message}");
            return _resizeBuffersHook!.OriginalFunction.Value.Invoke(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);
        }
        finally
        {
            _resizeRecursionLock = false;
        }
    }

    private void LogOnce(string message)
    {
        if (_loggedOnce.Count < 256 && _loggedOnce.Add(message))
            _log(message);
    }

    public void Disable()
    {
        _presentHook?.Disable();
        _resizeBuffersHook?.Disable();
    }

    public void Enable()
    {
        _presentHook?.Enable();
        _resizeBuffersHook?.Enable();
    }

    public void Dispose()
    {
        if (_initialized)
        {
            ImGui.ImGuiImplDX11Shutdown();
            _initialized = false;
        }
    }

    #region Hook Functions
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr ResizeBuffersImplStatic(IntPtr swapchainPtr, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags) =>
        Instance!.ResizeBuffersImpl(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr PresentImplStatic(IntPtr swapChainPtr, int syncInterval, PresentFlags flags) =>
        Instance!.PresentImpl(swapChainPtr, syncInterval, flags);
    #endregion
}
