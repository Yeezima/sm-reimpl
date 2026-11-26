using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>A tree whose rows can be dragged onto other rows (or onto empty space). A row's metadata is an object id (o3) or a group id (g1).</summary>
[Tool]
public partial class SceneRowTree : Tree
{
    /// <summary>Is dropping on this row (null: empty space) allowed? Receives the dragged ids.</summary>
    public Func<List<string>, string?, bool>? CanDropOn;
    public Action<List<string>, string?>? DroppedOn;

    public override Variant _GetDragData(Vector2 atPosition)
    {
        var ids = new List<string>();
        for (var it = GetNextSelected(null); it is not null; it = GetNextSelected(it)) ids.Add(it.GetMetadata(0).AsString());
        if (GetItemAtPosition(atPosition) is { } hit && hit.GetMetadata(0).AsString() is { } id && !ids.Contains(id)) ids = [id];
        if (ids.Count == 0) return default;
        SetDragPreview(new Label { Text = ids.Count == 1 ? ids[0] : $"{ids.Count} items" });
        return Variant.From(new Godot.Collections.Array<string>(ids));
    }

    static List<string>? Dragged(Variant data) => data.VariantType == Variant.Type.Array ? data.As<Godot.Collections.Array<string>>().ToList() : null;

    string? Target(Vector2 at) => GetItemAtPosition(at) is { } it ? it.GetMetadata(0).AsString() : null;

    public override bool _CanDropData(Vector2 atPosition, Variant data) => Dragged(data) is { Count: > 0 } ids && CanDropOn?.Invoke(ids, Target(atPosition)) == true;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (Dragged(data) is { Count: > 0 } ids) DroppedOn?.Invoke(ids, Target(atPosition));
    }
}

/// <summary>
/// The Scene tab of the dock (docs/mapmaker.md "Nesting and groups"): the Objects list, where any object can be dragged onto any other to make it a child (or onto empty
/// space to unparent it), and the Groups list, where named sets of objects (and smaller groups) are made, changed and selected as a whole.
/// </summary>
public partial class MapmakerDock
{
    const int SceneRowCap = 2000;

