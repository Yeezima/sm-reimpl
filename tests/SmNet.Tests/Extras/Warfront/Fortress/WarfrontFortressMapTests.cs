using System.Numerics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Fortress;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Fortress;

/// <summary>
/// W14a: the Ork fortress map's own gates and the arena interface (plan section 2): the build is deterministic with a pinned hash that includes the markers; a walking graph connects the landing zone to the Warboss spot by
/// three routes; every Ork source is reachable; every Ork opening is at least 8 m wide and 10 m clear; a stub (handmade-style) arena loads through the interface. Every size is ours (spec "Warfront fortress map profile").
/// </summary>
public class WarfrontFortressMapTests(ITestOutputHelper output)
{
    /// <summary>The level's hash, pinned: the same on every machine and in every process (the test passes in a fresh process each run).</summary>
    const ulong PinnedHash = 0xC3A89ACDE7D994ABUL;

    static readonly Lazy<(WarfrontFortressBuild Map, NavGraph Nav)> World = new(() =>
    {
        var map = WarfrontFortressMap.Build();
        return (map, NavFor(map));
    });

    static NavGraph NavFor(IWarfrontArena arena)
    {
        var collision = AshfallContent.ToMapContent(arena.Level).ToCollisionWorld();
        return NavGraph.Build(collision.Character, collision.KillVolumes, NavSettings.From(new SimConfig()), arena.WalkArea);
    }

    [Fact]
    public void Build_is_deterministic_in_one_process()
    {
        var a = WarfrontFortressMap.Build();
        var b = WarfrontFortressMap.Build();
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
        Assert.Equal(PinnedHash, WarfrontFortressMap.Build().Level.Hash);
    }

    [Fact]
    public void Hash_includes_the_markers_so_a_moved_source_changes_it()
    {
        var m = WarfrontFortressMap.Build();
        var moved = m.Sources.Select((s, i) => i == 0 ? s with { Position = s.Position + new Vector3(1f, 0f, 0f) } : s).ToList();
        ulong baseHash = WarfrontArenaGates.GeometryHash(m.Level.Parts, WarfrontFortressMap.Version);
        ulong a = WarfrontArenaGates.MarkerHash(baseHash, m.Level.Points, m.Level.Areas, m.Sources, m.Posts, m.Routes, m.Level.Spawns);
        ulong b = WarfrontArenaGates.MarkerHash(baseHash, m.Level.Points, m.Level.Areas, moved, m.Posts, m.Routes, m.Level.Spawns);
        Assert.Equal(m.Level.Hash, a);
        Assert.NotEqual(a, b);
        Assert.NotEqual(baseHash, a);
    }

    [Fact]
    public void Structure_gates_pass_for_the_generated_arena()
    {
        var m = WarfrontFortressMap.Build();
        Assert.Empty(WarfrontArenaGates.CheckStructure(m));
        Assert.All(m.RouteWidths, d => Assert.True(d.Value >= WarfrontArenaGates.OrkOpening, d.Name));
        Assert.Equal([0, 1, 2], m.Sources.Select(s => s.Stage).Distinct().Order());
        Assert.True(m.Routes.Count >= 2);
        Assert.Equal(45f, m.ArenaRadius);
        Assert.True(m.LandingZone.Y < m.Sources.Min(s => s.Position.Y) && m.BossSpot.Y > 0f);
        Assert.True(WarfrontFortressMap.GateClear >= 8f && WarfrontFortressMap.OpeningClear >= 8f);
    }

