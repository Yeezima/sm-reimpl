using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// Nesting and groups (docs/mapmaker.md "Nesting and groups"). Nesting: any object can be made a child of any other (a transform parent) from the Scene tab
/// or by dragging in Godot's Scene dock; both become <c>set-parent</c> operations, so they are saved and undoable. Groups: named sets for selecting several
/// objects at once; no transform, no effect in the game.
/// </summary>
public partial class MapmakerPlugin
{
    // ---- nesting ----------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Makes the objects children of <paramref name="parent"/> (null: no parent). Ones that would end up inside themselves are left out.</summary>
    public OpResult SetParentOf(IEnumerable<string> ids, string? parent)
    {
        if (Session is null) return OpResult.Fail("no-document", "No map is open: use New or Open first.");
        var doc = Session.Document;
        var list = ids.Distinct().Where(i => doc.Find(i) is not null && i != parent && (parent is null || !doc.IsDescendantOf(parent, i))).ToList();
        int skipped = ids.Distinct().Count() - list.Count;
        list = list.Where(i => doc.Get(i).Parent != parent).ToList();
        if (list.Count == 0)
        {
            SetStatus(skipped > 0 ? "Nothing changed: an object cannot become a child of itself or of something inside it." : "Nothing to change: already there.");
            return OpResult.Fail("empty-selection", "Nothing to change.");
        }
        var label = parent is null ? "Unparent" : $"Make child of {doc.Get(parent).Name}";
        var r = Do(label, list.Select(i => Op("set-parent", ("id", i), ("parent", parent))).ToArray());
        if (r.Ok && skipped > 0) SetStatus($"{label}: {skipped} object(s) were left out (an object cannot become a child of itself or of something inside it).");
        return r;
    }

    /// <summary>
    /// A drag in Godot's Scene dock moved nodes under other nodes. The document is the truth, so those moves become <c>set-parent</c> operations (saved,
    /// undoable) instead of being undone by the next view sync. Returns true when it handled something.
    /// </summary>
    bool ReconcileParents()
    {
        if (Session is null || MapHost.Root is not { } root) return false;
        var doc = Session.Document;
        var ops = new List<System.Text.Json.Nodes.JsonObject>();
        foreach (var o in doc.Objects)
        {
            if (!_nodes.TryGetValue(o.Id, out var node) || !IsInstanceValid(node) || node.GetParent() is not { } actual) continue;
            if (actual != root && actual is not MapmakerObject) continue;
            string? now = actual is MapmakerObject po ? po.ObjectId : null;
            if (now is not null && doc.Find(now) is null) continue;
            if (now == o.Parent) continue;
            ops.Add(Op("set-parent", ("id", o.Id), ("parent", now)));
        }
        if (ops.Count == 0) return false;
        var r = Do("Reparent", ops.ToArray());
        if (!r.Ok) SyncView(false);   // refused (a cycle, say): the nodes snap back to the document
        DocChanged?.Invoke();
        return true;
    }

    // ---- groups -----------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The members to store for a set of objects: a group whose objects are all in the set counts as one member (the biggest such groups first).</summary>
    public List<string> CoverWithGroups(IEnumerable<string> objectIds)
    {
        var rest = objectIds.Distinct().ToList();
        var result = new List<string>();
        if (Session is null) return rest;
        var doc = Session.Document;
        foreach (var (group, objs) in doc.Groups.Select(g => (g, doc.GroupObjects(g.Id))).Where(x => x.Item2.Count > 0).OrderByDescending(x => x.Item2.Count))
            if (objs.All(rest.Contains))
            {
                result.Add(group.Id);
                rest = rest.Where(r => !objs.Contains(r)).ToList();
            }
        result.AddRange(rest);
        return result;
    }

    /// <summary>A new group of the selected objects (whole groups among them count as one member each).</summary>
    public OpResult GroupSelected(string? name = null)
    {
        var members = CoverWithGroups(SelectedIds());
        if (members.Count == 0) { SetStatus("Select objects in the 3D view first, then make a group of them."); return OpResult.Fail("empty-selection", "Nothing is selected."); }
        var ops = Op("group", ("ids", Strs(members)));
        if (!string.IsNullOrWhiteSpace(name)) ops["name"] = name;
        return Do("Group", ops);
    }

    public OpResult AddSelectedToGroup(string groupId) => Do("Add to group", Op("group-add", ("group", groupId), ("ids", Strs(CoverWithGroups(SelectedIds())))));

    public OpResult RemoveSelectedFromGroups()
    {
        var ids = SelectedIds().ToList();
        if (ids.Count == 0) { SetStatus("Select objects in the 3D view first."); return OpResult.Fail("empty-selection", "Nothing is selected."); }
        return Do("Remove from group", Op("group-remove", ("ids", Strs(ids))));
    }

    /// <summary>Selects every object of the groups (and the single objects) given: a group selects as a whole and then moves together.</summary>
    public void SelectMembers(IEnumerable<string> memberIds)
    {
        if (Session is null) return;
        var doc = Session.Document;
        var ids = new List<string>();
        foreach (var m in memberIds)
            foreach (var o in MapGroup.IsGroupId(m) ? doc.GroupObjects(m) : [m])
                if (!ids.Contains(o)) ids.Add(o);
        Select(ids);
    }

    string? _lastExpanded;
    ulong _lastExpandedAt;

    /// <summary>A double-click on an object of a group in the 3D view selects the whole group (the next one, soon after, the group around it).</summary>
    void GroupDoubleClick(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, DoubleClick: true, ButtonIndex: MouseButton.Left } b || MapHost.Tool != Tool.Select || Session is null) return;
        if (!TryRay(b.Position, out _, out _)) return;
        _ = ExpandToGroup();
    }

    async Task ExpandToGroup()
    {
        for (int i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (Session is null) return;
        var picked = SelectedIds().ToList();
        if (picked.Count != 1 || Session.Document.GroupOf(picked[0]) is not { } inner) return;
        var doc = Session.Document;
        var target = inner;
        ulong now = Time.GetTicksMsec();
        if (_lastExpanded is { } last && now - _lastExpandedAt < 3000 && doc.FindGroup(last) is { } lastGroup && doc.GroupContains(lastGroup.Id, picked[0])
            && doc.GroupOf(lastGroup.Id) is { } outer) target = outer;
        _lastExpanded = target.Id; _lastExpandedAt = now;
        SelectMembers([target.Id]);
        SetStatus($"Selected group '{target.Name}' ({doc.GroupObjects(target.Id).Count} objects). Double-click again to take the group around it.");
    }
}
