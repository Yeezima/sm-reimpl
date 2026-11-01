using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Battles on a real map's terms (pass 2, M1), on a synthetic level with walls, points of different heights, a kill volume, a prop-free floor and spawn-room blockers; no original bytes. The hub's
/// place, the composed hash, the map's points as positions, the drawn sites and the wire that shows them.
/// </summary>
public class TfwWorldTests
{
    /// <summary>A yard: a 120 x 80 m floor at z = 0, a raised 30 x 30 m block in the east (its top at z = 4), a wall across the middle with a gap, a kill volume below and a blocker round the Chaos start.</summary>
    static readonly Vector3 PointA = new(-40f, -10f, 0f), PointB = new(0f, -10f, 0f), PointC = new(40f, -10f, 4f);
    static readonly StartArea Marines = new(Team.SpaceMarines, new Vector3(0f, -35f, 0f), new Vector3(10f, 8f, 5f), 0f);
    static readonly StartArea Chaos = new(Team.Chaos, new Vector3(-30f, 30f, 0f), new Vector3(10f, 8f, 5f), MathF.PI);
    static readonly KillVolume Pit = new(new Vector3(0f, 0f, -12f), new Vector3(80f, 60f, 1f), 0f);

    static Vector3[] Yard()
    {
        TfwSolid[] solids =
        [
            new(new Vector3(-60f, -40f, -1f), new Vector3(60f, 40f, 0f)),
            new(new Vector3(25f, -25f, 0f), new Vector3(55f, 5f, 4f)),
            new(new Vector3(-60f, 12f, 0f), new Vector3(-5f, 13f, 6f)),
            new(new Vector3(5f, 12f, 0f), new Vector3(60f, 13f, 6f)),
        ];
        return solids.SelectMany(TfwMap.Triangles).ToArray();
    }

    static CollisionWorld Level(bool blockers = true)
    {
        var corners = Yard();
        var mesh = new StaticMesh(corners, Enumerable.Range(0, corners.Length).ToArray());
        SpawnBlocker[] rooms = blockers ? [new SpawnBlocker(Team.Chaos, new Vector3(-30f, 25f, 2f), new Vector3(10f, 1f, 4f), 0f)] : [];
        return new CollisionWorld(mesh, mesh, [Pit], rooms);
    }

    static readonly ControlPoint[] Points = [new("a", PointA, 15f, 10f), new("b", PointB, 13.5f, 10f), new("c", PointC, 15f, 10f)];
    static TfwMapData Compose() => TfwMap.Compose(Level(), Points, [Marines, Chaos], [Marines.Center, Chaos.Center]);

    static NavGraph Nav(TfwMapData data) => NavGraph.Build(data.Collision.Character, data.Collision.KillVolumes, NavSettings.From(new SimConfig()), data.Field.WalkArea());

    // ---------------------------------------------------------------- the hub's place

    [Fact]
    public void No_triangle_or_kill_volume_of_the_map_falls_inside_the_hub()
    {
        var level = Level();
        var hub = TfwHub.Beyond(level).Box;
        var corners = Yard();
        foreach (var v in corners) Assert.False(Vector3.Clamp(v, hub.Min, hub.Max) == v, $"{v} in the hub");
        Assert.True(hub.Max.Y <= level.Character.Min.Y - TfwMap.HubClearance + 1f, "clear of the level");
        Assert.True(hub.Max.Y < Pit.Center.Y - Pit.HalfExtents.Y - TfwMap.WalkMargin, "clear of the kill volume");
        Assert.Equal(SmContent.BotNavigation.Margin, TfwMap.WalkMargin);
        Assert.True(TfwMap.HubClearance > SmContent.BotNavigation.Margin + 50f);
    }

    [Fact]
    public void The_place_is_the_same_on_every_build_in_whole_metres_at_ground_height()
    {
        var a = TfwHub.Beyond(Level());
        Assert.Equal(a, TfwHub.Beyond(Level()));
        Assert.Equal(0f, a.Centre.Z);
        Assert.Equal(MathF.Round(a.Centre.X), a.Centre.X);
    }