    [Fact]
    public void Lintels_stand_clear_of_the_floor_by_the_stated_headroom()
    {
        var m = WarfrontFortressMap.Build();
        Assert.Equal(4, m.RoofParts.Count);   // the gate and the arena's three openings
        Assert.Equal(WarfrontFortressMap.GateClear, m.Level.Parts[m.RoofParts[0]].Corners.Min(c => c.Z), 3);
        foreach (int i in m.RoofParts.Skip(1)) Assert.Equal(WarfrontFortressMap.OpeningClear, m.Level.Parts[i].Corners.Min(c => c.Z), 3);
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Walking_graph_connects_the_landing_zone_to_the_warboss_spot_by_every_route_and_reaches_every_source()
    {
        var (m, nav) = World.Value;
        output.WriteLine($"graph: {nav.NodeCount:N0} nodes");
        Assert.Empty(WarfrontArenaGates.CheckWalk(m, nav));
        Assert.True(m.Routes.Count >= 3);
        WalkRoute(nav, m, "centre", out var centre);
        WalkRoute(nav, m, "west lane", out var west);
        WalkRoute(nav, m, "east lane", out var east);
        // the lanes really go round: through a side opening, not the south one
        Assert.Contains(west, p => p.X < -48f && p.Y is > 5f and < 95f);
        Assert.Contains(east, p => p.X > 48f && p.Y is > 5f and < 95f);
        Assert.DoesNotContain(centre, p => MathF.Abs(p.X) > 50f && p.Y > 0f);
        var whole = nav.FindPath(m.LandingZone, m.BossSpot, goalSnap: 1.5f);
        Assert.NotNull(whole);
        Assert.False(whole!.Partial);
        string? dir = Environment.GetEnvironmentVariable("WF_FORTRESS_OVERHEAD_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            WarfrontFortressOverhead.Write(Combine_(dir, "fortress-overhead-roofed.png"), m, roofs: true, route: null);
            WarfrontFortressOverhead.Write(Combine_(dir, "fortress-overhead-cutaway-routes.png"), m, roofs: false, route: [.. centre, .. west, .. east]);
        }
    }

    static string Combine_(string dir, string file) => Path.Combine(dir, file);

    static void WalkRoute(NavGraph nav, WarfrontFortressBuild m, string routeName, out List<Vector3> points)
    {
        var route = m.Routes.Single(r => r.Name == routeName);
        var stops = new List<Vector3> { m.LandingZone };
        stops.AddRange(route.Via);
        stops.Add(m.BossSpot);
        points = [stops[0]];
        for (int i = 0; i + 1 < stops.Count; i++)
        {
            var p = nav.FindPath(stops[i], stops[i + 1], goalSnap: 1.5f);
            Assert.NotNull(p);
            Assert.False(p!.Partial, $"{routeName} leg {i}");
            points.AddRange(p.Waypoints.Select(w => w.Position));
        }
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Every_marker_stands_on_the_walking_graph()
    {
        var (m, nav) = World.Value;
        foreach (var p in m.Level.Spawns) Assert.True(nav.Nearest(p, 1f, 1f) >= 0, $"no graph node at {p}");
        Assert.True(nav.Nearest(m.BossSpot, 1f, 1f) >= 0 && nav.Nearest(m.LandingZone, 1f, 1f) >= 0);
    }

    /// <summary>The widest walkable row of nodes across an opening (a node sits at least 0.4 m from a wall, so an 8 m opening shows about 7.2 m of nodes).</summary>
    static float Extent(NavGraph nav, bool alongX, float at, float lo, float hi)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < nav.NodeCount; i++)
        {
            var p = nav.Position(i);
            float across = alongX ? p.Y : p.X, along = alongX ? p.X : p.Y;
            if (MathF.Abs(across - at) > 0.26f || MathF.Abs(p.Z) > 0.6f || along < lo || along > hi) continue;
            min = MathF.Min(min, along); max = MathF.Max(max, along);
        }
        return max < min ? 0f : max - min;
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Walkable_width_across_every_opening_on_the_ork_route_is_at_least_8_m_less_the_body_margin()
    {
        var nav = World.Value.Nav;
        const float needed = 7.0f;   // 8 m between walls leaves node rows 0.4 m inside each
        var rows = new (string Name, float Extent)[]
        {
            ("south gate", Extent(nav, true, -96f, -10f, 10f)),
            ("gap between block columns west", Extent(nav, false, -66f, -76f, -50f)), ("gap between block columns east", Extent(nav, false, 66f, -76f, -50f)),
            ("cross street west", Extent(nav, false, -48f, -64f, -40f)), ("cross street east", Extent(nav, false, 48f, -64f, -40f)),
            ("arena south opening", Extent(nav, true, 2f, -12f, 12f)),
            ("arena west opening", Extent(nav, false, -48f, 40f, 60f)), ("arena east opening", Extent(nav, false, 48f, 40f, 60f)),
        };
        foreach (var (name, e) in rows) output.WriteLine($"{name}: {e:0.0} m of walkable nodes");
        Assert.All(rows, r => Assert.True(r.Extent >= needed, $"{r.Name}: {r.Extent:0.0} m"));
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void The_gate_is_the_only_way_in_from_the_landing_zone()
    {
        var (m, nav) = World.Value;
        var path = nav.FindPath(m.LandingZone, m.Sources.First(s => s.Name == "street centre").Position, goalSnap: 1.5f);
        Assert.NotNull(path);
        Assert.All(path!.Waypoints.Where(w => w.Position.Y is > -99f and < -93f), w => Assert.InRange(MathF.Abs(w.Position.X), 0f, 6f));
        // no walking node inside the compound's rock or on the far side of the wall except through the gate: a node row at y -96 exists only in the gate
        for (int i = 0; i < nav.NodeCount; i++)
        {
            var p = nav.Position(i);
            if (MathF.Abs(p.Y + 96f) < 0.5f) Assert.InRange(MathF.Abs(p.X), 0f, 6f);
        }
    }

    // ---- the interface: a stub (handmade-style) arena, used only here

    sealed class StubArena : IWarfrontArena
    {
        public string Name => "stub (handmade style)";
        public uint Version => 7;
        public AshfallLevel Level { get; }
        public Vector3 LandingZone { get; } = new(-40f, 0f, 0f);
        public Vector3 BossSpot { get; } = new(40f, 0f, 0f);
        public float ArenaRadius => 20f;
        public IReadOnlyList<WarfrontArenaSource> Sources { get; } = [new("a", new(0f, 9f, 0f), 0), new("b", new(30f, -10f, 0f), 1)];
        public IReadOnlyList<WarfrontArenaPost> Posts { get; } = [new("post", new(-40f, 5f, 0f), "landing")];
        public IReadOnlyList<WarfrontArenaRoute> Routes { get; } = [new("north", [new(0f, 9f, 0f)]), new("south", [new(0f, -9f, 0f)])];
        public IReadOnlyList<WarfrontArenaDimension> RouteWidths { get; } = [new("pass", 12f, "m")];
        public IReadOnlyList<int> RoofParts { get; } = [];
        public NavArea WalkArea { get; }

        public StubArena()
        {
            var parts = WarfrontArenaGates.Boxes(
                (AshfallKind.Ground, -60f, -30f, -2f, 60f, 30f, 0f),
                (AshfallKind.Wall, -4f, -30f, 0f, 4f, -14f, 12f),   // three blocks in the middle leave two 10 m passages
                (AshfallKind.Wall, -4f, -4f, 0f, 4f, 4f, 12f), (AshfallKind.Wall, -4f, 14f, 0f, 4f, 30f, 12f),
                (AshfallKind.Boundary, -64f, -34f, -2f, 64f, -30f, 64f), (AshfallKind.Boundary, -64f, 30f, -2f, 64f, 34f, 64f),
                (AshfallKind.Boundary, -64f, -34f, -2f, -60f, 34f, 64f), (AshfallKind.Boundary, 60f, -34f, -2f, 64f, 34f, 64f));
            ControlPoint[] points = [new("Arena", BossSpot, 20f, 8f)];
            StartArea[] areas = [new(Team.SpaceMarines, LandingZone, new Vector3(10f, 10f, 8f), 0f)];
            Vector3[] spawns = [LandingZone, BossSpot, .. Sources.Select(s => s.Position), .. Posts.Select(p => p.Position)];
            WalkArea = new NavArea(new Vector2(-60f, -30f), new Vector2(60f, 30f), spawns);
            Level = new AshfallLevel(parts, points, areas, spawns, WarfrontArenaGates.MarkerHash(WarfrontArenaGates.GeometryHash(parts, Version), points, areas, Sources, Posts, Routes, spawns));
        }
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void A_stub_arena_loads_through_the_interface_and_passes_the_same_gates()
    {
        IWarfrontArena stub = new StubArena();
        Assert.Empty(WarfrontArenaGates.CheckStructure(stub));
        Assert.Equal(stub.Level.Hash, stub.Hash);
        Assert.Equal(AshfallContent.ToMapContent(stub.Level).Hash, stub.Hash);   // loads as collision content with the same hash the Challenge would compare
        Assert.Empty(WarfrontArenaGates.CheckWalk(stub, NavFor(stub)));
        // the interface carries everything the battle reads, whichever arena stands behind it
        IWarfrontArena generated = WarfrontFortressMap.Build();
        Assert.NotEqual(stub.Hash, generated.Hash);
        Assert.All(new[] { stub, generated }, a => Assert.True(a.Routes.Count >= 2 && a.Sources.Count > 0 && a.ArenaRadius > 0f));
    }

    [Fact]
    public void The_gates_reject_a_narrow_opening_a_missing_route_and_a_marker_outside_the_area()
    {
        var m = WarfrontFortressMap.Build();
        var narrow = new Override(m) { Widths = [new("slit", 5f, "m")] };
        Assert.Contains(WarfrontArenaGates.CheckStructure(narrow), l => l.Contains("slit"));
        var one = new Override(m) { RoutesOverride = [m.Routes[0]] };
        Assert.Contains(WarfrontArenaGates.CheckStructure(one), l => l.Contains("route"));
        var out_ = new Override(m) { Landing = new Vector3(500f, 0f, 0f) };
        Assert.Contains(WarfrontArenaGates.CheckStructure(out_), l => l.Contains("landing"));
    }

    sealed class Override(WarfrontFortressBuild m) : IWarfrontArena
    {
        public IReadOnlyList<WarfrontArenaDimension>? Widths { get; init; }
        public IReadOnlyList<WarfrontArenaRoute>? RoutesOverride { get; init; }
        public Vector3? Landing { get; init; }
        public string Name => m.Name;
        public uint Version => m.Version;
        public AshfallLevel Level => m.Level;
        public Vector3 LandingZone => Landing ?? m.LandingZone;
        public Vector3 BossSpot => m.BossSpot;
        public float ArenaRadius => m.ArenaRadius;
        public IReadOnlyList<WarfrontArenaSource> Sources => m.Sources;
        public IReadOnlyList<WarfrontArenaPost> Posts => m.Posts;
        public IReadOnlyList<WarfrontArenaRoute> Routes => RoutesOverride ?? m.Routes;
        public IReadOnlyList<WarfrontArenaDimension> RouteWidths => Widths ?? m.RouteWidths;
        public IReadOnlyList<int> RoofParts => m.RoofParts;
        public NavArea WalkArea => m.WalkArea;
    }
}
