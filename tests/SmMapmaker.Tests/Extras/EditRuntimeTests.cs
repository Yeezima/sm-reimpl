using System.Numerics;
using SmContent;
using SmMapmaker;
using SmSim;

namespace SmMapmaker.Tests;

/// <summary>
/// Edit mode in play (docs/mapmaker.md "Edit mode in play"; owner-approved brief 2026-11-25 "Blender-style controls", plan M2): what the editor shows is what plays. Edited
/// pieces collide by their mesh, edited markers act by their exact shape (inside the shape activates, inside only the bounding box does not), spawn spots and doors follow the
/// edited boundary, and an unedited map is bit for bit what it was. Synthetic maps only.
/// </summary>
public sealed class EditRuntimeTests
{
    static ulong Fnv(ulong h, uint v) { for (int i = 0; i < 4; i++) { h ^= (v >> (8 * i)) & 0xFF; h *= 0x100000001B3UL; } return h; }
    static ulong Fnv(ulong h, ulong v) => Fnv(Fnv(h, (uint)(v & 0xFFFFFFFF)), (uint)(v >> 32));
    static ulong Fnv(ulong h, float f) => Fnv(h, BitConverter.SingleToUInt32Bits(f));
    static ulong Fnv(ulong h, Vector3 v) => Fnv(Fnv(Fnv(h, v.X), v.Y), v.Z);
    const ulong Offset = 0xCBF29CE484222325UL;

    static MapSession Layout(Lab lab, string id)
    {
        var s = lab.Session(withSm1: false);
        var r = s.Apply(MapTemplate.Find(id)!.Ops());
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
        return s;
    }

    /// <summary>Everything the match reads from a compiled map: the hash, the collision world's hash and side meshes, spots, facings, point presence and kill tests.</summary>
    static ulong PlayFingerprint(CustomMap map)
    {
        ulong h = Fnv(Offset, map.Hash);
        var c = map.Content;
        var world = c.ToCollisionWorld();
        h = Fnv(h, world.ContentHash);
        foreach (var side in new[] { Team.SpaceMarines, Team.Chaos, Team.None }) h = Fnv(h, world.CharacterFor(side).ContentHash);
        var sim = new World(new SimConfig(), world, c.SpawnPoints()) { StartAreas = c.SimStartAreas(), ControlPoints = c.SimControlPoints(), SpawnBlockers = c.SimBlockers() };
        for (int i = 0; i < sim.StartAreas.Count; i++)
        {
            foreach (var s in sim.StartAreaSpots(i)) h = Fnv(h, s);
            foreach (var y in sim.StartAreaFacings(i).Yaws) h = Fnv(h, y);
            foreach (var s in MapCheck.SpotsIn(c.StartAreas[i], world.Character)) h = Fnv(h, s);
        }
        var bounds = map.SolidBounds!.Value;
        for (float x = bounds.Min.X; x <= bounds.Max.X; x += 1.7f)
            for (float y = bounds.Min.Y; y <= bounds.Max.Y; y += 1.7f)
                for (float z = -1f; z <= 8f; z += 1.1f)
                {
                    var p = new Vector3(x, y, z);
                    foreach (var cp in sim.ControlPoints) h = Fnv(h, cp.Contains(p) ? 1u : 0u);
                    foreach (var k in world.KillVolumes) h = Fnv(h, k.Contains(p) ? 1u : 0u);
                    foreach (var b in world.Blockers) h = Fnv(h, b.Contains(p) ? 1u : 0u);
                }
        foreach (var line in MapCheck.Run(map).ToText().Split('\n')) foreach (char ch in line) h = Fnv(h, (uint)ch);
        return h;
    }

    // ---- an unedited map is bit for bit what it was: pinned on the contract v2 tip (08be29c6) without the M2 code (the v0 pins, taken on 864fada1, changed with contract v2 alone, checked by running this test on 08be29c6) ----

    [Theory]
    [InlineData("arena", 16976518806739088172UL)]
    [InlineData("open-field", 7851701334809318962UL)]
    [InlineData("blank", 4795470007704499624UL)]
    public void An_unedited_layout_plays_exactly_as_before(string id, ulong expected)
    {
        using var lab = new Lab();
        var map = CustomMap.Compile(Layout(lab, id).Document);
        Assert.Empty(map.Volumes);
        Assert.Equal(expected, PlayFingerprint(map));
    }