    [Fact]
    public void A_walker_on_the_level_cannot_reach_the_hub_and_its_room_is_closed()
    {
        var data = Compose();
        var hub = data.Field.Hub;
        var inside = hub.Centre + new Vector3(0f, 0f, 2f);
        foreach (var way in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            Assert.NotNull(data.Collision.Character.Raycast(inside, way, 20f));
        Assert.All(data.Spawns, s => Assert.True(hub.Contains(s.Position)));
        Assert.True(hub.Contains(hub.DestinationStation) && hub.Contains(hub.EquipmentStation));
    }

    // ---------------------------------------------------------------- the composed world

    [Fact]
    public void The_hash_is_the_rule_and_both_ends_compute_it_from_the_level()
    {
        var data = Compose();
        var (world, hub, hash) = TfwMap.WithHub(Level());
        Assert.Equal(data.Hash, hash);
        Assert.Equal(data.Field.Hub, hub);
        Assert.Equal(data.Collision.ContentHash, world.ContentHash);
        Assert.Equal(world.ContentHash ^ 0x7466775F7761725FUL ^ 4UL, hash);
        Assert.Equal(4u, TfwMap.Version);
        Assert.NotEqual(hash, TfwMap.WithHub(Level(blockers: false)).Hash ^ 1UL);
    }

    [Fact]
    public void The_kill_volumes_stay_the_blockers_go_and_the_hub_comes_to_both_meshes()
    {
        var level = Level();
        var (world, hub, _) = TfwMap.WithHub(level);
        int hubTriangles = hub.Corners().Length / 3;
        Assert.Single(world.KillVolumes);
        Assert.Empty(world.Blockers);   // the commander stands in the Chaos room: Marines must reach him
        Assert.Equal(level.Character.TriangleCount + hubTriangles, world.Character.TriangleCount);
        Assert.Equal(level.Weapons.TriangleCount + hubTriangles, world.Weapons.TriangleCount);
    }

    [Fact]
    public void A_map_without_three_points_or_a_side_is_refused_by_name()
    {
        Assert.Contains("3", Assert.Throws<InvalidDataException>(() => TfwMap.Compose(Level(), Points[..2], [Marines, Chaos], [Marines.Center])).Message);
        Assert.Contains("start area", Assert.Throws<InvalidDataException>(() => TfwMap.Compose(Level(), Points, [Marines], [Marines.Center])).Message);
    }

    [Fact]
    public void Without_manufactorum_the_server_says_so_and_starts_nothing()
    {
        string? name = null;
        string dir = Path.Combine(Path.GetTempPath(), "sm-no-game-" + Guid.NewGuid().ToString("N"));
        Assert.Null(GameServer.GeneratedMap(ContentExtras.Musou, dir, ref name, out string? error));
        Assert.Contains("Manufactorum", error);
        Assert.Contains("--game-dir", error);
    }

    [Fact]
    public void The_graph_leaves_the_hub_out_and_reaches_every_point_and_the_commander()
    {
        var data = Compose();
        var nav = Nav(data);
        for (int n = 0; n < nav.NodeCount; n++) Assert.False(data.Field.Hub.Contains(nav.Position(n)));
        Assert.All(data.Field.WalkSeeds, s => Assert.False(data.Field.Hub.Contains(s)));
        var start = nav.Position(nav.Nearest(Marines.Center));
        foreach (var at in new[] { PointA, PointB, Chaos.Center })
            Assert.False(Assert.IsType<NavPath>(nav.FindPath(start, at)).Partial, $"to {at}");
    }

    [Fact]
    public void An_order_stays_on_the_battlefield()
    {
        var field = Compose().Field;
        var p = field.Clamp(new Vector3(9000f, -9000f, 9000f));
        Assert.Equal(field.Bounds.Max.X, p.X);
        Assert.Equal(field.Bounds.Min.Y, p.Y);
        Assert.Equal(field.Bounds.Max.Z, p.Z);
    }

    // ---------------------------------------------------------------- the layout

    static TfwSites Sites(out TfwMapData data)
    {
        data = Compose();
        return new TfwSites(data.Field, Nav(data), data.Collision);
    }

    [Theory]
    [InlineData(1, 0, 2)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 0, 2)]
    public void The_points_run_a_to_c_or_c_to_a_by_node_and_b_is_always_the_depot(int node, int first, int third)
    {
        var layout = Sites(out var data).Layout(TfwLane.A, node, 7);
        float Off(int position, int point) => new Vector2(layout.Positions[position].X - data.Field.Points[point].Centre.X, layout.Positions[position].Y - data.Field.Points[point].Centre.Y).Length();
        Assert.True(Off(0, first) < 1f);
        Assert.True(Off(1, 1) < 1f);
        Assert.True(Off(2, third) < 1f);
        Assert.Equal(data.Field.Points[first].Radius, layout.Radius(0));
        Assert.Equal(13.5f, layout.Radius(1));
    }

