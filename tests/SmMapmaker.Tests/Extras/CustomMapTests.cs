using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;
using SmMapmaker;
using SmSim;

namespace SmMapmaker.Tests;

/// <summary>Custom maps (docs/mapmaker.md "Playing a map"): pieces, layouts, the compiler, the check, the hash and the file lookup. Synthetic documents only.</summary>
public sealed class CustomMapTests : IDisposable
{
    readonly Lab _lab = new();
    public void Dispose() => _lab.Dispose();

    static MapDocument Layout(string id, string name = "Test Layout")
    {
        var s = MapSession.New(name, new MapLibrary(Path.Combine(Path.GetTempPath(), "smmapmaker-test-lib")));
        var r = s.Apply(MapTemplate.Find(id)!.Ops());
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
        return s.Document;
    }

    static MapDocument With(MapDocument doc, params JsonObject[] ops)
    {
        var s = new MapSession(doc, new MapLibrary(Path.Combine(Path.GetTempPath(), "smmapmaker-test-lib")));
        var r = s.Apply(ops);
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
        return s.Document;
    }

    [Theory]
    [InlineData("arena")]
    [InlineData("open-field")]
    [InlineData("blank")]
    public void Every_layout_compiles_and_the_check_is_clean_for_the_modes_it_supports(string id)
    {
        var map = CustomMap.Compile(Layout(id));
        var report = MapCheck.Run(map);
        Assert.True(report.AnnihilationReady, report.ToText());
        Assert.Equal(id != "blank", report.SeizeReady);   // the blank layout has no capture points: Annihilation only, and it says so
        Assert.Equal(2, map.Content.StartAreas.Count);
        Assert.All(map.Content.StartAreas, a => Assert.True(MapCheck.SpotsIn(a, map.ToCollisionWorld().Character).Count >= 8, "a spawn area fits a full side"));
    }

    [Fact]
    public void Arena_has_three_points_two_doors_and_a_kill_floor_under_the_ground()
    {
        var map = CustomMap.Compile(Layout("arena"));
        Assert.Equal(3, map.Content.ControlPoints.Count);
        Assert.Equal(2, map.Content.Blockers.Count);
        var floor = Assert.Single(map.Content.KillRegions, k => k.Name == "cm_kill_floor");
        Assert.True(floor.Position.Z + floor.Scale.Z / 2f < map.SolidBounds!.Value.Min.Z, "the pit lies below the lowest solid piece");
        Assert.True(map.ToCollisionWorld().KillVolumes.Any(k => k.Contains(new Vector3(0, 0, map.SolidBounds!.Value.Min.Z - CustomMap.KillFloorDepth - 3f))), "a falling player dies");
        Assert.False(map.ToCollisionWorld().KillVolumes.Any(k => k.Contains(new Vector3(0, 0, 1f))), "standing on the floor is safe");
    }

    [Fact]
    public void Piece_round_trips_in_the_file_and_has_exactly_one_of_asset_prefab_piece()
    {
        var doc = Layout("blank");
        var again = MapDocument.Parse(doc.ToJson());
        Assert.Equal(doc.ToJson(), again.ToJson());
        Assert.Contains(again.Objects, o => o.Piece is { Kind: PieceKind.SpawnArea, Side: PieceSpec.Marines });
        var bad = JsonNode.Parse(doc.ToJson())!.AsObject();
        bad["objects"]![0]!["asset"] = new JsonObject { ["source"] = "custom", ["id"] = "x" };
        var e = Assert.Throws<MapException>(() => MapDocument.Parse(bad.ToJsonString()));
        Assert.Equal("bad-file", e.Code);
        var side = JsonNode.Parse(doc.ToJson())!.AsObject();
        side["objects"]![1]!["piece"] = new JsonObject { ["kind"] = "spawn-area" };   // a spawn area without a side
        Assert.Throws<MapException>(() => MapDocument.Parse(side.ToJsonString()));
    }