    // ---- helpers ----

    /// <summary>The compile with edited volumes carried into the simulation (<see cref="CustomMap.VolumesInPlay"/> is held off in play until contract v3; these tests exercise the wiring).</summary>
    static CustomMap Play(MapDocument doc) => CustomMap.Compile(doc, null, volumesInPlay: true);

    [Fact]
    public void In_play_edited_volumes_stay_held_back_until_contract_v3_and_their_shapes_are_still_compiled()
    {
        Assert.False(CustomMap.VolumesInPlay);
        using var lab = new Lab();
        var s = Map(lab, Floor, """{"op":"place","piece":{"kind":"capture-point"},"transform":{"position":[0,0,0],"scale":[8,8,4]}}""",
            """{"op":"place","piece":{"kind":"spawn-area","side":"marines"},"transform":{"position":[20,0,0],"scale":[12,12,3]}}""");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"vertex","ids":[0,16],"move":[4,0,0]}""");
        Run(s, """{"op":"edit-mesh","id":"o3","mode":"vertex","ids":[3,7],"move":[-11,0,0]}""");
        var held = CustomMap.Compile(s.Document);
        Assert.Equal(2, held.Volumes.Count);
        Assert.All(held.Content.ControlPoints, p => Assert.Null(p.Volume));
        Assert.All(held.Content.StartAreas, a => Assert.Null(a.Volume));
        var wired = Play(s.Document);
        Assert.Equal(held.Hash, wired.Hash);   // the switch changes play, not the map's identity
        Assert.NotNull(wired.Content.ControlPoints[0].Volume);
        // the edited area's spawn marker stands under a point inside its shape, in both (the box centre of this wedge is near its cut edge)
        var spawn = held.Content.Spawns[0].Position;
        Assert.True(held.Volumes["cm_start_marines_1"].Crosses(spawn, spawn + new Vector3(0, 0, 1)), $"{spawn}");
    }

    const string Floor = """{"op":"place","piece":{"kind":"box"},"name":"floor","transform":{"position":[0,0,-1],"scale":[80,80,1]}}""";

    static OpResult Run(MapSession s, string json)
    {
        var r = s.Apply(json);
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Code + ": " + e.Message)));
        return r;
    }

    static MapSession Map(Lab lab, params string[] ops)
    {
        var s = lab.Session(withSm1: false);
        foreach (var op in ops) Run(s, op);
        return s;
    }

    /// <summary>Pulls every vertex of <paramref name="id"/>'s shape whose unit X is above a quarter (its +X end) by <paramref name="dx"/> metres along the object's X.</summary>
    static void PullPlusX(MapSession s, string id, float dx = 1f)
    {
        var ids = EditMesh.Of(s.Document.Get(id)).Vertices.Select((v, i) => (v, i)).Where(x => x.v.X > 0.25f).Select(x => x.i);
        Run(s, $$"""{"op":"edit-mesh","id":"{{id}}","mode":"vertex","ids":[{{string.Join(",", ids)}}],"move":[{{dx}},0,0]}""");
    }

    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static ClassLoadout Tac(string name, Team side) => new(name, 0, -1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], Side = side, Role = "tactical" };
    static readonly SimConfig TeamConfig = new() { Weapons = [Rifle], Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)], RespawnTime = 60f };

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    /// <summary>The Build tab's presets, read from the editor's own list (mapmaker/addons/sm_mapmaker/MapmakerPlugin.Flow.cs): id, kind, side and size.</summary>
    static List<(string Id, string Kind, string? Side, Vector3 Size)> BuildPresets()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "mapmaker", "addons", "sm_mapmaker", "MapmakerPlugin.Flow.cs"));
        var rx = new System.Text.RegularExpressions.Regex(@"new\(""([a-z-]+)"", ""[^""]*"", PieceKind\.(\w+), (null|PieceSpec\.\w+), new\(([-\d.f]+), ([-\d.f]+), ([-\d.f]+)\)");
        static float F(string t) => float.Parse(t.TrimEnd('f'), System.Globalization.CultureInfo.InvariantCulture);
        var list = new List<(string, string, string?, Vector3)>();
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(text))
        {
            string kind = (string)typeof(PieceKind).GetField(m.Groups[2].Value)!.GetRawConstantValue()!;
            string? side = m.Groups[3].Value == "null" ? null : (string)typeof(PieceSpec).GetField(m.Groups[3].Value["PieceSpec.".Length..])!.GetRawConstantValue()!;
            list.Add((m.Groups[1].Value, kind, side, new Vector3(F(m.Groups[4].Value), F(m.Groups[5].Value), F(m.Groups[6].Value))));
        }
        return list;
    }

    // ---- every Build preset through the shared compiler ----

    [Fact]
    public void Every_Build_preset_with_its_end_pulled_out_plays_by_its_edited_shape()
    {
        var presets = BuildPresets();
        Assert.Equal(14, presets.Count);   // floor, wall, cover, ramp, column, invisible, move-wall, classic-cover, both spawns, both doors, point, kill
        foreach (var (pid, kind, side, size) in presets.Append(("move-wall-cylinder", PieceKind.MoveWall, null, new Vector3(3, 3, 3))))
        {
            using var lab = new Lab();
            string sideJson = side is null ? "" : $",\"side\":\"{side}\"";
            string shapeJson = pid.EndsWith("-cylinder") ? ",\"shape\":\"cylinder\"" : "";
            string scale = string.Join(",", new[] { size.X, size.Y, size.Z }.Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var s = Map(lab, "{\"op\":\"place\",\"piece\":{\"kind\":\"" + kind + "\"" + sideJson + shapeJson + "},\"transform\":{\"position\":[0,0,0],\"scale\":[" + scale + "]}}");
            var stock = Play(s.Document);
            PullPlusX(s, "o1");
            var map = Play(s.Document);
            Assert.DoesNotContain(map.Notes, n => n.Contains("left out"));
            float end = size.X / 2f + 1f, mid = size.Z / 2f;
            var beyond = new Vector3(size.X / 2f + 0.5f, 0, mid);   // in the pulled-out part only
            var c = map.Content;
            switch (kind)
            {
                case PieceKind.Box or PieceKind.Ramp or PieceKind.Column or PieceKind.CollisionBox:
                    Assert.Equal(end, c.Layers.First(l => l.Name == MapContent.CharacterLayer).Vertices.Max(v => v.X), 3);
                    Assert.Equal(end, c.Layers.First(l => l.Name == MapContent.WeaponLayer).Vertices.Max(v => v.X), 3);
                    if (PieceKind.IsDrawn(kind)) Assert.Equal(end, map.Pieces[0].Render().Positions.Max(v => v.X), 3);   // what the client draws
                    break;
                case PieceKind.MoveWall:
                    Assert.Equal(end, Assert.Single(c.Walls).Vertices!.Max(v => v.X), 3);
                    Assert.True(c.ToCollisionWorld().CharacterFor(Team.SpaceMarines).Raycast(beyond + new Vector3(0, -10, 0), Vector3.UnitY, 20f) is not null, pid);
                    Assert.Null(c.ToCollisionWorld().Weapons.Raycast(beyond + new Vector3(0, -10, 0), Vector3.UnitY, 20f));
                    break;
                case PieceKind.Cover:
                    Assert.Equal(end, Assert.Single(c.Props).Vertices.Max(v => v.X), 3);
                    break;
                case PieceKind.SpawnArea:
                {
                    var a = Assert.Single(c.SimStartAreas());
                    Assert.True(a.Volume!.Contains(beyond), pid);
                    Assert.Null(Assert.Single(stock.Content.SimStartAreas()).Volume);
                    break;
                }
                case PieceKind.Blocker:
                {
                    var b = Assert.Single(c.SimBlockers());
                    Assert.True(b.Contains(beyond), pid);
                    Assert.False(Assert.Single(stock.Content.SimBlockers()).Contains(beyond));
                    break;
                }
                case PieceKind.CapturePoint:
                {
                    var p = Assert.Single(c.SimControlPoints());
                    Assert.True(p.Contains(beyond with { Z = 0 }), pid);
                    Assert.False(Assert.Single(stock.Content.SimControlPoints()).Contains(beyond with { Z = 0 }));
                    break;
                }
                case PieceKind.KillZone:
                {
                    var k = c.ToCollisionWorld().KillVolumes.First(v => v.Shape is not null);
                    Assert.True(k.Contains(beyond), pid);
                    Assert.False(stock.Content.ToCollisionWorld().KillVolumes[0].Contains(beyond));
                    break;
                }
                default:
                    Assert.Fail($"preset {pid}: kind {kind} has no in-play check");
                    break;
            }
        }
    }

    // ---- geometry in play: walk and fire against the edited end ----

    /// <summary>A 1 x 4 x 3 m piece of <paramref name="kind"/> across x = 0 (y -2..2); pulled: its +Y end out to y = 6. A player starts at (-5, 4) and walks +X for 3 s.</summary>
    static (float EndX, ShotTrace Shot, ShotTrace Pierce) WalkAndFire(string kind, bool pulled)
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, """{"op":"place","piece":{"kind":"@K@"},"transform":{"position":[0,0,0],"scale":[1,4,3]}}""".Replace("@K@", kind));
        if (pulled)
        {
            var ids = EditMesh.Of(s.Document.Get("o2")).Vertices.Select((v, i) => (v, i)).Where(x => x.v.Y > 0.25f).Select(x => x.i);
            Run(s, $$"""{"op":"edit-mesh","id":"o2","mode":"vertex","ids":[{{string.Join(",", ids)}}],"move":[0,4,0]}""");
        }
        var map = Play(s.Document);
        var collision = map.ToCollisionWorld();
        var world = new World(TeamConfig, collision, [new SpawnPoint(new Vector3(-5, 4, 0), -MathF.PI / 2f)]);
        world.Spawn(1, 0);
        for (int t = 0; t < 90; t++) world.Step(id => new PlayerInput(0, 0, 1, -MathF.PI / 2f, InputButtons.None));   // yaw -90 degrees faces +X
        var from = new Vector3(-5, 4, 1.5f);
        var pierce = new PierceSpec(10f, 1f, null, false);
        return (world.Players[1].Position.X, collision.TraceShot(from, Vector3.UnitX, 20f, null), collision.TraceShot(from, Vector3.UnitX, 20f, pierce));
    }

    [Fact]
    public void A_solid_wall_stops_walking_and_shots_at_its_edited_end()
    {
        var (stockX, stockShot, _) = WalkAndFire(PieceKind.Box, pulled: false);
        Assert.True(stockX > 1f, $"{stockX}");          // the stock wall ends at y = 2: the player walks past at y = 4
        Assert.Null(stockShot.Stop);
        var (x, shot, _) = WalkAndFire(PieceKind.Box, pulled: true);
        Assert.True(x < -0.4f, $"{x}");                 // stopped at the edited end (x = -0.5 face)
        Assert.Equal(4.5f, shot.Stop!.Value.Distance, 2);
    }

    [Fact]
    public void A_players_only_wall_blocks_movement_at_its_edited_end_and_passes_shots()
    {
        Assert.True(WalkAndFire(PieceKind.MoveWall, pulled: false).EndX > 1f);
        var (x, shot, _) = WalkAndFire(PieceKind.MoveWall, pulled: true);
        Assert.True(x < -0.4f, $"{x}");
        Assert.Null(shot.First);                        // shots fly through it
    }

    [Fact]
    public void Edited_cover_stops_players_and_plain_shots_and_a_piercing_shot_passes_it_as_Classic_cover()
    {
        Assert.True(WalkAndFire(PieceKind.Cover, pulled: false).EndX > 1f);
        var (x, shot, pierce) = WalkAndFire(PieceKind.Cover, pulled: true);
        Assert.True(x < -0.4f, $"{x}");
        Assert.Equal(4.5f, shot.Stop!.Value.Distance, 2);   // a shot without a pierce spec stops at it
        Assert.Null(pierce.Stop);                            // a piercing shot passes (Classic's BakedProps rule)
        Assert.Single(pierce.Pierced);
    }

    // ---- gameplay volumes ----

    [Fact]
    public void An_asymmetric_capture_point_is_captured_from_inside_its_shape_and_never_from_inside_only_its_box()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, """{"op":"place","piece":{"kind":"capture-point"},"transform":{"position":[0,0,0],"scale":[8,8,4]}}""");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"vertex","ids":[0,16],"move":[4,0,0]}""");   // a thin spike toward +X (x up to 8)
        var map = Play(s.Document);
        var point = Assert.Single(map.Content.SimControlPoints());
        Assert.NotNull(point.Volume);
        foreach (var (spot, captures) in new[] { (new Vector3(6, 0, 0), true), (new Vector3(6, 3.5f, 0), false), (new Vector3(-3.5f, -3.5f, 0), false) })
        {
            var rules = MatchRules.For(GameMode.SeizeGround) with { TimeLimit = 0f };
            var w = new World(TeamConfig, map.ToCollisionWorld(), map.Content.SpawnPoints()) { Rules = rules, ControlPoints = [point],
                StartAreas = [new StartArea(Team.SpaceMarines, spot + new Vector3(0, 0, 1), new Vector3(1, 1, 2), 0f), new StartArea(Team.Chaos, new Vector3(-30, 30, 1), new Vector3(1, 1, 2), 0f)] };
            w.Spawn(1, 0); w.Spawn(2, 1);
            for (int t = 0; t < 60; t++) w.Step(id => new PlayerInput(0, 0, 0, w.Players[id].Yaw, InputButtons.None));
            Assert.Equal(captures, w.Points[0].Side == Team.SpaceMarines && w.Points[0].Progress > 0);
        }
    }

    [Fact]
    public void A_tapered_kill_zone_kills_inside_its_shape_and_not_inside_only_its_box()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, """{"op":"place","piece":{"kind":"kill-zone"},"transform":{"position":[0,0,0],"scale":[10,10,4]}}""");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"face","ids":[1],"scale":0.2}""");   // a pyramid frustum: top 2 x 2 m
        var world = Play(s.Document).ToCollisionWorld();
        float h = TeamConfig.CapsuleHeight;
        Assert.True(world.KillsPlayer(new Vector3(0, 0, 0.1f), new Vector3(0, 0, 0.1f), h));
        Assert.False(world.KillsPlayer(new Vector3(4.5f, 4.5f, 3.5f), new Vector3(4.5f, 4.5f, 3.5f), 0.2f));   // a top corner of the box, beside the frustum
        Assert.True(world.KillsPlayer(new Vector3(4.5f, 4.5f, 0.2f), new Vector3(4.5f, 4.5f, 0.2f), 0.1f));    // the same corner at the bottom, inside it
    }

    [Fact]
    public void Spawn_spots_come_only_from_inside_an_edited_spawn_area_in_the_simulation_and_the_check_alike()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, """{"op":"place","piece":{"kind":"spawn-area","side":"marines"},"transform":{"position":[0,0,0],"scale":[12,12,3]}}""");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"vertex","ids":[3,7],"move":[-11,0,0]}""");   // the +X +Y corner pulled back to x = -5: a wedge
        var map = Play(s.Document);
        var area = Assert.Single(map.Content.SimStartAreas());
        var world = new World(TeamConfig, map.ToCollisionWorld(), map.Content.SpawnPoints()) { StartAreas = [area] };
        var spots = world.StartAreaSpots(0);
        var stock = new World(TeamConfig, map.ToCollisionWorld(), map.Content.SpawnPoints()) { StartAreas = [area with { Volume = null }] }.StartAreaSpots(0);
        Assert.NotEmpty(spots);
        Assert.True(spots.Count < stock.Count, $"{spots.Count} vs {stock.Count}");
        Assert.All(spots, q => Assert.True(area.Volume!.Contains(q + new Vector3(0, 0, 0.1f)), $"{q}"));
        Assert.DoesNotContain(spots, q => q.X > 2f && q.Y > 2f);   // nothing in the cut-away corner of the box
        var check = MapCheck.SpotsIn(Assert.Single(map.Content.StartAreas), map.ToCollisionWorld().Character);
        Assert.Equal(spots.OrderBy(q => q.X).ThenBy(q => q.Y), check.OrderBy(q => q.X).ThenBy(q => q.Y));   // the editor's check counts the same spots
        // every spawned player stands inside the edited shape
        var match = new World(TeamConfig, map.ToCollisionWorld(), map.Content.SpawnPoints()) { Rules = MatchRules.For(GameMode.Annihilation), StartAreas = [area] };
        for (int id = 1; id <= 6; id++) Assert.True(area.Volume!.Contains(match.Spawn(id, 0).Position + new Vector3(0, 0, 0.1f)));
    }

    [Fact]
    public void A_spawn_door_stops_only_the_other_side_at_its_edited_boundary()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, """{"op":"place","piece":{"kind":"blocker","side":"marines"},"transform":{"position":[0,0,0],"scale":[12,4,6]}}""");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"vertex","ids":[3,7],"move":[-8,0,0]}""");   // +Y edge now x -6..-2, the slope from (6,-2) to (-2,2)
        var map = Play(s.Document);
        var world = map.ToCollisionWorld();
        var down = -Vector3.UnitY;
        var from = new Vector3(4, 10, 1);
        var chaos = world.CharacterFor(Team.Chaos).Raycast(from, down, 20f);
        Assert.Equal(11f, chaos!.Value.Distance, 2);      // at the slope (y = -1 at x = 4), not the box's +Y face (y = 2, 8 m)
        Assert.Null(world.CharacterFor(Team.SpaceMarines).Raycast(from, down, 20f));   // its own side passes
        Assert.Null(world.Weapons.Raycast(from, down, 20f));                           // shots pass
        var b = Assert.Single(world.Blockers);
        Assert.True(b.Contains(new Vector3(4, -1.5f, 1)));
        Assert.False(b.Contains(new Vector3(4, 1.5f, 1)));   // inside the box, outside the door
    }

    [Fact]
    public void The_check_finds_the_floor_and_kill_zones_by_the_edited_shapes()
    {
        using var lab = new Lab();
        // a 10 x 10 m floor; a capture point whose spike reaches 20 m past it, so the middle of its box is over the void
        var s = Map(lab, """{"op":"place","piece":{"kind":"box"},"transform":{"position":[0,0,-1],"scale":[10,10,1]}}""",
            """{"op":"place","piece":{"kind":"capture-point"},"transform":{"position":[0,0,0],"scale":[8,8,4]}}""",
            """{"op":"place","piece":{"kind":"kill-zone"},"transform":{"position":[0,0,0],"scale":[10,10,2]}}""");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"vertex","ids":[0,16],"move":[20,0,0]}""");
        var map = Play(s.Document);
        Assert.True(map.Content.ControlPoints[0].Position.X > 5f);   // its box's middle is off the floor (and outside the kill zone)
        var report = MapCheck.Run(map);
        Assert.DoesNotContain(report.Issues, i => i.Code == "point-no-floor");   // the shape has floor under it near the origin
        Assert.Contains(report.Issues, i => i.Code == "in-kill-zone");           // and that floor is inside the kill zone
        // the same layout unedited: the middle of the point is on the floor and in the kill zone, as before
        Run(s, """{"op":"reset-mesh","ids":["o2"]}""");
        var plain = MapCheck.Run(Play(s.Document));
        Assert.DoesNotContain(plain.Issues, i => i.Code == "point-no-floor");
        Assert.Contains(plain.Issues, i => i.Code == "in-kill-zone");
    }

    // ---- drawing ----

    [Theory]
    [InlineData(PieceKind.Box)]
    [InlineData(PieceKind.Ramp)]
    [InlineData(PieceKind.Column)]
    public void The_rendered_stock_shape_covers_the_same_surface_as_the_old_drawing(string kind)
    {
        using var lab = new Lab();
        var s = Map(lab, """{"op":"place","piece":{"kind":"@K@"},"transform":{"position":[3,-2,1],"rotation":[0,0,30],"scale":[4,6,2]}}""".Replace("@K@", kind));
        var p = Play(s.Document).Pieces[0];
        var r = p.Render();
        var (v, tri) = PieceMesh.Unit(kind)!.Value;
        var old = tri.Select(i => Vector3.Transform(v[i], p.World)).ToArray();
        static float Area(IReadOnlyList<Vector3> c) { float a = 0; for (int t = 0; t + 2 < c.Count; t += 3) a += Vector3.Cross(c[t + 1] - c[t], c[t + 2] - c[t]).Length() / 2f; return a; }
        Assert.Equal(Area(old), Area(r.Positions), 2);
        Assert.True(Vector3.Distance(old.Aggregate(Vector3.Min), r.Positions.Aggregate(Vector3.Min)) < 1e-3f);
        Assert.True(Vector3.Distance(old.Aggregate(Vector3.Max), r.Positions.Aggregate(Vector3.Max)) < 1e-3f);
        // every face points outward (the shapes are convex), as the stock path's normals do, so the client's one corner swap gives Godot's front faces
        var centre = v.Select(q => Vector3.Transform(q, p.World)).Aggregate(Vector3.Add) / v.Length;   // the mean corner: inside every convex shape (a ramp's box centre lies on its slope)
        for (int t = 0; t + 2 < r.Positions.Length; t += 3)
            Assert.True(Vector3.Dot(r.Normals[t], (r.Positions[t] + r.Positions[t + 1] + r.Positions[t + 2]) / 3f - centre) > 0f, $"triangle {t / 3}");
    }
}
