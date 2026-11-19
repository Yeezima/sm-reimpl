using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;
using SmMapmaker;
using SmSim;

namespace SmMapmaker.Tests;

/// <summary>
/// Slice S1 of "Mapmaker SM1 import as Classic plays it" (docs/mapmaker.md "Classic link"): file format 3 (editor-only <c>src</c> link, game-visible <c>look</c>), the new piece kinds
/// <c>move-wall</c> and <c>cover</c> and what they compile to. Synthetic documents only.
/// </summary>
public sealed class ClassicLinkTests
{
    static OpResult Run(MapSession s, string json) => s.Apply(Lab.Op(json));

    const string Floor = """{"op":"place","piece":{"kind":"box"},"name":"Floor","transform":{"position":[0,0,-1],"scale":[100,100,1]}}""";

    /// <summary>A flat floor (top at z = 0) and the pieces in <paramref name="extra"/>.</summary>
    static MapSession Map(Lab lab, params string[] extra)
    {
        var s = lab.Session();
        Assert.True(Run(s, Floor).Ok);
        foreach (var op in extra) { var r = Run(s, op); Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message))); }
        return s;
    }

    const string Wall = """{"op":"place","piece":{"kind":"move-wall"},"transform":{"position":[3,0,0],"scale":[1,20,3]}}""";
    const string Cover = """{"op":"place","piece":{"kind":"cover"},"transform":{"position":[3,0,0],"scale":[1.2,3,1.5]}}""";
    const string Kill = """{"op":"place","piece":{"kind":"kill-zone"},"transform":{"position":[20,0,0],"scale":[4,4,4]}}""";

    static readonly SimConfig Config = new();
    static float East => MathF.Atan2(-1f, 0f);

    /// <summary>Walks a fresh character from x = -6 toward +X for two seconds and returns the largest x it reached.</summary>
    static float DeepestWalk(CollisionWorld level)
    {
        var s = Combat.Respawned(new PlayerState(1, new Vector3(-6, 0, 0), Vector3.Zero, East, true) { Team = Team.SpaceMarines }, Config);
        float deepest = float.MinValue;
        for (int t = 0; t < 120; t++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, East, InputButtons.None), Config, out _, level);
            deepest = MathF.Max(deepest, s.Position.X);
        }
        return deepest;
    }

    static readonly PierceSpec Lascannon = new(10f, 1f, new ConeSpec(15f, 1f, 0f, 0f, new DamageSpec(50f, 0f, 1f, 1f, 1f, 1f, false), [], []), true);

    [Fact]
    public void A_document_compiles_walls_props_and_kill_regions_beside_the_kill_floor()
    {
        using var lab = new Lab();
        var map = CustomMap.Compile(Map(lab, Wall, Cover, Kill,
            """{"op":"place","piece":{"kind":"move-wall","shape":"cylinder"},"transform":{"position":[0,10,0],"scale":[2,2,3]}}""").Document);
        Assert.Equal(2, map.Content.Walls.Count);
        Assert.Equal([1, 3], map.Content.Walls.Select(w => w.Shape));
        Assert.Equal(new Vector3(3, 0, 1.5f), map.Content.Walls[0].Position);   // the centre: the piece's bottom is its z
        Assert.Equal(new Vector3(1, 20, 3), map.Content.Walls[0].Scale);
        var prop = Assert.Single(map.Content.Props);
        Assert.Equal(8, prop.Vertices.Length);
        Assert.Equal(36, prop.Indices.Length);
        Assert.Equal(1, map.Content.KillRegions.Count(k => k.Name == "cm_kill_1"));
        Assert.Contains(map.Content.KillRegions, k => k.Name == "cm_kill_floor");   // the generated floor stays
        var world = map.ToCollisionWorld();
        Assert.Equal(2, world.Walls.Count);
        Assert.Single(world.Props);
        Assert.Equal(1, map.SolidCount);   // only the floor piece is terrain: a wall and cover are not
    }

    [Fact]
    public void An_invisible_wall_for_players_stops_a_walking_character_but_not_a_shot()
    {
        using var lab = new Lab();
        var open = CustomMap.Compile(Map(lab).Document).ToCollisionWorld();
        var walled = CustomMap.Compile(Map(lab, Wall).Document).ToCollisionWorld();
        Assert.True(DeepestWalk(open) > 10f, "no wall: the character walks on");
        float deepest = DeepestWalk(walled);
        Assert.True(deepest <= 2.5f - Config.CapsuleRadius + 2e-3f, $"stopped by the wall, reached x {deepest}");
        Assert.True(deepest >= 2.5f - Config.CapsuleRadius - 0.1f, $"reached the wall, got x {deepest}");
        Assert.Null(walled.Weapons.Raycast(new Vector3(-6, 0, 1), Vector3.UnitX, 40f));   // shots fly through it
        Assert.Null(walled.TraceShot(new Vector3(-6, 0, 1), Vector3.UnitX, 40f, null).Stop);
    }

    [Fact]
    public void A_cylinder_wall_stops_a_walking_character_too()
    {
        using var lab = new Lab();
        var level = CustomMap.Compile(Map(lab, """{"op":"place","piece":{"kind":"move-wall","shape":"cylinder"},"transform":{"position":[12,0,0],"scale":[20,20,3]}}""").Document).ToCollisionWorld();   // 20 m across: a head-on walk cannot slide round it
        float deepest = DeepestWalk(level);
        Assert.True(deepest < 2.5f, $"reached x {deepest}");   // the prism's near side is about 2 m
        Assert.True(deepest > 0.5f, $"never reached it, got x {deepest}");
        Assert.Null(level.Weapons.Raycast(new Vector3(-6, 0, 1), Vector3.UnitX, 40f));
    }

    [Fact]
    public void Cover_blocks_movement_and_a_pierce_capable_shot_passes_it_like_a_baked_prop()
    {
        using var lab = new Lab();
        var level = CustomMap.Compile(Map(lab, Cover).Document).ToCollisionWorld();
        float deepest = DeepestWalk(level);
        Assert.True(deepest <= 2.4f - Config.CapsuleRadius + 2e-3f, $"stopped by the cover, reached x {deepest}");
        var origin = new Vector3(-6, 0, 1);
        Assert.Equal(8.4f, level.Weapons.Raycast(origin, Vector3.UnitX, 40f)!.Value.Distance, 2);   // a plain shot stops at it
        Assert.NotNull(level.TraceShot(origin, Vector3.UnitX, 40f, null).Stop);
        var trace = level.TraceShot(origin, Vector3.UnitX, 40f, Lascannon);                         // the lascannon's goes through
        Assert.Null(trace.Stop);
        var pierced = Assert.Single(trace.Pierced);
        Assert.True(pierced.IntoOpen);
    }

    [Fact]
    public void The_classic_link_never_changes_the_hash_but_the_look_does()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall);
        ulong plain = CustomMap.Compile(s.Document).Hash;
        string gameText = s.Document.ToGameJson();
        var withSrc = s.Document.Clone();
        withSrc.Objects[1] = withSrc.Objects[1] with { Src = new MapObjectSrc("mp_tdm_09", "d_blockers/everyone_blocker_01", "abc123", MapObjectSrc.Wall, true) };
        Assert.Contains("\"src\"", withSrc.ToJson());
        Assert.DoesNotContain("\"src\"", withSrc.ToGameJson());
        Assert.Equal(gameText, withSrc.ToGameJson());
        Assert.Equal(plain, CustomMap.Compile(withSrc).Hash);
        Assert.True(Run(s, """{"op":"set-look","sm1Map":"mp_tdm_09"}""").Ok);
        ulong looked = CustomMap.Compile(s.Document).Hash;
        Assert.NotEqual(plain, looked);
        Assert.Contains("\"look\"", s.Document.ToGameJson());
        Assert.True(Run(s, """{"op":"set-look","sm1Map":"mp_tdm_08"}""").Ok);
        Assert.NotEqual(looked, CustomMap.Compile(s.Document).Hash);
        Assert.True(Run(s, """{"op":"set-look","sm1Map":null}""").Ok);
        Assert.Equal(plain, CustomMap.Compile(s.Document).Hash);
        Assert.Equal(6, CustomMap.CompilerVersion);
    }

    [Fact]
    public void The_look_is_validated_in_the_operation_and_in_a_file()
    {
        using var lab = new Lab();
        var s = lab.Session();
        foreach (var bad in new[] { "mp", "tdm_09", "MP_TDM_09", "mp_tdm 09", "mp_" + new string('a', 60) })
            Assert.Equal("bad-op", Run(s, $$"""{"op":"set-look","sm1Map":"{{bad}}"}""").Errors[0].Code);
        Assert.Null(s.Document.Look);
        var node = JsonNode.Parse(s.Document.ToJson())!.AsObject();
        node["look"] = JsonNode.Parse("""{"sm1Map":"../etc"}""");
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => MapDocument.Parse(node.ToJsonString())).Code);
        node["look"] = JsonNode.Parse("""{"sm1Map":"mp_tdm_09"}""");
        Assert.Equal("mp_tdm_09", MapDocument.Parse(node.ToJsonString()).Look!.Sm1Map);
    }

    [Fact]
    public void A_version_2_file_upgrades_to_3_and_a_newer_one_is_refused()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall);
        var node = JsonNode.Parse(s.Document.ToJson())!.AsObject();
        Assert.Equal(3, MapDocument.FormatVersion);
        Assert.Equal(3, (int)node["formatVersion"]!);
        node["formatVersion"] = 2;
        var up = MapDocument.Parse(node.ToJsonString());
        Assert.Equal(2, up.LoadedVersion);
        Assert.Equal(3, (int)JsonNode.Parse(up.ToJson())!["formatVersion"]!);
        Assert.Equal(CustomMap.Compile(s.Document).Hash, CustomMap.Compile(up).Hash);   // the identity step loses nothing
        node["formatVersion"] = MapDocument.FormatVersion + 1;
        Assert.Contains("newer Mapmaker", Assert.Throws<MapException>(() => MapDocument.Parse(node.ToJsonString())).Message);
    }

    [Fact]
    public void The_link_round_trips_through_save_open_undo_and_is_repaired_in_a_file()
    {
        using var lab = new Lab();
        var s = Map(lab);
        Assert.True(Run(s, """{"op":"place","piece":{"kind":"move-wall"},"name":"d_blockers 1","visible":false,"src":{"map":"mp_tdm_09","key":"d_blockers/b1","fp":"f00d","kind":"wall","classicHidden":true}}""").Ok);
        var expected = new MapObjectSrc("mp_tdm_09", "d_blockers/b1", "f00d", MapObjectSrc.Wall, true);
        Assert.Equal(expected, s.Document.Objects[1].Src);
        Assert.False(s.Document.Objects[1].Visible);
        string path = Path.Combine(lab.Dir, "linked.smmap");
        s.Save(path);
        var loaded = MapSession.Load(path, lab.Library);
        Assert.Equal(expected, loaded.Document.Objects[1].Src);
        Assert.Equal(s.Document.ToJson(), loaded.Document.ToJson());
        // delete, undo: the link comes back with the object; a duplicate is a new object with no link
        Assert.True(Run(s, """{"op":"delete","ids":["o2"]}""").Ok);
        Assert.True(s.Undo().Ok);
        Assert.Equal(expected, s.Document.Get("o2").Src);
        Assert.True(Run(s, """{"op":"transform","id":"o2","move":[0,1,0]}""").Ok);
        Assert.Equal(expected, s.Document.Get("o2").Src);
        Assert.True(Run(s, """{"op":"duplicate","id":"o2"}""").Ok);
        Assert.Null(s.Document.Objects[^1].Src);
        // a hostile or odd link is dropped, never an error; a bad link in an operation is refused
        var node = JsonNode.Parse(loaded.Document.ToJson())!.AsObject();
        node["objects"]![1]!["src"] = JsonNode.Parse("""{"map":"../x","key":"k","fp":"f","kind":"wall"}""");
        Assert.Null(MapDocument.Parse(node.ToJsonString()).Objects[1].Src);
        node["objects"]![1]!["src"] = JsonNode.Parse("""{"map":"mp_tdm_09","key":"k","fp":"f","kind":"banana"}""");
        Assert.Null(MapDocument.Parse(node.ToJsonString()).Objects[1].Src);
        node["objects"]![1]!["src"] = 7;
        Assert.Null(MapDocument.Parse(node.ToJsonString()).Objects[1].Src);
        Assert.Equal("bad-op", Run(s, """{"op":"place","piece":{"kind":"cover"},"src":{"map":"nope","key":"k","fp":"f","kind":"cover"}}""").Errors[0].Code);
    }

    [Fact]
    public void Pieces_validate_their_shape_and_the_hash_covers_it()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall);
        Assert.Equal("bad-op", Run(s, """{"op":"place","piece":{"kind":"box","shape":"cylinder"}}""").Errors[0].Code);
        Assert.Equal("bad-op", Run(s, """{"op":"place","piece":{"kind":"move-wall","shape":"sphere"}}""").Errors[0].Code);
        ulong box = CustomMap.Compile(s.Document).Hash;
        Assert.True(Run(s, """{"op":"set-piece","id":"o2","shape":"cylinder"}""").Ok);
        Assert.True(s.Document.Get("o2").Piece!.IsCylinder);
        Assert.NotEqual(box, CustomMap.Compile(s.Document).Hash);
        Assert.True(Run(s, """{"op":"set-piece","id":"o2","shape":"box"}""").Ok);
        Assert.Equal(box, CustomMap.Compile(s.Document).Hash);   // "box" is the default and is written the one way
        Assert.Contains(PieceKind.All, k => k.Id == PieceKind.MoveWall && k.Label == "Invisible wall (players only)");
        Assert.Contains(PieceKind.All, k => k.Id == PieceKind.Cover && k.Label == "Cover (shoot-through)");
        Assert.Equal(PieceKind.CollisionBox, PieceKind.Find(PieceKind.CollisionBox)!.Id);   // the existing Invisible wall stays
        Assert.True(PieceKind.Find(PieceKind.CollisionBox)!.Solid);
    }

    [Fact]
    public void A_map_with_no_new_pieces_compiles_no_walls_or_props()
    {
        using var lab = new Lab();
        var map = CustomMap.Compile(Map(lab, Kill).Document);
        Assert.Empty(map.Content.Walls);
        Assert.Empty(map.Content.Props);
    }
}