    [Fact]
    public void Missing_pieces_are_named_in_plain_words_with_a_fix_and_the_mode_they_stop()
    {
        var empty = CustomMap.Compile(MapDocument.Create("Nothing"));
        var r = MapCheck.Run(empty);
        Assert.False(r.AnnihilationReady);
        Assert.False(r.SeizeReady);
        Assert.Contains(r.Issues, i => i.Code == "no-ground" && i.Action == "add-floor");
        Assert.Contains(r.Issues, i => i.Code == "no-spawn-marines" && i.Message.Contains("Space Marines"));
        Assert.Contains(r.Issues, i => i.Code == "no-spawn-chaos" && i.Fix is not null);
        Assert.Contains(r.Issues, i => i.Code == "no-points" && i.Mode == CheckMode.Seize);

        var noPoints = CustomMap.Compile(Layout("blank"));
        var rr = MapCheck.Run(noPoints);
        Assert.True(rr.AnnihilationReady);
        Assert.False(rr.SeizeReady);
        Assert.Equal("Ready to play: Annihilation. Seize Ground needs a capture point.", rr.Headline);
    }

    [Fact]
    public void A_spawn_area_in_the_air_or_overlapping_the_other_side_or_in_a_kill_zone_is_an_error()
    {
        var doc = Layout("blank");
        var marine = doc.Objects.First(o => o.Piece is { Kind: PieceKind.SpawnArea, Side: PieceSpec.Marines });
        // lifted well above the floor: no standable ground inside the box
        var lifted = With(Layout("blank"), Lab.Op($$"""{"op":"transform","id":"{{marine.Id}}","position":[-26,0,30]}"""));
        Assert.Contains(MapCheck.Run(CustomMap.Compile(lifted)).Issues, i => i.Code == "spawn-no-floor" && i.Severity == Severity.Error);
        // dragged onto the Chaos area
        var overlap = With(Layout("blank"), Lab.Op($$"""{"op":"transform","id":"{{marine.Id}}","position":[26,0,0]}"""));
        Assert.Contains(MapCheck.Run(CustomMap.Compile(overlap)).Issues, i => i.Code == "spawn-overlap");
        // a kill zone over the spawn
        var killed = With(Layout("blank"), Lab.Op("""{"op":"place","piece":{"kind":"kill-zone"},"transform":{"position":[-26,0,0],"scale":[20,20,6]}}"""));
        Assert.Contains(MapCheck.Run(CustomMap.Compile(killed)).Issues, i => i.Code == "in-kill-zone");
    }

    [Fact]
    public void A_missing_door_is_a_warning_not_an_error_and_a_cut_apart_map_is_found_by_the_bots_route()
    {
        var doc = Layout("blank");
        var doors = doc.Objects.Where(o => o.Piece is { Kind: PieceKind.Blocker }).Select(o => o.Id).ToArray();
        var noDoors = With(doc, Lab.Op($$"""{"op":"delete","ids":[{{string.Join(",", doors.Select(d => $"\"{d}\""))}}]}"""));
        var r = MapCheck.Run(CustomMap.Compile(noDoors));
        Assert.True(r.AnnihilationReady);
        Assert.Contains(r.Issues, i => i.Code == "no-door-marines" && i.Severity == Severity.Warning && i.Action == "add-door-marines");
    }

