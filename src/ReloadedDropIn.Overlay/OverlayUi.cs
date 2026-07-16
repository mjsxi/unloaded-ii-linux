using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using DearImguiSharp;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// The Dear ImGui overlay window. Rendered every frame by the swapchain hook;
/// INSERT toggles visibility. All changes are saved to disk immediately and
/// applied by the drop-in's sync on the next game launch.
/// </summary>
public sealed class OverlayUi(string gameDirectory, Action<string> log)
{
    private const int VK_INSERT = 0x2D;

    private readonly ModCatalog _catalog = new(gameDirectory);
    private bool _visible;
    private bool _catalogLoaded;
    private bool _insertHeld;
    private bool _pendingRelaunchChanges;
    private string? _lastSaveError;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void Render()
    {
        if (!_catalogLoaded)
        {
            SafeReload();
            _catalogLoaded = true;
        }

        RenderWatermark();

        HandleToggleKey();

        // Games hide the hardware cursor; have ImGui draw a software cursor
        // while the panel is open so the mouse is usable. CursorLiberator's
        // hooks read PanelOpen to stop the game recentering/capturing it.
        ImGui.GetIO().MouseDrawCursor = _visible;
        CursorLiberator.PanelOpen = _visible;

        if (!_visible)
            return;

        var open = true;
        if (!ImGui.Begin("Reloaded Drop-In", ref open, (int)ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        ImGui.Text($"Mods folder: {Path.Combine(gameDirectory, "mods")}");
        if (_lastSaveError is not null)
            ImGui.TextColored(new ImVec4 { X = 1f, Y = 0.3f, Z = 0.3f, W = 1f },
                $"SAVE FAILED - change will NOT apply: {_lastSaveError}");
        else if (_pendingRelaunchChanges)
            ImGui.TextColored(new ImVec4 { X = 1f, Y = 0.8f, Z = 0.2f, W = 1f },
                "Changes saved - they apply on the next game launch.");
        ImGui.Separator();

        if (ImGui.SmallButton("Rescan mods"))
            SafeReload();

        ImGui.SameLine(0, 12);
        var showWatermark = !_catalog.HideWatermark;
        if (ImGui.Checkbox("Corner watermark", ref showWatermark))
        {
            _catalog.HideWatermark = !showWatermark;
            Save(() => _catalog.SaveToggles(), "watermark setting");
        }

        RenderModList(baseMods: false, "Your mods");
        RenderModList(baseMods: true, "Base mods (required)");

        ImGui.End();

        if (!open)
            _visible = false;
    }

    private void RenderModList(bool baseMods, string header)
    {
        if (!ImGui.CollapsingHeaderTreeNodeFlags(header,
                (int)(baseMods ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen)))
            return;

        var any = false;
        // Snapshot: a failed Save inside the loop reloads the catalog, which
        // rebuilds the live Mods list.
        foreach (var mod in _catalog.Mods.Where(m => m.IsBaseMod == baseMods).ToList())
        {
            any = true;
            ImGui.PushID_Str(mod.ModId);

            if (baseMods)
            {
                ImGui.Text($"[on] {mod.Name} {mod.Version}");
            }
            else
            {
                var enabled = mod.Enabled;
                if (ImGui.Checkbox($"{mod.Name} {mod.Version}", ref enabled))
                {
                    mod.Enabled = enabled;
                    Save(() => _catalog.SaveToggles(), "toggles");
                }
            }

            if (mod.UserConfig is not null)
            {
                ImGui.SameLine(0, 12);
                if (ImGui.SmallButton(mod.ConfigExpanded ? "close settings" : "settings"))
                    mod.ConfigExpanded = !mod.ConfigExpanded;
                if (mod.ConfigExpanded)
                    RenderConfigEditor(mod);
            }

            ImGui.PopID();
        }

        if (!any)
            ImGui.TextDisabled(baseMods ? "none" : "none yet - drop mod folders into mods/");
    }

    private void RenderConfigEditor(CatalogMod mod)
    {
        var config = mod.UserConfig!;
        ImGui.Indent(16);
        var changed = false;

        foreach (var property in config.ToList())
        {
            if (property.Value is not JsonValue value)
                continue;

            if (value.TryGetValue<bool>(out var boolValue))
            {
                if (ImGui.Checkbox(property.Key, ref boolValue))
                {
                    config[property.Key] = boolValue;
                    changed = true;
                }
            }
            else if (value.TryGetValue<long>(out var longValue))
            {
                // Integer-valued field: keep it integral so mods with int
                // config properties can still deserialize the file.
                var intValue = (int)longValue;
                if (ImGui.InputInt(property.Key, ref intValue, 1, 10, 0))
                {
                    config[property.Key] = (long)intValue;
                    changed = true;
                }
            }
            else if (value.TryGetValue<double>(out var numberValue))
            {
                if (ImGui.InputDouble(property.Key, ref numberValue, 0.1, 1.0, "%g", 0))
                {
                    config[property.Key] = numberValue;
                    changed = true;
                }
            }
            else if (value.TryGetValue<string>(out var stringValue))
            {
                var buffer = new byte[256];
                var bytes = Encoding.UTF8.GetBytes(stringValue);
                Array.Copy(bytes, buffer, Math.Min(bytes.Length, buffer.Length - 1));
                var edited = false;
                unsafe
                {
                    fixed (byte* bufferPtr = buffer)
                    {
                        edited = ImGui.InputText(property.Key, (sbyte*)bufferPtr, buffer.Length, 0, null!, IntPtr.Zero);
                    }
                }

                if (edited)
                {
                    var terminator = Array.IndexOf(buffer, (byte)0);
                    config[property.Key] = Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
                    changed = true;
                }
            }
        }

        if (changed)
            Save(() => _catalog.SaveConfig(mod), $"config for {mod.ModId}");

        ImGui.Unindent(16);
    }

    /// <summary>
    /// Always-on corner badge (REFramework-style) proving the loader is active:
    /// "Unloaded-II vX · N mods". Bottom-right, click-through, toggleable from
    /// the panel. Drawn by us instead of patching the game's version text -
    /// rewriting the game's text tables breaks on current game builds.
    /// </summary>
    private void RenderWatermark()
    {
        if (_catalog.HideWatermark)
            return;

        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(
            new ImVec2 { X = display.X - 10, Y = display.Y - 10 },
            (int)ImGuiCond.Always,
            new ImVec2 { X = 1, Y = 1 });
        ImGui.SetNextWindowBgAlpha(0.35f);

        var open = true;
        const int flags = (int)(ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse |
                                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
                                ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.NoMove);
        if (ImGui.Begin("##dropin-watermark", ref open, flags))
        {
            // ASCII only: even Latin-1 glyphs render as '?' through this stack.
            var version = _catalog.DropInVersion.EndsWith("-dev")
                ? _catalog.DropInVersion[..^4]
                : _catalog.DropInVersion;
            ImGui.Text($"Unloaded-II - v{version} - Press Insert");
        }

        ImGui.End();
    }

    private void HandleToggleKey()
    {
        var down = (GetAsyncKeyState(VK_INSERT) & 0x8000) != 0;
        if (down && !_insertHeld)
        {
            _visible = !_visible;
            if (_visible)
                SafeReload();
        }

        _insertHeld = down;
    }

    private void SafeReload()
    {
        try
        {
            _catalog.Reload();
        }
        catch (Exception ex)
        {
            log($"mod scan failed: {ex.Message}");
        }
    }

    private void Save(Action save, string what)
    {
        try
        {
            save();
            _pendingRelaunchChanges = true;
            _lastSaveError = null;
        }
        catch (Exception ex)
        {
            log($"failed to save {what}: {ex.Message}");
            _lastSaveError = $"{what} ({ex.GetType().Name}: {ex.Message})";
            // Re-read disk truth so the UI can't show a toggle that didn't stick.
            SafeReload();
        }
    }
}
