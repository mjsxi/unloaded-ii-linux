using System.Numerics;
using NenTools.ImGui.Interfaces;
using NenTools.ImGui.Interfaces.Shell;
using Reloaded.Mod.Interfaces;

namespace ReloadedDropIn.Overlay.Faith;

/// <summary>
/// Loaded reflectively only on FFXVI, after Faith has exported its controllers.
/// Keeping this in a side assembly prevents GBFR/P5R from acquiring a runtime
/// dependency on Faith's interface assembly.
/// </summary>
public static class FaithBridge
{
    public static Action<bool> Register(IModLoader loader, string gameDirectory, Action<string> log)
    {
        var imGuiController = loader.GetController<IImGui>();
        if (imGuiController?.TryGetTarget(out var imgui) != true || imgui is null)
            throw new InvalidOperationException("Faith IImGui controller is unavailable");

        var shellController = loader.GetController<IImGuiShell>();
        if (shellController?.TryGetTarget(out var shell) != true || shell is null)
            throw new InvalidOperationException("Faith IImGuiShell controller is unavailable");

        // Faith owns FFXVI's keyboard input and already toggles its menu from
        // INSERT. GetAsyncKeyState is not dependable for this game under
        // Proton, so this bridge follows Faith's menu state instead.
        var ui = new OverlayUi(gameDirectory, new FaithOverlayImGui(imgui), log,
            handleToggleKey: false);
        var component = new FaithOverlayComponent(imgui, ui, log);
        shell.AddComponent(
            component, overrideCategory: "Mods", overridePriority: 100, overrideOwner: "Unloaded-II");
        return enabled => component.Enabled = enabled;
    }
}

internal sealed class FaithOverlayComponent(
    IImGui imgui,
    OverlayUi ui,
    Action<string> log) : IImGuiComponent
{
    private bool _menuStateKnown;
    private bool _wasMainMenuOpen;
    private bool _renderFaulted;

    public bool IsOverlay => true;
    public bool Enabled { get; set; } = true;

    public void RenderMenu(IImGuiShell imGuiShell)
    {
        if (Enabled && imgui.MenuItem("Unloaded-II Panel"))
            ui.ToggleVisible();
    }

    public void Render(IImGuiShell imGuiShell)
    {
        if (!Enabled || _renderFaulted)
            return;

        try
        {
            // Faith handles INSERT inside FFXVI's input path. Open/close our
            // panel with Faith's menu while still allowing its Mods menu item
            // to toggle the panel independently while that menu is open.
            var mainMenuOpen = imGuiShell.IsMainMenuOpen;
            if (!_menuStateKnown || mainMenuOpen != _wasMainMenuOpen)
            {
                _menuStateKnown = true;
                _wasMainMenuOpen = mainMenuOpen;
                ui.SetVisible(mainMenuOpen);
            }

            ui.Render();
        }
        catch (Exception ex)
        {
            // Do not let an overlay UI problem disable Faith's entire DX12
            // renderer (and every other Faith component) for this session.
            _renderFaulted = true;
            log($"Faith panel render failed; only the Unloaded-II panel was disabled: {ex}");
        }
    }
}

internal sealed class FaithOverlayImGui(IImGui imgui) : IOverlayImGui
{
    public Vector2 DisplaySize
    {
        get
        {
            // Faith's own FFXVI overlays use the main viewport. Under Proton,
            // IO.DisplaySize can briefly remain zero or describe a stale
            // swapchain after the splash-to-game transition, which places our
            // bottom-right watermark at (-10, -10).
            var viewport = imgui.GetMainViewport();
            return viewport is null
                ? imgui.GetIO().DisplaySize
                : viewport.WorkPos + viewport.WorkSize;
        }
    }

    public void SetMouseDrawCursor(bool enabled)
    {
        // Faith owns input/cursor state for its menu. Only request a software
        // cursor while our panel is open; never hide Faith's cursor.
        if (enabled)
            imgui.GetIO().MouseDrawCursor = true;
    }

    public bool Begin(string title, ref bool open, int flags) =>
        imgui.Begin(title, ref open, (ImGuiWindowFlags)flags);
    public void End() => imgui.End();
    public void Text(string text) => imgui.Text(text);
    public void TextColored(Vector4 color, string text) => imgui.TextColored(color, text);
    public void TextDisabled(string text) => imgui.TextDisabled(text);
    public void Separator() => imgui.Separator();
    public bool SmallButton(string label) => imgui.SmallButton(label);
    public void SameLine(float offset, float spacing) => imgui.SameLineEx(offset, spacing);
    public bool Checkbox(string label, ref bool value) => imgui.Checkbox(label, ref value);
    public bool CollapsingHeader(string label, int flags) =>
        imgui.CollapsingHeader(label, (ImGuiTreeNodeFlags)flags);
    public void PushId(string id) => imgui.PushID(id);
    public void PopId() => imgui.PopID();
    public void Indent(float amount) => imgui.IndentEx(amount);
    public void Unindent(float amount) => imgui.UnindentEx(amount);
    public bool InputInt(string label, ref int value) => imgui.InputInt(label, ref value);
    public bool InputDouble(string label, ref double value) => imgui.InputDouble(label, ref value);
    public bool InputText(string label, byte[] buffer) =>
        imgui.InputText(label, buffer.AsSpan(), ImGuiInputTextFlags.ImGuiInputTextFlags_None);
    public void SetNextWindowPos(Vector2 position, int condition, Vector2 pivot) =>
        imgui.SetNextWindowPosEx(position, (ImGuiCond)condition, pivot);
    public void SetNextWindowBgAlpha(float alpha) => imgui.SetNextWindowBgAlpha(alpha);
}