    [Fact]
    public void The_hash_is_stable_follows_every_edit_and_the_wire_name_is_a_valid_map_name()
    {
        var doc = Layout("arena", "Ã‡afÃ© Arena #1 with a rather long name indeed");
        var a = CustomMap.Compile(doc);
        var b = CustomMap.Compile(MapDocument.Parse(doc.ToJson()));
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Content.Hash, a.Hash);
        var moved = CustomMap.Compile(With(doc, Lab.Op("""{"op":"transform","id":"o1","move":[0,0,0.5]}""")));
        Assert.NotEqual(a.Hash, moved.Hash);
        Assert.True(a.Name.Length <= 24 && a.Name.StartsWith("cm_") && a.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), a.Name);
        Assert.True(CustomMap.IsCustomName(a.Name));
        Assert.False(CustomMap.IsCustomName("mp_tdm_09"));
        Assert.Equal("cm_map", CustomMap.WireName("###"));
    }

    [Fact]
    public void The_same_file_builds_the_same_collision_everywhere()
    {
        var doc = Layout("arena");
        var one = CustomMap.Compile(doc).ToCollisionWorld();
        var two = CustomMap.Compile(MapDocument.Parse(doc.ToJson())).ToCollisionWorld();
        Assert.Equal(one.ContentHash, two.ContentHash);
        // the floor holds a player up and a wall stops a shot
        Assert.NotNull(one.Character.Raycast(new Vector3(10, 5, 5), -Vector3.UnitZ, 10f));
        Assert.NotNull(one.Weapons.Raycast(new Vector3(-20, 0, 1), Vector3.UnitY, 100f));
    }

    [Fact]
    public void Files_are_found_by_wire_name_or_path_and_a_wrong_copy_is_refused_with_instructions()
    {
        var doc = Layout("arena", "Find Me");
        string dir = Path.Combine(_lab.Dir, "maps");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "whatever-name.smmap");
        File.WriteAllText(file, doc.ToJson());
        Environment.SetEnvironmentVariable("SM_MAPS_DIR", dir);
        try
        {
            Assert.Equal(Path.GetFullPath(file), CustomMaps.Locate("cm_find_me"));
            Assert.Equal(Path.GetFullPath(file), CustomMaps.Locate(file));
            Assert.Null(CustomMaps.Locate("cm_nobody"));
            var loaded = CustomMaps.LoadForPlay("cm_find_me", null);
            Assert.Equal(CustomMap.Compile(doc).Hash, loaded.Hash);
            var missing = Assert.Throws<MapException>(() => CustomMaps.LoadForPlay("cm_nobody", null));
            Assert.Equal("missing-map", missing.Code);
            Assert.Contains(".smmap", missing.Message);
            Assert.Contains("--custom-map", missing.Message);
            var wrong = Assert.Throws<MapException>(() => CustomMaps.LoadForPlay("cm_other_name", file));
            Assert.Equal("wrong-map", wrong.Code);
        }
        finally { Environment.SetEnvironmentVariable("SM_MAPS_DIR", null); }
    }

    [Fact]
    public void Unreadable_files_are_plain_errors_not_crashes()
    {
        string file = Path.Combine(_lab.Dir, "broken.smmap");
        File.WriteAllText(file, "{ not json");
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => CustomMaps.Load(file)).Code);
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => CustomMaps.Load(Path.Combine(_lab.Dir, "absent.smmap"))).Code);
    }

    [Fact]
    public void Set_piece_turns_a_spawn_area_to_the_other_side_and_rejects_other_objects()
    {
        var doc = Layout("blank");
        var marine = doc.Objects.First(o => o.Piece is { Kind: PieceKind.SpawnArea, Side: PieceSpec.Marines });
        var flipped = With(doc, Lab.Op($$"""{"op":"set-piece","id":"{{marine.Id}}","side":"chaos"}"""));
        Assert.Equal(PieceSpec.Chaos, flipped.Get(marine.Id).Piece!.Side);
        Assert.Equal("Chaos spawn area", flipped.Get(marine.Id).Name);   // the default name follows
        var floor = doc.Objects.First(o => o.Name == "Floor");
        var s = new MapSession(doc, new MapLibrary(Path.Combine(_lab.Dir, "lib")));
        Assert.False(s.Apply(Lab.Op($$"""{"op":"set-piece","id":"{{floor.Id}}","side":"chaos"}""")).Ok);
    }

    [Fact]
    public void A_map_declares_its_modes_in_any_combination_and_each_declared_mode_is_checked()
    {
        var doc = Layout("blank");   // no capture points
        var seizeOnly = With(Layout("blank"), Lab.Op("""{"op":"set-modes","modes":["seize"]}"""));
        var r = MapCheck.Run(CustomMap.Compile(seizeOnly));
        Assert.False(r.AnnihilationReady);   // not declared
        Assert.False(r.SeizeReady);          // declared, needs a point
        Assert.Contains(r.Issues, i => i.Code == "no-points");
        var annOnly = With(Layout("blank"), Lab.Op("""{"op":"set-modes","modes":["annihilation"]}"""));
        var a = MapCheck.Run(CustomMap.Compile(annOnly));
        Assert.True(a.AnnihilationReady);
        Assert.False(a.SeizeReady);
        Assert.DoesNotContain(a.Issues, i => i.Code == "no-points");   // Seize is not declared, so no point is asked for
        var future = With(Layout("arena"), Lab.Op("""{"op":"set-modes","modes":["annihilation","seize","king-of-the-hill"]}"""));
        var f = MapCheck.Run(CustomMap.Compile(future));
        Assert.True(f.AnnihilationReady && f.SeizeReady);
        Assert.Contains(f.Issues, i => i.Code == "unknown-mode");   // kept in the file, said so, not an error
        Assert.Contains("king-of-the-hill", MapDocument.Parse(future.ToJson()).Modes);
        Assert.DoesNotContain("modes", JsonNode.Parse(doc.ToJson())!.AsObject());   // the default is not written
        Assert.False(new MapSession(Layout("blank"), new MapLibrary(Path.Combine(_lab.Dir, "lib"))).Apply(Lab.Op("""{"op":"set-modes","modes":[]}""")).Ok);
    }

    [Fact]
    public void Tour_places_cover_both_spawns_every_point_and_an_overview()
    {
        var places = MapTour.Places(CustomMap.Compile(Layout("arena")));
        Assert.Equal(6, places.Count);   // two spawns, three points, one overview
        Assert.All(places, p => Assert.Equal(5, p.Pose.Split(',').Length));
    }

    [Fact]
    public void Server_hook_builds_the_game_map_the_clients_will_check_against()
    {
        var doc = Layout("arena", "Hook Arena");
        string file = Path.Combine(_lab.Dir, "hook.smmap");
        File.WriteAllText(file, doc.ToJson());
        string? name = null;
        var map = SmServer.GameServer.CustomMap(file, ref name, out var error);
        Assert.Null(error);
        Assert.NotNull(map);
        Assert.Equal("cm_hook_arena", name);
        Assert.Equal(CustomMap.Compile(doc).Hash, map!.Hash);
        Assert.Equal(3, map.ControlPoints!.Count);
        Assert.Equal(2, map.StartAreas!.Count);
        Assert.Equal(2, map.Collision.Blockers.Count);

        string? n2 = null;
        Assert.Null(SmServer.GameServer.CustomMap(Path.Combine(_lab.Dir, "nothing.smmap"), ref n2, out var error2));
        Assert.Contains("cannot play", error2);
    }

    [Fact]
    public void A_world_on_the_arena_spawns_each_side_in_its_room_facing_the_door()
    {
        var map = CustomMap.Compile(Layout("arena"));
        var game = new SmNet.GameMap(map.Name, map.ToCollisionWorld(), map.Content.SpawnPoints(), map.Hash, map.Content.SimControlPoints(), map.Content.SimStartAreas(), map.Content.SimBlockers());
        var world = new World(new SimConfig(), game.Collision, game.Spawns) { StartAreas = game.StartAreas!, ControlPoints = game.ControlPoints! };
        for (int i = 0; i < world.StartAreas.Count; i++)
        {
            var spots = world.StartAreaSpots(i);
            Assert.True(spots.Count >= 8);
            var facing = world.StartAreaFacings(i);
            Assert.NotNull(facing.Exit);   // the spawn door gives the room an exit, so spawning players face it
            bool marines = world.StartAreas[i].Side == Team.SpaceMarines;
            Assert.True(marines ? facing.Exit!.Value.X > spots.Max(s => s.X) : facing.Exit!.Value.X < spots.Min(s => s.X), "the exit is on the arena side of the room");
        }
    }
}

