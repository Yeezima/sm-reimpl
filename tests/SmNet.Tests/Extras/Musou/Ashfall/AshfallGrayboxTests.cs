using System.Diagnostics;
using System.Numerics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall Citadel's graybox (G1, docs/musou-ashfall-graybox.md), checked from the generator alone: determinism, places on walkable ground outside the solids, triangle budgets, the rubble rules, the
/// enemies' graph (what is walked and what only a jump reaches) and the isolation of the classic modes. No original bytes, no install, no bots.
/// </summary>
public class AshfallGrayboxTests(ITestOutputHelper output)
{
    static readonly AshfallLevel Level = AshfallMap.Build();
    static readonly StaticMesh Mesh = new(Level.SolidCorners(), Enumerable.Range(0, Level.SolidCorners().Length).ToArray());

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void Two_builds_are_bit_for_bit_equal_and_the_hash_follows_the_version()
    {
        var a = AshfallMap.Build();
        var b = AshfallMap.Build();
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Parts.Count, b.Parts.Count);
        for (int i = 0; i < a.Parts.Count; i++)
        {
            Assert.Equal(a.Parts[i].Kind, b.Parts[i].Kind);
            Assert.Equal(a.Parts[i].Corners, b.Parts[i].Corners);
        }
        Assert.Equal(a.Points, b.Points);
        Assert.Equal(a.Spawns, b.Spawns);
        Assert.Equal(Hash(a, AshfallMap.Version), a.Hash);
        Assert.NotEqual(Hash(a, AshfallMap.Version + 1), a.Hash);   // a new generator version moves the hash
        Assert.Equal(AshfallContent.ToMapContent(a).Hash, a.Hash);
    }

    /// <summary>The hash's rule, written out independently of the generator: FNV-1a 64 over the version, then every solid corner's float bits.</summary>
    static ulong Hash(AshfallLevel level, uint version)
    {
        ulong h = 14695981039346656037UL;
        void Mix(uint v) { for (int i = 0; i < 4; i++) { h ^= (v >> (8 * i)) & 0xFF; h *= 1099511628211UL; } }
        Mix(version);
        foreach (var v in level.SolidCorners()) { Mix(BitConverter.SingleToUInt32Bits(v.X)); Mix(BitConverter.SingleToUInt32Bits(v.Y)); Mix(BitConverter.SingleToUInt32Bits(v.Z)); }
        return h;
    }

    // ---------------------------------------------------------------- shape

    [Fact]
    public void Every_box_winds_outward_and_every_triangle_is_finite_and_in_the_world()
    {
        foreach (var part in Level.Parts)
        {
            Assert.True(part.Corners.Length % 3 == 0, part.Kind.ToString());
            Assert.All(part.Corners, c => Assert.True(float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.Z) && MathF.Abs(c.X) < 400f && MathF.Abs(c.Y) < 400f && c.Z < 100f));
            if (part.Corners.Length != 36) continue;
            var centre = part.Corners.Aggregate(Vector3.Zero, (s, c) => s + c) / 36f;
            for (int i = 0; i < 36; i += 3)
            {
                var n = Vector3.Cross(part.Corners[i + 1] - part.Corners[i], part.Corners[i + 2] - part.Corners[i]);
                Assert.True(Vector3.Dot(n, part.Corners[i] - centre) > 0f, $"{part.Kind} triangle {i / 3} winds inward");
            }
        }
        // The heightfield faces up wherever it is level, the objective discs face up.
        Assert.All(Level.Parts.Where(p => p.Kind == AshfallKind.Objective), p =>
        {
            for (int i = 0; i < p.Corners.Length; i += 3) Assert.True(Vector3.Cross(p.Corners[i + 1] - p.Corners[i], p.Corners[i + 2] - p.Corners[i]).Z > 0f);
        });
        Assert.All(Level.Parts.Where(p => p.Kind == AshfallKind.Objective), p => Assert.False(p.Solid));
    }

    [Fact]
    public void The_triangle_budgets_hold_and_the_counts_are_reported()
    {
        var perKind = Level.Parts.Where(p => p.Solid).GroupBy(p => p.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Corners.Length / 3));
        foreach (var (kind, n) in perKind) output.WriteLine($"{kind,-10} {n,7} triangles");
        int total = Level.SolidCorners().Length / 3;
        output.WriteLine($"total {total} collision triangles; {Level.Parts.Count} parts");
        Assert.True(total < 200_000, $"{total} triangles");
        Assert.True(perKind[AshfallKind.Ground] < 20_000);
        Assert.True(perKind[AshfallKind.Rubble] < 10_000);
        Assert.True(perKind[AshfallKind.Stair] < 10_000);
        Assert.Equal(total, Mesh.TriangleCount);   // no degenerate triangle
    }

    // ---------------------------------------------------------------- places

    static bool Inside(Vector3 p)
    {
        foreach (var part in Level.Parts)
        {
            if (!part.Solid || part.Corners.Length != 36) continue;
            bool inside = true;
            for (int i = 0; i < 36 && inside; i += 3)
            {
                var n = Vector3.Normalize(Vector3.Cross(part.Corners[i + 1] - part.Corners[i], part.Corners[i + 2] - part.Corners[i]));
                if (Vector3.Dot(n, p - part.Corners[i]) > 1e-4f) inside = false;
            }
            if (inside) return true;
        }
        return false;
    }

    static void StandsOnGround(string what, Vector3 p)
    {
        Assert.False(Inside(p + new Vector3(0f, 0f, 0.1f)), $"{what} {p} is inside a solid");
        Assert.False(Inside(p + new Vector3(0f, 0f, 1.0f)), $"{what} {p} has a solid at 1 m");
        var hit = Mesh.Raycast(p + new Vector3(0f, 0f, 1.5f), -Vector3.UnitZ, 3f);
        Assert.True(hit is { } h && MathF.Abs(h.Point.Z - p.Z) <= 0.1f && h.Normal.Z > 0.7f, $"{what} {p} has no walkable ground under it ({hit?.Point})");
    }

    [Fact]
    public void Spawns_start_areas_and_capture_centres_stand_on_walkable_ground_outside_every_solid()
    {
        foreach (var s in Level.Spawns) StandsOnGround("spawn", s);
        foreach (var a in Level.Areas) StandsOnGround("start area", a.Center);
        foreach (var p in Level.Points) StandsOnGround("capture point " + p.Name, p.Center);
        Assert.Equal(["A", "B", "C"], Level.Points.Select(p => p.Name));
        Assert.Equal([14f, 16f, 14f], Level.Points.Select(p => p.Radius));
        Assert.Equal(Team.SpaceMarines, Level.Areas[0].Side);
        Assert.Equal(Team.Chaos, Level.Areas[1].Side);
        Assert.True(Level.Spawns.Count >= 14);
    }

    [Fact]
    public void Start_areas_and_capture_zones_have_open_ground_within_the_start_rectangles()
    {
        // The Marine compound's start rectangle and the courtyard's hold no solid above the floor (a 2 m probe on a 4 m grid).
        foreach (var a in Level.Areas)
            for (float x = a.Center.X - a.Size.X / 2 + 2; x < a.Center.X + a.Size.X / 2; x += 4)
                for (float y = a.Center.Y - a.Size.Y / 2 + 2; y < a.Center.Y + a.Size.Y / 2; y += 4)
                    Assert.False(Inside(new Vector3(x, y, a.Center.Z + 1f)), $"{a.Side} area blocked at {x}, {y}");
    }

    // ---------------------------------------------------------------- rubble and cover rules

    static List<Vector2> Footprint(AshfallPart p)
    {
        float low = p.Corners.Min(c => c.Z);
        return p.Corners.Where(c => c.Z - low < 1e-3f).Select(c => new Vector2(c.X, c.Y)).Distinct().ToList();
    }

    [Fact]
    public void Rubble_and_cover_never_stand_in_the_dead_band_and_gaps_are_zero_or_wide()
    {
        var pieces = Level.Parts.Where(p => p.Kind is AshfallKind.Rubble or AshfallKind.Cover).ToList();
        Assert.True(pieces.Count > 100);
        foreach (var p in pieces)
        {
            float h = p.Corners.Max(c => c.Z) - p.Corners.Min(c => c.Z);
            Assert.False(h > 0.4f + 1e-3f && h < 0.9f - 1e-3f, $"{p.Kind} {h} m high");
        }
        var rubble = Level.Parts.Where(p => p.Kind == AshfallKind.Rubble).ToList();
        Assert.All(rubble, p =>
        {
            float h = p.Corners.Max(c => c.Z) - p.Corners.Min(c => c.Z);
            Assert.True(h <= 0.35f + 1e-3f || (h >= 0.9f && h <= 2.5f + 1e-3f), $"rubble {h} m");
        });
        var shapes = pieces.Select(Footprint).ToList();
        int checkedPairs = 0;
        for (int i = 0; i < shapes.Count; i++)
            for (int j = i + 1; j < shapes.Count; j++)
            {
                float d = PolygonGap(shapes[i], shapes[j]);
                if (d > 1e-3f) { Assert.True(d >= 1.2f - 1e-3f, $"gap {d:0.00} m between {pieces[i].Kind} at {shapes[i][0]} and {pieces[j].Kind} at {shapes[j][0]}"); checkedPairs++; }
            }
        output.WriteLine($"{pieces.Count} pieces, {checkedPairs} separated pairs checked");
    }

    /// <summary>The distance between two convex footprints (0 when they touch or overlap).</summary>
    static float PolygonGap(List<Vector2> a, List<Vector2> b)
    {
        var ha = Order(a); var hb = Order(b);
        if (Contains(ha, hb[0]) || Contains(hb, ha[0])) return 0f;
        float best = float.MaxValue;
        for (int i = 0; i < ha.Count; i++)
            for (int j = 0; j < hb.Count; j++)
            {
                Vector2 p = ha[i], q = ha[(i + 1) % ha.Count], r = hb[j], s = hb[(j + 1) % hb.Count];
                if (Cross(p, q, r, s)) return 0f;
                best = MathF.Min(best, MathF.Min(MathF.Min(Dist(p, r, s), Dist(q, r, s)), MathF.Min(Dist(r, p, q), Dist(s, p, q))));
            }
        return best;
    }

    static List<Vector2> Order(List<Vector2> p)
    {
        var c = new Vector2(p.Average(v => v.X), p.Average(v => v.Y));
        return p.OrderBy(v => MathF.Atan2(v.Y - c.Y, v.X - c.X)).ToList();
    }

    static bool Contains(List<Vector2> poly, Vector2 p)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Count];
            if ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X) < 0f) return false;
        }
        return true;
    }

    static bool Cross(Vector2 p, Vector2 q, Vector2 r, Vector2 s)
    {
        static float O(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return O(p, q, r) * O(p, q, s) < 0f && O(r, s, p) * O(r, s, q) < 0f;
    }

    static float Dist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    // ---------------------------------------------------------------- the enemies' graph

    sealed record Built(NavGraph Graph, TimeSpan Time, long Bytes, long CacheBytes, TimeSpan LoadTime, TfwMapData Data);

    /// <summary>The graph as the server builds it: the war's world on Ashfall, its walking area (the level's markers and the range), through the cache into a folder of its own.</summary>
    static readonly Lazy<Built> Nav = new(() =>
    {
        var data = TfwWorld.Load("unused", out _)!;
        string dir = Path.Combine(Path.GetTempPath(), "ashfall-nav-" + Guid.NewGuid().ToString("N"));
        try
        {
            long before = GC.GetTotalMemory(true);
            var watch = Stopwatch.StartNew();
            var graph = TfwWorld.Nav(data.Collision, data.Field, new SimConfig(), out _, dir);
            watch.Stop();
            var built = watch.Elapsed;
            long after = GC.GetTotalMemory(true);
            long cache = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);
            watch.Restart();
            TfwWorld.Nav(data.Collision, data.Field, new SimConfig(), out string note, dir);
            watch.Stop();
            Assert.Contains("cache", note, StringComparison.OrdinalIgnoreCase);
            return new Built(graph, built, after - before, cache, watch.Elapsed, data);
        }
        finally { Directory.Delete(dir, recursive: true); }
    });

    static bool Walks(NavGraph graph, Vector3 from, Vector3 to)
    {
        if (graph.Nearest(to, 1.5f, 1.0f) < 0) return false;
        var path = graph.FindPath(from, to, goalSnap: 1.5f, maxExpanded: 8_000_000);
        return path is { Partial: false } p && p.Waypoints.All(w => w.Kind is NavLinkKind.Walk or NavLinkKind.Drop);
    }

    [Fact]
    [Trait("Speed", "Slow")]   // builds the war's whole walking graph (about a minute: see the Results of docs/musou-ashfall-graybox.md)
    public void The_enemies_graph_connects_every_place_and_floor_a_player_walks_to_and_no_perch()
    {
        var built = Nav.Value;
        output.WriteLine($"nav build {built.Time.TotalSeconds:0.0} s, {built.Graph.NodeCount} nodes, about {built.Bytes / 1048576.0:0.0} MiB retained, cache file {built.CacheBytes / 1048576.0:0.0} MiB, loaded from the cache in {built.LoadTime.TotalSeconds:0.0} s");
        var graph = built.Graph;
        var start = Level.Areas[0].Center;
        Vector3 V(float x, float y, float z) => new(x, y, z);
        var barrel = V(-162f + 20f * 0.9396926f * 0.7071068f, -162f + 20f * 0.9396926f * 0.7071068f, 3f + 20f * 0.3420201f + 0.05f);
        (string Name, Vector3 At)[] reachable =
        [
            ("A", Level.Points[0].Center), ("A plinth top", V(-170f, 160f, 3f)), ("B", Level.Points[1].Center), ("C", Level.Points[2].Center),
            ("courtyard", Level.Areas[1].Center), ("plaza NW", V(-56f, 56f, 0.27f)), ("plaza NE", V(56f, 56f, 0.27f)), ("plaza SW", V(-56f, -56f, 0.27f)), ("plaza SE", V(56f, -56f, 0.27f)),
            ("C first floor", V(160f, 184f, 6f)), ("C first floor by the stair", V(172f, 176f, 6f)), ("E ruin first floor", V(170f, 10f, 6f)), ("small ruin floor", V(136f, -48f, 6f)),
            ("bunker roof", V(170f, -164f, 6f)), ("artillery platform", V(-164f, -164f, 3f)), ("barrel top", barrel),
            ("west L wall top", V(-70f, 150f, 12f)), ("east L wall top", V(70f, 150f, 12f)), ("west L south leg", V(-40f, 114f, 12f)),
            ("small monument plinth", V(-92f, 3f, 2f)),
        ];
        var missing = reachable.Where(r => !Walks(graph, start, r.At)).Select(r => r.Name).ToList();
        Assert.True(missing.Count == 0, "not reached on foot: " + string.Join(", ", missing));
        (string Name, Vector3 At)[] perches =
        [
            ("A obelisk", V(-164f, 168f, 15f)), ("small obelisk", V(-92f, 0f, 12f)), ("arch top", V(92f, 0f, 14f)), ("keep top", V(0f, 190f, 18f)),
            ("C roof", V(157f, 180f, 12f)), ("E ruin roof", V(170f, 16f, 12f)),
        ];
        var reached = perches.Where(r => Walks(graph, start, r.At)).Select(r => r.Name).ToList();
        Assert.True(reached.Count == 0, "reached on foot (should be jump-only): " + string.Join(", ", reached));
    }

    // ---------------------------------------------------------------- the war's composition

    [Fact]
    public void The_war_composes_on_ashfall_with_the_ranges_own_walls_and_a_hash_of_its_own()
    {
        var world = AshfallContent.ToMapContent(Level).ToCollisionWorld();
        var ashfall = TfwMap.WithHub(world, manufactorum: false);
        var old = TfwMap.WithHub(world, manufactorum: true);
        Assert.NotEqual(ashfall.Hash, old.Hash);
        Assert.True(ashfall.World.Character.TriangleCount < old.World.Character.TriangleCount);   // no Manufactorum backdrop in the range
        Assert.Equal(ashfall.World.ContentHash ^ 0x7466775F7761725FUL ^ 8UL, ashfall.Hash);
        var data = TfwMap.Compose(world, Level.Points, Level.Areas, Level.Spawns, manufactorum: false);
        Assert.Equal(ashfall.Hash, data.Hash);
        Assert.Equal(3, data.Field.Points.Count);
        Assert.Equal(Level.Areas[1].Center, data.Field.Commander);
        Assert.Equal(Level.Areas[0].Center, data.Field.MarineStart);
        // The hub and the range stand clear of the level: south and east of everything solid.
        Assert.True(data.Field.Hub.Box.Max.Y < world.Character.Min.Y);
        Assert.True(data.Field.Range!.Box.Min.X > world.Character.Max.X);
        Assert.True(TfwMap.IsWarMap(TfwMap.Name) && TfwMap.IsWarMap(TfwMap.ManufactorumName));
        Assert.False(TfwMap.IsWarMap("mp_tdm_09") || TfwMap.IsWarMap(null) || TfwMap.IsWarMap(""));
        Assert.Equal(TfwMap.Name, "tfw_war");
        Assert.Equal("tfw_war_m09", TfwMap.ManufactorumName);
    }

    [Fact]
    public void The_world_loads_without_an_install_and_the_wire_box_is_precise_on_the_larger_map()
    {
        var data = Assert.IsType<TfwMapData>(TfwWorld.Load(Path.Combine(Path.GetTempPath(), "no-such-install"), out string? problem));
        Assert.Null(problem);
        var again = TfwWorld.Load(Path.Combine(Path.GetTempPath(), "no-such-install"), out _)!;
        Assert.Equal(data.Hash, again.Hash);
        // The wire quantises each axis to 16 bits over the director's box (the graph's box widened by the Stormbird and the range): the step is box span / 65535.
        var field = data.Field;
        var seeds = field.WalkSeeds;
        var lo = seeds.Aggregate(seeds[0], Vector3.Min) - new Vector3(TfwMap.WalkMargin, TfwMap.WalkMargin, 0f);
        var hi = seeds.Aggregate(seeds[0], Vector3.Max) + new Vector3(TfwMap.WalkMargin, TfwMap.WalkMargin, 0f);
        var box = new MusouBox(Vector3.Min(lo, Vector3.Min(field.Hub.Box.Min, field.Range!.Box.Min)), Vector3.Max(hi, Vector3.Max(field.Hub.Box.Max, field.Range.Box.Max)));
        var span = box.Max - box.Min;
        var step = span / 65535f;
        output.WriteLine($"wire box span {span}, step {step.X * 100f:0.00} / {step.Y * 100f:0.00} / {step.Z * 100f:0.00} cm (worst error half a step)");
        Assert.True(MathF.Max(step.X, MathF.Max(step.Y, step.Z)) / 2f < 0.05f, "a quantised position is off by more than 5 cm");
    }

    // ---------------------------------------------------------------- isolation

    [Fact]
    public void The_classic_modes_config_hashes_are_unchanged_by_the_new_battlefield()
    {
        // Pinned from the tree before G1 (no core file changed): Annihilation and Seize Ground, faithful and extended, hash as they did.
        Assert.Equal(FaithfulHash, new SimConfig().Hash());
        Assert.Equal(ExtendedHash, new SimConfig { Extras = ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks }.Hash());
        Assert.NotEqual(FaithfulHash, new SimConfig { Extras = ContentExtras.Musou }.Hash());   // Musou Mode is a profile of its own
    }

    const ulong FaithfulHash = 11924134867276107435UL, ExtendedHash = 910451572435291136UL;
}
