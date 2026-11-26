using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>Which tool the 3D viewport's left click belongs to.</summary>
public enum Tool { Select, PickSm1, Place }

/// <summary>
/// The editor-side state shared by the plugin, the panels and the scene root: the loaded SM1 maps (in memory only), the library, the tool and the snap
/// settings. The open document lives in <see cref="MapSessionHost"/> (the core's session); Godot nodes only view it.
/// </summary>
public static class MapHost
{
    static readonly Dictionary<string, Sm1Source> Loaded = new(StringComparer.OrdinalIgnoreCase);
    static MapLibrary? _library;

    public static MapmakerRoot? Root { get; private set; }
    public static MapmakerPlugin? Plugin { get; set; }

    /// <summary>The SM1 map shown as the pick view / backdrop.</summary>
    public static Sm1Source? Sm1 { get; set; }

    public static Tool Tool { get; set; } = Tool.Select;
    /// <summary>Picked SM1 placements (index into the map's object list; one part of a composite placement, or -1 = whole) waiting for "Extract to prefab".</summary>
    public static readonly List<PickRef> PickedSm1 = new();
    /// <summary>The library item armed for placement: an asset or a prefab.</summary>
    public static LibraryItem? Armed { get; set; }
    /// <summary>True while the dock starts a new empty map after the "Not a Mapmaker map" dialog; placement clicks are ignored meanwhile.</summary>
    public static bool StartingEmptyMap { get; set; }

    // Snap starts off (owner 2026-11-27: "by default snap mode should be off"): every editor session and every map opened or started begins with grid and angle snap off (the
    // Edit mode's move and scale use the grid snap, its rotate the angle snap), and so does Godot's own Use Snap of the 3D view. A box ticked stays on until the editor closes or
    // another map is opened; nothing is saved. The steps keep their values.
    public static bool SnapGrid { get; set; }
    public static float GridStep { get; set; } = 0.25f;
    public static bool SnapAngle { get; set; }
    public static float AngleStep { get; set; } = 15f;

    /// <summary>The snap settings went back to off (a map was opened or started); the dock unticks its boxes.</summary>
    public static event Action? SnapReset;

    /// <summary>Every snap kind off: the Mapmaker's grid and angle snap and Godot's own Use Snap (<see cref="EditorSnap"/>).</summary>
    public static void ResetSnap()
    {
        SnapGrid = false; SnapAngle = false;
        EditorSnap.SwitchOffBuiltIn();
        SnapReset?.Invoke();
    }

    public static event Action? RootChanged;
    public static event Action? Sm1Changed;
    public static event Action? PickedChanged;
    public static event Action? LibraryChanged;

    /// <summary>The library folder: the plugin setting, else the core's default under %LOCALAPPDATA%\sm-reimpl\mapmaker.</summary>
    public static MapLibrary Library
    {
        get => _library ??= new MapLibrary(null);
        set { _library = value; ContentFactory.ForgetAssets(); LibraryChanged?.Invoke(); }
    }

    public static void NotifyLibraryChanged() { ContentFactory.ForgetAssets(); LibraryChanged?.Invoke(); }
    public static void NotifySm1Changed() => Sm1Changed?.Invoke();
    public static void NotifyPickedChanged() => PickedChanged?.Invoke();

    public static void AttachRoot(MapmakerRoot root) { Root = root; RootChanged?.Invoke(); }
    public static void DetachRoot(MapmakerRoot root) { if (Root == root) { Root = null; RootChanged?.Invoke(); } }

    /// <summary>Drops every held resource (maps, templates, glTF copies) so nothing outlives the editor session.</summary>
    public static void Shutdown()
    {
        foreach (var s in Loaded.Values) s.Release();
        Loaded.Clear(); Sm1 = null; PickedSm1.Clear(); Armed = null;
        ContentFactory.ForgetAssets();
        RootChanged = null; Sm1Changed = null; PickedChanged = null; LibraryChanged = null; SnapReset = null;
        SnapGrid = false; SnapAngle = false;   // a plugin reload in the same editor starts with snap off too
    }

    public static Sm1Source? Sm1For(string map) => Loaded.GetValueOrDefault(map);
    public static IEnumerable<string> LoadedMaps => Loaded.Keys;

    /// <summary>Test aid: forgets a loaded SM1 map (the self test checks that placed pieces come back when it is loaded again).</summary>
    public static void Forget(string map)
    {
        if (Loaded.Remove(map, out var s)) { if (Sm1 == s) Sm1 = null; s.Release(); }
    }

    /// <summary>Keeps a loaded map. With <paramref name="makeCurrent"/> false (a background load for the library or for placed pieces) it does not become the pick / backdrop map.</summary>
    public static void Register(Sm1Source source, bool makeCurrent = true)
    {
        Loaded[source.Map] = source;
        if (makeCurrent) Sm1 = source;
        Sm1Changed?.Invoke();
    }
}

/// <summary>A row of the library browser.</summary>
public sealed record LibraryItem(LibraryKind Kind, string Id, string Label, string? Map = null)
{
    /// <summary>A map heading in the SM1 list (not an item; not selectable).</summary>
    public bool IsHeader => Kind == LibraryKind.Sm1 && Id.Length == 0;
    public string Key => Kind switch { LibraryKind.Sm1 => $"sm1:{Map}:{Id}", LibraryKind.Asset => $"asset:{Id}", LibraryKind.Piece => $"piece:{Id}", _ => $"prefab:{Id}" };
}

public enum LibraryKind { Sm1, Asset, Prefab, Piece }

/// <summary>A picked SM1 placement: <paramref name="Object"/> indexes the map's objects; <paramref name="Part"/> is one of its nearest-LOD parts, or -1 for the whole.</summary>
public readonly record struct PickRef(int Object, int Part = -1);