    [Fact]
    public void The_commander_stands_in_the_chaos_start_area()
    {
        var layout = Sites(out _).Layout(TfwLane.B, 1, 3);
        Assert.True(new Vector2(layout.Hq.X - Chaos.Center.X, layout.Hq.Y - Chaos.Center.Y).Length() < 1f);
    }

    [Fact]
    public void The_same_seed_draws_the_same_sites_and_other_seeds_vary_them()
    {
        var sites = Sites(out _);
        Assert.Equal(sites.Layout(TfwLane.A, 1, 11).Sources, sites.Layout(TfwLane.A, 1, 11).Sources);
        var distinct = Enumerable.Range(0, 12).Select(seed => sites.Layout(TfwLane.A, 1, (ulong)seed).Sources[0]).Distinct().Count();
        Assert.True(distinct >= 6, $"{distinct} different sources");
        Assert.NotEqual(sites.Layout(TfwLane.A, 1, 1).Sources, sites.Layout(TfwLane.C, 1, 1).Sources);
    }

    [Fact]
    public void Every_source_keeps_the_ring_and_stands_on_the_graph_away_from_the_landing_zone()
    {
        var sites = Sites(out var data);
        var nav = Nav(data);
        Assert.True(sites.Candidates > 100);
        for (ulong seed = 0; seed < 40; seed++)
        {
            var layout = sites.Layout(TfwLane.A, 1, seed);
            Vector3[] anchors = [.. layout.Positions, layout.Hq];
            for (int k = 0; k < anchors.Length; k++)
            {
                var s = layout.Sources[k];
                float ring = new Vector2(s.X - anchors[k].X, s.Y - anchors[k].Y).Length();
                Assert.InRange(ring, TfwSites.SourceNear, TfwSites.SourceFar);
                Assert.True(new Vector2(s.X - layout.LandingZone.X, s.Y - layout.LandingZone.Y).Length() >= TfwSites.FromImperials, $"seed {seed}: source {k} {s} by {layout.LandingZone}");
                Assert.True(nav.Nearest(s) >= 0, $"{s} is off the graph");
            }
        }
    }

    [Fact]
    public void A_landing_zone_is_the_start_area_or_open_ground_far_from_the_points()
    {
        var sites = Sites(out var data);
        var seen = new HashSet<Vector3>();
        for (ulong seed = 0; seed < 30; seed++)
        {
            var lz = sites.Layout(TfwLane.A, 1, seed).LandingZone;
            seen.Add(lz);
            Assert.False(data.Field.Hub.Contains(lz));
        }
        Assert.Contains(seen, lz => new Vector2(lz.X - Marines.Center.X, lz.Y - Marines.Center.Y).Length() < 1f);
    }

    [Fact]
    public void Without_a_graph_the_places_are_fixed_and_finite()
    {
        var data = Compose();
        var layout = new TfwSites(data.Field, null, null).Layout(TfwLane.A, 1, 5);
        Assert.Equal(Marines.Center, layout.LandingZone);
        Assert.Equal(new TfwSites(data.Field, null, null).Layout(TfwLane.A, 1, 99).Sources, layout.Sources);
        Assert.All(layout.Sources, s => Assert.True(float.IsFinite(s.X + s.Y + s.Z)));
    }

    // ---------------------------------------------------------------- a battle on it

    [Fact]
    public void An_ork_on_a_raised_point_holds_it_within_the_points_height()
    {
        var sim = new MusouSim(null);
        var layout = new TfwLayout(default, [new(0f, 0f, 0f), new(30f, 0f, 0f), new(60f, 0f, 0f)], new Vector3(90f, 0f, 0f), [default, default, default, default], [15f, 15f, 15f], [10f, 10f, 10f]);
        Assert.Equal(10f, layout.Reach(0));
        Assert.Equal(TfwLayout.DefaultHalfHeight, new TfwLayout(default, [default], default, []).Reach(0));
        Span<ushort> one = stackalloc ushort[1];
        Assert.NotEqual((ushort)0, sim.Spawn(MusouEnemyType.Boy, new Vector3(5f, 0f, 6f), 0f, 10f));
        Assert.Equal(0, sim.Query(layout.Positions[0], layout.Radius(0), one));   // the default's reach misses him
        Assert.Equal(1, sim.Query(layout.Positions[0], layout.Radius(0), one, layout.Reach(0)));
    }

