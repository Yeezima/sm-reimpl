using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>import-sm1-map: an original map's placements become editable objects under one group; synthetic source only, no original bytes.</summary>
public sealed class Sm1ImportTests : IDisposable
{
    readonly Lab _lab = new();
    public void Dispose() => _lab.Dispose();

    sealed class ImportSm1(int extra = 0, Sm1Spawns? spawns = null) : ISm1Source
    {
        public static readonly Matrix4x4 A = MapScene.Transform(new Vector3(10, 20, 0), new Vector3(0, 0, 90), Vector3.One);
        public static readonly Matrix4x4 B = MapScene.Transform(new Vector3(-3.25f, 8.5f, 1.5f), new Vector3(30, 45, 60), new Vector3(1, 2, 1.5f));

        public Sm1Spawns? LoadSpawns(string map) => spawns;

        public Sm1Map LoadMap(string map)
        {
            if (map != "mp_fake") throw new MapException("unknown-map", $"No map '{map}'.");
            var objects = new List<Sm1Object>
            {
                new("wall_a", "def_wall", "l", A, 0),
                new("tilted", "def_wall", "l", B, 0),
                new("marker", "def_none", "l", Matrix4x4.Identity, 0),
                new("shear", "def_wall", "l", new Matrix4x4(1, 0, 0, 0, 0.5f, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1), 0),
                new("armoury", "def_wall", "l", Matrix4x4.Identity, 0),
                new("", "def_wall", "l", MapScene.Transform(new Vector3(1, 2, 3), Vector3.Zero, Vector3.One), 0),
            };
            for (int i = 0; i < extra; i++) objects.Add(new($"x{i}", "def_wall", "l", MapScene.Transform(new Vector3(i, 0, 0), Vector3.Zero, Vector3.One), 0));
            var defs = new Dictionary<string, Sm1Definition>(StringComparer.OrdinalIgnoreCase)
            {
                ["def_wall"] = new("def_wall", 1, new Bounds(new Vector3(-2, -0.5f, 0), new Vector3(2, 0.5f, 3))),
                ["def_none"] = new("def_none", 0, null),
            };
            return new Sm1Map(map, objects, defs, []) { Hidden = new HashSet<int> { 4 } };
        }
    }

