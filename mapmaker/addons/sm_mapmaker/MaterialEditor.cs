using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using SmClient;
using SmMapmaker;
using SysNum = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// The node editor of one map material (docs/mapmaker.md "Materials (editor)"; owner-approved brief 2026-11-25 "Textures and shader nodes"): a Godot <see cref="GraphEdit"/> with the six
/// node types of <see cref="NodeTypes"/>, sockets coloured by type (vector, colour, float, normal, surface; a colour may feed a float input: its red channel), and the parameters of each
/// node on the node. Every user step (connect, disconnect, add, delete, move, a parameter) is one <c>material-edit</c> operation, so one editor undo step; the graph is redrawn from the
/// document after each (and after undo / redo), so what it shows is what is saved. A refusal (<c>unsupported-graph</c>, <c>bad-graph</c>) changes nothing and its message is shown in
/// red under the graph. The preview at the right is a 2 m block drawn by <see cref="MaterialBuild"/>, the builder the game uses.
/// </summary>
[Tool]
public partial class MaterialEditor : Window
{
    readonly MapmakerPlugin _plugin;
    readonly GraphEdit _graph = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, RightDisconnects = true, ShowGrid = true };
    readonly Label _error = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
    readonly Label _head = new() { ThemeTypeVariation = "HeaderSmall" };
    readonly LineEdit _name = new() { CustomMinimumSize = new Vector2(180, 0), TooltipText = "The material's name (Enter renames it)." };
    readonly MenuButton _add = new() { Text = "Add node", Flat = false, TooltipText = "Adds a node at the middle of the graph." };
    readonly SubViewport _previewPort = new() { Size = new Vector2I(256, 256), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Msaa3D = Viewport.Msaa.Msaa4X };
    readonly Node3D _previewRoot = new() { Basis = Frame.C };
    readonly Label _previewNote = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(256, 0) };
    bool _rebuilding;

    /// <summary>The material shown (map material id, <c>m&lt;N&gt;</c>), or null.</summary>
    public string? MaterialId { get; private set; }
    /// <summary>The last refusal's message ("" when the last step worked).</summary>
    public string ErrorText => _error.Text;
    public GraphEdit Graph => _graph;
    public SubViewport PreviewViewport => _previewPort;

    // Socket types as GraphEdit slot types, with their colours (Blender's: vector violet, colour yellow, float grey, normal blue, surface green).
    static readonly string[] SocketTypes = [NodeTypes.Vector, NodeTypes.Color, NodeTypes.Float, NodeTypes.Normal, NodeTypes.Surface];
    static readonly Color[] SocketColours = [new(0.39f, 0.39f, 0.78f), new(0.78f, 0.78f, 0.16f), new(0.63f, 0.63f, 0.63f), new(0.39f, 0.55f, 1f), new(0.39f, 0.78f, 0.39f)];
    static int SlotType(string socketType) => Array.IndexOf(SocketTypes, socketType);

    static readonly Dictionary<string, string> Titles = new()
    {
        [NodeTypes.ImageTexture] = "Image Texture", [NodeTypes.Mapping] = "Mapping", [NodeTypes.NormalMap] = "Normal Map",
        [NodeTypes.SeparateRgb] = "Separate RGB", [NodeTypes.PbrSurface] = "PBR Surface", [NodeTypes.Output] = "Material Output",
    };

    public MaterialEditor(MapmakerPlugin plugin)
    {
        _plugin = plugin;
        Title = "Mapmaker material";
        Size = new Vector2I(1180, 680);
        Visible = false;
        Exclusive = false;
        Transient = true;
        WrapControls = false;
        CloseRequested += () => Hide();
        var panel = new PanelContainer { AnchorRight = 1, AnchorBottom = 1 };
        panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(panel);
        var v = new VBoxContainer();
        panel.AddChild(v);
        var top = new HBoxContainer();
        top.AddChild(_head);
        top.AddChild(new Label { Text = "  Name" });
        top.AddChild(_name);
        _name.TextSubmitted += t => Rename(t);
        var popup = _add.GetPopup();
        foreach (var (type, i) in NodeTypes.All.Select((t, i) => (t, i))) popup.AddItem(Titles[type], i);
        popup.IdPressed += id => AddNode(NodeTypes.All[(int)id]);
        top.AddChild(_add);
        top.AddChild(new Label { Text = "  Drag from an output to an input to link; drag a linked input away to unlink; Delete removes the selected nodes.", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true });
        v.AddChild(top);
        var h = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        h.AddChild(_graph);
        var side = new VBoxContainer { CustomMinimumSize = new Vector2(260, 0) };
        side.AddChild(new Label { Text = "Preview (the game's surface)", ThemeTypeVariation = "HeaderSmall" });
        var container = new SubViewportContainer { CustomMinimumSize = new Vector2(256, 256), Stretch = true };
        container.AddChild(_previewPort);
        side.AddChild(container);
        _previewNote.AddThemeFontSizeOverride("font_size", 12);
        side.AddChild(_previewNote);
        h.AddChild(side);
        v.AddChild(h);
        _error.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.4f));
        v.AddChild(_error);
        for (int i = 0; i < SocketTypes.Length; i++) _graph.AddValidConnectionType(i, i);
        _graph.AddValidConnectionType(SlotType(NodeTypes.Color), SlotType(NodeTypes.Float));   // a colour into a float input takes its red channel
        _graph.ConnectionRequest += (from, fromPort, to, toPort) => Connect(from, (int)fromPort, to, (int)toPort);
        _graph.DisconnectionRequest += (from, fromPort, to, toPort) => Disconnect(to, (int)toPort);
        _graph.DeleteNodesRequest += names => DeleteNodes(names.Select(n => n.ToString()).ToList());
        _graph.EndNodeMove += MovedNodes;
        BuildPreviewScene();
        _plugin.DocChanged += () => { if (Visible && MaterialId is not null) Rebuild(); };
    }

    /// <summary>Shows the editor for <paramref name="materialId"/>.</summary>
    public void Open(string materialId)
    {
        MaterialId = materialId;
        _error.Text = "";
        Rebuild();
        if (Visible) return;
        if (DisplayServer.GetName() == "headless") Show(); else PopupCentered(Size);
        Callable.From(FitView).CallDeferred();
    }

    /// <summary>Zooms and scrolls so every node is in view (on opening, and the "frame" for stills).</summary>
    public void FitView()
    {
        var nodes = _graph.GetChildren().OfType<GraphNode>().ToList();
        if (nodes.Count == 0 || _graph.Size.X < 10) return;
        var box = new Rect2(nodes[0].PositionOffset, nodes[0].Size);
        foreach (var n in nodes) box = box.Merge(new Rect2(n.PositionOffset, n.Size));
        box = box.Grow(30);
        float zoom = Mathf.Clamp(Mathf.Min(_graph.Size.X / box.Size.X, _graph.Size.Y / box.Size.Y), 0.3f, 1f);
        _graph.Zoom = zoom;
        _graph.ScrollOffset = box.Position * zoom - (_graph.Size - box.Size * zoom) / 2;
    }

    MaterialGraph? Current => MaterialId is not null ? _plugin.Session?.Document.FindMaterial(MaterialId) : null;

    // ---- one material-edit per user step ----

    /// <summary>Applies one <c>material-edit</c> (fields of <paramref name="edit"/> plus the id) as one undo step; a refusal is shown and changes nothing. True when it applied.</summary>
    public bool Edit(string label, JsonObject edit)
    {
        if (MaterialId is null) return false;
        var op = new JsonObject { ["op"] = "material-edit", ["id"] = MaterialId };
        foreach (var (k, val) in edit) op[k] = val?.DeepClone();
        var r = _plugin.Do("Material: " + label, op);
        _error.Text = r.Ok ? "" : string.Join("\n", r.Errors.Select(e => e.Message));
        if (!r.Ok) Rebuild();   // the graph view may already show the refused link: redraw it from the document
        return r.Ok;
    }

    MaterialNode? NodeAt(StringName name) => Current?.Node(name.ToString());

    /// <summary>A link from output port <paramref name="fromPort"/> of node <paramref name="from"/> to input port <paramref name="toPort"/> of <paramref name="to"/> (replaces a link already in that input).</summary>
    public bool Connect(StringName from, int fromPort, StringName to, int toPort)
    {
        if (NodeAt(from) is not { } a || NodeAt(to) is not { } b) return false;
        var outs = NodeTypes.Outputs(a.Type); var ins = NodeTypes.Inputs(b.Type);
        if (fromPort < 0 || fromPort >= outs.Count || toPort < 0 || toPort >= ins.Count) return false;
        // A material has one UV mapping (MaterialGraph.Resolve): a mapping linked into one picture is linked into every picture of the material in the same step (ours).
        var g = Current!;
        var targets = a.Type == NodeTypes.Mapping && b.Type == NodeTypes.ImageTexture ? g.Nodes.Where(n => n.Type == NodeTypes.ImageTexture).Select(n => n.Id).ToList() : [b.Id];
        var links = new JsonArray(targets.Select(t => (JsonNode)new JsonObject { ["from"] = a.Id, ["out"] = outs[fromPort].Name, ["to"] = t, ["in"] = ins[toPort].Name }).ToArray());
        // and a picture without a mapping that is linked into the surface while the material has one takes that mapping in the same step
        if (a.Type == NodeTypes.ImageTexture && g.Links.All(l => l.To != a.Id || l.In != "vector")
            && g.Links.FirstOrDefault(l => l.In == "vector" && g.Node(l.From)?.Type == NodeTypes.Mapping) is { } mapped)
            links.Add(new JsonObject { ["from"] = mapped.From, ["out"] = "vector", ["to"] = a.Id, ["in"] = "vector" });
        return Edit(targets.Count > 1 ? "link mapping" : "link", new JsonObject { ["link"] = links });
    }

    /// <summary>By socket names (the selftest's way in; the same step).</summary>
    public bool Connect(string from, string output, string to, string input)
    {
        if (NodeAt(from) is not { } a || NodeAt(to) is not { } b) return false;
        return Connect(from, IndexOf(NodeTypes.Outputs(a.Type), output), to, IndexOf(NodeTypes.Inputs(b.Type), input));
    }

    static int IndexOf(IReadOnlyList<(string Name, string Type)> sockets, string name) { for (int i = 0; i < sockets.Count; i++) if (sockets[i].Name == name) return i; return -1; }

    public bool Disconnect(StringName to, int toPort)
    {
        if (NodeAt(to) is not { } b || toPort < 0 || toPort >= NodeTypes.Inputs(b.Type).Count) return false;
        var input = NodeTypes.Inputs(b.Type)[toPort].Name;
        if (Current!.Links.All(l => l.To != b.Id || l.In != input)) return false;
        // Unlinking a picture's mapping unlinks it from every picture (one mapping per material, as above).
        var from = Current!.Links.First(l => l.To == b.Id && l.In == input).From;
        var targets = b.Type == NodeTypes.ImageTexture && Current.Node(from)?.Type == NodeTypes.Mapping
            ? Current.Links.Where(l => l.From == from && l.In == "vector").Select(l => l.To).ToList() : [b.Id];
        return Edit(targets.Count > 1 ? "unlink mapping" : "unlink", new JsonObject { ["unlink"] = new JsonArray(targets.Select(t => (JsonNode)new JsonObject { ["to"] = t, ["in"] = input }).ToArray()) });
    }

    public bool Disconnect(string to, string input) => NodeAt(to) is { } b && Disconnect(to, IndexOf(NodeTypes.Inputs(b.Type), input));

    public bool AddNode(string type, Vector2? at = null)
    {
        var pos = at ?? (_graph.ScrollOffset + _graph.Size / 2) / Math.Max(0.01f, _graph.Zoom) - new Vector2(80, 40);
        return Edit("add " + Titles[type], new JsonObject { ["addNodes"] = new JsonArray(new JsonObject { ["type"] = type, ["position"] = new JsonArray(MathF.Round(pos.X), MathF.Round(pos.Y)) }) });
    }

    public bool DeleteNodes(IReadOnlyList<string> ids)
    {
        var have = ids.Where(id => Current?.Node(id) is not null).ToList();
        if (have.Count == 0) return false;
        return Edit(have.Count == 1 ? "delete node" : $"delete {have.Count} nodes", new JsonObject { ["removeNodes"] = MapmakerPlugin.Strs(have) });
    }

    public bool SetParams(string node, JsonObject ps, string label = "parameter") =>
        Edit(label, new JsonObject { ["setParams"] = new JsonArray(new JsonObject { ["node"] = node, ["params"] = ps }) });

    void MovedNodes()
    {
        if (Current is not { } g) return;
        var moves = new JsonArray();
        foreach (var gn in _graph.GetChildren().OfType<GraphNode>())
        {
            if (g.Node(gn.Name.ToString()) is not { } n) continue;
            var p = gn.PositionOffset;
            if (MathF.Abs(p.X - n.Position.X) < 0.5f && MathF.Abs(p.Y - n.Position.Y) < 0.5f) continue;
            moves.Add(new JsonObject { ["node"] = n.Id, ["position"] = new JsonArray(MathF.Round(p.X), MathF.Round(p.Y)) });
        }
        if (moves.Count > 0) Edit(moves.Count == 1 ? "move node" : "move nodes", new JsonObject { ["moveNodes"] = moves });
    }

    void Rename(string name)
    {
        if (MaterialId is null || Current is not { } g || name.Trim() == g.Name) return;
        var r = _plugin.Do("Rename material", MapmakerPlugin.Op("material-set", ("id", MaterialId), ("name", name.Trim())));
        _error.Text = r.Ok ? "" : string.Join("\n", r.Errors.Select(e => e.Message));
    }

    // ---- drawing the graph from the document ----

    /// <summary>Redraws every node and link from the document (after each step, undo and redo).</summary>
    public void Rebuild()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        try
        {
            _graph.ClearConnections();
            foreach (var c in _graph.GetChildren().OfType<GraphNode>().ToList()) { _graph.RemoveChild(c); c.QueueFree(); }
            if (Current is not { } g) { _head.Text = "No material"; _name.Text = ""; return; }
            _head.Text = $"Material {g.Id}";
            if (!_name.HasFocus()) _name.Text = g.Name;
            Title = $"Mapmaker material: {g.Name}";
            foreach (var n in g.Nodes) _graph.AddChild(NodeView(n));
            foreach (var l in g.Links)
            {
                if (g.Node(l.From) is not { } a || g.Node(l.To) is not { } b) continue;
                _graph.ConnectNode(a.Id, IndexOf(NodeTypes.Outputs(a.Type), l.Out), b.Id, IndexOf(NodeTypes.Inputs(b.Type), l.In));
            }
            UpdatePreview(g);
        }
        finally { _rebuilding = false; }
    }

    GraphNode NodeView(MaterialNode n)
    {
        var gn = new GraphNode { Name = n.Id, Title = Titles[n.Type], PositionOffset = new Vector2(n.Position.X, n.Position.Y), CustomMinimumSize = new Vector2(200, 0), TooltipText = n.Id };
        int row = 0;
        foreach (var (name, type) in NodeTypes.Outputs(n.Type))
        {
            gn.AddChild(new Label { Text = Pretty(name), HorizontalAlignment = HorizontalAlignment.Right });
            gn.SetSlot(row++, false, 0, Colors.White, true, SlotType(type), SocketColours[SlotType(type)]);
        }
        foreach (var (name, type) in NodeTypes.Inputs(n.Type))
        {
            gn.AddChild(new Label { Text = Pretty(name) });
            gn.SetSlot(row++, true, SlotType(type), SocketColours[SlotType(type)], false, 0, Colors.White);
        }
        foreach (var c in ParamControls(n)) { gn.AddChild(c); row++; }
        return gn;
    }

    static string Pretty(string socket) => socket switch { "base-color" => "Base Color", "ao" => "AO", "r" => "R", "g" => "G", "b" => "B", _ => char.ToUpperInvariant(socket[0]) + socket[1..] };

    /// <summary>A stored number (the core keeps parameters as float, double or int JSON values), or <paramref name="d"/>.</summary>
    static float Num(JsonNode? n, float d) => n is not JsonValue v ? d : v.TryGetValue<float>(out var f) ? f : v.TryGetValue<double>(out var x) ? (float)x : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var l) ? l : d;

    IEnumerable<Control> ParamControls(MaterialNode n)
    {
        var p = n.Params;
        switch (n.Type)
        {
            case NodeTypes.ImageTexture:
            {
                var pick = new OptionButton { ClipText = true, CustomMinimumSize = new Vector2(180, 0), TooltipText = "The picture (Library > Materials > Import image adds one to the map)." };
                pick.AddItem("(no image)"); pick.SetItemMetadata(0, "");
                string current = p["image"]?.ToString() ?? "";
                var images = _plugin.Session?.Document.Images ?? [];
                foreach (var img in images.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    pick.AddItem($"{img.Name}  ({img.Width}x{img.Height})");
                    pick.SetItemMetadata(pick.ItemCount - 1, img.Id);
                    if (img.Id == current) pick.Select(pick.ItemCount - 1);
                }
                pick.ItemSelected += i => { var id = pick.GetItemMetadata((int)i).AsString(); SetParams(n.Id, new JsonObject { ["image"] = id.Length == 0 ? null : id }, "image"); };
                yield return pick;
                break;
            }
            case NodeTypes.Mapping:
            {
                var s = p["scale"] as JsonArray; var o = p["offset"] as JsonArray;
                yield return PairRow("Scale", Num(s?[0], 1), Num(s?[1], 1), 0.01, "Repeats per metre (U, V).", (u, v) => SetParams(n.Id, new JsonObject { ["scale"] = new JsonArray(u, v) }, "mapping scale"));
                yield return PairRow("Offset", Num(o?[0], 0), Num(o?[1], 0), 0.01, "Shift of the picture (U, V), in repeats.", (u, v) => SetParams(n.Id, new JsonObject { ["offset"] = new JsonArray(u, v) }, "mapping offset"));
                break;
            }
            case NodeTypes.NormalMap:
            {
                yield return ValueRow("Strength", Num(p["strength"], 1), 0, 10, "How strong the relief is (0 to 10).", x => SetParams(n.Id, new JsonObject { ["strength"] = x }, "normal strength"));
                var conv = new OptionButton { TooltipText = "OpenGL (green up) is what Godot expects; DirectX normal maps (green down, many Megascans and Unreal pictures) have their green channel flipped." };
                conv.AddItem("OpenGL"); conv.AddItem("DirectX (flip green)");
                conv.Select(p["convention"]?.ToString() == "directx" ? 1 : 0);
                conv.ItemSelected += i => SetParams(n.Id, new JsonObject { ["convention"] = i == 1 ? "directx" : "opengl" }, "normal convention");
                yield return conv;
                break;
            }
            case NodeTypes.PbrSurface:
            {
                var bc = p["baseColor"] is JsonArray a && a.Count == 3 ? new Color(Num(a[0], 0.8f), Num(a[1], 0.8f), Num(a[2], 0.8f)) : new Color(0.8f, 0.8f, 0.8f);
                var colour = new ColorPickerButton { Color = bc, EditAlpha = false, CustomMinimumSize = new Vector2(60, 24), TooltipText = "Base colour when no picture is linked." };
                colour.PopupClosed += () => { var c = colour.Color; SetParams(n.Id, new JsonObject { ["baseColor"] = new JsonArray(Round(c.R), Round(c.G), Round(c.B)) }, "base colour"); };
                var row = new HBoxContainer(); row.AddChild(new Label { Text = "Color", CustomMinimumSize = new Vector2(70, 0) }); row.AddChild(colour);
                yield return row;
                yield return ValueRow("Metallic", Num(p["metallic"], 0), 0, 1, "Used when nothing is linked into Metallic.", x => SetParams(n.Id, new JsonObject { ["metallic"] = x }, "metallic"));
                yield return ValueRow("Roughness", Num(p["roughness"], 0.5f), 0, 1, "Used when nothing is linked into Roughness.", x => SetParams(n.Id, new JsonObject { ["roughness"] = x }, "roughness"));
                yield return ValueRow("AO", Num(p["ao"], 1), 0, 1, "Ambient occlusion used when nothing is linked into AO.", x => SetParams(n.Id, new JsonObject { ["ao"] = x }, "ao"));
                break;
            }
        }
    }

    static double Round(float x) => Math.Round(x, 4);

    static HBoxContainer ValueRow(string label, float value, double min, double max, string tip, Action<double> set)
    {
        var spin = new SpinBox { MinValue = min, MaxValue = max, Step = 0.001, CustomArrowStep = 0.05, Value = value, TooltipText = tip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, UpdateOnTextChanged = false };
        spin.ValueChanged += x => set(Math.Round(x, 4));
        var row = new HBoxContainer(); row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(70, 0) }); row.AddChild(spin);
        return row;
    }

    static HBoxContainer PairRow(string label, float u, float v, double step, string tip, Action<double, double> set)
    {
        var su = new SpinBox { MinValue = -10000, MaxValue = 10000, Step = 0.001, CustomArrowStep = step * 10, Value = u, TooltipText = tip, CustomMinimumSize = new Vector2(64, 0) };
        var sv = new SpinBox { MinValue = -10000, MaxValue = 10000, Step = 0.001, CustomArrowStep = step * 10, Value = v, TooltipText = tip, CustomMinimumSize = new Vector2(64, 0) };
        su.ValueChanged += x => set(Math.Round(x, 4), Math.Round(sv.Value, 4));
        sv.ValueChanged += x => set(Math.Round(su.Value, 4), Math.Round(x, 4));
        var row = new HBoxContainer(); row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(50, 0) }); row.AddChild(su); row.AddChild(sv);
        return row;
    }

    // ---- preview: a 2 m block lit from an angle, drawn by the game's builder ----

    MeshInstance3D? _previewMesh;

    void BuildPreviewScene()
    {
        var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.16f, 0.17f, 0.2f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.7f, 0.72f, 0.78f), AmbientLightEnergy = 0.45f };
        _previewPort.AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D { LightEnergy = 1.1f, ShadowEnabled = true };
        _previewPort.AddChild(sun);
        sun.LookAtFromPosition(new Vector3(3, 4, 4), Vector3.Zero, Vector3.Up);
        var cam = new Camera3D { Fov = 40 };
        _previewPort.AddChild(cam);
        cam.LookAtFromPosition(new Vector3(3.2f, 2.6f, 3.6f), new Vector3(0, 1f, 0), Vector3.Up);
        _previewPort.AddChild(_previewRoot);
        var shape = EditMesh.Stock(MeshTopology.Box);
        var scale = new SysNum.Vector3(2, 2, 2);
        _previewMesh = new MeshInstance3D { Mesh = MaterialBuild.Mesh(RenderMesh.Build(shape, scale, SysNum.Matrix4x4.CreateScale(scale))) };
        _previewRoot.AddChild(_previewMesh);
        _previewRoot.Position = new Vector3(0, 0, 0);
    }

    void UpdatePreview(MaterialGraph g)
    {
        if (_previewMesh is null || _plugin.Session is not { } s) return;
        var cache = new MaterialBuild.Cache(s.Document, s.Path, MapHost.Library);
        _previewMesh.MaterialOverride = cache.For(g.Id);
        _previewNote.Text = cache.Notes.Count == 0 ? DescribeResolved(g) : string.Join("\n", cache.Notes);
    }

    static string DescribeResolved(MaterialGraph g)
    {
        try
        {
            var m = g.Resolve();
            var parts = new List<string>();
            foreach (var b in MaterialPlan.Of(m).Textures) parts.Add($"{b.Feature}{(b.Channel is TextureChannel.Rgb ? "" : " (" + b.Channel + ")")}{(b.FlipGreen ? ", green flipped" : "")}");
            return parts.Count == 0 ? "No pictures linked: the surface uses its values." : "Pictures: " + string.Join(", ", parts) + (m.UvScale != SysNum.Vector2.One || m.UvOffset != SysNum.Vector2.Zero ? $"; mapping {m.UvScale.X:0.##} x {m.UvScale.Y:0.##} per metre" : "");
        }
        catch (MapException e) { return e.Message; }
    }
}
