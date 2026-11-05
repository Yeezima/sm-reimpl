using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient;

/// <summary>
/// The kit look of Ashfall Citadel (docs/musou-ashfall-graybox.md "Kit look (client)"): the dressing's items drawn with the owner's generated models
/// (<see cref="AshfallKitLibrary"/>), the graybox parts the dressing hides left out, and the rest of the graybox in a stone and ash material (no checker).
/// Lives in game space (X east, Y north, Z up; the parent converts). The graybox view stays as the other look: <see cref="SetKit"/> / F8 toggle between them
/// (both are built once; the toggle only changes visibility). Models arrive in the background; an item shows when its model is loaded, and a part the
/// dressing hides stays drawn until every Skin item over it is shown (so a missing model leaves the graybox part for that spot). **Placeholder demonstration.**
/// </summary>
public partial class AshfallKitView : Node3D
{
    /// <summary>The toggle key (F8: F1 overlay, F9 fire test, F10 observer and F11 are taken; F8 was free).</summary>
    public const Key ToggleKey = Key.F8;

    /// <summary>
    /// The transform that fits a model into an item's box by the contract (<see cref="AshfallDressItem"/>): the raw bounds on the mapped axes fill <see cref="AshfallDressItem.Size"/>, the
    /// box is centred on the position in X/Y with its bottom at the position's Z, glTF +Y becomes game +Z, then yaw about Z. The mapping is a rotation (no mirror): not swapped,
    /// glTF X to local X and glTF -Z to local Y (the model's front, glTF +Z, faces -Y at yaw 0); swapped, glTF Z to local X and glTF X to local Y. Result: glTF frame to game frame.
    /// </summary>
    public static Transform3D Fit(AshfallDressItem item, AshfallKitModel model)
    {
        var raw = model.RawSize;
        var min = model.RawMin;
        float rx = Math.Max(raw.X, 1e-4f), ry = Math.Max(raw.Y, 1e-4f), rz = Math.Max(raw.Z, 1e-4f);
        Basis m;
        if (!item.SwapXZ)
            m = new Basis(new Vector3(item.Size.X / rx, 0, 0), new Vector3(0, 0, item.Size.Z / ry), new Vector3(0, -item.Size.Y / rz, 0));   // columns: where glTF X, Y, Z go
        else
            m = new Basis(new Vector3(0, item.Size.Y / rx, 0), new Vector3(0, 0, item.Size.Z / ry), new Vector3(item.Size.X / rz, 0, 0));
        var centre = new Vector3((min.X + model.RawMax.X) * 0.5f, min.Y, (min.Z + model.RawMax.Z) * 0.5f);
        var yaw = new Basis(new Vector3(0, 0, 1), item.Yaw);
        var basis = yaw * m;
        var origin = new Vector3(item.Position.X, item.Position.Y, item.Position.Z) - basis * centre;
        return new Transform3D(basis, origin);
    }

    // ours: the stone and ash colours of the remaining graybox parts (kind by kind)
    static Color StoneOf(AshfallKind k) => k switch
    {
        AshfallKind.Ground => new Color(0.36f, 0.33f, 0.30f),
        AshfallKind.Road => new Color(0.24f, 0.23f, 0.22f),
        AshfallKind.Paving => new Color(0.44f, 0.42f, 0.39f),
        AshfallKind.Curb => new Color(0.46f, 0.44f, 0.41f),
        AshfallKind.Wall => new Color(0.40f, 0.38f, 0.36f),
        AshfallKind.Cover => new Color(0.44f, 0.42f, 0.40f),
        AshfallKind.Rubble => new Color(0.34f, 0.31f, 0.28f),
        AshfallKind.Floor => new Color(0.38f, 0.36f, 0.34f),
        AshfallKind.Stair => new Color(0.42f, 0.40f, 0.37f),
        AshfallKind.Perch => new Color(0.40f, 0.38f, 0.36f),
        AshfallKind.Stronghold => new Color(0.30f, 0.26f, 0.25f),
        AshfallKind.Boundary => new Color(0.18f, 0.17f, 0.17f),
        AshfallKind.Backdrop => new Color(0.16f, 0.16f, 0.17f),
        _ => new Color(0.4f, 0.38f, 0.36f),
    };

    static ImageTexture? _stone;

