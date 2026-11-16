using System.Numerics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Warfront.Hq;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Hq;

/// <summary>
/// W13a: the HQ defense map's own gates (plan section 2): the build is deterministic; a walking graph over it connects assembly -> W -> K -> V -> landing; the Ork route is at least 8 m wide everywhere; the parapet is reached only from
/// inside (the courtyard's stairs); tunnel roofs leave the graph alone; every marker stands on the graph. Every size is ours (spec "Warfront HQ defense map").
/// </summary>
public class WarfrontHqMapTests(ITestOutputHelper output)
{
    /// <summary>The level's hash, pinned: it is the same on every machine and in every process (the test passes in a fresh process each run).</summary>
    const ulong PinnedHash = 0xBFEE19CA85F8E28EUL;

    static readonly Lazy<(WarfrontHqBuild Map, NavGraph Nav)> World = new(() =>
    {
        var map = WarfrontHqMap.Build();
        var collision = AshfallContent.ToMapContent(map.Level).ToCollisionWorld();
        var nav = NavGraph.Build(collision.Character, collision.KillVolumes, NavSettings.From(new SimConfig()), map.WalkArea);
        return (map, nav);
    });

    [Fact]
    public void Build_is_deterministic_in_one_process()
    {
        var a = WarfrontHqMap.Build();
        var b = WarfrontHqMap.Build();
        Assert.Equal(a.Level.Hash, b.Level.Hash);
        Assert.Equal(a.Level.Parts.Count, b.Level.Parts.Count);
        for (int i = 0; i < a.Level.Parts.Count; i++)
        {
            Assert.Equal(a.Level.Parts[i].Kind, b.Level.Parts[i].Kind);
            Assert.True(a.Level.Parts[i].Corners.SequenceEqual(b.Level.Parts[i].Corners));
        }
        Assert.Equal(AshfallContent.ToMapContent(a.Level).Hash, a.Level.Hash);
        output.WriteLine($"hash 0x{a.Level.Hash:X16}, {a.Level.Parts.Count} parts");
    }

    [Fact]
    public void Build_hash_is_pinned_so_two_processes_and_two_machines_agree()
    {
        Assert.Equal(PinnedHash, WarfrontHqMap.Build().Level.Hash);
    }

    [Fact]
    public void Every_opening_on_the_ork_route_is_at_least_8_m_wide()
    {
        Assert.All(WarfrontHqMap.RouteWidths, d => Assert.True(d.Value >= 8f, d.Name));
        Assert.True(WarfrontHqMap.GateWidth >= 8f && WarfrontHqMap.DoorWidth >= 8f && WarfrontHqMap.TunnelWidth >= 8f);
    }