    [Fact]
    public void A_battle_on_the_yard_starts_on_the_ground_with_each_points_radius_on_the_objectives()
    {
        var data = Compose();
        var nav = Nav(data);
        var w = new World(TfwRig.Config, data.Collision, data.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = data.StartAreas };
        Assert.True(w.StartTfw(nav, data.Field));
        w.Tfw!.RetainerUp = _ => false;
        w.Spawn(TfwRig.Human, w.AssignClass(0));
        TfwRig.Steps(w, 30);
        var battle = TfwRig.Deploy(w);
        var objectives = battle.Objectives;
        Assert.Equal(13.5f, objectives[1].Radius);
        Assert.Equal(TfwMap.HqRadius, objectives[TfwBattle.CommanderObjective].Radius);
        var p = w.Players[TfwRig.Human].Position;
        Assert.False(data.Field.Hub.Contains(p));
        Assert.True(Vector2.Distance(new(p.X, p.Y), new(battle.Layout.LandingZone.X, battle.Layout.LandingZone.Y)) < TfwMap.LandingRadius);
        Assert.True(w.Musou!.Alive >= TfwBattleRules.Garrison * WarRules.Positions - 2);
        Assert.True(w.Musou.Box.Min.Y <= data.Field.Hub.Box.Min.Y, "the wire's box holds the Stormbird");
    }

    // ---------------------------------------------------------------- the wire

    static readonly MusouBox WireBox = new(new Vector3(-300f, -1300f, -20f), new Vector3(300f, -600f, 60f));

    [Fact]
    public void Tag_8_carries_each_radius_and_the_sites()
    {
        var objectives = new[] { new TfwObjective(0, TfwPositionState.Held, 0f, new Vector3(110f, -785f, 4f), 15f), new TfwObjective(1, TfwPositionState.Locked, 0.5f, new Vector3(200f, -800f, 0f), 13.5f) };
        Vector3[] sites = [new(245f, -937f, 5.5f), new(100f, -760f, 2f)];
        byte[] bytes = TfwWire.WriteObjectives(objectives, WireBox, sites);
        Assert.Equal(2 + 2 * TfwWire.ObjectiveBytes + 2 * TfwWire.SiteBytes, bytes.Length);
        var back = Assert.IsType<TfwObjective[]>(TfwWire.ReadObjectives(bytes, WireBox));
        Assert.Equal(15f, back[0].Radius);
        Assert.Equal(13.5f, back[1].Radius);
        var read = TfwWire.ReadSites(bytes, WireBox);
        Assert.Equal(2, read.Length);
        Assert.True(Vector3.Distance(sites[0], read[0]) < 0.05f);
    }

    [Fact]
    public void A_tag_8_with_too_many_sites_or_the_wrong_length_is_malformed()
    {
        byte[] good = TfwWire.WriteObjectives([], WireBox, [Vector3.Zero]);
        Assert.NotNull(TfwWire.ReadObjectives(good, WireBox));
        Assert.Null(TfwWire.ReadObjectives(good[..^1], WireBox));
        Assert.Null(TfwWire.ReadObjectives([0], WireBox));
        byte[] many = (byte[])good.Clone();
        many[1] = TfwWire.MaxSites + 1;
        Assert.Null(TfwWire.ReadObjectives(many, WireBox));
    }

    [Fact]
    public void Tag_8_stays_small_a_battle_is_at_most_72_bytes()
    {
        var objectives = Enumerable.Range(0, 4).Select(i => new TfwObjective((byte)i, TfwPositionState.Held, 0f, default, 12f)).ToArray();
        Assert.True(TfwWire.WriteObjectives(objectives, WireBox, new Vector3[TfwWire.MaxSites]).Length <= 76);
        Assert.True(TfwWire.MaxObjectives >= 4);
    }

    [Fact]
    public void The_stations_follow_the_hub()
    {
        var hub = Compose().Field.Hub;
        Assert.Equal(TfwStation.Destination, TfwHudModel.StationAt(hub.DestinationStation, hub));
        Assert.Equal(TfwStation.Destination, TfwHudModel.StationAt(hub.DestinationStation + new Vector3(0.9f, 0.9f, 0f), hub));
        Assert.Equal(TfwStation.Equipment, TfwHudModel.StationAt(hub.EquipmentStation, hub));
        Assert.Equal(TfwStation.None, TfwHudModel.StationAt(hub.Centre, hub));
        Assert.Equal(TfwStation.None, TfwHudModel.StationAt(hub.OnFloor(new Vector2(0f, TfwShip.RampEndY - 1f)), hub));   // the ramp's end
        Assert.All(hub.Spawns, s => Assert.Equal(TfwStation.None, TfwHudModel.StationAt(s.Position, hub)));   // nobody spawns on a pad
        Assert.True(Vector3.Distance(hub.DestinationStation, hub.EquipmentStation) > 2f * TfwMap.StationRadius, "the two pads do not touch");
    }
}
