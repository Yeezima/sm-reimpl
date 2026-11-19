using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>One row of the "Refresh from Classic" dialog: what Classic changed (or what the owner deleted) and whether the owner ticked it.</summary>
public sealed class RefreshRow
{
    public const string Added = "added", Removed = "removed", Changed = "changed", Deleted = "deleted", Skipped = "skipped";
    public string Section = "", Map = "", Kind = "", Key = "", Name = "", Reason = "";
    public string? Id;
    public bool Edited, Hidden, Checked;
    public bool Pickable => Section is Removed or Changed or Deleted;

    /// <summary>What <c>apply</c> names for this row: the object's id, else <c>map|kind|key</c> (a deleted item has no object).</summary>
    public string ApplyName => Id ?? $"{Map}|{Kind}|{Key}";

    public string Text
    {
        get
        {
            string what = Kind switch { "wall" => "Invisible wall", "kill" => "Kill box", "cover" => "Cover", _ => Hidden ? "Hidden object" : "Object" };
            string head = $"{what}: {(Name.Length > 0 ? Name : Key)}";
            if (Id is not null) head += $"  ({Id})";
            if (Section is Removed) head += "  - gone from Classic";
            if (Section is Changed) head += Kind == "placement" && Hidden ? "  - Classic now hides it" : "  - Classic moved or resized it";
            if (Edited) head += "  - edited by you";
            if (Section is Skipped) head += $"  - {SkipReason(Reason)}";
            return head;
        }
    }

    static string SkipReason(string r) => r switch
    {
        "too-many-objects" => "the map is full (object limit)",
        "too-many-pieces" => "too many pieces",
        "file-size" => "the map file would be too big",
        _ => r,
    };
}

/// <summary>The preview of a refresh (the core's <c>refresh</c> result) as dialog rows, and the <c>apply</c> list the ticked rows make.</summary>
public sealed class RefreshModel
{
    public List<RefreshRow> Rows { get; } = [];
    public List<string> Warnings { get; } = [];

    public IEnumerable<RefreshRow> In(string section) => Rows.Where(r => r.Section == section);
    public bool Empty => Rows.Count == 0;