    /// <summary>A seamless 128 px noise (ash and grit) the triplanar stone material tiles every 4 m.</summary>
    static ImageTexture Stone()
    {
        if (_stone is not null) return _stone;
        var noise = new FastNoiseLite { Frequency = 0.045f, FractalOctaves = 4, Seed = 7 };
        var img = noise.GetSeamlessImage(128, 128);
        img.Convert(Image.Format.Rgb8);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                float v = 0.78f + 0.22f * img.GetPixel(x, y).R;
                img.SetPixel(x, y, new Color(v, v, v));
            }
        img.GenerateMipmaps();
        return _stone = ImageTexture.CreateFromImage(img);
    }

    /// <summary>What the last build and load measured (see the log line).</summary>
    public static string Report { get; private set; } = "";

    readonly AshfallKitLibrary _library = new();
    readonly AshfallDressing _dressing;
    readonly Node3D? _graybox;
    readonly Dictionary<string, List<int>> _itemsByAsset = new();
    readonly Dictionary<int, MeshInstance3D> _hiddenPartNodes = new();
    readonly Dictionary<int, int> _partWaiting = new();
    readonly Node3D _items = new() { Name = "Items" };
    readonly Node3D _remainder = new() { Name = "Remainder" };
    readonly HashSet<int> _drawnItems = [];
    int _shown, _shownInstances, _failedItems;
    long _startMs;
    bool _reported;
    bool _kit = true;

    /// <summary>True while the kit look is the one drawn.</summary>
    public bool Kit => _kit;
    public AshfallKitLibrary Library => _library;
    public int ItemsShown => _shown;
    public int InstancesShown => _shownInstances;
    public int ItemsFailed => _failedItems;

    public AshfallKitView(AshfallLevel level, AshfallDressing dressing, Node3D? graybox)
    {
        Name = "KitLook";
        _dressing = dressing;
        _graybox = graybox;
        AddChild(_remainder);
        AddChild(_items);
        BuildRemainder(level);
        ApplyArguments();
        for (int i = 0; i < dressing.Items.Count; i++)
        {
            string id = dressing.Items[i].Asset;
            if (!_itemsByAsset.TryGetValue(id, out var list)) _itemsByAsset[id] = list = [];
            list.Add(i);
        }
        foreach (var part in dressing.HiddenParts) _partWaiting[part] = 0;
        foreach (var item in dressing.Items)
            if (item.Role == AshfallDressRole.Skin && _partWaiting.ContainsKey(item.Part)) _partWaiting[item.Part]++;
        foreach (var (part, waiting) in _partWaiting.ToArray())
            if (waiting == 0) HidePart(part);
        _startMs = (long)Time.GetTicksMsec();
        _library.Request(_itemsByAsset.Keys);
    }

    /// <summary>The camera-facing options: <c>--ashfall-tex N</c>, <c>--ashfall-lods off</c> and <c>--ashfall-shadows off</c> (measurement aids).</summary>
    bool _shadows = true;
    /// <summary>Mesh LOD threshold (pixels of error) while the kit look is drawn; the engine default is 1. Measured: 1 costs about 20 ms of GPU time on the dense map, 4 about 3 to 5 ms (ours; <c>--ashfall-lod-threshold</c>).</summary>
    public float LodThreshold { get; set; } = 4f;
    float _baseLod = 1f;
    bool _lodKept;
    void ApplyArguments()
    {
        var args = OS.GetCmdlineUserArgs();
        string? Value(string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Value("ashfall-tex") is { } t && int.TryParse(t, out var tex)) _library.TextureMax = tex;
        if (Value("ashfall-lods") == "off") _library.GenerateLods = false;
        if (Value("ashfall-shadows") == "off") _shadows = false;
        if (Value("ashfall-lod-threshold") is { } lt && float.TryParse(lt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var th) && th >= 0.1f) LodThreshold = th;
    }

    void BuildRemainder(AshfallLevel level)
    {
        var hidden = _dressing.HiddenParts.ToHashSet();
        // Parts drawn as an inset core under their model: skinned walls and every prop's collision proxy (a full-size box would cover the model's relief).
        var skinned = _dressing.Items.Where(i => i.Role is AshfallDressRole.Skin or AshfallDressRole.Prop && i.Part >= 0).Select(i => i.Part).ToHashSet();
        foreach (var group in level.Parts.Select((p, i) => (Part: p, Index: i)).Where(x => x.Part.Kind != AshfallKind.Objective).GroupBy(x => x.Part.Kind))
        {
            var material = new StandardMaterial3D
            {
                AlbedoColor = StoneOf(group.Key), AlbedoTexture = Stone(),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                Uv1Triplanar = true, Uv1WorldTriplanar = true, Uv1Scale = new Vector3(0.25f, 0.25f, 0.25f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 1f,
            };
            var always = group.Where(x => !hidden.Contains(x.Index)).SelectMany(x => skinned.Contains(x.Index) ? Inset(x.Part.Corners) : x.Part.Corners).ToArray();
            if (always.Length > 0)
                _remainder.AddChild(new MeshInstance3D { Name = group.Key.ToString(), Mesh = AshfallView.MeshOf(always), MaterialOverride = material });
            foreach (var x in group.Where(x => hidden.Contains(x.Index)))
            {
                var node = new MeshInstance3D { Name = $"Part{x.Index}", Mesh = AshfallView.MeshOf(x.Part.Corners), MaterialOverride = material };
                _hiddenPartNodes[x.Index] = node;
                _remainder.AddChild(node);
            }
        }
    }

    /// <summary>A skinned part that stays drawn (walls, stronghold, boundary: the models' window and arch openings must show stone, not a see-through hole over solid
    /// collision) is drawn as its box pulled in to a core: <see cref="CoreInset"/> of its thickness on each side of the thin axis, <see cref="BackingInset"/> on the long one, so the fitted model's relief stays in front.
    /// Top and bottom are kept (walkable tops stay where the collision is).</summary>
    const float BackingInset = 0.15f;
    /// <summary>Share of the thickness the backing gives up on each side of a skinned wall's thin axis (ours): 0.4 leaves the middle fifth as a stone core.</summary>
    const float CoreInset = 0.4f;

    static System.Numerics.Vector3[] Inset(System.Numerics.Vector3[] corners)
    {
        var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
        foreach (var v in corners) { min = System.Numerics.Vector3.Min(min, v); max = System.Numerics.Vector3.Max(max, v); }
        var c = (min + max) / 2f;
        float sx = max.X - min.X, sy = max.Y - min.Y;
        // The thin axis is pulled in to its core (the fitted model's relief spans the whole thickness), the long axis only by BackingInset.
        float ix = sx <= sy ? CoreInset * sx : MathF.Min(BackingInset, sx / 4f), iy = sy < sx ? CoreInset * sy : MathF.Min(BackingInset, sy / 4f);
        var result = new System.Numerics.Vector3[corners.Length];
        for (int i = 0; i < corners.Length; i++)
        {
            var v = corners[i];
            result[i] = new(v.X < c.X ? v.X + ix : v.X > c.X ? v.X - ix : v.X, v.Y < c.Y ? v.Y + iy : v.Y > c.Y ? v.Y - iy : v.Y, v.Z);
        }
        return result;
    }

    void HidePart(int part)
    {
        if (_hiddenPartNodes.TryGetValue(part, out var node)) node.Visible = false;
    }

    /// <summary>Switches the look: the kit (this node, the graybox view's objective markers only) or the graybox (the whole graybox view). Logged.</summary>
    public void SetKit(bool kit)
    {
        _kit = kit;
        Visible = kit;
        if (IsInsideTree())
        {
            var viewport = GetViewport();
            if (!_lodKept) { _baseLod = viewport.MeshLodThreshold; _lodKept = true; }
            viewport.MeshLodThreshold = kit ? LodThreshold : _baseLod;
        }
        if (_graybox is not null)
            foreach (var child in _graybox.GetChildren().OfType<Node3D>())
                child.Visible = !kit || child.Name.ToString().StartsWith("Objective", StringComparison.Ordinal) || child.Name.ToString().StartsWith("Letter", StringComparison.Ordinal);
        GD.Print($"ashfall: look {(kit ? "kit" : "graybox")}");
    }

    /// <summary>The viewport is shared with every later map (classic PvP too): leave it with the threshold it had before the kit look changed it.</summary>
    public override void _ExitTree()
    {
        if (_lodKept && IsInsideTree()) GetViewport().MeshLodThreshold = _baseLod;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: ToggleKey }) SetKit(!_kit);
    }

    public override void _Process(double delta)
    {
        long budgetEnd = (long)Time.GetTicksUsec() + 4000;
        foreach (var id in _library.TakeReady())
        {
            Instantiate(id);
            if ((long)Time.GetTicksUsec() > budgetEnd) break;
        }
        if (!_reported && _library.Pending == 0 && _library.Loaded + _library.Failed > 0)
        {
            _reported = true;
            var assets = _library.All.Where(a => a.Ok).ToList();
            Report = $"{_library.Loaded} models loaded, {_library.Failed} failed, wall {_library.WallMs:F0} ms (sum of per-model {_library.All.Sum(a => a.Ms):F0} ms), " +
                     $"{assets.Sum(a => a.Triangles):N0} triangles at full detail, textures ~{assets.Sum(a => a.TextureBytes) / 1048576.0:F0} MiB, items shown {_shown} ({_shownInstances} mesh instances), items left graybox {_failedItems}";
            GD.Print("ashfall kit: " + Report);
        }
    }

    // Mesh instances of one asset are created once its model arrives; the pieces share the cached meshes.
    void Instantiate(string id)
    {
        var asset = _library.Get(id);
        var model = AshfallKit.Models.FirstOrDefault(m => m.Id == id);
        if (!_itemsByAsset.TryGetValue(id, out var indexes)) return;
        foreach (int i in indexes)
        {
            var item = _dressing.Items[i];
            if (asset is not { Ok: true } || model is null) { _failedItems++; continue; }
            var fit = Fit(item, model);
            foreach (var piece in asset.Pieces)
            {
                _items.AddChild(new MeshInstance3D
                {
                    Mesh = piece.Mesh, Transform = fit * piece.Local,
                    CastShadow = _shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                });
                _shownInstances++;
            }
            _shown++;
            if (item.Role == AshfallDressRole.Skin && _partWaiting.TryGetValue(item.Part, out var waiting))
            {
                _partWaiting[item.Part] = --waiting;
                if (waiting <= 0) HidePart(item.Part);
            }
        }
    }
}
