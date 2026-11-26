using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmMapmakerEditor;

/// <summary>
/// Godot's own "Use Snap" toggle of the 3D view (key Y), which snaps a gizmo drag (move, rotate and scale) while it happens. Godot keeps its state per scene in the editor's
/// layout files, so it can come back on by itself; the Mapmaker switches it off when a session starts and when a map is opened (owner 2026-11-27: snap starts off), through the
/// button itself so the editor's state stays consistent. Turning it on afterwards is left alone.
/// The button is found by its shortcut: the editor's own registered "spatial_editor/snap" shortcut object when the editor settings hand it out, else a toggle whose shortcut is named
/// "Use Snap" (Godot 4.7.2 names the shortcut by its label and leaves its path empty; the review of 2026-11-27 found the old match by path never found it).
/// </summary>
public static class EditorSnap
{
    const string ShortcutPath = "spatial_editor/snap", ShortcutLabel = "Use Snap";

    static Shortcut? Registered()
    {
        try
        {
            var settings = EditorInterface.Singleton.GetEditorSettings();
            if (ClassDB.ClassHasMethod("EditorSettings", "get_shortcut") && settings.Call("get_shortcut", ShortcutPath).AsGodotObject() is Shortcut s) return s;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>The 3D editor's Use Snap toolbar button(s).</summary>
    static List<Button> Buttons()
    {
        var list = new List<Button>();
        try
        {
            var registered = Registered();
            foreach (var n in EditorInterface.Singleton.GetEditorMainScreen().FindChildren("*", "Button", true, false))
                if (n is Button { ToggleMode: true } b && b.Shortcut is { } s
                    && ((registered is not null && s == registered) || s.ResourceName == ShortcutLabel || s.ResourceName == ShortcutPath || s.ResourcePath.EndsWith(ShortcutPath, StringComparison.Ordinal)))
                    list.Add(b);
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>Test aid: Godot's Use Snap is on (null when the button was not found: the editor check fails on that).</summary>
    public static bool? BuiltInOn => Buttons() is { Count: > 0 } b ? b.Any(x => x.ButtonPressed) : null;

    /// <summary>Test aid: switches Godot's Use Snap on, as a user (or a scene's saved state) would.</summary>
    public static void TestSwitchOnBuiltIn() { foreach (var b in Buttons()) if (!b.ButtonPressed) b.ButtonPressed = true; }

    public static void SwitchOffBuiltIn()
    {
        foreach (var b in Buttons()) if (b.ButtonPressed) b.ButtonPressed = false;   // emits toggled, which the 3D editor listens to
    }
}