    public static RefreshModel From(JsonObject refresh, IEnumerable<string>? warnings = null)
    {
        var m = new RefreshModel();
        if (warnings is not null) m.Warnings.AddRange(warnings);
        foreach (var section in new[] { RefreshRow.Added, RefreshRow.Removed, RefreshRow.Changed, RefreshRow.Deleted, RefreshRow.Skipped })
        {
            if (refresh[section] is not JsonArray list) continue;
            foreach (var n in list.OfType<JsonObject>())
            {
                var row = new RefreshRow
                {
                    Section = section, Map = Str(n, "map"), Kind = Str(n, "kind"), Key = Str(n, "key"), Name = Str(n, "name"), Reason = Str(n, "reason"),
                    Id = n["id"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null,
                    Edited = Flag(n, "edited"), Hidden = Flag(n, "hidden"),
                };
                // Default ticks: what Classic changed and the owner has not touched. Edited items and deletions stay off until chosen.
                row.Checked = row.Pickable && section != RefreshRow.Deleted && !row.Edited;
                m.Rows.Add(row);
            }
        }
        return m;
    }

    static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static bool Flag(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>The ticked removed, changed and deleted rows, as <c>apply</c> names.</summary>
    public JsonArray ApplyList(bool onlyDeleted = false) =>
        new(Rows.Where(r => r.Pickable && r.Checked && (!onlyDeleted || r.Section == RefreshRow.Deleted)).Select(r => (JsonNode)JsonValue.Create(r.ApplyName)!).ToArray());

    /// <summary>The result line after a run: what was inserted, applied and brought back.</summary>
    public static string ResultLine(JsonObject? r)
    {
        if (r is null) return "Refreshed from Classic.";
        int Count(string k, Func<JsonObject, bool>? f = null) => r[k] is JsonArray a ? a.OfType<JsonObject>().Count(x => f is null || f(x)) : 0;
        bool Flag(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        int added = Count("added"), removed = Count("removed", x => Flag(x, "applied")), changed = Count("changed", x => Flag(x, "applied")), restored = Count("deleted", x => Flag(x, "restored")), skipped = Count("skipped");
        int left = Count("removed", x => !Flag(x, "applied")) + Count("changed", x => !Flag(x, "applied"));
        string line = $"Refreshed from Classic: {added} added, {changed} changed, {removed} removed, {restored} restored.";
        if (left > 0) line += $" {left} change(s) left for you to decide.";
        if (skipped > 0) line += $" {skipped} skipped (the map is full).";
        return line + " Undo (Ctrl+Z) takes it all back.";
    }
}

/// <summary>Editor side of the Classic import (docs/mapmaker.md "Classic link"): the look, hidden objects, Refresh from Classic and the dock's Undo button.</summary>
public partial class MapmakerPlugin
{
    public const string NoInstallMessage = "The Space Marine install was not found, so the map cannot be refreshed from Classic. Set SM_GAME_DIR or pass --game-dir.";

    /// <summary>The editor's undo: the open Mapmaker scene's own history (Godot 4.7's EditorUndoRedoManager has no undo method, only the per-scene history behind it).</summary>
    public bool UndoLast()
    {
        var ur = GetUndoRedo();
        int id = MapHost.Root is { } root ? ur.GetObjectHistoryId(root) : (int)EditorUndoRedoManager.SpecialHistory.GlobalHistory;
        var history = ur.GetHistoryUndoRedo(id);
        if (history is null || !history.HasUndo()) { SetStatus("Nothing to undo."); return false; }
        return history.Undo();
    }

    /// <summary>The Classic map the open map is drawn with in play, or null.</summary>
    public string? CurrentLook => Session?.Document.Look?.Sm1Map;

    /// <summary>Sets (or, with null, clears) the look as one undo step; nothing happens when it already holds.</summary>
    public OpResult? SetLook(string? map)
    {
        if (Session is null) { SetStatus("Open or make a map first."); return null; }
        if (Session.Document.Look?.Sm1Map == map) return null;
        return Do(map is null ? "Look: none" : $"Look: {map}", Op("set-look", ("sm1Map", map is null ? null : JsonValue.Create(map))));
    }

    /// <summary>What a refresh would do, without changing anything (a dry run through the core op).</summary>
    public OpResult PreviewRefresh() =>
        Session is null ? OpResult.Fail("no-document", "No map is open: use New or Open first.")
            : Session.Apply(Op("refresh-from-classic", ("preview", true)), dryRun: true);

    /// <summary>Runs the refresh as one undo step: inserts what Classic added, applies the named removed and changed items (and, with <paramref name="allUnedited"/>, every one not edited).</summary>
    public OpResult ApplyRefresh(JsonArray apply, bool allUnedited)
    {
        var op = Op("refresh-from-classic", ("preview", false), ("apply", apply), ("applyAllUnedited", allUnedited));
        var r = Do("Refresh from Classic", op);
        if (r.Ok) SetStatus(RefreshModel.ResultLine(r.Refresh));
        return r;
    }

    /// <summary>The counts line of an import: what the new map holds from each Classic kind, counted from the objects linked to that map.</summary>
    public static string ImportSummary(MapDocument doc, string map)
    {
        
        var made = doc.Objects.Where(o => o.Src?.Map == map).ToList();
        int Kind(string k) => made.Count(o => o.Src?.Kind == k && !o.Src.ClassicHidden);
        int hidden = made.Count(o => o.Src is { ClassicHidden: true });
        return $"Imported {Kind("placement")} object(s), {Kind("wall")} invisible wall(s), {Kind("kill")} kill box(es), {Kind("cover")} cover and {hidden} hidden by Classic.";
    }

    /// <summary>Objects switched off because Classic hides them, in document order.</summary>
    public static IEnumerable<MapObject> ClassicHidden(MapDocument doc) => doc.Objects.Where(o => o.Src is { ClassicHidden: true });
}