    [Fact]
    public void Markers_are_named_and_inside_the_map_in_the_plan_order()
    {
        var m = World.Value.Map;
        Assert.Equal(["W", "K", "V"], m.Level.Points.Select(p => p.Name));
        Assert.True(m.AssemblyCentre.Y < m.W.Y && m.W.Y < m.K.Y && m.K.Y < m.V.Y && m.V.Y < m.LandingZone.Y);
        Assert.All(m.Sources, s => Assert.InRange(s.Position.Y, WarfrontHqMap.SouthEdge, WarfrontHqMap.NorthEdge));
        Assert.Equal([0, 1, 2], m.Sources.Select(s => s.Stage).Distinct().Order());
        Assert.Equal([0, 1, 2], m.Posts.Select(p => p.DefencePosition).Distinct().Order());
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Walking_graph_connects_assembly_wall_keep_caves_and_landing()
    {
        var (m, nav) = World.Value;
        output.WriteLine($"graph: {nav.NodeCount:N0} nodes");
        var stops = new[] { m.AssemblyCentre, m.W, m.K, m.V, m.LandingZone };
        var route = new List<Vector3> { stops[0] };
        for (int i = 0; i + 1 < stops.Length; i++)
        {
            var path = nav.FindPath(stops[i], stops[i + 1], goalSnap: 1.5f);
            Assert.NotNull(path);
            Assert.False(path!.Partial, $"{i} -> {i + 1} is partial");
            output.WriteLine($"leg {i}: {path.Length:0.0} m");
            route.AddRange(path.Waypoints.Select(w => w.Position));
        }
        var whole = nav.FindPath(m.AssemblyCentre, m.LandingZone, goalSnap: 1.5f);
        Assert.NotNull(whole);
        Assert.False(whole!.Partial);
        Overhead(m, whole.Waypoints.Select(w => w.Position).ToList());
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Every_marker_stands_on_the_walking_graph()
    {
        var (m, nav) = World.Value;
        foreach (var p in m.Level.Spawns) Assert.True(nav.Nearest(p, 1f, 1f) >= 0, $"no graph node at {p}");
        Assert.All(m.Sources, s => Assert.True(nav.Nearest(s.Position, 1f, 1f) >= 0, s.Name));
        Assert.All(m.Posts, s => Assert.True(nav.Nearest(s.Position, 1f, 1f) >= 0, s.Name));
    }

    /// <summary>The widest walkable row of nodes across an opening: a node sits at least 0.4 m from a wall, so an 8 m opening shows about 7.2 m of nodes.</summary>
    static float Extent(NavGraph nav, float y, float z, float xLo, float xHi)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < nav.NodeCount; i++)
        {
            var p = nav.Position(i);
            if (MathF.Abs(p.Y - y) > 0.26f || MathF.Abs(p.Z - z) > 0.6f || p.X < xLo || p.X > xHi) continue;
            lo = MathF.Min(lo, p.X); hi = MathF.Max(hi, p.X);
        }
        return hi < lo ? 0f : hi - lo;
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Walkable_width_across_every_opening_on_the_ork_route_is_at_least_8_m_less_the_body_margin()
    {
        var nav = World.Value.Nav;
        const float needed = 7.0f;   // 8 m between walls leaves node rows 0.4 m inside each: about 7.2 m
        float P = WarfrontHqMap.PlateauZ;
        var rows = new (string Name, float Extent)[]
        {
            ("ramp", Extent(nav, -90f, 5.7f, -20f, 20f)),
            ("gate", Extent(nav, -43f, P, -10f, 10f)),
            ("west door", Extent(nav, 42f, P, -36f, -16f)), ("east door", Extent(nav, 42f, P, 16f, 36f)),
            ("west hall opening", Extent(nav, 98f, P, -34f, -10f)), ("east hall opening", Extent(nav, 98f, P, 10f, 34f)),
            ("west tunnel", Extent(nav, 112f, P, -34f, -10f)), ("east tunnel", Extent(nav, 112f, P, 10f, 34f)),
        };
        foreach (var (name, e) in rows) output.WriteLine($"{name}: {e:0.0} m of walkable nodes");
        Assert.All(rows, r => Assert.True(r.Extent >= needed, $"{r.Name}: {r.Extent:0.0} m"));
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Both_doors_and_both_tunnels_are_real_approaches()
    {
        var (m, nav) = World.Value;
        float P = WarfrontHqMap.PlateauZ;
        foreach (float sx in new[] { -1f, 1f })
        {
            var door = nav.FindPath(new Vector3(sx * 26f, 34f, P), new Vector3(sx * 26f, 52f, P), goalSnap: 1.5f);
            Assert.NotNull(door);
            Assert.False(door!.Partial);
            Assert.True(door.Length < 20f, $"detour of {door.Length:0.0} m: the door is not the way through");   // 18 m straight through the door itself
            var tunnel = nav.FindPath(new Vector3(sx * 22f, 90f, P), new Vector3(sx * 22f, 160f, P), goalSnap: 1.5f);
            Assert.NotNull(tunnel);
            Assert.False(tunnel!.Partial);
            Assert.All(tunnel.Waypoints.Where(w => w.Position.Y is > 104f and < 148f), w => Assert.InRange(MathF.Abs(w.Position.X), 18f, 26f));   // inside its own tunnel, 6 m of headroom over it
        }
    }

    [Fact]
    public void Tunnel_roofs_and_the_chamber_roof_stand_clear_of_the_floor_by_the_stated_headroom()
    {
        var m = WarfrontHqMap.Build();
        float P = WarfrontHqMap.PlateauZ;
        var roofs = m.RoofParts.Select(i => m.Level.Parts[i]).ToList();
        Assert.Equal(5, roofs.Count);   // the hall's slab, two tunnel roofs, the cross passage roof, the chamber roof
        float[] clear = [WarfrontHqMap.KeepTop - 2f - P, WarfrontHqMap.TunnelClear, WarfrontHqMap.TunnelClear, WarfrontHqMap.CrossClear, WarfrontHqMap.ChamberClear];
        for (int i = 0; i < roofs.Count; i++)
            Assert.Equal(P + clear[i], roofs[i].Corners.Min(c => c.Z), 3);
        Assert.True(WarfrontHqMap.TunnelClear >= 8f && WarfrontHqMap.CrossClear >= 8f);   // a Boss Nob stands about 6.9 m
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void The_cross_passage_joins_the_tunnels_halfway_and_the_posts_stand_in_its_mouths()
    {
        var (m, nav) = World.Value;
        float P = WarfrontHqMap.PlateauZ;
        var path = nav.FindPath(new Vector3(-22f, 125f, P), new Vector3(22f, 125f, P), goalSnap: 1.5f);
        Assert.NotNull(path);
        Assert.False(path!.Partial);
        Assert.True(path.Length < 50f, $"{path.Length:0.0} m: not straight through the passage");
        Assert.Contains(m.Posts, p => p.DefencePosition == 2 && p.Position.Y is > 121f and < 129f);
        Assert.True(nav.Nearest(new Vector3(0f, 125f, P), 1f, 1f) >= 0);
        // the courtyard's second row: eight cover walls in all
        Assert.True(m.Level.Parts.Count(q => q.Kind == SmSim.Extras.Musou.Ashfall.AshfallKind.Cover) >= 12);
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void The_parapet_is_reached_only_from_inside_by_the_courtyard_stairs()
    {
        var (m, nav) = World.Value;
        float top = WarfrontHqMap.WallTop;
        foreach (float sx in new[] { -1f, 1f })
        {
            // from the assembly ground the walk reaches the wall top only through the gate and up one of the stairs (x 40..68 either side)
            var path = nav.FindPath(m.AssemblyCentre, new Vector3(sx * 56f, -43f, top), goalSnap: 1.5f);
            Assert.NotNull(path);
            Assert.False(path!.Partial);
            var firstUp = path.Waypoints.First(w => w.Position.Z > WarfrontHqMap.PlateauZ + 0.8f && w.Position.Y > WarfrontHqMap.WallSouth);   // first point up on the wall's own height
            Assert.True(path.Waypoints.Any(w => w.Position.Y > WarfrontHqMap.WallNorth && MathF.Abs(w.Position.Z - WarfrontHqMap.PlateauZ) < 0.6f), "never entered the courtyard");
            Assert.InRange(MathF.Abs(firstUp.Position.X), 38f, 70f);
            Assert.True(firstUp.Position.Y > WarfrontHqMap.WallNorth - 0.5f, $"climbed from outside at {firstUp.Position}");
        }
        // no walking node on the wall's top outside the walk from the courtyard: the ramp and assembly sides have no node above the 12 m plateau
        int outsideUp = 0;
        for (int i = 0; i < nav.NodeCount; i++)
        {
            var p = nav.Position(i);
            if (p.Y < WarfrontHqMap.WallSouth - 0.1f && p.Z > 12.6f) outsideUp++;
        }
        Assert.Equal(0, outsideUp);
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Every_defence_post_is_on_the_graph_near_its_position()
    {
        var (m, nav) = World.Value;
        // a post of each position is on the graph, inside its marker's radius (or on the parapet above it) and on the map's inside
        foreach (var post in m.Posts)
        {
            var pt = m.Level.Points[post.DefencePosition];
            float d = Vector2.Distance(new Vector2(post.Position.X, post.Position.Y), new Vector2(pt.Center.X, pt.Center.Y));
            Assert.True(d <= pt.Radius, $"{post.Name} is {d:0} m from {pt.Name}");
            Assert.True(nav.Nearest(post.Position, 1f, 1f) >= 0);
        }
        output.WriteLine($"{m.Posts.Count} posts, {m.Sources.Count} sources");
    }

    static void Overhead(WarfrontHqBuild m, List<Vector3> route)
    {
        string? dir = Environment.GetEnvironmentVariable("WF_HQ_OVERHEAD_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        WarfrontHqOverhead.Write(Path.Combine(dir, "hq-overhead-roofed.png"), m, roofs: true, route: null);
        WarfrontHqOverhead.Write(Path.Combine(dir, "hq-overhead-cutaway-route.png"), m, roofs: false, route: route);
    }
}

