using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// The Dear ImGui overlay window. Rendered every frame by the swapchain hook;
/// INSERT toggles visibility. All changes are saved to disk immediately and
/// applied by the drop-in's sync on the next game launch.
/// </summary>
public sealed class OverlayUi(
    string gameDirectory,
    IOverlayImGui imgui,
    Action<string> log,
    bool handleToggleKey = true)
{
    private const int VK_INSERT = 0x2D;

    private readonly ModCatalog _catalog = new(gameDirectory);
    private readonly IOverlayImGui _imgui = imgui;
    private bool _visible;
    private bool _catalogLoaded;
    private bool _insertHeld;
    private bool _pendingRelaunchChanges;
    private string? _lastSaveError;

    // Self-update download state (touched from the render thread + bg thread).
    private volatile int _downloadState; // 0=idle, 1=downloading, 2=done, 3=error
    private string _downloadStatus = "";

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr pszPath);

    private static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    private string DisplayVersion => _catalog.DropInVersion.EndsWith("-dev")
        ? _catalog.DropInVersion[..^4]
        : _catalog.DropInVersion;

    public void Render()
    {
        if (!_catalogLoaded)
        {
            SafeReload();
            _catalogLoaded = true;
        }

        RenderWatermark();

        if (handleToggleKey)
            HandleToggleKey();

        // Games hide the hardware cursor; have ImGui draw a software cursor
        // while the panel is open so the mouse is usable. CursorLiberator's
        // hooks read PanelOpen to stop the game recentering/capturing it.
        _imgui.SetMouseDrawCursor(_visible);
        CursorLiberator.PanelOpen = _visible;

        if (!_visible)
            return;

        var open = true;
        // "###" keeps the window ID stable while the visible title carries the
        // version, so ImGui window state survives version changes.
        if (!_imgui.Begin($"Reloaded-II Drop-In v{DisplayVersion}###dropin-panel", ref open, WindowNoCollapse))
        {
            _imgui.End();
            return;
        }

        _imgui.Text($"Mods folder: {Path.Combine(gameDirectory, "mods")}");
        RenderUpdateBanner();
        if (_lastSaveError is not null)
            _imgui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
                $"SAVE FAILED - change will NOT apply: {_lastSaveError}");
        else if (_pendingRelaunchChanges)
            _imgui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                "Changes saved - they apply on the next game launch.");
        _imgui.Separator();

        if (_imgui.SmallButton("Rescan mods"))
            SafeReload();

        _imgui.SameLine(0, 12);
        var showWatermark = !_catalog.HideWatermark;
        if (_imgui.Checkbox("Corner watermark", ref showWatermark))
        {
            _catalog.HideWatermark = !showWatermark;
            Save(() => _catalog.SaveToggles(), "watermark setting");
        }

        RenderModList(baseMods: false, "Your mods");
        RenderModList(baseMods: true, "Base mods (required)");

        _imgui.End();

        if (!open)
            _visible = false;
    }

    private void RenderModList(bool baseMods, string header)
    {
        if (!_imgui.CollapsingHeader(header, baseMods ? 0 : TreeDefaultOpen))
            return;

        var any = false;
        // Snapshot: a failed Save inside the loop reloads the catalog, which
        // rebuilds the live Mods list.
        foreach (var mod in _catalog.Mods.Where(m => m.IsBaseMod == baseMods).ToList())
        {
            any = true;
            _imgui.PushId(mod.ModId);

            if (baseMods)
            {
                _imgui.Text($"[on] {mod.Name} {mod.Version}");
            }
            else
            {
                var enabled = mod.Enabled;
                if (_imgui.Checkbox($"{mod.Name} {mod.Version}", ref enabled))
                {
                    mod.Enabled = enabled;
                    Save(() => _catalog.SaveToggles(), "toggles");
                }
            }

            if (mod.UserConfig is not null)
            {
                _imgui.SameLine(0, 12);
                if (_imgui.SmallButton(mod.ConfigExpanded ? "close settings" : "settings"))
                    mod.ConfigExpanded = !mod.ConfigExpanded;
                if (mod.ConfigExpanded)
                    RenderConfigEditor(mod);
            }

            _imgui.PopId();
        }

        if (!any)
            _imgui.TextDisabled(baseMods ? "none" : "none yet - drop mod folders into mods/");
    }

    private void RenderConfigEditor(CatalogMod mod)
    {
        var config = mod.UserConfig!;
        _imgui.Indent(16);
        var changed = false;

        foreach (var property in config.ToList())
        {
            if (property.Value is not JsonValue value)
                continue;

            if (value.TryGetValue<bool>(out var boolValue))
            {
                if (_imgui.Checkbox(property.Key, ref boolValue))
                {
                    config[property.Key] = boolValue;
                    changed = true;
                }
            }
            else if (value.TryGetValue<long>(out var longValue))
            {
                // Integer-valued field: keep it integral so mods with int
                // config properties can still deserialize the file.
                // Clamp to int range: InputInt uses a 32-bit ref, and mod
                // configs with large longs would silently truncate otherwise.
                var intValue = (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
                if (_imgui.InputInt(property.Key, ref intValue))
                {
                    config[property.Key] = (long)intValue;
                    changed = true;
                }
            }
            else if (value.TryGetValue<double>(out var numberValue))
            {
                // Floating-point field (also catches JSON numbers like 1.5
                // that don't match the long branch above).
                if (_imgui.InputDouble(property.Key, ref numberValue))
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
                var edited = _imgui.InputText(property.Key, buffer);

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

        _imgui.Unindent(16);
    }

    /// <summary>
    /// Banner shown when the sync pass found a newer release on GitHub.
    /// Offers a one-click download to the user's Downloads folder.
    /// </summary>
    private void RenderUpdateBanner()
    {
        if (!_catalog.UpdateAvailable || _catalog.UpdateDownloadUrl is null)
            return;

        _imgui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f),
            $"Update available: v{_catalog.LatestVersion} (you have v{DisplayVersion})");
        _imgui.SameLine(0, 12);

        switch (_downloadState)
        {
            case 0: // idle
                if (_imgui.SmallButton("Download"))
                    StartDownload(_catalog.UpdateDownloadUrl, _catalog.LatestVersion!);
                break;
            case 1: // downloading
                _imgui.TextDisabled("downloading...");
                break;
            case 2: // done
                _imgui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), _downloadStatus);
                break;
            case 3: // error
                _imgui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), _downloadStatus);
                _imgui.SameLine(0, 12);
                if (_imgui.SmallButton("Retry"))
                    StartDownload(_catalog.UpdateDownloadUrl, _catalog.LatestVersion!);
                break;
        }
    }

    private void StartDownload(string url, string version)
    {
        _downloadState = 1;
        _downloadStatus = "";
        var fileName = $"unloaded-ii-{version}.zip";

        Task.Run(async () =>
        {
            try
            {
                var downloadsDir = GetDownloadsFolder();
                Directory.CreateDirectory(downloadsDir);
                var destPath = Path.Combine(downloadsDir, fileName);

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Unloaded-II-DropIn");
                var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(destPath, bytes).ConfigureAwait(false);

                _downloadStatus = $"saved to {destPath}";
                _downloadState = 2;
                log($"downloaded update: {destPath}");
            }
            catch (Exception ex)
            {
                _downloadStatus = $"download failed: {ex.Message}";
                _downloadState = 3;
                log($"update download failed: {ex.Message}");
            }
        });
    }

    private static string GetDownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FolderDownloads, 0, IntPtr.Zero, out var pathPtr) == 0)
            {
                var path = Marshal.PtrToStringUni(pathPtr);
                Marshal.FreeCoTaskMem(pathPtr);
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
        }
        catch
        {
            // Fall through to the manual path.
        }

        // Fallback: under Wine/Proton the Windows user profile maps to the
        // Linux home, so ~/Downloads is almost always correct.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
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

        var display = _imgui.DisplaySize;
        _imgui.SetNextWindowPos(
            new Vector2(display.X - 10, display.Y - 10),
            CondAlways,
            Vector2.One);
        _imgui.SetNextWindowBgAlpha(0.35f);

        var open = true;
        const int flags = WindowNoTitleBar | WindowNoResize | WindowNoScrollbar |
                          WindowNoCollapse | WindowAlwaysAutoResize | WindowNoSavedSettings |
                          WindowNoFocusOnAppearing | WindowNoNav | WindowNoMouseInputs | WindowNoMove;
        if (_imgui.Begin("##dropin-watermark", ref open, flags))
        {
            // ASCII only: even Latin-1 glyphs render as '?' through this stack.
            _imgui.Text($"Unloaded-II - v{DisplayVersion} - Press Insert");
        }

        _imgui.End();
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

    public void ToggleVisible()
    {
        SetVisible(!_visible);
    }

    public void SetVisible(bool visible)
    {
        if (_visible == visible)
            return;

        _visible = visible;
        if (visible)
            SafeReload();
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

    // Dear ImGui flag values are stable across the 1.88 D3D11 and Faith 1.92
    // bindings. Keeping them local avoids leaking either binding into the UI.
    private const int WindowNoTitleBar = 1 << 0;
    private const int WindowNoResize = 1 << 1;
    private const int WindowNoMove = 1 << 2;
    private const int WindowNoScrollbar = 1 << 3;
    private const int WindowNoCollapse = 1 << 5;
    private const int WindowAlwaysAutoResize = 1 << 6;
    private const int WindowNoSavedSettings = 1 << 8;
    private const int WindowNoMouseInputs = 1 << 9;
    private const int WindowNoFocusOnAppearing = 1 << 12;
    private const int WindowNoNav = 1 << 18;
    private const int TreeDefaultOpen = 1 << 5;
    private const int CondAlways = 1 << 0;
}
