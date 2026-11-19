using System;
using System.Collections.Generic;
using Godot;

namespace SmMapmakerEditor;

/// <summary>The SM1 library's plumbing (owner 2026-11-19): loading a map for what is placed from it, and removing the opened SM1 backdrop.</summary>
public partial class MapmakerPlugin
{
    /// <summary>
    /// Loads <paramref name="map"/> from the install in the background (without making it the backdrop) so its pieces can be drawn and previewed; the library
    /// lists every map without it, so a definition can be placed from a map that was never opened.
    /// </summary>
    public void RequestSm1Load(string map)
    {
        if (MapHost.Sm1For(map) is not null) return;
        ContentFactory.MissingMaps.Add(map);
        LoadMissingSm1();
    }

    /// <summary>
    /// The Map tab's "Remove SM1 backdrop": takes the opened original map out of the view for good (not only hidden): the mesh nodes are freed, the pick tool and
    /// the restart resume forget it. Objects the map holds, imported or placed, are not touched. One undo step puts the backdrop back.
    /// </summary>
    public void RemoveBackdrop()
    {
        var root = MapHost.Root;
        if (root?.Backdrop is null || root.BackdropSource is not { } source) { SetStatus("There is no SM1 backdrop to remove."); return; }
        string map = source.Map;
        bool shown = root.BackdropVisible;
        BackdropRemoveStep();
        var ur = GetUndoRedo();
        ur.CreateAction($"Remove SM1 backdrop {map}", UndoRedo.MergeMode.Disable, root);
        ur.AddDoMethod(this, MethodName.BackdropRemoveStep);
        ur.AddUndoMethod(this, MethodName.BackdropRestoreStep, map, shown);
        ur.CommitAction(false);
        SetStatus($"Removed the SM1 backdrop {map}. Your objects are untouched; Undo (Ctrl+Z) brings it back.");
    }

    /// <summary>Do and redo of <see cref="RemoveBackdrop"/>.</summary>
    public void BackdropRemoveStep()
    {
        if (MapHost.Root is not { } root) return;
        var was = root.BackdropSource;
        root.ShowBackdrop(null, new List<string>());
        if (was is not null && MapHost.Sm1 == was) MapHost.Sm1 = null;
        MapHost.PickedSm1.Clear();
        if (MapHost.Tool == Tool.PickSm1) MapHost.Tool = Tool.Select;
        RefreshPicked();
        _dock?.Refresh();
    }

    /// <summary>Undo of <see cref="RemoveBackdrop"/>: the map is still held in memory (placed pieces draw from it), so the backdrop is rebuilt from it.</summary>
    public void BackdropRestoreStep(string map, bool shown)
    {
        if (MapHost.Root is not { } root) return;
        if (MapHost.Sm1For(map) is not { } source) { SetStatus($"The SM1 map {map} is no longer loaded; open it again to bring the backdrop back."); return; }
        root.ShowBackdrop(source, new List<string>());
        root.BackdropVisible = shown;
        MapHost.Sm1 = source;
        _dock?.Refresh();
    }
}
