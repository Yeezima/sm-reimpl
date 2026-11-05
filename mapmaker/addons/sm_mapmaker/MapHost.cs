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

    public static bool SnapGrid { get; set; } = true;
    public static float GridStep { get; set; } = 0.25f;
    public static bool SnapAngle { get; set; } = true;
    public static float AngleStep { get; set; } = 15f;

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
        RootChanged = null; Sm1Changed = null; PickedChanged = null; LibraryChanged = null;
    }

    public static Sm1Source? Sm1For(string map) => Loaded.GetValueOrDefault(map);
    public static IEnumerable<string> LoadedMaps => Loaded.Keys;

    /// <summary>Test aid: forgets a loaded SM1 map (the self test checks that placed pieces come back when it is loaded again).</summary>
    public static void Forget(string map)
    {
        if (Loaded.Remove(map, out var s)) { if (Sm1 == s) Sm1 = null; s.Release(); }
    }

    public static void Register(Sm1Source source)
    {
        Loaded[source.Map] = source;
        Sm1 = source;
        Sm1Changed?.Invoke();
    }
}

/// <summary>A row of the library browser.</summary>
public sealed record LibraryItem(LibraryKind Kind, string Id, string Label, string? Map = null)
{
    public string Key => Kind switch { LibraryKind.Sm1 => $"sm1:{Map}:{Id}", LibraryKind.Asset => $"asset:{Id}", _ => $"prefab:{Id}" };
}

public enum LibraryKind { Sm1, Asset, Prefab }

/// <summary>A picked SM1 placement: <paramref name="Object"/> indexes the map's objects; <paramref name="Part"/> is one of its nearest-LOD parts, or -1 for the whole.</summary>
public readonly record struct PickRef(int Object, int Part = -1);
