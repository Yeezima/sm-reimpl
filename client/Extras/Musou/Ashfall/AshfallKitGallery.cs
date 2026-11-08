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
    /// <summary>Shots drawn with the translucent collision overlay on (Shift+F8 in the map); the camera of each repeats an existing shot.</summary>
    readonly HashSet<string> _overlayShots = [];
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
        if (_args.ContainsKey("ashfall-game-view"))   // look-pass aid: the game camera's field of view and the game environment's SSAO (NetworkView), so views compare with a match
        {
            _camera.Fov = 65f;
            _env.SsaoEnabled = true;
        }
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
            foreach (var piece in asset.Pieces) _game.AddChild(new MeshInstance3D { Name = m.Id, Mesh = piece.Mesh, Transform = AshfallKitView.DrawOf(item, m, piece.Local) });
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
        else if (_args.ContainsKey("ashfall-no-roadway-rule"))
        {
            var before = AshfallMap.RebuildWithoutRoadwayRule();   // draft 8 report aid: the placement without the roadway rule (before/after views)
            (level, dressing) = (before.Level, before.Dressing);
            GD.Print($"ashfall gallery: WITHOUT the draft-8 roadway rule (report aid): {dressing.Items.Count} items");
        }
        else if (_args.ContainsKey("ashfall-no-overlap-rule"))
        {
            var before = AshfallMap.RebuildWithoutOverlapRule();   // draft 7 report aid: the placement without the overlap rule (before/after views)
            (level, dressing) = (before.Level, before.Dressing);
            GD.Print($"ashfall gallery: WITHOUT the draft-7 overlap rule (report aid): {dressing.Items.Count} items");
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
        if (!AshfallStorm.Off) AshfallStorm.Attach(_gray, level, dressing, _sun, _env);   // draft 7's storm look, as in the map
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
        // Draft 5: the same views as pointA, street_gate and street_eastruins with the collision overlay over the models.
        foreach (var (name, from) in new[] { ("collision_pointA", "pointA"), ("collision_gate", "street_gate"), ("collision_eastruins", "street_eastruins") })
        {
            var src = _shots.First(x => x.Name == from);
            _shots.Add((name, src.At, src.Look));
            _overlayShots.Add(name);
        }
        if (!_args.ContainsKey("ashfall-dense")) { AddDraft6Shots(dressing, A, B, C); AddDraft7Shots(level, dressing, B); }
        // --ashfall-cam "name:x,y,z,tx,ty,tz;..." adds views (game frame, metres); a name starting with "collision" is drawn with the overlay.
        if (_args.GetValueOrDefault("ashfall-cam") is { } cams)
            foreach (var spec in cams.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = spec.Split(':');
                var v = parts.Length == 2 ? parts[1].Split(',').Select(x => Number(x, float.NaN)).ToArray() : [];
                if (v.Length != 6 || v.Any(float.IsNaN)) { GD.PrintErr($"ashfall gallery: bad --ashfall-cam \"{spec}\" (name:x,y,z,tx,ty,tz)"); continue; }
                _shots.Add((parts[0], new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5])));
                if (parts[0].StartsWith("collision", StringComparison.Ordinal)) _overlayShots.Add(parts[0]);
            }
    }

    /// <summary>
    /// Draft 6 views for the owner (the Meshy expansion placed; shot names start with <c>d6_</c>, <c>--shots d6</c> takes them all): an overview from a high spot, each objective, the gatehouse and a breach, the keep bridge,
    /// two of the new firing platforms, a street with the new cover, the boundary wall at a grazing angle (the shimmer check), the plaza's new slabs and one collision-overlay view of a tower. The cameras are placed from the
    /// dressing's items, so they follow the placement.
    /// </summary>
    void AddDraft6Shots(AshfallDressing dressing, Vector3 A, Vector3 B, Vector3 C)
    {
        Vector3 P(AshfallDressItem it, float up = 0f) => new(it.Position.X, it.Position.Y, it.Position.Z + up);
        AshfallDressItem? First(Func<AshfallDressItem, bool> f) => dressing.Items.FirstOrDefault(f);
        _shots.Add(("d6_overview", new Vector3(-60, -250, 140), new Vector3(0, 40, 0)));
        _shots.Add(("d6_pointA", A + new Vector3(-30, -34, 12), A));
        _shots.Add(("d6_pointB", B + new Vector3(-36, -36, 12), B));
        _shots.Add(("d6_pointC", C + new Vector3(10, -48, 14), C));
        if (First(i => i.Asset == "AC-042") is { } gate) _shots.Add(("d6_gate", P(gate) + new Vector3(-14, -42, 6), P(gate, 5)));
        // a breach of the ring (its AC-053 mound), from outside the ring
        if (First(i => i.Asset == "AC-053" && MathF.Abs(i.Position.X) > 60f) is { } mound)
        {
            float side = MathF.Sign(mound.Position.X);
            _shots.Add(("d6_breach", P(mound) + new Vector3(-side * 22, -22, 16), P(mound, 2)));   // from inside the ring, above
        }
        if (First(i => i.Asset == "AC-045") is { } bridge) _shots.Add(("d6_keepbridge", P(bridge) + new Vector3(-8, -46, 30), P(bridge, 8)));
        var added = dressing.Items.Where(i => i.Asset is "AC-049" or "AC-050" && i.Role == AshfallDressRole.Prop).ToList();
        if (added.FirstOrDefault(i => i.Asset == "AC-049") is { } low) _shots.Add(("d6_platform_low", P(low) + new Vector3(-22, -26, 10), P(low, 2)));
        if (added.FirstOrDefault(i => i.Asset == "AC-050") is { } mid) _shots.Add(("d6_platform_mid", P(mid) + new Vector3(24, -24, 12), P(mid, 3)));
        // a street with the new low cover: the first AC-054..056 beside the east avenue (x 92), seen along the avenue at eye height
        if (First(i => i.Asset is "AC-054" or "AC-055" or "AC-056" && MathF.Abs(i.Position.X - 92f) < 22f && MathF.Abs(i.Position.Y) < 180f) is { } cover)
            _shots.Add(("d6_street_cover", new Vector3(92, cover.Position.Y - 34, 2.4f), new Vector3(cover.Position.X, cover.Position.Y + 10, 1f)));
        _shots.Add(("d6_plaza", B + new Vector3(-18, -36, 7), B));
        _shots.Add(("d6_boundary_grazing", new Vector3(-215, -226, 2.6f), new Vector3(120, -240, 9)));
        int ti = Enumerable.Range(0, dressing.Items.Count).FirstOrDefault(i => dressing.Items[i].Asset == "AC-052" && i < dressing.ItemParts.Count && dressing.ItemParts[i].Count > 0, -1);
        if (ti >= 0 && dressing.Items[ti] is var tower)
        {
            _shots.Add(("d6_collision_tower", P(tower) + new Vector3(-36, -38, 22), P(tower, 10)));
            _overlayShots.Add("d6_collision_tower");
        }
    }

    /// <summary>
    /// Draft 7 views (<c>--shots d7</c>): the storm look from above and at ground level facing the low sun, a fire spot, a smoke plume, and the place of the deepest overlap the draft-7 rule removed (a platform run into a ruin
    /// in the placement without the rule; with <c>--ashfall-no-overlap-rule</c> the same camera shows it before). Cameras follow the level.
    /// </summary>
    void AddDraft7Shots(AshfallLevel level, AshfallDressing dressing, Vector3 B)
    {
        float az = Mathf.DegToRad(AshfallStorm.SunAzimuth);
        var sunward = new Vector3(MathF.Sin(az), MathF.Cos(az), 0f);
        var boxes = AshfallOverlap.Boxes(level, dressing);
        _shots.Add(("d7_overview", new Vector3(-20, -232, 95), new Vector3(0, 60, 0)));
        Add7("d7_plaza_sun", B + new Vector3(0, 0, 1f), -sunward, 30f, 2.6f, boxes, sunward * 120f + new Vector3(0, 0, 14f));
        _shots.Add(("d7_street_sun", new Vector3(0, -120, 2.6f), new Vector3(-30, 60, 14f)));
        var spots = AshfallAtmosphere.FireSpots(level, dressing);
        // the fire nearest the map's middle, from 10 m out of its ruin at eye height; the southernmost plume from 60 m inside the map
        if (spots.Where(s => !s.Plume).OrderBy(s => s.Position.X * s.Position.X + s.Position.Y * s.Position.Y).FirstOrDefault() is { Size: > 0 } fire)
            Add7("d7_fire", new Vector3(fire.Position.X, fire.Position.Y, fire.Position.Z + 1.2f), new Vector3(fire.Out.X, fire.Out.Y, 0f), 10f, 2.4f, boxes);
        if (spots.Where(s => s.Plume).OrderBy(s => s.Position.Y).FirstOrDefault() is { Size: > 0 } plume)
            _shots.Add(("d7_plume", new Vector3(plume.Position.X, plume.Position.Y, 0f) + new Vector3(plume.Out.X, plume.Out.Y, 0f) * 150f + new Vector3(0, 0, 12f), new Vector3(plume.Position.X, plume.Position.Y, plume.Position.Z + 30f)));
        // the deepest overlap of the placement without the rule that involves a platform (the owner's screenshot: the stronghold's terraces, an AC-008 slab run into a ruin), from above and behind like his view
        var before = AshfallMap.RebuildWithoutOverlapRule();
        var pairs = AshfallOverlap.Find(AshfallOverlap.Boxes(before.Level, before.Dressing), AshfallOverlap.Tolerance, AshfallMap.OverlapExempt(before.Dressing));
        if (pairs.Where(q => before.Dressing.Items[q.A].Asset is "AC-049" or "AC-050" or "AC-008" || before.Dressing.Items[q.B].Asset is "AC-049" or "AC-050" or "AC-008").OrderByDescending(q => q.Depth).FirstOrDefault() is { Depth: > 0 } worst)
        {
            var at = new Vector3(worst.At.X, worst.At.Y, 1.5f);
            _shots.Add(("d7_overlap_spot", at + new Vector3(-6, -22, 12f), at));
            GD.Print($"ashfall gallery: d7_overlap_spot at ({worst.At.X:0}, {worst.At.Y:0}): {before.Dressing.Items[worst.A].Asset} x {before.Dressing.Items[worst.B].Asset}, {worst.Depth:0.00} m deep without the rule");
        }
    }

    /// <summary>
    /// A view of <paramref name="look"/> from <paramref name="dist"/> m along <paramref name="dir"/> at <paramref name="height"/> m: the first of the directions turned 0, +-30, +-60 ... degrees and the distances 1, 0.7, 0.5 that
    /// stands in free air inside the boundary with nothing between it and the target (the boxes of the level's items).
    /// </summary>
    void Add7(string name, Vector3 look, Vector3 dir, float dist, float height, List<(int Item, AshfallBox Box)> boxes, Vector3? lookOffset = null)
    {
        bool Solid(Vector3 p) => MathF.Abs(p.X) > 234f || MathF.Abs(p.Y) > 234f || boxes.Any(b => p.Z > b.Box.Z0 - 0.5f && p.Z < b.Box.Z1 + 0.5f && AshfallAtmosphere.Inside(b.Box, new Num.Vector2(p.X, p.Y), 0.6f));
        bool Clear(Vector3 a, Vector3 b) { for (int i = 1; i < 24; i++) if (Solid(a.Lerp(b, i / 24f))) return false; return true; }
        var target = look + (lookOffset ?? Vector3.Zero);
        foreach (float f in new[] { 1f, 0.7f, 0.5f })
            for (int k = 0; k < 12; k++)
            {
                float turn = (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * MathF.PI / 6f;
                var d = new Vector3(dir.X * MathF.Cos(turn) - dir.Y * MathF.Sin(turn), dir.X * MathF.Sin(turn) + dir.Y * MathF.Cos(turn), 0f).Normalized();
                var at = new Vector3(look.X, look.Y, 0f) + d * dist * f + new Vector3(0, 0, AshfallMap.SurfaceAt(look.X + d.X * dist * f, look.Y + d.Y * dist * f) + height);
                if (Solid(at) || !Clear(at, look)) continue;
                _shots.Add((name, at, target));
                return;
            }
        _shots.Add((name, look + dir * dist + new Vector3(0, 0, height + 10f), target));
        GD.Print($"ashfall gallery: no clear view for {name}; taken from above");
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
            if (_map && ShimmerMode) { if (Shimmer()) Done(0); return; }   // hotfix/ashfall-floor-shimmer: the floor shimmer measurement (AshfallKitGallery.Shimmer.cs)
            var wanted = (_args.GetValueOrDefault("shots") ?? "all").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = _shots.Where(s => wanted.Contains("all") || wanted.Contains(s.Name) || (wanted.Contains("overview") && s.Name.StartsWith("overview", StringComparison.Ordinal)) || (wanted.Contains("d6") && s.Name.StartsWith("d6_", StringComparison.Ordinal)) || (wanted.Contains("d7") && s.Name.StartsWith("d7_", StringComparison.Ordinal))).ToList();
            if (_step >= list.Count)
            {
                if (_map && (_args.ContainsKey("ashfall-dense") || _args.ContainsKey("ashfall-perf"))) { _phase = 2; _step = 0; _wait = 0; _sample = 0; return; }
                Done(0); return;
            }
            var s = list[_step];
            if (!_aimed) { Aim(s.At, s.Look); if (_kit is not null) _kit.SetOverlay(_overlayShots.Contains(s.Name)); _aimed = true; _wait = 3; return; }   // let the frames with the new camera render
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
        var wantedPerf = (_args.GetValueOrDefault("ashfall-perf") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var views = _shots.Where(x => !_overlayShots.Contains(x.Name) && (wantedPerf.Length == 0 || wantedPerf.Contains(x.Name))).ToList();
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
