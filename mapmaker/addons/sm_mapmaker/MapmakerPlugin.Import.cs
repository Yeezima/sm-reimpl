using System;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>"Import SM1 map for editing" (owner 2026-11-18): turns every drawn placement of an original map into an editable object of the open map.</summary>
public partial class MapmakerPlugin
{
    /// <summary>
    /// Imports <paramref name="map"/> into the open map (a new "Remix of ..." map when none is open) as one undoable step: a group node named after the map
    /// with a child node per placement, all in the Scene tree. Needs the SM1 map in memory (loaded here when it is not). The read-only backdrop is hidden
    /// afterwards, so the editable copy is not drawn twice. Nothing is written until the owner saves (as a new map; the file holds references only).
    /// </summary>
    public async Task<OpResult?> ImportSm1ForEditing(string map)
    {
        map = map.Trim();
        if (map.Length == 0) { SetStatus("Type the SM1 map's name first (for example mp_tdm_09)."); return null; }
        if (!EnsureSceneRoot()) { SetStatus("Opening the Mapmaker scene; press Import again."); return null; }
        SetStatus($"Reading {map} from the install...");
        try
        {
            if (MapHost.Sm1For(map) is null)
            {
                var source = await Task.Run(() => Sm1Source.Load(map));
                if (MapHost.Sm1For(map) is null) MapHost.Register(source); else source.Release();
            }
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { SetStatus($"Could not read {map}: {e.Message}"); return null; }
        // Always a new, unsaved map (owner: "save them as a new map"): with a saved map open, its content moves to an unnamed copy, so a plain Save asks
        // for a new file and never overwrites the original map file.
        if (Session is null) NewMap($"Remix of {map}");
        else if (Session.Path is not null) OpenMapFromJson(Session.Document.ToJson());
        _sm1Adapter?.Invalidate();
        var result = Do($"Import SM1 map {map}", Op("import-sm1-map", ("map", map)));
        if (result.Ok)
        {
            MapHost.Root?.ShowBackdrop(null, new System.Collections.Generic.List<string>());
            if (MapHost.Sm1For(map)?.BoundsCore() is { } b) _ = Frame3D(b.Min, b.Max);
            SetStatus(string.Join(" ", result.Warnings) + " Save asks for a new file name; the map you had open is not touched.");
        }
        return result;
    }
}