    readonly SceneRowTree _objectTree = new() { HideRoot = true, SelectMode = Tree.SelectModeEnum.Multi, DropModeFlags = (int)Tree.DropModeFlagsEnum.OnItem, CustomMinimumSize = new Vector2(250, 170), SizeFlagsHorizontal = SizeFlags.ExpandFill };
    readonly SceneRowTree _groupTree = new() { HideRoot = true, SelectMode = Tree.SelectModeEnum.Multi, DropModeFlags = (int)Tree.DropModeFlagsEnum.OnItem, CustomMinimumSize = new Vector2(250, 170), SizeFlagsHorizontal = SizeFlags.ExpandFill };
    readonly LineEdit _groupName = new() { PlaceholderText = "Group name", SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "The name for New group or Rename." };
    readonly Label _sceneInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0) };
    readonly HashSet<string> _collapsed = new();
    object? _sceneKey;
    int _sceneVersion = -1;
    bool _sceneSyncing;
    AcceptDialog? _parentPicker;
    ItemList? _parentList;
    LineEdit? _parentFilter;
    readonly List<string> _parentCandidates = new();

    /// <summary>Test aid: the Scene tab's trees.</summary>
    public SceneRowTree ObjectTree => _objectTree;
    public SceneRowTree GroupTree => _groupTree;

    Control SceneTab()
    {
        var v = Page();
        Section(v, "Objects", "Drag an object onto another to make it a child (it then follows its parent). Drag onto empty space to unparent. Works for any kind of object. Dragging in Godot's Scene dock does the same.");
        HiddenFilterRow(v);
        v.AddChild(_objectTree);
        v.AddChild(Row(Btn("Make child of...", ShowParentPicker, "Choose the parent for the selected objects."), Btn("Unparent", () => _plugin.SetParentOf(_plugin.SelectedIds().ToList(), null), "Takes the selected objects out of their parent; they stay where they are.")));
        Section(v, "Groups", "A group is a named set of objects you select together. Click an object to select only it; click a group (or double-click one of its objects in the 3D view) to select the whole group. Groups can hold smaller groups. They are only for editing: nothing changes in the game.");
        v.AddChild(_groupTree);
        v.AddChild(_groupName);
        v.AddChild(Row(Btn("New group from selection", () => { var r = _plugin.GroupSelected(_groupName.Text); if (r.Ok) _groupName.Text = ""; }, "Makes a group of the objects selected in the 3D view."), Btn("Rename", RenameGroup, "Renames the selected group row to the name typed above.")));
        v.AddChild(Row(Btn("Add selected", () => WithGroup(g => _plugin.AddSelectedToGroup(g)), "Adds the objects selected in the 3D view to the selected group row."), Btn("Remove selected", () => _plugin.RemoveSelectedFromGroups(), "Takes the selected objects out of their group.")));
        v.AddChild(Row(Btn("Ungroup", () => WithGroup(g => _plugin.Do("Ungroup", MapmakerPlugin.Op("ungroup", ("id", g)))), "Dissolves the selected group; its members stay (inside the group around it, if any)."), Btn("Delete group", () => WithGroup(g => _plugin.Do("Delete group", MapmakerPlugin.Op("delete-group", ("id", g)))), "Deletes the group and the smaller groups in it. The objects stay.")));
        v.AddChild(_sceneInfo);

        _objectTree.MultiSelected += (_, _, _) => SceneRowsSelected(_objectTree);
        _groupTree.MultiSelected += (_, _, _) => SceneRowsSelected(_groupTree);
        _objectTree.ItemCollapsed += RememberCollapsed; _groupTree.ItemCollapsed += RememberCollapsed;
        _groupTree.ItemActivated += () => { if (SelectedRow(_groupTree) is { } id && MapGroup.IsGroupId(id)) { _groupName.Text = _plugin.Session?.Document.FindGroup(id)?.Name ?? ""; _groupName.GrabFocus(); } };
        _groupName.TextSubmitted += _ => RenameGroup();

        _objectTree.CanDropOn = (ids, onto) => _plugin.Session is { } s && (onto is null ? ids.Any(i => s.Document.Find(i)?.Parent is not null) : s.Document.Find(onto) is not null && ids.Any(i => s.Document.Find(i) is not null && i != onto && !s.Document.IsDescendantOf(onto, i)));
        _objectTree.DroppedOn = (ids, onto) => _plugin.SetParentOf(ids.Where(i => !MapGroup.IsGroupId(i)), onto);
        _groupTree.CanDropOn = (ids, onto) => _plugin.Session is { } s && onto is not null && MapGroup.IsGroupId(onto) && ids.Any(i => i != onto && !(MapGroup.IsGroupId(i) && s.Document.GroupContains(i, onto)));
        _groupTree.DroppedOn = (ids, onto) => { if (onto is not null) _plugin.Do("Add to group", MapmakerPlugin.Op("group-add", ("group", onto), ("ids", MapmakerPlugin.Strs(_plugin.CoverWithGroups(ids.Where(i => !MapGroup.IsGroupId(i)).ToList()).Concat(ids.Where(MapGroup.IsGroupId)))))); };
        _plugin.DocChanged += RefreshScene;
        RefreshScene();
        return Scroll(v);
    }

    void RememberCollapsed(TreeItem item)
    {
        if (_sceneSyncing) return;
        var id = item.GetMetadata(0).AsString();
        if (item.Collapsed) _collapsed.Add(id); else _collapsed.Remove(id);
    }

    static string? SelectedRow(Tree tree) => tree.GetNextSelected(null) is { } it ? it.GetMetadata(0).AsString() : null;

    void WithGroup(Func<string, OpResult> act)
    {
        string? id = null;
        for (var it = _groupTree.GetNextSelected(null); it is not null && id is null; it = _groupTree.GetNextSelected(it))
            if (it.GetMetadata(0).AsString() is { } m && MapGroup.IsGroupId(m)) id = m;
        if (id is null) { _plugin.SetStatus("Click a group in the Groups list first."); return; }
        act(id);
    }

    void RenameGroup()
    {
        var name = _groupName.Text.Trim();
        if (name.Length == 0) { _plugin.SetStatus("Type the new name above, then press Rename."); return; }
        WithGroup(g => _plugin.Do("Rename group", MapmakerPlugin.Op("group-rename", ("id", g), ("name", name))));
    }

    /// <summary>A row was clicked: the objects of every selected row (a group stands for all of its objects) become the selection in the 3D view, so they move together.</summary>
    void SceneRowsSelected(SceneRowTree tree)
    {
        if (_sceneSyncing || _plugin.Session is null) return;
        var ids = new List<string>();
        for (var it = tree.GetNextSelected(null); it is not null; it = tree.GetNextSelected(it)) ids.Add(it.GetMetadata(0).AsString());
        if (tree == _groupTree) _objectTree.DeselectAll(); else _groupTree.DeselectAll();
        _sceneSyncing = true;
        try { _plugin.SelectMembers(ids); }
        finally { _sceneSyncing = false; }
        if (SelectedRow(tree) is { } first && MapGroup.IsGroupId(first)) _groupName.Text = _plugin.Session.Document.FindGroup(first)?.Name ?? "";
    }

    string _sceneShape = "\0";

    /// <summary>What the two lists show: every object's id, name, parent, switch and Classic flag, in order, and the groups. A move, turn or resize changes none of it.</summary>
    static string SceneShape(MapDocument? doc)
    {
        if (doc is null) return "";
        var sb = new System.Text.StringBuilder(doc.Objects.Count * 24);
        foreach (var o in doc.Objects) sb.Append(o.Id).Append('\u0001').Append(o.Name).Append('\u0001').Append(o.Parent).Append(o.Visible ? '+' : '-').Append(o.Src is { ClassicHidden: true } ? 'h' : '.').Append('\u0002');
        foreach (var g in doc.Groups) { sb.Append('\u0003').Append(g.Id).Append('\u0001').Append(g.Name); foreach (var m in g.Members) sb.Append('\u0001').Append(m); }
        return sb.ToString();
    }

    /// <summary>
    /// Rebuilds both lists when what they show changed (not on every selection change, and not on a move: the edit-hang fix, 2026-11-27, a 2000-row list was rebuilt after every
    /// transform edit).
    /// </summary>
    void RefreshScene()
    {
        var s = _plugin.Session;
        if (!ReferenceEquals(s, _sceneKey) || (s?.Version ?? -1) != _sceneVersion)
        {
            _sceneKey = s; _sceneVersion = s?.Version ?? -1;
            string shape = SceneShape(s?.Document);
            if (shape != _sceneShape)
            {
                _sceneShape = shape;
                _sceneSyncing = true;
                try { BuildObjectTree(s?.Document); BuildGroupTree(s?.Document); }
                finally { _sceneSyncing = false; }
            }
        }
        var doc = s?.Document;
        _sceneInfo.Text = doc is null ? "No map open." : $"{doc.Objects.Count} objects, {doc.Objects.Count(o => o.Parent is not null)} nested, {doc.Groups.Count} groups.";
    }

    static string RowText(MapObject o) => $"{o.Name}  ({o.Id})";

    /// <summary>One Objects row; an object that is switched off (Classic hides it, or you did) is greyed.</summary>
    TreeItem AddRow(TreeItem parent, MapObject o)
    {
        EditCost.SceneRows++;
        var item = _objectTree.CreateItem(parent);
        item.SetText(0, TreeText(o)); item.SetMetadata(0, o.Id); item.SetTooltipText(0, o.Src is { ClassicHidden: true } ? $"{o.Id}: Classic hides this object in play" : o.Id);
        if (!o.Visible) item.SetCustomColor(0, OffColour);
        return item;
    }

    void BuildObjectTree(MapDocument? doc)
    {
        _objectTree.Clear();
        if (doc is null) return;
        var root = _objectTree.CreateItem();
        int count = 0;
        if (_hiddenFilter.ButtonPressed)
        {
            // The filter lists what Classic hides, flat: a way to find those objects, switch them on and select them.
            var hidden = MapmakerPlugin.ClassicHidden(doc).ToList();
            foreach (var o in hidden.Take(SceneRowCap)) AddRow(root, o);
            if (hidden.Count == 0) { var none = _objectTree.CreateItem(root); none.SetText(0, "No objects hidden by Classic in this map."); none.SetMetadata(0, ""); none.SetSelectable(0, false); }
            else if (hidden.Count > SceneRowCap) { var more = _objectTree.CreateItem(root); more.SetText(0, $"... {hidden.Count - SceneRowCap} more (not listed)"); more.SetMetadata(0, ""); more.SetSelectable(0, false); }
            return;
        }
        // children and ids looked up once (a scan per object made the list cost the square of the map: about 0.1 s for 2000 objects after every placement)
        var ids = doc.Objects.Select(o => o.Id).ToHashSet();
        var children = doc.Objects.Where(o => o.Parent is not null).ToLookup(o => o.Parent!);
        void Add(TreeItem parent, MapObject o, int depth)
        {
            if (count >= SceneRowCap || depth > 200) return;
            count++;
            var item = AddRow(parent, o);
            foreach (var c in children[o.Id]) Add(item, c, depth + 1);
            item.Collapsed = _collapsed.Contains(o.Id);
        }
        foreach (var o in doc.Objects.Where(o => o.Parent is null || !ids.Contains(o.Parent))) Add(root, o, 0);
        if (count >= SceneRowCap) { var more = _objectTree.CreateItem(root); more.SetText(0, $"... {doc.Objects.Count - count} more (not listed; use the 3D view)"); more.SetMetadata(0, ""); more.SetSelectable(0, false); }
    }

    void BuildGroupTree(MapDocument? doc)
    {
        _groupTree.Clear();
        if (doc is null) return;
        var root = _groupTree.CreateItem();
        int count = 0;
        void AddGroup(TreeItem parent, MapGroup g, int depth)
        {
            if (count >= SceneRowCap || depth > 200) return;
            count++;
            var item = _groupTree.CreateItem(parent);
            item.SetText(0, $"{g.Name}  ({g.Id}, {doc.GroupObjects(g.Id).Count} objects)"); item.SetMetadata(0, g.Id);            foreach (var m in g.Members)
            {
                if (MapGroup.IsGroupId(m)) { if (doc.FindGroup(m) is { } sub) AddGroup(item, sub, depth + 1); }
                else if (doc.Find(m) is { } o && count < SceneRowCap)
                {
                    count++;
                    var row = _groupTree.CreateItem(item);
                    row.SetText(0, RowText(o)); row.SetMetadata(0, o.Id);
                }
            }
            item.Collapsed = _collapsed.Contains(g.Id);
        }
        foreach (var g in doc.Groups.Where(g => doc.GroupOf(g.Id) is null)) AddGroup(root, g, 0);
    }

    // ---- Make child of... ----

    void ShowParentPicker()
    {
        if (_plugin.Session is null) return;
        var ids = _plugin.SelectedIds().ToList();
        if (ids.Count == 0) { _plugin.SetStatus("Select objects first (in the 3D view or the Objects list), then choose their parent."); return; }
        if (_parentPicker is null)
        {
            _parentPicker = new AcceptDialog { Title = "Make child of...", OkButtonText = "Make child", Exclusive = true, MinSize = new Vector2I(360, 380) };
            var box = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            _parentFilter = new LineEdit { PlaceholderText = "Filter by name or id", ClearButtonEnabled = true };
            _parentList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(320, 260) };
            box.AddChild(Hint("Choose the object the selected ones become children of. Any kind of object can be a parent."));
            box.AddChild(_parentFilter); box.AddChild(_parentList);
            _parentPicker.AddChild(box);
            _parentFilter.TextChanged += _ => FillParentPicker();
            _parentList.ItemActivated += _ => _parentPicker.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            _parentPicker.Confirmed += () =>
            {
                var picked = _parentList.GetSelectedItems();
                if (picked.Length == 0 || _plugin.Session is null) return;
                _plugin.SetParentOf(_plugin.SelectedIds().ToList(), _parentCandidates[picked[0]]);
            };
            AddChild(_parentPicker);
        }
        _parentFilter!.Text = "";
        FillParentPicker();
        _parentPicker.PopupCentered();
    }

    void FillParentPicker()
    {
        if (_plugin.Session is null || _parentList is null) return;
        var doc = _plugin.Session.Document;
        var selected = _plugin.SelectedIds().ToHashSet();
        var filter = _parentFilter?.Text.Trim() ?? "";
        _parentList.Clear(); _parentCandidates.Clear();
        foreach (var o in doc.Objects)
        {
            if (selected.Contains(o.Id) || selected.Any(sel => doc.IsDescendantOf(o.Id, sel))) continue;   // not itself, not inside what moves
            if (filter.Length > 0 && !RowText(o).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _parentList.AddItem(RowText(o)); _parentCandidates.Add(o.Id);
            if (_parentCandidates.Count >= 500) break;
        }
        if (_parentList.ItemCount > 0) _parentList.Select(0);
    }
}
