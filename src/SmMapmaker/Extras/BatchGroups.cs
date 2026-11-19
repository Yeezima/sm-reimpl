using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// The group operations (docs/mapmaker.md "Groups"): named sets of objects and smaller groups for selecting several things at once. They change nothing
/// in the map itself: no transforms, no parenting, nothing the compiler reads.
/// </summary>
internal sealed partial class Batch
{
    void CheckMember(string id)
    {
        if (MapGroup.IsGroupId(id)) Doc.GetGroup(id); else Doc.Get(id);
    }

    void SetMembers(MapGroup g, IEnumerable<string> members) => Doc.Groups[Doc.Groups.FindIndex(x => x.Id == g.Id)] = g with { Members = members.ToList() };

    /// <summary>Puts the ids into the group (taking them out of the group that holds them now), after refusing a group inside itself.</summary>
    void Move(MapGroup target, IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            CheckMember(id);
            if (MapGroup.IsGroupId(id) && Doc.GroupContains(id, target.Id)) throw new MapException("group-cycle", $"Group '{target.Id}' is inside '{id}'.", id);
        }
        Doc.DetachFromGroups(ids);
        var now = Doc.GetGroup(target.Id);
        SetMembers(now, now.Members.Concat(ids));
    }

    /// <summary>group {ids: [objects and groups], name?}: a new group holding them (each leaves the group that held it).</summary>
    void CreateGroup(JsonObject op)
    {
        var ids = Ids(op);
        foreach (var id in ids) CheckMember(id);
        if (Doc.Groups.Count >= MapDocument.MaxGroups) throw new MapException("too-big", $"A map holds at most {MapDocument.MaxGroups} groups.");
        var id0 = Doc.NewGroupId();
        var name = Json.Str(op["name"], "name", "bad-op", nullable: true);
        if (string.IsNullOrWhiteSpace(name)) name = "Group " + id0[1..];
        if (name.Length > MapDocument.MaxNameLength) throw new MapException("bad-op", "A group name is too long.");
        // the new group sits where its first member was, so nesting a selection inside a group keeps it there
        var parent = Doc.GroupOf(ids[0]);
        if (parent is not null && ids.Any(i => MapGroup.IsGroupId(i) && Doc.GroupContains(i, parent.Id))) parent = null;   // the new group would end up inside itself
        Doc.DetachFromGroups(ids);
        Doc.Groups.Add(new MapGroup(id0, name, ids));
        if (parent is not null && Doc.FindGroup(parent.Id) is { } p) SetMembers(p, p.Members.Append(id0));
        Created.Add(id0);
    }

    void RenameGroup(JsonObject op)
    {
        var g = Doc.GetGroup(ReqId(op));
        var name = Json.ReqStr(op, "name", "group-rename", "bad-op");
        if (string.IsNullOrWhiteSpace(name)) throw new MapException("bad-op", "A group name must not be empty.", g.Id);
        if (name.Length > MapDocument.MaxNameLength) throw new MapException("bad-op", "A group name is too long.", g.Id);
        Doc.Groups[Doc.Groups.FindIndex(x => x.Id == g.Id)] = g with { Name = name };
    }

    /// <summary>group-add {group, ids}: puts objects or groups into a group.</summary>
    void GroupAdd(JsonObject op) => Move(Doc.GetGroup(ReqId(op, "group")), Ids(op));

    /// <summary>group-remove {ids}: takes objects or groups out of the group that holds them (they stay in the map, ungrouped).</summary>
    void GroupRemove(JsonObject op)
    {
        var ids = Ids(op);
        foreach (var id in ids) CheckMember(id);
        Doc.DetachFromGroups(ids);
    }

    /// <summary>ungroup {id}: dissolves the group; its members go to the group around it, or become ungrouped. The objects stay.</summary>
    void Ungroup(JsonObject op)
    {
        var g = Doc.GetGroup(ReqId(op));
        var outer = Doc.GroupOf(g.Id);
        var members = g.Members.ToList();
        Doc.Groups.RemoveAll(x => x.Id == g.Id);
        if (outer is not null && Doc.FindGroup(outer.Id) is { } o)
            SetMembers(o, o.Members.SelectMany(m => m == g.Id ? members : [m]));
    }

    /// <summary>delete-group {id}: removes the group and the smaller groups in it; the objects stay.</summary>
    void DeleteGroup(JsonObject op)
    {
        var g = Doc.GetGroup(ReqId(op));
        var gone = Doc.Groups.Where(x => Doc.GroupContains(g.Id, x.Id)).Select(x => x.Id).ToHashSet();
        Doc.DetachFromGroups(gone);
        Doc.Groups.RemoveAll(x => gone.Contains(x.Id));
    }
}
