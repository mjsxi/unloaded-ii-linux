using System.Numerics;
using DearImguiSharp;
using DearImGui = DearImguiSharp.ImGui;

namespace ReloadedDropIn.Overlay;

/// <summary>
/// The mod panel is renderer-independent. GBFR/P5R call DearImguiSharp through
/// Reloaded.Imgui.Hook, while FFXVI calls Faith's IImGui controller so both
/// Unloaded-II and Faith UI share the same context and patched DX12 backend.
/// </summary>
public interface IOverlayImGui
{
    Vector2 DisplaySize { get; }
    void SetMouseDrawCursor(bool enabled);
    bool Begin(string title, ref bool open, int flags);
    void End();
    void Text(string text);
    void TextColored(Vector4 color, string text);
    void TextDisabled(string text);
    void Separator();
    bool SmallButton(string label);
    void SameLine(float offset, float spacing);
    bool Checkbox(string label, ref bool value);
    bool CollapsingHeader(string label, int flags);
    void PushId(string id);
    void PopId();
    void Indent(float amount);
    void Unindent(float amount);
    bool InputInt(string label, ref int value);
    bool InputDouble(string label, ref double value);
    bool InputText(string label, byte[] buffer);
    void SetNextWindowPos(Vector2 position, int condition, Vector2 pivot);
    void SetNextWindowBgAlpha(float alpha);
}

internal sealed unsafe class DearOverlayImGui : IOverlayImGui
{
    public Vector2 DisplaySize
    {
        get
        {
            var size = DearImGui.GetIO().DisplaySize;
            return new Vector2(size.X, size.Y);
        }
    }

    public void SetMouseDrawCursor(bool enabled) => DearImGui.GetIO().MouseDrawCursor = enabled;
    public bool Begin(string title, ref bool open, int flags) => DearImGui.Begin(title, ref open, flags);
    public void End() => DearImGui.End();
    public void Text(string text) => DearImGui.Text(text);
    public void TextColored(Vector4 color, string text) => DearImGui.TextColored(
        new ImVec4 { X = color.X, Y = color.Y, Z = color.Z, W = color.W }, text);
    public void TextDisabled(string text) => DearImGui.TextDisabled(text);
    public void Separator() => DearImGui.Separator();
    public bool SmallButton(string label) => DearImGui.SmallButton(label);
    public void SameLine(float offset, float spacing) => DearImGui.SameLine(offset, spacing);
    public bool Checkbox(string label, ref bool value) => DearImGui.Checkbox(label, ref value);
    public bool CollapsingHeader(string label, int flags) =>
        DearImGui.CollapsingHeaderTreeNodeFlags(label, flags);
    public void PushId(string id) => DearImGui.PushID_Str(id);
    public void PopId() => DearImGui.PopID();
    public void Indent(float amount) => DearImGui.Indent(amount);
    public void Unindent(float amount) => DearImGui.Unindent(amount);
    public bool InputInt(string label, ref int value) => DearImGui.InputInt(label, ref value, 1, 10, 0);
    public bool InputDouble(string label, ref double value) =>
        DearImGui.InputDouble(label, ref value, 0.1, 1.0, "%g", 0);

    public bool InputText(string label, byte[] buffer)
    {
        fixed (byte* bufferPtr = buffer)
            return DearImGui.InputText(label, (sbyte*)bufferPtr, buffer.Length, 0, null!, IntPtr.Zero);
    }

    public void SetNextWindowPos(Vector2 position, int condition, Vector2 pivot) =>
        DearImGui.SetNextWindowPos(
            new ImVec2 { X = position.X, Y = position.Y }, condition,
            new ImVec2 { X = pivot.X, Y = pivot.Y });

    public void SetNextWindowBgAlpha(float alpha) => DearImGui.SetNextWindowBgAlpha(alpha);
}
