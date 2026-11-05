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

    static ImageTexture? _terrain, _masonry;

    /// <summary>
    /// A seamless 256 px ash and stone texture (ours): big soft patches of dark ash and pale dust, fine grit and scattered pale stone flecks; triplanar, one tile every 8 m. Not flat, not a checker.
    /// </summary>
    static ImageTexture Terrain()
    {
        if (_terrain is not null) return _terrain;
        var big = new FastNoiseLite { Frequency = 0.012f, FractalOctaves = 4, Seed = 11 }.GetSeamlessImage(256, 256);
        var mid = new FastNoiseLite { Frequency = 0.05f, FractalOctaves = 3, Seed = 23 }.GetSeamlessImage(256, 256);
        var grit = new FastNoiseLite { Frequency = 0.35f, FractalOctaves = 2, Seed = 5 }.GetSeamlessImage(256, 256);
        var img = Image.CreateEmpty(256, 256, false, Image.Format.Rgb8);
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float b = big.GetPixel(x, y).R, m = mid.GetPixel(x, y).R, g = grit.GetPixel(x, y).R;
                float v = 0.58f + 0.34f * (b - 0.5f) + 0.24f * (m - 0.5f) + 0.20f * (g - 0.5f);
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663);
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                if (h % 97 == 0) v += 0.18f;      // a pale stone fleck
                else if (h % 61 == 0) v -= 0.14f; // a dark pit
                v = Math.Clamp(v, 0.18f, 1f);
                img.SetPixel(x, y, new Color(v * 1.01f, v, v * 0.97f));   // a hint warm: ash, not blue-grey
            }
        img.GenerateMipmaps();
        return _terrain = ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// A seamless 256 px ashlar masonry (ours): courses 1 m high and blocks 4 m long over a 4 m x 4 m tile (triplanar, one tile every 4 m), joints stepped half a block on every other course, each course its own tone and grit, mortar lines dark.
    /// </summary>
    static ImageTexture Masonry()
    {
        if (_masonry is not null) return _masonry;
        var grit = new FastNoiseLite { Frequency = 0.12f, FractalOctaves = 3, Seed = 31 }.GetSeamlessImage(256, 256);
        var img = Image.CreateEmpty(256, 256, false, Image.Format.Rgb8);
        for (int y = 0; y < 256; y++)
        {
            int course = y / 64, row = y % 64;
            for (int x = 0; x < 256; x++)
            {
                int xx = (x + (course % 2) * 128) % 256;   // the joint sits at 0 on even courses, at half a block on odd ones
                uint bh = (uint)(course * 40503 + 12345) * 2654435761u; bh ^= bh >> 15;
                float tone = 0.50f + 0.14f * ((bh % 100) / 100f - 0.5f) * 2f + (course % 2 == 0 ? 0.02f : -0.02f);
                float g = grit.GetPixel(x, y).R;
                float v = tone + 0.20f * (g - 0.5f);
                if (xx < 2 || row < 2) v *= 0.45f;   // the mortar line
                else if (xx < 4 || xx > 252 || row < 4 || row > 61) v *= 0.8f;   // a soft bevel
                img.SetPixel(x, y, new Color(v * 1.02f, v, v * 0.95f));
            }
        }
        img.GenerateMipmaps();
        return _masonry = ImageTexture.CreateFromImage(img);
    }

    static StandardMaterial3D Triplanar(Texture2D texture, Color tint, float perMetre) => new()
    {
        AlbedoColor = tint, AlbedoTexture = texture,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        Uv1Triplanar = true, Uv1WorldTriplanar = true, Uv1Scale = new Vector3(perMetre, perMetre, perMetre),
        CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 1f,
    };

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
        foreach (var part in dressing.HiddenParts) _hiddenAll.Add(part);
        _covers = CoveredParts(level, dressing);
        BuildRemainder(level);
        ApplyArguments();
        for (int i = 0; i < dressing.Items.Count; i++)
        {
            string id = dressing.Items[i].Asset;
            if (!_itemsByAsset.TryGetValue(id, out var list)) _itemsByAsset[id] = list = [];
            list.Add(i);
        }
        foreach (var part in _hiddenAll) _partWaiting[part] = 0;
        for (int i = 0; i < dressing.Items.Count; i++)
            foreach (var part in _covers[i])
                if (_partWaiting.ContainsKey(part)) _partWaiting[part]++;
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
        var hidden = _hiddenAll;
        // Draft 3: the ground kinds (Ground, Road, Paving, Curb) are drawn as ash and stone terrain, the boundary as a flat masonry wall, and every part of the dressing's hidden list only until its models show.
        foreach (var group in level.Parts.Select((p, i) => (Part: p, Index: i)).Where(x => x.Part.Kind != AshfallKind.Objective).GroupBy(x => x.Part.Kind))
        {
            StandardMaterial3D material = group.Key switch
            {
                AshfallKind.Ground => Triplanar(Terrain(), new Color(0.62f, 0.59f, 0.56f), 0.125f),
                AshfallKind.Curb => Triplanar(Terrain(), new Color(0.70f, 0.67f, 0.63f), 0.125f),
                AshfallKind.Road => Triplanar(Terrain(), new Color(0.52f, 0.50f, 0.48f), 0.125f),
                AshfallKind.Paving => Triplanar(Terrain(), new Color(0.95f, 0.92f, 0.86f), 0.125f),
                AshfallKind.Boundary => Triplanar(Masonry(), new Color(0.62f, 0.58f, 0.55f), 0.25f),
                _ => Triplanar(Stone(), StoneOf(group.Key), 0.25f),
            };
            var always = group.Where(x => !hidden.Contains(x.Index)).SelectMany(x => x.Part.Corners).ToArray();
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

    /// <summary>Per item, the hidden graybox parts it covers (its box meets the part's bounds): a part hides once every item over it is shown. A hidden part no item meets hides once all items are placed.</summary>
    readonly int[][] _covers;

    /// <summary>Every part of the dressing's hidden list.</summary>
    readonly HashSet<int> _hiddenAll = [];

    static int[][] CoveredParts(AshfallLevel level, AshfallDressing dressing)
    {
        var hidden = dressing.HiddenParts.Distinct().OrderBy(x => x).ToArray();
        var min = new Vector3[hidden.Length]; var max = new Vector3[hidden.Length];
        const float cell = 32f;
        var grid = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < hidden.Length; k++)
        {
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var v in level.Parts[hidden[k]].Corners) { lo = new(Math.Min(lo.X, v.X), Math.Min(lo.Y, v.Y), Math.Min(lo.Z, v.Z)); hi = new(Math.Max(hi.X, v.X), Math.Max(hi.Y, v.Y), Math.Max(hi.Z, v.Z)); }
            min[k] = lo; max[k] = hi;
            for (int gx = (int)Math.Floor(lo.X / cell); gx <= (int)Math.Floor(hi.X / cell); gx++)
                for (int gy = (int)Math.Floor(lo.Y / cell); gy <= (int)Math.Floor(hi.Y / cell); gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var l)) grid[(gx, gy)] = l = [];
                    l.Add(k);
                }
        }
        var result = new int[dressing.Items.Count][];
        var seen = new HashSet<int>();
        for (int i = 0; i < dressing.Items.Count; i++)
        {
            var item = dressing.Items[i];
            if (item.Role != AshfallDressRole.Skin) { result[i] = []; continue; }
            float s = MathF.Sin(item.Yaw), c = MathF.Cos(item.Yaw);
            float ex = MathF.Abs(c) * item.Size.X / 2f + MathF.Abs(s) * item.Size.Y / 2f, ey = MathF.Abs(s) * item.Size.X / 2f + MathF.Abs(c) * item.Size.Y / 2f;
            float x0 = item.Position.X - ex, x1 = item.Position.X + ex, y0 = item.Position.Y - ey, y1 = item.Position.Y + ey, z0 = item.Position.Z, z1 = item.Position.Z + item.Size.Z;
            seen.Clear();
            var list = new List<int>();
            for (int gx = (int)Math.Floor(x0 / cell); gx <= (int)Math.Floor(x1 / cell); gx++)
                for (int gy = (int)Math.Floor(y0 / cell); gy <= (int)Math.Floor(y1 / cell); gy++)
                    if (grid.TryGetValue((gx, gy), out var l))
                        foreach (int k in l)
                            if (seen.Add(k) && min[k].X <= x1 && max[k].X >= x0 && min[k].Y <= y1 && max[k].Y >= y0 && min[k].Z <= z1 && max[k].Z >= z0) list.Add(hidden[k]);
            result[i] = [.. list];
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
        bool libraryDone = _library.Pending == 0;   // read before draining: a model finishing in between is then still placed before the report
        foreach (var id in _library.TakeReady()) _toPlace.Enqueue(id);
        // Models that arrived but did not fit this frame's budget wait for the next frame (TakeReady drains the library's queue).
        while (_toPlace.Count > 0 && (long)Time.GetTicksUsec() <= budgetEnd) Instantiate(_toPlace.Dequeue());
        if (!_reported && libraryDone && _toPlace.Count == 0 && _library.Loaded + _library.Failed > 0)
        {
            _reported = true;
            var assets = _library.All.Where(a => a.Ok).ToList();
            Report = $"{_library.Loaded} models loaded, {_library.Failed} failed, wall {_library.WallMs:F0} ms (sum of per-model {_library.All.Sum(a => a.Ms):F0} ms), " +
                     $"{assets.Sum(a => a.Triangles):N0} triangles at full detail, textures ~{assets.Sum(a => a.TextureBytes) / 1048576.0:F0} MiB, items shown {_shown} ({_shownInstances} mesh instances), items left graybox {_failedItems}";
            GD.Print("ashfall kit: " + Report);
        }
    }

    // Mesh instances of one asset are created once its model arrives; the pieces share the cached meshes.
    readonly Queue<string> _toPlace = new();

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
            foreach (var part in _covers[i])
                if (_partWaiting.TryGetValue(part, out var waiting))
                {
                    _partWaiting[part] = --waiting;
                    if (waiting <= 0) HidePart(part);
                }
        }
    }
}