    static void Fill(MapSession s, int count)
    {
        var ops = Enumerable.Range(0, count).Select(_ => Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"}}""")).ToList();
        Assert.True(s.Apply(ops).Ok);
    }

    /// <summary>With no spawn data the import adds a placeholder spawn area and floor for each side and three capture points with floors: ten pieces.</summary>
    const int Placeholders = 10;

    MapSession Session(int extra = 0) => MapSession.New("imported", _lab.Library, new ImportSm1(extra));

    [Fact]
    public void Import_makes_one_group_and_an_editable_object_per_drawn_placement_with_exact_transforms()
    {
        var s = Session();
        var r = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.True(r.Ok, r.ToJson());
        var group = s.Document.Objects.Single(o => o.Parent is null);
        Assert.Equal("mp_fake", group.Name);
        Assert.Null(group.Asset);
        var kids = s.Document.ChildrenOf(group.Id).Where(k => k.Asset is not null).ToList();
        Assert.Equal(3, kids.Count);   // wall_a, tilted and the unnamed one; marker (no visual), shear and the hidden armoury piece are left out
        Assert.All(kids, k => Assert.Equal(AssetRef.Sm1("mp_fake", "def_wall"), k.Asset));
        Assert.True(TransformMath.Close(kids[0].Transform.ToMatrix(), ImportSm1.A, 1e-4f));
        Assert.True(TransformMath.Close(kids[1].Transform.ToMatrix(), ImportSm1.B, 1e-4f));
        Assert.Equal("def_wall", kids[2].Name);   // an unnamed placement is named after its definition
        Assert.Contains(r.Warnings, w => w.Contains("Imported 3 of 6"));
        Assert.Contains(r.Warnings, w => w.Contains("sheared"));
        Assert.Contains(r.Warnings, w => w.Contains("Not imported"));
        Assert.Equal(r.Created.Single(), group.Id);
    }

    [Fact]
    public void Import_saves_as_a_new_map_holding_only_references_and_loads_back_the_same()
    {
        var s = Session();
        Assert.True(s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake","name":"My Remix"}""")).Ok);
        var path = Path.Combine(_lab.Dir, "cm_remix.smmap");
        s.Save(path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"definition\": \"def_wall\"", text);
        Assert.DoesNotContain("vertices", text, StringComparison.OrdinalIgnoreCase);   // references only, never mesh data
        Assert.True(new FileInfo(path).Length < 20_000);
        var back = MapSession.Load(path, _lab.Library, new ImportSm1());
        Assert.Equal(s.Document.ToJson(), back.Document.ToJson());
        Assert.Equal("My Remix", back.Document.Objects.Single(o => o.Parent is null).Name);
        Assert.Equal(4 + Placeholders, back.Document.Objects.Count);
        // the imported objects are ordinary objects: move one, delete one, undo both
        var kid = back.Document.Objects.First(o => o.Parent is not null);
        Assert.True(back.Apply(Lab.Op($$"""{"op":"transform","id":"{{kid.Id}}","move":[5,0,0]}""")).Ok);
        Assert.True(back.Apply(Lab.Op($$"""{"op":"delete","ids":["{{kid.Id}}"]}""")).Ok);
        Assert.Equal(3 + Placeholders, back.Document.Objects.Count);
        Assert.True(back.Undo().Ok);
        Assert.Equal(4 + Placeholders, back.Document.Objects.Count);
    }

    [Fact]
    public void Import_is_one_undo_step_and_unknown_map_or_missing_install_fail_cleanly()
    {
        var s = Session();
        Assert.True(s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}""")).Ok);
        Assert.True(s.Undo().Ok);
        Assert.Empty(s.Document.Objects);
        var bad = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_nope"}"""));
        Assert.False(bad.Ok);
        Assert.Equal("unknown-map", bad.Errors[0].Code);
        Assert.Empty(s.Document.Objects);
        var none = MapSession.New("x", _lab.Library).Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.Equal("missing-install", none.Errors[0].Code);
    }

    [Fact]
    public void Import_stops_at_the_limit_and_says_so()
    {
        var s = Session(extra: Sm1Import.MaxObjects);
        var r = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.True(r.Ok, r.ToJson());
        Assert.Equal(Sm1Import.MaxObjects + 1, s.Document.Objects.Count);
        Assert.Contains(r.Warnings, w => w.Contains("left out") && w.Contains(Sm1Import.MaxObjects.ToString()));
        // and the saved file stays under the loader's limits
        var json = s.Document.ToJson();
        Assert.True(json.Length < MapDocument.MaxChars, $"{json.Length} chars");
        Assert.Equal(Sm1Import.MaxObjects + 1, MapDocument.Parse(json).Objects.Count);
    }

    [Fact]
    public void A_second_import_keeps_the_whole_map_under_the_object_limit()
    {
        var s = Session(extra: 12000);
        Assert.True(s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}""")).Ok);
        var r = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake","name":"again"}"""));
        Assert.True(r.Ok, r.ToJson());
        Assert.True(s.Document.Objects.Count <= MapDocument.MaxObjects, s.Document.Objects.Count.ToString());
        Assert.Contains(r.Warnings, w => w.Contains("left out") && w.Contains(MapDocument.MaxObjects.ToString()));
        Assert.Equal(s.Document.Objects.Count, MapDocument.Parse(s.Document.ToJson()).Objects.Count);   // reopens
    }

    [Fact]
    public void Import_into_a_nearly_full_map_takes_what_fits_and_a_full_map_is_refused()
    {
        var s = Session(extra: 30);
        Fill(s, MapDocument.MaxObjects - 10);
        var r = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.True(r.Ok, r.ToJson());
        Assert.Equal(MapDocument.MaxObjects, s.Document.Objects.Count);   // filled to the limit exactly (the group plus what fits)
        Assert.Contains(r.Warnings, w => w.Contains("left out"));
        Assert.Equal(s.Document.Objects.Count, MapDocument.Parse(s.Document.ToJson()).Objects.Count);
        Fill(s, MapDocument.MaxObjects - s.Document.Objects.Count);
        var full = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.False(full.Ok);
        Assert.Equal("too-big", full.Errors[0].Code);
    }

    // ---- spawns and doors (owner 2026-11-19): the original's start areas and blockers become editable pieces ----

    static Sm1Spawns FakeSpawns(bool chaosToo = true, bool points = true) => new(
        [
            new MapStartArea("mkr_start_marine", SmSim.Team.SpaceMarines, new Vector3(50, -30, 5.5f), 0f, new Vector3(10, 8, 5), 1),
            .. chaosToo ? [new MapStartArea("mkr_Start_Ork", SmSim.Team.Chaos, new Vector3(-40, 20, 5.5f), MathF.PI, new Vector3(10, 8, 5), 1)] : Array.Empty<MapStartArea>(),
        ],
        [
            new MapBlocker("marine_blocker_01", SmSim.Team.SpaceMarines, new Vector3(60, -30, 8), MathF.PI / 2f, new Vector3(1, 11, 15)),
            new MapBlocker("chaos_blocker_01", SmSim.Team.Chaos, new Vector3(-50, 20, 8), 0f, new Vector3(11, 1, 15)),
        ],
        points ? [new MapControlPoint("mkr_objective_1", new Vector3(5, 0, 2), 0f, new Vector3(8, 8, 4), 3), new MapControlPoint("mkr_objective_2", new Vector3(5, 30, 2), 0f, new Vector3(6, 6, 4), 3)] : []);

    [Fact]
    public void Import_places_the_originals_spawn_areas_and_doors_as_pieces_at_the_marker_places()
    {
        var s = MapSession.New("imported", _lab.Library, new ImportSm1(spawns: FakeSpawns()));
        var r = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.True(r.Ok, r.ToJson());
        var pieces = s.Document.Objects.Where(o => o.Piece is not null).ToList();
        var marines = pieces.Single(o => o.Piece!.Kind == PieceKind.SpawnArea && o.Piece.Side == PieceSpec.Marines);
        Assert.Equal(new Vector3(50, -30, 3f), marines.Transform.Position);   // the piece sits on the floor: the marker's centre minus half its height
        Assert.Equal(new Vector3(10, 8, 5), marines.Transform.Scale);
        var chaos = pieces.Single(o => o.Piece!.Kind == PieceKind.SpawnArea && o.Piece.Side == PieceSpec.Chaos);
        Assert.Equal(180f, MathF.Abs(chaos.Transform.Rotation.Z), 3);
        var doors = pieces.Where(o => o.Piece!.Kind == PieceKind.Blocker).ToList();
        Assert.Equal(2, doors.Count);
        Assert.Equal(90f, doors.Single(d => d.Piece!.Side == PieceSpec.Marines).Transform.Rotation.Z, 3);
        Assert.All(pieces, p => Assert.NotNull(p.Parent));   // under the import group, so they move with it
        Assert.Contains(r.Warnings, w => w.StartsWith("Spawns: 2 spawn area(s) and 2 spawn door(s)"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("placeholder was added") || w.Contains("could not be read"));
    }

    [Fact]
    public void An_imported_map_passes_the_map_check_and_compiles_to_the_originals_start_areas()
    {
        var s = MapSession.New("imported", _lab.Library, new ImportSm1(spawns: FakeSpawns()));
        Assert.True(s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}""")).Ok);
        var map = CustomMap.Compile(s.Document);
        var report = MapCheck.Run(map);
        Assert.True(report.AnnihilationReady, report.ToText());
        Assert.DoesNotContain(report.Issues, i => i.Code is "no-spawn-marines" or "no-spawn-chaos" or "spawn-no-floor" or "no-ground" or "no-door-marines" or "no-door-chaos");
        var m = map.Content.StartAreas.Single(a => a.Side == SmSim.Team.SpaceMarines);
        Assert.Equal(new Vector3(50, -30, 5.5f), m.Position);   // back to the marker's own centre
        Assert.Equal(new Vector3(10, 8, 5), m.Scale);
        var c = map.Content.StartAreas.Single(a => a.Side == SmSim.Team.Chaos);
        Assert.Equal(MathF.PI, MathF.Abs(c.Yaw), 3);
        Assert.Equal(2, map.Content.Blockers.Count);
        Assert.Equal(MathF.PI / 2f, map.Content.Blockers.Single(b => b.Side == SmSim.Team.SpaceMarines).Yaw, 3);
    }

    [Fact]
    public void A_side_the_data_lacks_gets_a_placeholder_and_the_warning_says_so()
    {
        var s = MapSession.New("imported", _lab.Library, new ImportSm1(spawns: FakeSpawns(chaosToo: false)));
        var r = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.True(r.Ok, r.ToJson());
        var chaos = s.Document.Objects.Single(o => o.Piece is { Kind: PieceKind.SpawnArea, Side: PieceSpec.Chaos });
        Assert.Contains("placeholder", chaos.Name);
        Assert.Contains(r.Warnings, w => w.Contains("no Chaos start area") && w.Contains("placeholder"));
        Assert.True(MapCheck.Run(CustomMap.Compile(s.Document)).AnnihilationReady);
    }

    [Fact]
    public void Unreadable_spawn_data_gives_two_placeholders_and_a_warning()
    {
        var s = Session();   // this fake source has no spawn reader
        var r = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.True(r.Ok, r.ToJson());
        Assert.Equal(2, s.Document.Objects.Count(o => o.Piece?.Kind == PieceKind.SpawnArea));
        Assert.Contains(r.Warnings, w => w.Contains("could not be read") && w.Contains("placeholders"));
        Assert.True(MapCheck.Run(CustomMap.Compile(s.Document)).AnnihilationReady);
    }

    [Fact]
    public void Imported_spawn_pieces_are_normal_pieces_that_can_be_moved_deleted_and_saved()
    {
        var s = MapSession.New("imported", _lab.Library, new ImportSm1(spawns: FakeSpawns()));
        Assert.True(s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}""")).Ok);
        var door = s.Document.Objects.First(o => o.Piece?.Kind == PieceKind.Blocker);
        Assert.True(s.Apply(Lab.Op($$"""{"op":"transform","id":"{{door.Id}}","move":[0,2,0]}""")).Ok);
        Assert.True(s.Apply(Lab.Op($$"""{"op":"delete","ids":["{{door.Id}}"]}""")).Ok);
        Assert.DoesNotContain(s.Document.Objects, o => o.Id == door.Id);
        var path = Path.Combine(_lab.Dir, "spawns.smmap");
        s.Save(path);
        var back = MapSession.Load(path, _lab.Library, new ImportSm1(spawns: FakeSpawns()));
        Assert.Equal(s.Document.ToJson(), back.Document.ToJson());
    }

    [Fact]
    public void Capture_points_come_from_the_originals_data_in_its_order_and_both_modes_pass_the_check()
    {
        var s = MapSession.New("imported", _lab.Library, new ImportSm1(spawns: FakeSpawns()));
        var r = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.True(r.Ok, r.ToJson());
        var points = s.Document.Objects.Where(o => o.Piece?.Kind == PieceKind.CapturePoint).ToList();
        Assert.Equal(2, points.Count);
        Assert.DoesNotContain(points, p => p.Name.Contains("placeholder"));
        Assert.Equal(new Vector3(5, 0, 0), points[0].Transform.Position);   // the centre's z (2) minus half the height (2)
        Assert.Equal(new Vector3(8, 8, 4), points[0].Transform.Scale);
        Assert.Equal(new Vector3(6, 6, 4), points[1].Transform.Scale);
        Assert.Contains(r.Warnings, w => w.StartsWith("Seize Ground: 2 capture point(s)"));
        var map = CustomMap.Compile(s.Document);
        Assert.Equal(new Vector3(5, 0, 2), map.Content.ControlPoints[0].Position);
        var report = MapCheck.Run(map);
        Assert.True(report.AnnihilationReady && report.SeizeReady, report.ToText());
    }

    [Fact]
    public void A_map_whose_data_has_no_capture_points_gets_three_placeholders_and_a_warning()
    {
        var s = MapSession.New("imported", _lab.Library, new ImportSm1(spawns: FakeSpawns(points: false)));
        var r = s.Apply(Lab.Op("""{"op":"import-sm1-map","map":"mp_fake"}"""));
        Assert.True(r.Ok, r.ToJson());
        Assert.Equal(3, s.Document.Objects.Count(o => o.Piece?.Kind == PieceKind.CapturePoint && o.Name.Contains("placeholder")));
        Assert.Contains(r.Warnings, w => w.Contains("no Seize Ground capture points") && w.Contains("placeholder"));
        var report = MapCheck.Run(CustomMap.Compile(s.Document));
        Assert.True(report.AnnihilationReady && report.SeizeReady, report.ToText());
    }
}
