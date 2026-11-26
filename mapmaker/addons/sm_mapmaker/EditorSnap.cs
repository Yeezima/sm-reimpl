using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmMapmakerEditor;

/// <summary>
/// Godot's own "Use Snap" toggle of the 3D view (key Y), which snaps a gizmo drag (move, rotate and scale) while it happens. Godot keeps its state per scene in the editor's
/// layout files, so it can come back on by itself; the Mapmaker switches it off when a session starts and when a map is opened (owner 2026-11-27: snap starts off), through the
/// button itself so the editor's state stays consistent. Turning it on afterwards is left alone.
/// </summary>
public static class EditorSnap
{
    const string ShortcutName = "spatial_editor/snap";

    /// <summary>The toolbar button(s) whose shortcut is the 3D editor's Use Snap.</summary>
    static List<Button> Buttons()
    {
        var list = new List<Button>();
        try
        {
            foreach (var n in EditorInterface.Singleton.GetEditorMainScreen().FindChildren("*", "Button", true, false))
                if (n is Button { ToggleMode: true } b && b.Shortcut is { } s && (s.ResourceName == ShortcutName || s.ResourcePath.EndsWith(ShortcutName, StringComparison.Ordinal))) list.Add(b);
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>Test aid: Godot's Use Snap is on (null when this Godot has no such button).</summary>
    public static bool? BuiltInOn => Buttons() is { Count: > 0 } b ? b.Any(x => x.ButtonPressed) : null;

    public static void SwitchOffBuiltIn()
    {
        foreach (var b in Buttons()) if (b.ButtonPressed) b.ButtonPressed = false;   // emits toggled, which the 3D editor listens to
    }
}
