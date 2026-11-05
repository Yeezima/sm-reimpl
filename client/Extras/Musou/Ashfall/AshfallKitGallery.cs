using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmSim.Extras.Musou.Ashfall;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Test aid for the Ashfall kit look (docs/musou-ashfall-graybox.md "Kit look (client)"): <c>--ashfall-kit-gallery</c> lays out the kit's 40 models, each fitted by the contract's rule into a
/// wireframe box (the manifest's proposed footprint and height; the ground tiles at their measured size), labelled, in a row per category, and loads them in the background like the map does.
/// <c>--shots overview,AC-001,..|all</c> (default all: a shot per category row and per model) saved to <c>--out</c> (default <c>%LOCALAPPDATA%\sm-reimpl\cache\previews\ashfall-kit</c>; never inside the repository
/// or the install), then quit (needs <c>godot.cmd --windowed</c>; headless only loads and reports). <c>--ashfall-tex N</c>, <c>--ashfall-lods off</c> as in the map.
/// <c>--ashfall-dress</c> adds the dressing pass's items (<c>AshfallMap.BuildWithDressing</c>) as a map view instead of the gallery; <c>--ashfall-dense</c> does the same with a synthetic
/// dressing (a Skin item over every Wall, Cover and Rubble part, test aid only) and measures frame time and draw load at a few viewpoints in both looks (KITPERF lines).
/// **Placeholder demonstration** (the models are the owner's generated drafts).
/// </summary>
public partial class AshfallKitGallery : Node3D
{
    static readonly Basis GameToGodot = new(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0));

    readonly Dictionary<string, string?> _args;
    readonly Node3D _game = new() { Name = "GameSpace", Basis = GameToGodot };
    readonly AshfallKitLibrary _library = new();
    Camera3D _camera = null!;
    DirectionalLight3D _sun = null!;
    Godot.Environment _env = null!;
    AshfallKitView? _kit;
    int _kitItemCount;
    AshfallView? _gray;
    AshfallLevel? _level;
    readonly List<(string Name, Vector3 At, Vector3 Look)> _shots = [];
    readonly List<(AshfallDressItem Item, AshfallKitModel Model, Vector3 Size)> _boxes = [];
    string _outDir = "";
    int _step, _wait, _sample;
    double _sumDelta, _frame;
    int _phase;   // 0 load, 1 shots, 2 perf, 3 done
    bool _map, _headless, _aimed;
    long _loadStart;
    readonly List<string> _perfLines = [];

    public AshfallKitGallery(Dictionary<string, string?> args) { _args = args; }
    public AshfallKitGallery() : this(new Dictionary<string, string?>()) { }

    static Vector3 G(Vector3 v) => GameToGodot * v;

    public override void _Ready()
    {
        _headless = DisplayServer.GetName() == "headless";
        _outDir = _args.GetValueOrDefault("out") ?? System.IO.Path.Combine(SmContent.GameInstall.DataRoot, "cache", "previews", "ashfall-kit");
        var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        if (SmFormats.InstallPaths.IsWithin(System.IO.Path.GetFullPath(_outDir), repoRoot)) { GD.PrintErr($"ashfall gallery: {_outDir} is inside the repository; screenshots refused"); _headless = true; }
        if (_args.GetValueOrDefault("ashfall-tex") is { } t && int.TryParse(t, out var tex)) _library.TextureMax = tex;
        if (_args.GetValueOrDefault("ashfall-lods") == "off") _library.GenerateLods = false;
        if (!AshfallKitLibrary.Available) { GD.Print($"ashfall gallery: the kit folder {AshfallKitLibrary.Root} does not exist; nothing to show"); GetTree().Quit(1); return; }
        AddChild(_game);
        _camera = new Camera3D { Fov = 40f, Near = 0.1f, Far = 2000f, Current = true };
        AddChild(_camera);
        _sun = new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = true, RotationDegrees = new Vector3(-50f, 40f, 0f), DirectionalShadowMaxDistance = 160f };
        AddChild(_sun);
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.30f, 0.30f, 0.32f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.6f, 0.6f, 0.62f), AmbientLightEnergy = 0.9f,
        };
        AddChild(new WorldEnvironment { Environment = _env });
        if (!_headless) DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        _loadStart = (long)Time.GetTicksMsec();
        _map = _args.ContainsKey("ashfall-dense") || _args.ContainsKey("ashfall-dress");
        if (_map) BuildMap(); else BuildGallery();
    }

    // ---- the gallery of 40 boxes -------------------------------------------------------------------------------------------------------------------------

    static float Number(string text, float fallback) => float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    static Vector3 BoxOf(AshfallKitModel m)
    {
        var raw = m.RawSize;
        if (m.ProposedFootprint.Contains('x') && m.ProposedFootprint.Split('x') is { Length: 2 } f)
            return new Vector3(Number(f[0], raw.X), Number(f[1], raw.Z), Number(m.ProposedHeight, raw.Y));
        return new Vector3(raw.X, raw.Z, raw.Y);   // ground tiles: measured (metres)
    }

    void BuildGallery()
    {
        float y = 0;
        var ids = new List<string>();
        foreach (var group in AshfallKit.Models.GroupBy(m => m.Category))
        {
            float x = 0, rowDepth = 0, rowTop = y;
            float maxW = 90f;
            var items = group.ToList();
            foreach (var m in items)
            {
                var size = BoxOf(m);
                if (x > 0 && x + size.X > maxW) { x = 0; y += rowDepth + 8f; rowDepth = 0; }
                var item = new AshfallDressItem(m.Id, new Num.Vector3(x + size.X / 2f, y + size.Y / 2f, 0f), 0f, new Num.Vector3(size.X, size.Y, size.Z), false, AshfallDressRole.Decoration, -1);
                _boxes.Add((item, m, size));
                AddBox(m, size, new Vector3(x + size.X / 2f, y + size.Y / 2f, 0f));
                ids.Add(m.Id);
                x += size.X + 6f;
                rowDepth = Math.Max(rowDepth, size.Y);
            }
            _shots.Add(($"overview_{group.Key}", new Vector3(maxW / 2f, rowTop - 38f, 30f), new Vector3(maxW / 2f, rowTop + (y - rowTop) / 2f + 4f, 4f)));
            y += rowDepth + 14f;
        }
        foreach (var (item, m, size) in _boxes)
        {
            var c = new Vector3(item.Position.X, item.Position.Y, item.Position.Z + size.Z / 2f);
            float d = Math.Max(size.Length() * 1.4f, 6f);
            // from the south-east above: the glTF front (+Z) faces south at yaw 0
            _shots.Add((m.Id, c + new Vector3(0.55f, -1f, 0.55f).Normalized() * d, c));
        }
        _library.Request(ids);
        _game.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(260, 260, 0.2f) }, Position = new Vector3(45, 60, -0.45f), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.20f, 0.20f, 0.21f) } });
        _sun.RotationDegrees = new Vector3(-50f, -30f, 0f);
    }

    void AddBox(AshfallKitModel m, Vector3 size, Vector3 baseCentre)
    {
        var lines = new ImmediateMesh();
        lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
        var a = baseCentre + new Vector3(-size.X / 2f, -size.Y / 2f, 0);
        var b = baseCentre + new Vector3(size.X / 2f, size.Y / 2f, size.Z);
        Vector3[] p = [new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z), new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z)];
        int[] e = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        foreach (int i in e) lines.SurfaceAddVertex(p[i]);
        lines.SurfaceEnd();
        _game.AddChild(new MeshInstance3D { Name = "Box" + m.Id, Mesh = lines, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.85f, 0.2f) } });
        _game.AddChild(new Label3D
        {
            Name = "Label" + m.Id, Text = $"{m.Id} {m.Name}\n{size.X:0.##} x {size.Y:0.##} x {size.Z:0.##} m", FontSize = 48, PixelSize = 0.0009f, FixedSize = true, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = new Color(1f, 0.95f, 0.7f), OutlineSize = 12, Shaded = false, NoDepthTest = true, Position = new Vector3(baseCentre.X, baseCentre.Y, size.Z + 1.2f),
        });
    }

    void PlaceGalleryModels()
    {
        foreach (var (item, m, size) in _boxes)
        {
            var asset = _library.Get(m.Id);
            if (asset is not { Ok: true }) continue;
            var fit = AshfallKitView.Fit(item, m);
            foreach (var piece in asset.Pieces) _game.AddChild(new MeshInstance3D { Name = m.Id, Mesh = piece.Mesh, Transform = fit * piece.Local });
        }
    }

    // ---- a map view (the dressing pass's items, or a synthetic dense one) ------------------------------------------------------------------------------

    void BuildMap()
    {
        AshfallLevel level; AshfallDressing dressing;
        if (_args.ContainsKey("ashfall-dense"))
        {
            level = AshfallMap.Build();
            dressing = SyntheticDressing(level);
            GD.Print($"ashfall gallery: synthetic dressing {dressing.Items.Count} items over {dressing.HiddenParts.Count} parts (test aid only)");
        }
        else
        {
            (level, dressing) = AshfallMap.BuildWithDressing();
            GD.Print($"ashfall gallery: dressing version {dressing.Version}: {dressing.Items.Count} items, {dressing.HiddenParts.Count} hidden parts");
        }
        _level = level;
        _gray = AshfallView.Build(level);
        _game.AddChild(_gray);
        AshfallView.ApplyLook(_sun, _env);
        _kit = new AshfallKitView(level, dressing, _gray); _kitItemCount = dressing.Items.Count;
        _game.AddChild(_kit);
        _kit.SetKit(true);
        var c = level.Points.Select(p => p.Center).ToList();
        var A = new Vector3(c[0].X, c[0].Y, c[0].Z); var B = new Vector3(c[1].X, c[1].Y, c[1].Z); var C = new Vector3(c[2].X, c[2].Y, c[2].Z);
        _shots.Add(("pointA", A + new Vector3(-40, -40, 14), A));
        _shots.Add(("pointB", B + new Vector3(-40, -40, 14), B));
        _shots.Add(("pointC", C + new Vector3(0, -50, 16), C));
        _shots.Add(("overview", B + new Vector3(0, -230, 150), B));
        // Ground level (eye about 2.5 m above the surface): what a player sees.
        _shots.Add(("street_spawn", new Vector3(0, -150, 2.8f), new Vector3(0, -40, 4)));
        _shots.Add(("street_westavenue", new Vector3(-92, -120, 2.5f), new Vector3(-92, 40, 6)));
        _shots.Add(("street_plaza", new Vector3(-40, -40, 2.8f), new Vector3(20, 20, 3)));
        _shots.Add(("street_gate", new Vector3(0, 70, 2.5f), new Vector3(0, 140, 10)));
        _shots.Add(("street_eastruins", new Vector3(120, -20, 2.8f), new Vector3(170, 20, 6)));
        _shots.Add(("street_craters", new Vector3(-110, -30, 2.8f), new Vector3(-180, 30, 2)));
    }

    /// <summary>A Skin item over every Wall, Cover and Rubble part (its box = the part's bounds, a model of a fitting category), for the measurement only.</summary>
    static AshfallDressing SyntheticDressing(AshfallLevel level)
    {
        var walls = AshfallKit.Models.Where(m => m.Category == AshfallKitCategory.Architecture).ToArray();
        var cover = AshfallKit.Models.Where(m => m.Category == AshfallKitCategory.Cover).ToArray();
        var rubble = AshfallKit.Models.Where(m => m.Category == AshfallKitCategory.Debris).ToArray();
        var items = new List<AshfallDressItem>();
        var hidden = new List<int>();
        for (int i = 0; i < level.Parts.Count; i++)
        {
            var part = level.Parts[i];
            var pool = part.Kind switch { AshfallKind.Wall => walls, AshfallKind.Cover => cover, AshfallKind.Rubble => rubble, _ => null };
            if (pool is null || pool.Length == 0 || part.Corners.Length == 0) continue;
            var min = new Num.Vector3(float.MaxValue); var max = new Num.Vector3(float.MinValue);
            foreach (var v in part.Corners) { min = Num.Vector3.Min(min, v); max = Num.Vector3.Max(max, v); }
            var size = max - min;
            if (size.X < 0.5f || size.Y < 0.5f || size.Z < 0.2f) continue;
            items.Add(new AshfallDressItem(pool[i % pool.Length].Id, new Num.Vector3((min.X + max.X) / 2f, (min.Y + max.Y) / 2f, min.Z), 0f, size, false, AshfallDressRole.Skin, i));
            hidden.Add(i);
        }
        return new AshfallDressing(items, hidden, 0);
    }

    // ---- the frame loop ----------------------------------------------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_phase == 0)
        {
            if (_map) { if (_kit!.Library.Pending > 0 || _kit.Library.Loaded + _kit.Library.Failed == 0 || _kit.ItemsShown + _kit.ItemsFailed < _kitItemCount) return; }
            else if (_library.Pending > 0) return;
            var lib = _map ? _kit!.Library : _library;
            var assets = lib.All.OrderBy(a => a.Id).ToList();
            GD.Print($"KITLOAD all {assets.Count} requested: {lib.Loaded} ok, {lib.Failed} failed, wall {lib.WallMs:F0} ms, sum of per-model {assets.Sum(a => a.Ms):F0} ms, triangles at full detail {assets.Sum(a => a.Triangles):N0}, textures ~{assets.Sum(a => a.TextureBytes) / 1048576.0:F0} MiB");
            foreach (var a in assets)
                GD.Print($"KITLOAD {a.Id} {(a.Ok ? "ok" : "FAILED " + a.Error)} {a.Ms:F0} ms, {a.Triangles:N0} tris, {a.Pieces.Length} mesh(es), lod surfaces {a.LodSurfaces}, {a.TextureNote}");
            if (!_map) PlaceGalleryModels();
            _phase = 1; _wait = 8;
            if (_headless) { GD.Print("ashfall gallery: headless, no screenshots (use godot.cmd --windowed)"); Done(0); }
            return;
        }
        if (_phase == 1)
        {
            if (_wait > 0) { _wait--; return; }
            var wanted = (_args.GetValueOrDefault("shots") ?? "all").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = _shots.Where(s => wanted.Contains("all") || wanted.Contains(s.Name) || (wanted.Contains("overview") && s.Name.StartsWith("overview", StringComparison.Ordinal))).ToList();
            if (_step >= list.Count)
            {
                if (_map && _args.ContainsKey("ashfall-dense")) { _phase = 2; _step = 0; _wait = 0; _sample = 0; return; }
                Done(0); return;
            }
            var s = list[_step];
            if (!_aimed) { Aim(s.At, s.Look); _aimed = true; _wait = 3; return; }   // let the frames with the new camera render
            _aimed = false;
            System.IO.Directory.CreateDirectory(_outDir);
            string path = System.IO.Path.Combine(_outDir, (_map ? "map_" : "") + s.Name + ".png");
            var err = GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print(err == Error.Ok ? $"screenshot -> {path}" : $"screenshot failed: {err}");
            _step++;
            return;
        }
        if (_phase == 2) { Perf(delta); return; }
    }

    void Aim(Vector3 at, Vector3 look)
    {
        _camera.Position = G(at);
        _camera.LookAt(G(look), Vector3.Up);
    }

    // Perf: for each viewpoint and each look, 25 frames to settle, then 90 sampled frames: mean frame time, draw calls, primitives, objects.
    void Perf(double delta)
    {
        var views = _shots.ToList();
        int total = views.Count * 2;
        if (_step >= total)
        {
            foreach (var l in _perfLines) GD.Print(l);
            Done(0);
            return;
        }
        var s = views[_step / 2];
        bool kit = _step % 2 == 0;
        if (_sample == 0)
        {
            _kit!.SetKit(kit);
            Aim(s.At, s.Look);
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            _sumDelta = 0;
        }
        _sample++;
        if (_sample > 25) _sumDelta += delta;
        if (_sample == 25 + 90)
        {
            var rid = GetViewport().GetViewportRid();
            double cpu = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid), gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
            long calls = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
            long prims = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
            long objs = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame);
            _perfLines.Add($"KITPERF {s.Name,-8} {(kit ? "kit    " : "graybox")} frame {_sumDelta / 90 * 1000:F2} ms ({90 / _sumDelta:F0} fps), render cpu {cpu:F2} ms gpu {gpu:F2} ms, draw calls {calls:N0}, primitives {prims:N0}, objects {objs:N0}, lod threshold {GetViewport().MeshLodThreshold:F1}");
            _step++; _sample = 0;
        }
    }

    void Done(int code)
    {
        _phase = 3;
        GD.Print($"ashfall gallery: done in {(long)Time.GetTicksMsec() - _loadStart} ms since start");
        GetTree().Quit(code);
    }
}
