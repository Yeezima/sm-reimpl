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
    static readonly AshfallBuild Gen = AshfallMap.BuildWithFeatures();
    static readonly AshfallLevel Level = Gen.Level;
    static readonly AshfallFeatures Features = Gen.Features;
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
        Assert.Equal(3u, AshfallMap.Version);
        Assert.Equal(Hash(a, AshfallMap.Version), a.Hash);
        Assert.NotEqual(Hash(a, AshfallMap.Version + 1), a.Hash);   // a new generator version moves the hash
        Assert.Equal(AshfallContent.ToMapContent(a).Hash, a.Hash);
        // Pinned (version 2): a layout change without a new AshfallMap.Version fails here, not at a client's handshake.
        Assert.Equal(PinnedLevelHash, a.Hash);
    }

    /// <summary>Ashfall Citadel version 3's level hash (pass 2 with the review's fixes, 2026-11-05; version 2 was 0x875D9199C67B1894, version 1 0x4D0658D87AF67B7B).</summary>
    const ulong PinnedLevelHash = 0xbb4d50af4614fd39UL;

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
    public void Nobody_jumps_over_the_boundary_from_the_highest_top_inside_it()
    {
        // The highest solid top inside the playable square (anything a jump pack can land on) plus a full jump's apex (17.54 m, spec) and a 4 m margin
        // stays below the boundary wall (review of 2026-11-05: the 5-storey towers' 30 m wall columns against the old 48 m wall left 0.5 m).
        var level = AshfallMap.Build();
        float top = level.Parts.Where(p => p.Solid && p.Kind is not (AshfallKind.Boundary or AshfallKind.Backdrop))
            .SelectMany(p => p.Corners).Where(c => MathF.Abs(c.X) < AshfallMap.Half && MathF.Abs(c.Y) < AshfallMap.Half).Max(c => c.Z);
        Assert.True(top + 17.54f + 4f <= AshfallMap.BoundaryHeight, $"highest top {top} m + a full jump reaches the {AshfallMap.BoundaryHeight} m boundary");
    }

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
        foreach (var (lo, hi) in new[] { (-224f, -104f), (-80f, 80f), (104f, 224f) })
            foreach (var (lo2, hi2) in new[] { (-224f, -104f), (-80f, 80f), (104f, 224f) })
            {
                var inBlock = Features.Ruins.Select(r => r.Footprint).Concat(Features.Towers.Select(t => t.Footprint)).Where(r => r.X >= lo && r.Z <= hi && r.Y >= lo2 && r.W <= hi2).ToList();
                output.WriteLine($"block x[{lo},{hi}] y[{lo2},{hi2}]: {inBlock.Count} buildings, {inBlock.Sum(r => (r.Z - r.X) * (r.W - r.Y)) / ((hi - lo) * (hi2 - lo2)) * 100f:0.0} % of the block covered");
            }
        Assert.True(total < 200_000, $"{total} triangles");
        Assert.True(perKind[AshfallKind.Ground] < 20_000);
        Assert.True(perKind[AshfallKind.Rubble] < 20_000);
        Assert.True(perKind[AshfallKind.Stair] < 40_000);
        Assert.True(perKind[AshfallKind.Wall] < 100_000);
        output.WriteLine($"pass 2: {Features.Ruins.Count} ruins, {Features.Towers.Count} tall buildings ({Features.Towers.Count(t => t.Core)} with a stair core), {Features.Platforms.Count} platforms, {Features.WallSegments} curb wall segments ({Features.WallLength:0} m), {Features.StaggerWalls} staggered avenue walls");
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


    // ---------------------------------------------------------------- pass 2: zones, lanes, rules

    static bool StructuralKind(AshfallKind k) => k is AshfallKind.Wall or AshfallKind.Cover or AshfallKind.Rubble or AshfallKind.Floor or AshfallKind.Stair or AshfallKind.Perch or AshfallKind.Stronghold;

    static (float X0, float Y0, float X1, float Y1, float Z0, float Z1) Box2(AshfallPart p) =>
        (p.Corners.Min(c => c.X), p.Corners.Min(c => c.Y), p.Corners.Max(c => c.X), p.Corners.Max(c => c.Y), p.Corners.Min(c => c.Z), p.Corners.Max(c => c.Z));

    [Fact]
    public void No_solid_stands_inside_a_capture_zone_cylinder_but_As_plinth_with_its_four_stairs()
    {
        foreach (var point in Level.Points)
            foreach (var part in Level.Parts.Where(p => StructuralKind(p.Kind)))
            {
                var (x0, y0, x1, y1, _, z1) = Box2(part);
                float dx = MathF.Max(MathF.Max(x0 - point.Center.X, point.Center.X - x1), 0f), dy = MathF.Max(MathF.Max(y0 - point.Center.Y, point.Center.Y - y1), 0f);
                if (dx * dx + dy * dy >= point.Radius * point.Radius || z1 <= 0.9f) continue;   // outside the cylinder, or low enough to walk over (rubble 0.35 m on the 0.25 m block)
                bool plinth = point.Name == "A" && x0 >= -179.01f && x1 <= -148.99f && y0 >= 148.99f && y1 <= 179.01f;
                Assert.True(plinth, $"{part.Kind} at ({x0:0.0}, {y0:0.0})..({x1:0.0}, {y1:0.0}) stands in capture zone {point.Name}");
            }
        // B is clear at its centre.
        Assert.False(Inside(Level.Points[1].Center + new Vector3(0f, 0f, 1f)));
    }

    [Fact]
    public void Ruins_towers_and_platforms_keep_out_of_the_zones_ring_the_start_areas_and_each_other()
    {
        var shapes = new List<(string Name, Vector4 R)>();
        shapes.AddRange(Features.Towers.Select(t => (t.Name, t.Footprint)));
        shapes.AddRange(Features.Ruins.Select(r => (r.Kind, r.Footprint)));
        foreach (var (name, r) in shapes)
        {
            foreach (var pt in Level.Points)
            {
                float dx = MathF.Max(MathF.Max(r.X - pt.Center.X, pt.Center.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - pt.Center.Y, pt.Center.Y - r.W), 0f);
                Assert.True(MathF.Sqrt(dx * dx + dy * dy) >= pt.Radius + 6f - 1e-3f, $"{name} {r} is within 6 m of zone {pt.Name}");
            }
            foreach (var a in Level.Areas)
            {
                float hx = a.Size.X / 2f, hy = a.Size.Y / 2f;
                Assert.False(r.X < a.Center.X + hx && r.Z > a.Center.X - hx && r.Y < a.Center.Y + hy && r.W > a.Center.Y - hy, $"{name} {r} is in the {a.Side} start area");
            }
            Assert.True(MathF.Sqrt(MathF.Pow(MathF.Max(MathF.Max(r.X, -r.Z), 0f), 2) + MathF.Pow(MathF.Max(MathF.Max(r.Y, -r.W), 0f), 2)) >= 22f - 1e-3f, $"{name} {r} is on the plaza's open centre");
            Assert.True(MathF.Max(MathF.Max(-r.X, -r.Y), MathF.Max(r.Z, r.W)) <= 224f, $"{name} {r} is on the perimeter road");
        }
        for (int i = 0; i < shapes.Count; i++)
            for (int j = i + 1; j < shapes.Count; j++)
            {
                var a = shapes[i].R; var b = shapes[j].R;
                float dx = MathF.Max(MathF.Max(a.X - b.Z, b.X - a.Z), 0f), dy = MathF.Max(MathF.Max(a.Y - b.W, b.Y - a.W), 0f);
                Assert.True(MathF.Sqrt(dx * dx + dy * dy) >= 8f - 1e-3f, $"lane under 8 m between {shapes[i].Name} {a} and {shapes[j].Name} {b}");
            }
    }

    [Fact]
    public void Each_avenues_and_the_perimeter_roads_8_m_corridor_stays_free_of_solids()
    {
        foreach (var part in Level.Parts.Where(p => StructuralKind(p.Kind)))
        {
            var (x0, y0, x1, y1, _, _) = Box2(part);
            foreach (float c in new[] { -92f, 92f, -232f, 232f })
            {
                // Corridor along y (x within 4 m of c) and along x (y within 4 m of c); the monument and the arch (|y| < 6 on the x = +-92 avenues) are the two hand-built exceptions of version 1.
                bool alongY = x0 < c + 4f - 1e-3f && x1 > c - 4f + 1e-3f && !(MathF.Abs(c) == 92f && y0 > -6f && y1 < 6f);
                bool alongX = y0 < c + 4f - 1e-3f && y1 > c - 4f + 1e-3f;
                Assert.False(alongY, $"{part.Kind} ({x0:0.0}, {y0:0.0})..({x1:0.0}, {y1:0.0}) closes the 8 m lane along x = {c}");
                Assert.False(alongX, $"{part.Kind} ({x0:0.0}, {y0:0.0})..({x1:0.0}, {y1:0.0}) closes the 8 m lane along y = {c}");
            }
        }
    }

    [Fact]
    public void Walls_and_platforms_follow_the_kit_rules_and_the_features_match_the_brief()
    {
        // No wall or cover top in the 0.4..0.9 m band above the ground.
        foreach (var p in Level.Parts.Where(p => p.Kind is AshfallKind.Wall or AshfallKind.Cover && p.Corners.Length == 36))
        {
            var (_, _, _, _, z0, z1) = Box2(p);
            if (z0 <= 0.001f) Assert.False(z1 > 0.4f + 1e-3f && z1 < 0.9f - 1e-3f, $"{p.Kind} top {z1}");
        }
        Assert.True(Features.Platforms.Count is >= 15 and <= 26, $"{Features.Platforms.Count} platforms");
        Assert.All(Features.Platforms, pl => Assert.True(pl.Height is >= 1.5f and <= 5.5f && pl.Height * 2f == MathF.Round(pl.Height * 2f), $"platform height {pl.Height}"));
        Assert.True(Features.Towers.Count is >= 6 and <= 12);
        Assert.All(Features.Towers, t => Assert.InRange(t.Storeys, 3, 5));
        Assert.True(Features.Towers.Count(t => t.Core) * 2 >= Features.Towers.Count);
        Assert.True(Features.Ruins.Count >= 80, $"{Features.Ruins.Count} ruins");
        // A tall building's footprint is 12 x 12 .. 16 x 24 and stands 10 m clear of the boundary (a jump from a 30 m top peaks 17.5 m higher; the boundary is 48 m).
        Assert.All(Features.Towers, t =>
        {
            var w = t.Footprint.Z - t.Footprint.X; var d = t.Footprint.W - t.Footprint.Y;
            Assert.True(Math.Min(w, d) >= 12f && Math.Max(w, d) <= 24f);
            Assert.True(t.Footprint.Z <= 230f && t.Footprint.X >= -230f && t.Footprint.W <= 230f && t.Footprint.Y >= -230f);
        });
        // The tall buildings really are tall: the highest wall of each reaches 6 m x its storeys.
        foreach (var t in Features.Towers)
        {
            float top = Level.Parts.Where(p => p.Kind == AshfallKind.Wall).Select(Box2)
                .Where(b => b.X0 >= t.Footprint.X - 0.01f && b.X1 <= t.Footprint.Z + 0.01f && b.Y0 >= t.Footprint.Y - 0.01f && b.Y1 <= t.Footprint.W + 0.01f).Max(b => b.Z1);
            Assert.Equal(6f * t.Storeys, top);
        }
    }

    [Fact]
    public void As_plinth_has_a_stair_on_each_of_its_four_sides_and_the_obelisk_stays()
    {
        var stairs = Level.Parts.Where(p => p.Kind == AshfallKind.Stair).Select(Box2).Where(b => b.X0 > -180f && b.X1 < -148f && b.Y0 > 148f && b.Y1 < 180f).ToList();
        Assert.Contains(stairs, b => b.Y1 <= 156.01f && b.Y0 < 150f && b.X0 < -167f && b.X1 > -161f);   // south
        Assert.Contains(stairs, b => b.Y0 >= 171.99f && b.Y1 > 177f);                                     // north
        Assert.Contains(stairs, b => b.X1 <= -171.99f && b.X0 < -177f);                                   // west
        Assert.Contains(stairs, b => b.X0 >= -156.01f && b.X1 > -151f);                                   // east
        Assert.All(stairs, b => Assert.True(b.Z1 <= 3.001f));
        Assert.Contains(Level.Parts, p => p.Kind == AshfallKind.Perch && Box2(p) is var b && b.Z1 == 15f && b.X0 > -170f && b.X1 < -160f);   // the obelisk, jump-only
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
        // Pass 2: A's plinth top is reached on foot from the foot of each of its four stairs (to the top's edge beside that stair).
        (string Name, Vector3 Foot, Vector3 Top)[] aSides =
        [
            ("south", V(-164f, 148f, AshfallMap.BlockTop), V(-164f, 157f, 3f)), ("north", V(-164f, 180f, AshfallMap.BlockTop), V(-164f, 171f, 3f)),
            ("west", V(-180f, 164f, AshfallMap.BlockTop), V(-171f, 164f, 3f)), ("east", V(-148f, 164f, AshfallMap.BlockTop), V(-157f, 164f, 3f)),
        ];
        var aMissing = aSides.Where(r => !Walks(graph, r.Foot, r.Top)).Select(r => r.Name).ToList();
        Assert.True(aMissing.Count == 0, "A's plinth top not reached from the stair foot on the side: " + string.Join(", ", aMissing));
        // Pass 2: every platform top, every ruin floor with a stair and every floor of a tall building with a stair core is walked to.
        var platformsMissing = Features.Platforms.Where(pl => !Walks(graph, start, pl.Top)).Select(pl => pl.Top.ToString()).ToList();
        Assert.True(platformsMissing.Count == 0, $"platform tops not reached on foot ({platformsMissing.Count} of {Features.Platforms.Count}): " + string.Join(", ", platformsMissing));
        var ruinFloors = Features.Ruins.Where(r => r.FloorPoint is not null).ToList();
        var ruinsMissing = ruinFloors.Where(r => !Walks(graph, start, r.FloorPoint!.Value)).Select(r => r.FloorPoint.ToString()).ToList();
        Assert.True(ruinsMissing.Count == 0, $"ruin floors not reached on foot ({ruinsMissing.Count} of {ruinFloors.Count}): " + string.Join(", ", ruinsMissing));
        var cored = Features.Towers.Where(t => t.Core).ToList();
        var towerMissing = cored.SelectMany(t => t.Floors.Select(f => (t.Name, f))).Where(x => !Walks(graph, start, x.f)).Select(x => $"{x.Name} {x.f}").ToList();
        Assert.True(towerMissing.Count == 0, "tall-building floors not reached on foot: " + string.Join(", ", towerMissing));
        output.WriteLine($"walked to {Features.Platforms.Count} platform tops, {ruinFloors.Count} ruin floors, {cored.Sum(t => t.Floors.Count)} tall-building floors in {cored.Count} buildings, 4 stair feet of A");
        (string Name, Vector3 At)[] perches =
        [
            ("A obelisk", V(-164f, 168f, 15f)), ("small obelisk", V(-92f, 0f, 12f)), ("arch top", V(92f, 0f, 14f)), ("keep top", V(0f, 190f, 18f)),
            ("C roof", V(157f, 180f, 12f)), ("E ruin roof", V(170f, 16f, 12f)),
        ];
        // Positive control: each perch point stands on a solid top at its height (a ray down from 2 m above meets it within 2.1 m), so "not reached" is a real finding.
        // The graph keeps only floors reached from its seeds, so it cannot serve as the control itself.
        var level = AshfallContent.ToMapContent(Level).ToCollisionWorld().Character;
        var floating = perches.Where(r => level.Raycast(r.At + new Vector3(0f, 0f, 2f), -Vector3.UnitZ, 2.1f) is null).Select(r => r.Name).ToList();
        Assert.True(floating.Count == 0, "perch point not on a solid top (wrong place or height, so its check proves nothing): " + string.Join(", ", floating));
        var reached = perches.Where(r => Walks(graph, start, r.At)).Select(r => r.Name).ToList();
        Assert.True(reached.Count == 0, "reached on foot (should be jump-only): " + string.Join(", ", reached));
        // Pass 2: the tall shells without a core (their floors are plates only a jump reaches) and the ruins' jump-only floors, with the same downward-ray positive control.
        var shellFloors = Features.Towers.Where(t => !t.Core).SelectMany(t => t.Floors.Select(f => (Name: $"{t.Name} {f}", At: f))).ToList();
        var shellFloating = shellFloors.Where(r => level.Raycast(r.At + new Vector3(0f, 0f, 2f), -Vector3.UnitZ, 2.1f) is null).Select(r => r.Name).ToList();
        Assert.True(shellFloating.Count == 0, "jump-only floor point not on a solid top: " + string.Join(", ", shellFloating));
        var shellReached = shellFloors.Where(r => Walks(graph, start, r.At)).Select(r => r.Name).ToList();
        Assert.True(shellReached.Count == 0, "tall shell floors reached on foot (should be jump-only): " + string.Join(", ", shellReached));
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
