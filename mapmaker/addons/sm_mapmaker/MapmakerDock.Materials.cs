using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using SmClient;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// Library > Materials and Test > Share > pack (docs/mapmaker.md "Materials (editor)"): the map's reusable materials (new, edit in the node editor, rename there, delete), standalone
/// pictures imported into the map (and wired into the chosen material by their file names), assigning a material to the selected blocks, ramps and columns (object level; one undo
/// step), replacing one material by another everywhere in the map, and packing the saved map with its pictures into one <c>.smpack</c> file / opening one.
/// The sections are added to the Library and Test pages when the dock is ready, so the dock's main file needs no change.
/// </summary>
public partial class MapmakerDock
{
    readonly ItemList _matList = new() { CustomMinimumSize = new Vector2(0, 110), SelectMode = ItemList.SelectModeEnum.Single, TooltipText = "The materials of this map. Double click opens the node editor." };
    readonly Label _matInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0) };
    readonly PopupMenu _replaceMenu = new();
    MaterialEditor? _materialEditor;
    bool _materialsAdded;

    public override void _Notification(int what)
    {
        if (what != NotificationReady || _materialsAdded) return;
        _materialsAdded = true;
        AddMaterialsSection();
        AddPackButtons();
        _plugin.DocChanged += RefreshMaterials;
        _plugin.DocChanged += ApplyMaterialsToView;
        _plugin.EditChanged += ApplyMaterialsToView;   // Edit mode: a drag's preview shape (and cancel) are drawn with the material too
        RefreshMaterials();
        if (MaterialTest.Wanted) GetTree().CreateTimer(3.0).Timeout += () => _ = new MaterialTest(_plugin, this).Run();
    }

    /// <summary>The node editor window (made on first use).</summary>
    public MaterialEditor MaterialEditorWindow
    {
        get
        {
            if (_materialEditor is null) { _materialEditor = new MaterialEditor(_plugin); AddChild(_materialEditor); }
            return _materialEditor;
        }
    }

    void AddMaterialsSection()
    {
        // After the Library list's info line (Library tab), before Import.
        if (_libInfo.GetParent() is not VBoxContainer v) return;
        int at = _libInfo.GetIndex() + 1;
        var box = new VBoxContainer();
        Section(box, "Materials", "Pictures and node materials for blocks, ramps and columns. Make a material, import its pictures in its node editor (Edit...), then assign it to the selected pieces.");
        _matList.ItemActivated += i => EditMaterial();
        _matList.ItemSelected += _ => RefreshMaterialInfo();
        box.AddChild(_matList);
        var newMat = Btn("New material", () => NewMaterial(), "Adds a material (a plain grey surface until pictures are linked) and opens its node editor.");
        var edit = Btn("Edit...", EditMaterial, "Opens the chosen material in the node editor.");
        newMat.SizeFlagsHorizontal = SizeFlags.ExpandFill; edit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddChild(Row(newMat, edit));
        var assign = Btn("Assign to selection", () => AssignSelected(SelectedMaterial), "Draws the selected blocks, ramps and columns with the chosen material (replacing the one they had). One undo step.");
        var clear = Btn("Clear", () => AssignSelected(null), "The selected pieces go back to the default look.");
        assign.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddChild(Row(assign, clear));
        var replace = Btn("Replace in map...", AskReplace, "Every piece drawn with the chosen material gets another material instead. One undo step.");
        var delete = Btn("Delete material", DeleteMaterial, "Removes the chosen material (refused while a piece uses it).");
        replace.SizeFlagsHorizontal = SizeFlags.ExpandFill; delete.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddChild(Row(replace, delete));
        box.AddChild(Btn("Remove unused images", RemoveUnusedImages, "Takes pictures that no material uses out of the map."));
        _matInfo.AddThemeColorOverride("font_color", HintColour); _matInfo.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(_matInfo);
        _replaceMenu.IdPressed += id => ReplaceWith((int)id);
        AddChild(_replaceMenu);
        v.AddChild(box);
        v.MoveChild(box, at);
    }

    void AddPackButtons()
    {
        // Test tab, Share: after the share line.
        if (_shareInfo.GetParent() is not VBoxContainer v) return;
        int at = _shareInfo.GetIndex();
        var pack = Btn("Pack for sharing (.smpack)", () => PackMap(null), "Saves the map, then writes one .smpack file beside it: the map and every picture it uses. Send that one file.");
        var open = Btn("Open .smpack...", () => Pick("Open a shared map (.smpack)", FileDialog.FileModeEnum.OpenFile, MapHost.Library.MapsDir, ["*.smpack ; Mapmaker shared map"], p => OpenPack(p)), "Unpacks a shared map (and its pictures) into your maps folder and opens it.");
        pack.SizeFlagsHorizontal = SizeFlags.ExpandFill; open.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var row = Row(pack, open);
        v.AddChild(row);
        v.MoveChild(row, at);
    }

    // ---- the list ----

    string? SelectedMaterial => _matList.GetSelectedItems() is { Length: > 0 } sel ? _matList.GetItemMetadata(sel[0]).AsString() : null;

    void RefreshMaterials()
    {
        string? keep = SelectedMaterial;
        _matList.Clear();
        var doc = _plugin.Session?.Document;
        if (doc is null) { _matInfo.Text = "No map open."; return; }
        foreach (var m in doc.Materials)
        {
            int users = doc.Objects.Count(o => o.Material == m.Id);
            int i = _matList.AddItem($"{m.Name}  ({users} piece{(users == 1 ? "" : "s")})");
            _matList.SetItemMetadata(i, m.Id);
            _matList.SetItemTooltip(i, $"{m.Name} ({m.Id}): {m.Nodes.Count} nodes, {m.Links.Count} links");
            if (m.Id == keep) _matList.Select(i);
        }
        RefreshMaterialInfo();
    }

    void RefreshMaterialInfo()
    {
        var doc = _plugin.Session?.Document;
        if (doc is null) return;
        var used = doc.Materials.SelectMany(m => m.ImageIds()).ToHashSet();
        string images = doc.Images.Count == 0 ? "No pictures in this map." : $"{doc.Images.Count} picture(s), {doc.Images.Count(i => !used.Contains(i.Id))} unused: " + string.Join(", ", doc.Images.Take(6).Select(i => i.Name)) + (doc.Images.Count > 6 ? ", ..." : "");
        string chosen = SelectedMaterial is { } id && doc.FindMaterial(id) is { } g ? $"{g.Name}: " + Describe(g) + ". " : "";
        _matInfo.Text = chosen + images;
    }

    static string Describe(MaterialGraph g)
    {
        try
        {
            var plan = MaterialPlan.Of(g.Resolve());
            return plan.Textures.Count == 0 ? "no pictures linked" : string.Join(", ", plan.Textures.Select(t => t.Feature.ToString() + (t.FlipGreen ? " (DirectX)" : "")));
        }
        catch (MapException e) { return e.Message; }
    }

    // ---- steps ----

    /// <summary>New material (one undo step); returns its id and opens the node editor (<paramref name="open"/>).</summary>
    public string? NewMaterial(string? name = null, bool open = true)
    {
        if (_plugin.Session is null) { _status.Text = "Open or create a map first."; return null; }
        var op = MapmakerPlugin.Op("material-add");
        if (name is not null) op["name"] = name;
        var r = _plugin.Do("New material", op);
        if (!r.Ok || r.CreatedMaterials.Count == 0) return null;
        var id = r.CreatedMaterials[0];
        RefreshMaterials();
        SelectMaterialRow(id);
        if (open) MaterialEditorWindow.Open(id);
        return id;
    }

    public void SelectMaterialRow(string id)
    {
        for (int i = 0; i < _matList.ItemCount; i++) if (_matList.GetItemMetadata(i).AsString() == id) { _matList.Select(i); break; }
        RefreshMaterialInfo();
    }

    void EditMaterial()
    {
        if (SelectedMaterial is not { } id) { _status.Text = "Choose a material in the Materials list (or press New material)."; return; }
        MaterialEditorWindow.Open(id);
    }

    /// <summary>
    /// Adds the pictures to the map (<c>image-add</c>) and, when <paramref name="wireInto"/> names a material, wires each into it by its file name (<see cref="MaterialPlan.WireByName"/>):
    /// all in one batch, so one undo step. The result's status line says what was linked.
    /// </summary>
    public OpResult ImportImages(IReadOnlyList<string> paths, string? wireInto)
    {
        if (_plugin.Session is not { } s) return OpResult.Fail("no-document", "Open or create a map first.");
        var ops = new List<JsonObject>();
        var wired = new List<(string Id, string File)>();
        foreach (var p in paths)
        {
            ops.Add(MapmakerPlugin.Op("image-add", ("path", p)));
            try
            {
                if (new FileInfo(p).Length <= MapImage.MaxBytes) wired.Add((MapImage.Inspect(File.ReadAllBytes(p), Path.GetFileName(p)).Id, Path.GetFileName(p)));
            }
            catch (Exception e) when (e is MapException or IOException or UnauthorizedAccessException) { }   // image-add reports it
        }
        if (wireInto is not null && s.Document.FindMaterial(wireInto) is { } g && g.Nodes.FirstOrDefault(n => n.Type == NodeTypes.PbrSurface) is { } surface)
        {
            // a picture already in the map and already used by this material is not wired a second time
            var fresh = wired.Where(w => !g.ImageIds().Contains(w.Id)).DistinctBy(w => w.Id).ToList();
            string? mapping = g.Links.FirstOrDefault(l => l.In == "vector" && g.Node(l.From)?.Type == NodeTypes.Mapping)?.From;
            float y0 = g.Nodes.Count == 0 ? -200 : g.Nodes.Min(n => n.Position.Y);
            if (MaterialPlan.WireByName(g.Id, surface.Id, g.Nodes.Select(n => n.Id).ToList(), fresh, mapping, surface.Position.X - 700, y0) is { } edit) ops.Add(edit);
        }
        var r = _plugin.Do(paths.Count == 1 ? "Import image" : $"Import {paths.Count} images", ops.ToArray());
        if (r.Ok) _status.Text = $"Added {r.CreatedImages.Count} picture(s)" + (wireInto is not null && ops.Count > paths.Count ? $" and wired them into {s.Document.FindMaterial(wireInto)?.Name}: {Describe(s.Document.FindMaterial(wireInto)!)}." : ".");
        RefreshMaterials();
        if (_materialEditor is { Visible: true }) _materialEditor.Rebuild();
        return r;
    }

    /// <summary>Assigns <paramref name="materialId"/> (null: clears) to the selected drawn pieces; other selected objects are left out and named in the status line.</summary>
    public OpResult AssignSelected(string? materialId, IReadOnlyList<string>? ids = null)
    {
        if (_plugin.Session is not { } s) return OpResult.Fail("no-document", "Open or create a map first.");
        var chosen = (ids ?? _plugin.SelectedIds().ToList()).Select(s.Document.Find).OfType<MapObject>().ToList();
        var drawn = chosen.Where(o => o.Piece is { } p && PieceKind.IsDrawn(p.Kind)).Select(o => o.Id).ToList();
        if (drawn.Count == 0) { _status.Text = "Select a block, ramp or column first (only drawn pieces take a material)."; return OpResult.Fail("empty-selection", _status.Text); }
        if (materialId is not null && s.Document.FindMaterial(materialId) is null) { _status.Text = "Choose a material in the Materials list."; return OpResult.Fail("unknown-material", _status.Text); }
        var r = _plugin.Do(materialId is null ? "Clear material" : "Assign material", MapmakerPlugin.Op("set-material", ("ids", MapmakerPlugin.Strs(drawn)), ("material", materialId)));
        if (r.Ok && chosen.Count > drawn.Count) _status.Text += $" ({chosen.Count - drawn.Count} selected object(s) are not drawn in the game and were left out.)";
        RefreshMaterials();
        return r;
    }

    void AskReplace()
    {
        var doc = _plugin.Session?.Document;
        if (doc is null || SelectedMaterial is not { } from) { _status.Text = "Choose the material to replace in the list."; return; }
        _replaceMenu.Clear();
        foreach (var (m, i) in doc.Materials.Select((m, i) => (m, i)).Where(x => x.m.Id != from)) _replaceMenu.AddItem(m.Name, i);
        _replaceMenu.AddItem("(default look)", 9999);
        _replaceMenu.Position = (Vector2I)DisplayServer.MouseGetPosition();
        _replaceMenu.Popup();
    }

    void ReplaceWith(int index)
    {
        var doc = _plugin.Session?.Document;
        if (doc is null || SelectedMaterial is not { } from) return;
        ReplaceMaterial(from, index == 9999 ? null : doc.Materials[index].Id);
    }

    /// <summary>Every piece drawn with <paramref name="from"/> gets <paramref name="to"/> (null: the default look). One undo step.</summary>
    public OpResult ReplaceMaterial(string from, string? to)
    {
        var doc = _plugin.Session?.Document;
        if (doc is null) return OpResult.Fail("no-document", "Open or create a map first.");
        var users = doc.Objects.Where(o => o.Material == from).Select(o => o.Id).ToList();
        if (users.Count == 0) { _status.Text = "No piece uses this material."; return OpResult.Fail("empty-selection", _status.Text); }
        var r = _plugin.Do("Replace material", MapmakerPlugin.Op("set-material", ("ids", MapmakerPlugin.Strs(users)), ("material", to)));
        RefreshMaterials();
        return r;
    }

    void DeleteMaterial()
    {
        if (SelectedMaterial is not { } id) { _status.Text = "Choose a material in the list."; return; }
        _plugin.Do("Delete material", MapmakerPlugin.Op("material-remove", ("id", id)));
        RefreshMaterials();
    }

    void RemoveUnusedImages()
    {
        var doc = _plugin.Session?.Document;
        if (doc is null) return;
        var used = doc.Materials.SelectMany(m => m.ImageIds()).ToHashSet();
        var ops = doc.Images.Where(i => !used.Contains(i.Id)).Select(i => MapmakerPlugin.Op("image-remove", ("id", i.Id))).ToArray();
        if (ops.Length == 0) { _status.Text = "Every picture is used by a material."; return; }
        _plugin.Do("Remove unused images", ops);
        RefreshMaterials();
    }

    // ---- the 3D view: pieces drawn with their map material ----

    // keyed by the node too (review of 919e4b4b): a scene-tab switch makes new nodes whose drawing count starts again at 1, which must not look like the one already drawn
    readonly Dictionary<string, (MapObject Obj, int Cache, int Content, ulong Node)> _matSeen = new();
    MaterialBuild.Cache? _viewCache;
    string _viewCacheKey = "";
    int _viewCacheVersion;

    /// <summary>Releases the 3D view's material handles (on the way out; see <see cref="MaterialBuild.Cache"/>). The next <see cref="ApplyMaterialsToView"/> builds them again.</summary>
    void ReleaseMaterialViews() { _viewCache?.Dispose(); _viewCache = null; _viewCacheKey = ""; _materialEditor?.ReleasePreview(); }

    /// <summary>A material's part of the view cache key: its id and what it resolves to (the surface drawn), or its unresolvable text (the error note shows).</summary>
    static string ResolvedKey(MaterialGraph m)
    {
        try { return m.Id + "=" + m.Resolve(); }
        catch (MapException) { return m.Id + "!" + m.ToNode().ToJsonString(); }
    }

    /// <summary>
    /// Draws every drawn piece of the open map with its material (<see cref="PieceViews.ApplySurface"/>, the game's builder) after each change, undo, redo and open; a piece whose content
    /// was rebuilt, or whose material, shape or size changed, is drawn again (in Edit mode also the drag's preview shape, on <c>EditChanged</c>); one without a material keeps (or gets back) the default look. Pictures are decoded once per set of materials.
    /// </summary>
    public void ApplyMaterialsToView()
    {
        if (_plugin.Session is not { } s) return;
        var doc = s.Document;
        // keyed on what each material resolves to (moving a graph node or renaming the material changes nothing drawn) and on the images listed; the old cache's handles are
        // released, and its decoded pictures are taken over (an image id is its SHA-256: the same id is the same picture)
        string key = $"{s.Path}|{MapHost.Library.Root}|" + string.Join(",", doc.Materials.Select(ResolvedKey)) + "|" + string.Join(",", doc.Images.Select(i => i.Id));
        if (key != _viewCacheKey || _viewCache is null)
        {
            var old = _viewCache;
            _viewCache = new MaterialBuild.Cache(doc, s.Path, MapHost.Library, old);
            old?.Dispose();
            _viewCacheKey = key; _viewCacheVersion++;
        }
        var preview = _plugin.ActiveDrag?.Preview;
        foreach (var o in doc.Objects)
        {
            if (o.Piece is not { } piece || !PieceKind.IsDrawn(piece.Kind)) continue;
            bool previewed = preview is not null && preview.ContainsKey(o.Id);
            if (_plugin.NodeOf(o.Id) is not { } node) continue;
            // unchanged since it was last drawn (same record, same material cache, same drawing, no Edit-mode preview): nothing to look at (the edit-hang fix, 2026-11-27)
            if (!previewed && _matSeen.TryGetValue(o.Id, out var seen) && ReferenceEquals(seen.Obj, o) && seen.Cache == _viewCacheVersion && seen.Content == node.ContentVersion && seen.Node == node.GetInstanceId()) continue;
            var mesh = previewed ? preview![o.Id] : o.Mesh;
            if (node.GetNodeOrNull<MeshInstance3D>("Content/Shape") is not { } shape) continue;
            if (!previewed) _matSeen[o.Id] = (o, _viewCacheVersion, node.ContentVersion, node.GetInstanceId());
            else _matSeen.Remove(o.Id);
            string sig = o.Material is null ? "" : $"{o.Material}|{_viewCacheVersion}|{mesh?.ToNode().ToJsonString()}|{o.Transform.Scale}";
            string have = shape.HasMeta("mm_surface") ? shape.GetMeta("mm_surface").AsString() : "";
            if (sig == have) continue;
            PieceViews.ApplySurface(shape, piece, mesh, o.Transform.Scale, o.Material is null ? null : _viewCache.For(o.Material));
            shape.SetMeta("mm_surface", sig);
        }
        foreach (var note in _viewCache.Notes.Distinct().Take(3)) GD.PushWarning("Mapmaker material: " + note);
        _viewCache.Notes.Clear();
    }

    // ---- sharing ----

    /// <summary>Saves the map (when it has changes or no file) and packs it with its pictures; returns the pack's path, or null (the status line says why).</summary>
    public string? PackMap(string? outPath)
    {
        if (_plugin.Session is not { } s) { _status.Text = "Open or create a map first."; return null; }
        if (s.Path is null || _plugin.Dirty)
        {
            if (!_plugin.SaveMap(s.Path ?? _plugin.NewMapPath(s.Name))) return null;
            s = _plugin.Session!;
        }
        string path = s.Path!;
        outPath ??= Path.ChangeExtension(path, MapPack.Extension);
        try
        {
            MapPack.Pack(path, outPath, MapHost.Library);
            _status.Text = $"Packed {Path.GetFileName(outPath)} ({s.Document.Images.Count} picture(s)): send this one file. The other person uses Open .smpack.";
            return outPath;
        }
        catch (Exception e) when (e is MapException or IOException or UnauthorizedAccessException or InvalidDataException) { _status.Text = "Pack failed: " + e.Message; return null; }
    }

    /// <summary>Unpacks a shared map into the maps folder (a sub-folder when a map of that file name is already there) and opens it; returns the opened map's path, or null.</summary>
    public string? OpenPack(string packPath, string? intoDir = null)
    {
        string baseDir = intoDir ?? MapHost.Library.MapsDir;
        try
        {
            string target;
            try { target = MapPack.Unpack(packPath, baseDir); }
            catch (MapException e) when (e.Code == "file-exists")
            {
                // never overwrite a map: a fresh folder of its own
                string stem = Path.GetFileNameWithoutExtension(packPath);
                int n = 2; string dir;
                do dir = Path.Combine(baseDir, $"{stem}-shared-{n++}"); while (Directory.Exists(dir));
                target = MapPack.Unpack(packPath, dir);
            }
            if (!_plugin.EnsureSceneRoot()) { _status.Text = $"Unpacked to {target}; open it with Open map."; return target; }
            _plugin.OpenMap(target);
            return target;
        }
        catch (Exception e) when (e is MapException or IOException or UnauthorizedAccessException or InvalidDataException) { _status.Text = "Could not open the pack: " + e.Message; return null; }
    }
}
