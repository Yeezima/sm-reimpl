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
/// Ashfall Citadel's v5 graybox (G1, docs/musou-ashfall-graybox.md; since draft 5 <see cref="AshfallMap.BuildGraybox"/>, the reference the draft-5 level starts from), checked from the generator alone: determinism, places on walkable ground outside the solids, triangle budgets, the rubble rules, the
/// enemies' graph (what is walked and what only a jump reaches) and the isolation of the classic modes. No original bytes, no install, no bots.
/// </summary>
public class AshfallGrayboxTests(ITestOutputHelper output)
{
    static readonly AshfallBuild Gen = AshfallMap.BuildGrayboxWithFeatures();
    static readonly AshfallLevel Level = Gen.Level;
    static readonly AshfallFeatures Features = Gen.Features;
    static readonly StaticMesh Mesh = new(Level.SolidCorners(), Enumerable.Range(0, Level.SolidCorners().Length).ToArray());

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void Two_builds_are_bit_for_bit_equal_and_the_hash_follows_the_version()
    {
        var a = AshfallMap.BuildGraybox();
        var b = AshfallMap.BuildGraybox();
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Parts.Count, b.Parts.Count);
        for (int i = 0; i < a.Parts.Count; i++)
        {
            Assert.Equal(a.Parts[i].Kind, b.Parts[i].Kind);
            Assert.Equal(a.Parts[i].Corners, b.Parts[i].Corners);
        }
        Assert.Equal(a.Points, b.Points);
        Assert.Equal(a.Spawns, b.Spawns);
        Assert.Equal(5u, AshfallMap.GrayboxVersion);
        Assert.Equal(Hash(a, AshfallMap.GrayboxVersion), a.Hash);
        Assert.NotEqual(Hash(a, AshfallMap.GrayboxVersion + 1), a.Hash);   // a new generator version moves the hash
        Assert.Equal(AshfallContent.ToMapContent(a).Hash, a.Hash);
        // Pinned (version 5): a layout change without a new AshfallMap.GrayboxVersion fails here, not at a client's handshake.
        Assert.Equal(PinnedLevelHash, a.Hash);
    }

    /// <summary>Ashfall Citadel version 5 level hash (pass 3, 2026-11-05: the stronghold rework of sketch 6; version 4 was 0xA07D05D38EB46CF2, version 3 was 0xBB4D50AF4614FD39, version 2 0x875D9199C67B1894, version 1 0x4D0658D87AF67B7B).</summary>
    const ulong PinnedLevelHash = 0xae096e323bb5a4d3UL;

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
        var level = AshfallMap.BuildGraybox();
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
            var (x0, y0, x1, y1, z0, _) = Box2(part);
            if (z0 >= 11.5f - 1e-3f && x0 >= -109f - 1e-3f && x1 <= -83f + 1e-3f && y0 >= 147f - 1e-3f && y1 <= 153f + 1e-3f) continue;   // pass 2b: only the twin towers' bridge (deck and parapets from 11.5 m) spans the west avenue's lane overhead
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
        Assert.True(Features.Towers.Count is >= 6 and <= 20);   // pass 3 added the two stair towers and the keep tower (17 in all)
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
                .Where(b => b.X0 >= t.Footprint.X - 0.01f && b.X1 <= t.Footprint.Z + 0.01f && b.Y0 >= t.Footprint.Y - 0.01f && b.Y1 <= t.Footprint.W + 0.01f && b.Z1 <= 6f * t.Storeys + 0.01f).Max(b => b.Z1);   // the pointed roofs (pass 2b) stand above
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


    // ---------------------------------------------------------------- pass 2b

    [Fact]
    public void The_pointed_roofs_are_closed_outward_wound_steep_solids_below_the_boundary()
    {
        var roofs = Level.Parts.Where(p => p.Kind == AshfallKind.Wall && p.Corners.Length != 36).ToList();
        Assert.Equal(2, roofs.Count);   // a gable and a spire on the east tower
        float highest = 0f;
        foreach (var roof in roofs)
        {
            var centre = roof.Corners.Distinct().Aggregate(Vector3.Zero, (s, c) => s + c) / roof.Corners.Distinct().Count();
            for (int i = 0; i < roof.Corners.Length; i += 3)
            {
                var n = Vector3.Cross(roof.Corners[i + 1] - roof.Corners[i], roof.Corners[i + 2] - roof.Corners[i]);
                var mid = (roof.Corners[i] + roof.Corners[i + 1] + roof.Corners[i + 2]) / 3f;
                Assert.True(Vector3.Dot(n, mid - centre) > 0f, $"roof triangle {i / 3} winds inward");
                // Every sloped face is steeper than 60 degrees from the horizontal (a face whose normal's z is above sin 30 = 0.5 could be stood on).
                var nn = Vector3.Normalize(n);
                if (MathF.Abs(nn.Z) > 1e-3f && nn.Z > 0f) Assert.True(nn.Z < 0.5f, $"roof face {i / 3} is {MathF.Acos(nn.Z) * 180f / MathF.PI:0} degrees from flat");
            }
            highest = MathF.Max(highest, roof.Corners.Max(c => c.Z));
        }
        Assert.True(highest <= 40f, $"roof top {highest} m");
        Assert.Equal(2, roofs.Select(r => r.Corners.Max(c => c.Z)).Distinct().Count());   // different heights
        Assert.True(highest + 17.54f + 4f <= AshfallMap.BoundaryHeight);
    }

    [Fact]
    public void The_keep_has_a_walkable_roof_a_stair_up_its_east_face_and_the_twin_towers_a_bridge()
    {
        // Keep: the roof is a Floor slab at 18 m; three flights of 4 m wide steps (0.25 m risers) rise along x 32..36 to 6, 12 and 18 m.
        Assert.Contains(Level.Parts, p => p.Kind == AshfallKind.Floor && Box2(p) is var b && b.Z1 == 18f && b.X0 == -32f && b.X1 == 32f && b.Y0 == 164f && b.Y1 == 212f);
        Assert.DoesNotContain(Level.Parts, p => p.Kind == AshfallKind.Perch && Box2(p) is var b && b.X0 > -32f && b.X1 < 32f && b.Y0 > 164f && b.Y1 < 212f);
        var keepStairs = Level.Parts.Where(p => p.Kind == AshfallKind.Stair).Select(Box2).Where(b => b.X0 >= 31.99f && b.X1 <= 36.01f && b.Y0 >= 163f && b.Y1 <= 212.01f).ToList();
        Assert.True(keepStairs.Count >= 72 + 3, $"{keepStairs.Count} keep stair parts");
        Assert.Equal(18f, keepStairs.Max(b => b.Z1));
        Assert.True(keepStairs.All(b => b.X1 <= 36.01f));   // 32 m from the east L wall (68..72): the gate approach and the courtyard stay clear
        // Bridge: a deck at 12 m at least 4 m wide and parapets 1.0..1.2 m on both sides, 6 m clear of the ground under the corridor.
        var deck = Level.Parts.Single(p => p.Kind == AshfallKind.Floor && Box2(p) is var b && b.X0 == -109f && b.X1 == -83f);
        var d = Box2(deck);
        Assert.Equal(12f, d.Z1);
        Assert.True(d.Y1 - d.Y0 >= 4f);
        var rails = Level.Parts.Where(p => p.Kind == AshfallKind.Cover && Box2(p) is var b && b.Z0 == 12f && b.X0 == -108f && b.X1 == -84f).Select(Box2).ToList();
        Assert.Equal(2, rails.Count);
        Assert.All(rails, r => Assert.InRange(r.Z1 - r.Z0, 1.0f, 1.2f + 1e-3f));
        Assert.Contains(Features.Towers, t => t.Name == "Twin tower west" && t.Storeys == 4 && t.Core);
        Assert.Contains(Features.Towers, t => t.Name == "Twin tower east" && t.Storeys == 4 && t.Core);
        // The east tower's gallery is at least 2.5 m deep, with parapets 1.0..1.2 m.
        var gallery = Level.Parts.Where(p => p.Kind == AshfallKind.Floor).Select(Box2).Single(b => b.Z1 == 18f && b.Y0 >= 70.9f && b.Y1 > 74.9f && b.Y1 <= 75.01f && b.X0 > 205f && b.X1 < 215f);
        Assert.True(gallery.Y1 - 72f >= 2.5f);
        var galleryRails = Level.Parts.Where(p => p.Kind == AshfallKind.Cover).Select(Box2).Where(b => b.Z0 == 18f && b.X0 >= 200f && b.X1 <= 220f && b.Y0 >= 68f && b.Y1 <= 78f).ToList(); // the gallery's footprint only (pass 3's 18 m rampart parapets are also Cover at 18 m)
        Assert.True(galleryRails.Count >= 3 && galleryRails.All(r => r.Z1 - r.Z0 is >= 1.0f and <= 1.2f + 1e-3f));
    }

    // ---------------------------------------------------------------- pass 3: the stronghold rework (sketch 6)

    /// <summary>The ring's 4 m thick wall boxes of the given top height (12 m west half, 18 m east half).</summary>
    static List<(float X0, float Y0, float X1, float Y1)> RingWalls(float z) => Level.Parts.Where(p => p.Kind == AshfallKind.Wall && p.Corners.Length == 36).Select(Box2)
        .Where(b => b.Z0 <= 0.001f && b.Z1 == z && MathF.Min(b.X1 - b.X0, b.Y1 - b.Y0) > 3.99f && b.X0 >= -72.01f && b.X1 <= 72.01f && b.Y0 >= 111.99f && b.Y1 <= 220.01f).Select(b => (b.X0, b.Y0, b.X1, b.Y1)).ToList();

    /// <summary>The length of [lo, hi] (along x or y) that parapets of the given base height cover inside the strip.</summary>
    static float Covered(bool alongX, float lo, float hi, float x0, float y0, float x1, float y1, float z)
    {
        var spans = Level.Parts.Where(p => p.Kind == AshfallKind.Cover).Select(Box2)
            .Where(b => MathF.Abs(b.Z0 - z) < 1e-3f && b.X0 >= x0 - 1e-3f && b.X1 <= x1 + 1e-3f && b.Y0 >= y0 - 1e-3f && b.Y1 <= y1 + 1e-3f)
            .Select(b => alongX ? (A: MathF.Max(b.X0, lo), B: MathF.Min(b.X1, hi)) : (A: MathF.Max(b.Y0, lo), B: MathF.Min(b.Y1, hi))).Where(s => s.B > s.A).OrderBy(s => s.A).ToList();
        float total = 0f, at = lo;
        foreach (var (a, b) in spans) { float from = MathF.Max(a, at); if (b > from) { total += b - from; at = b; } }
        return total;
    }

    [Fact]
    public void Pass3_every_wall_top_has_parapets_on_both_sides_except_at_towers_gateways_and_the_bridge_mouth()
    {
        // Every parapet on the ring is Cover, 1.0..1.2 m tall and at most 0.5 m thick, standing on a wall top (12 m west half, 18 m east half).
        var ring = Level.Parts.Where(p => p.Kind == AshfallKind.Cover).Select(Box2).Where(b => (MathF.Abs(b.Z0 - 12f) < 1e-3f || MathF.Abs(b.Z0 - 18f) < 1e-3f) && b.X0 >= -72.01f && b.X1 <= 72.01f && b.Y0 >= 111.99f && b.Y1 <= 220.01f).ToList();
        Assert.True(ring.Count >= 12, $"{ring.Count} rampart parapets");
        Assert.All(ring, b => { Assert.InRange(b.Z1 - b.Z0, 1.0f, 1.2f + 1e-3f); Assert.True(MathF.Min(b.X1 - b.X0, b.Y1 - b.Y0) <= 0.5f + 1e-3f); });
        // Each long side of each wall run: at least 60 % is covered and the uncovered stretch is the one the design names (a tower against the wall, the twin gateway, the bridge's mouth, 0.5 m at a corner).
        (string Side, bool AlongX, float Lo, float Hi, float X0, float Y0, float X1, float Y1, float Z, float Uncovered)[] sides =
        [
            ("west leg, west side", false, 112f, 220f, -72f, 112f, -71.5f, 220f, 12f, 16f),               // the twin tower's face y 144..160
            ("west leg, east side", false, 116f, 216f, -68.5f, 116f, -68f, 216f, 12f, 22f),                // the SW tower y 116..132 and the bridge's mouth y 199..205
            ("south leg west, south side", true, -72f, -12f, -72f, 112f, -12f, 112.5f, 12f, 0f),
            ("south leg west, north side", true, -68f, -12f, -68f, 115.5f, -12f, 116f, 12f, 16f),          // the SW tower x -68..-52
            ("north wall, north side", true, -72f, 68f, -72f, 219.5f, 68f, 220f, 12f, 0f),
            ("north wall, south side", true, -68f, 68f, -68f, 216f, 68f, 216.5f, 12f, 0f),
            ("east leg, east side", false, 112f, 220f, 71.5f, 112f, 72f, 220f, 18f, 0f),
            ("east leg, west side", false, 116f, 220f, 68f, 116f, 68.5f, 220f, 18f, 16f),                  // the SE tower y 116..132
            ("south leg east, south side", true, 12f, 71.5f, 12f, 112f, 71.5f, 112.5f, 18f, 0f),
            ("south leg east, north side", true, 12f, 68f, 12f, 115.5f, 68f, 116f, 18f, 16f),              // the SE tower x 52..68
        ];
        foreach (var s in sides)
        {
            float length = s.Hi - s.Lo, covered = Covered(s.AlongX, s.Lo, s.Hi, s.X0, s.Y0, s.X1, s.Y1, s.Z);
            output.WriteLine($"{s.Side}: {covered:0.0} of {length:0.0} m covered");
            Assert.True(covered >= 0.6f * length, $"{s.Side}: only {covered} of {length} m has a parapet");
            Assert.True(length - covered <= s.Uncovered + 0.51f, $"{s.Side}: {length - covered} m without a parapet, the design allows {s.Uncovered}");
        }
        // The ring's walls: the west half 12 m (south leg, west leg, north wall), the east half raised to 18 m (south leg, east leg), all 4 m thick.
        Assert.Equal(3, RingWalls(12f).Count);
        Assert.Equal(2, RingWalls(18f).Count);
    }

    [Fact]
    public void Pass3_the_stair_towers_stand_inside_the_compound_and_the_keep_tower_and_its_bridge_stand_as_designed()
    {
        // Stair towers: inside the ring's inner faces (x -68..68, y 116..216), south of the keep at the courtyard's corners, 3 and 4 storeys (floors to 12 and to 18 m, the rampart heights).
        var sw = Features.Towers.Single(t => t.Name == "Stair tower SW");
        var se = Features.Towers.Single(t => t.Name == "Stair tower SE");
        foreach (var t in new[] { sw, se })
        {
            Assert.True(t.Footprint.X >= -68f && t.Footprint.Z <= 68f && t.Footprint.Y >= 116f && t.Footprint.W <= 216f, $"{t.Name} {t.Footprint} is not inside the compound");
            Assert.True(t.Core);
            Assert.True(t.Footprint.W < 164f, $"{t.Name} is not by the courtyard");
        }
        Assert.Equal(3, sw.Storeys); Assert.Equal(12f, sw.Floors.Max(f => f.Z));
        Assert.Equal(4, se.Storeys); Assert.Equal(18f, se.Floors.Max(f => f.Z));
        Assert.True(sw.Footprint.X == -68f && se.Footprint.Z == 68f);   // each stands against its own wall's inner face
        // The gateways: nothing solid in the opening (from the floor up to 4 m above it) and wall under the sill.
        foreach (var (name, x, y, z) in new[] { ("SW gateway", -67.5f, 126f, 12f), ("SE gateway", 67.5f, 126f, 18f), ("keep tower gateway", -47.5f, 202f, 12f), ("twin east gateway onto the west rampart", -72.5f, 154f, 12f) })
        {
            Assert.False(Inside(new Vector3(x, y, z + 0.5f)) || Inside(new Vector3(x, y, z + 2f)) || Inside(new Vector3(x, y, z + 4f)), $"{name} is closed above its sill");
            Assert.True(Inside(new Vector3(x, y, z - 0.5f)), $"{name} has no wall under it");
        }
        // The twin tower east's ground storey is sealed (no door, no window on any face): the bridge is the way in.
        var twin = Features.Towers.Single(t => t.Name == "Twin tower east").Footprint;
        foreach (float z in new[] { 0.5f, 2f, 4f })
        {
            for (float y = twin.Y + 0.5f; y < twin.W; y += 1f) Assert.True(Inside(new Vector3(twin.X + 0.5f, y, z)) && Inside(new Vector3(twin.Z - 0.5f, y, z)), $"the twin tower east's ground storey is open at y {y}, z {z}");
            for (float x = twin.X + 0.5f; x < twin.Z; x += 1f) Assert.True(Inside(new Vector3(x, twin.Y + 0.5f, z)) && Inside(new Vector3(x, twin.W - 0.5f, z)), $"the twin tower east's ground storey is open at x {x}, z {z}");
        }
        // Keep tower: 5 storeys (30 m, above the 18 m keep roof), 16 x 16 m on the keep's west face at its north end, a core reaching every floor.
        var keepTower = Features.Towers.Single(t => t.Name == "Keep tower");
        Assert.Equal(new Vector4(-48f, 196f, -32f, 212f), keepTower.Footprint);
        Assert.Equal(5, keepTower.Storeys);
        Assert.True(keepTower.Core && keepTower.Floors.Count == 4);
        Assert.True(6f * keepTower.Storeys >= 18f + 6f);
        Assert.Contains(Level.Parts, p => p.Kind == AshfallKind.Wall && Box2(p) is var b && b.Z1 == 30f && b.X0 >= -48.01f && b.X1 <= -31.99f);
        // The bridge: a deck 6 m wide (at least 4 m) at 12 m from the west rampart (x -68) to the keep tower's west wall (x -48), parapets 1.0..1.2 m on both sides.
        var deck = Box2(Level.Parts.Single(p => p.Kind == AshfallKind.Floor && Box2(p) is var b && b.X0 == -68f && b.X1 == -48f && b.Y0 == 199f));
        Assert.Equal(12f, deck.Z1);
        Assert.True(deck.Y1 - deck.Y0 >= 4f);
        var rails = Level.Parts.Where(p => p.Kind == AshfallKind.Cover && Box2(p) is var b && b.Z0 == 12f && b.X0 == -68f && b.X1 == -48f).Select(Box2).ToList();
        Assert.Equal(2, rails.Count);
        Assert.All(rails, r => Assert.InRange(r.Z1 - r.Z0, 1.0f, 1.2f + 1e-3f));
        Assert.True(rails.Any(r => r.Y0 == 199f) && rails.Any(r => r.Y1 == 205f));
    }

    [Fact]
    public void Pass3_the_ring_is_closed_on_the_ground_except_the_gate_and_the_outerworks_keep_their_distance()
    {
        // The ring's centre line at 1 m height, probed every metre: solid everywhere except the gate x -12..12 on the south leg.
        void Probe(string side, float x, float y)
        {
            bool gate = y < 117f && x > -12f && x < 12f;
            Assert.True(gate != Inside(new Vector3(x, y, 1f)), $"{side} at ({x}, {y}): {(gate ? "the gate is blocked" : "a gap in the ring")}");
        }
        for (float x = -71.5f; x <= 71.5f; x += 1f) { Probe("south leg", x, 114f); Probe("north wall", x, 218f); }
        for (float y = 112.5f; y <= 219.5f; y += 1f) { Probe("west leg", -70f, y); Probe("east leg", 70f, y); }
        // Outerworks (y 99..108, south of the wall): tops at most 6 m (6 m or more under the lowest rampart), 4 m or more (in plan) from the wall, 8 m from the gate's approach x -12..12, clear of the avenue's 8 m lane (y 88..96).
        var outer = Level.Parts.Where(p => p.Kind is AshfallKind.Wall or AshfallKind.Floor or AshfallKind.Stair).Select(Box2).Where(b => b.Y0 >= 99.99f && b.Y1 <= 108.01f && b.Z1 <= 7.01f && b.X1 > -73f && b.X0 < 60f).ToList();
        Assert.True(outer.Count >= 30, $"{outer.Count} outerwork parts");
        Assert.All(outer, b =>
        {
            Assert.True(12f - b.Z1 >= 5f, "an outerwork top is within jumping reach of the rampart");
            Assert.True(112f - b.Y1 >= 4f - 1e-3f, $"outerwork part at y {b.Y1} is within 4 m of the wall");
            Assert.True(b.X1 <= -20f + 1e-3f || b.X0 >= 20f - 1e-3f, $"outerwork part {b.X0}..{b.X1} is within 8 m of the gate's approach");
        });
        // Item 6: three terraces at 2, 4 and 6 m (walkable Floor tops); item 7: the outer wall on the gate's east side (6 m, stepping south-east).
        foreach (var (x0, top) in new[] { (-68f, 2f), (-52f, 4f), (-36f, 6f) })
            Assert.Contains(Level.Parts, p => p.Kind == AshfallKind.Floor && Box2(p) is var b && b.X0 == x0 && b.X1 == x0 + 16f && b.Y0 == 102f && b.Y1 == 108f && b.Z1 == top);
        Assert.Contains(Level.Parts, p => p.Kind == AshfallKind.Wall && Box2(p) is var b && b.X0 == 20f && b.X1 == 36f && b.Y0 == 104f && b.Z1 == 6f);
        Assert.Contains(Level.Parts, p => p.Kind == AshfallKind.Wall && Box2(p) is var b && b.X0 == 36f && b.X1 == 56f && b.Y1 == 104f && b.Z1 == 6f);
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
        var data = TfwMap.Compose(AshfallContent.ToMapContent(Level).ToCollisionWorld(), Level.Points, Level.Areas, Level.Spawns, manufactorum: false);
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

    /// <summary>A walk from <paramref name="from"/> to <paramref name="to"/> whose every waypoint stays at or above <paramref name="minZ"/>: proves the way is the one meant (the bridge, a gateway), not a detour by the ground.</summary>
    static bool WalksAbove(NavGraph graph, Vector3 from, Vector3 to, float minZ)
    {
        if (graph.Nearest(to, 1.5f, 1.0f) < 0) return false;
        var path = graph.FindPath(from, to, goalSnap: 1.5f, maxExpanded: 8_000_000);
        return path is { Partial: false } p && p.Waypoints.All(w => w.Kind is NavLinkKind.Walk or NavLinkKind.Drop && w.Position.Z >= minZ);
    }

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
            ("keep roof", V(0f, 190f, 18f)), ("keep roof by the stair", V(30f, 210f, 18f)), ("keep stair foot", V(34f, 162f, AshfallMap.BlockTop)), ("keep stair deck", V(34f, 210f, 18f)), ("west L wall top", V(-70f, 150f, 12f)), ("east L wall top (raised to 18 m in pass 3)", V(70f, 150f, 18f)), ("west L south leg", V(-40f, 114f, 12f)),
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

        // Pass 2b: the twin towers and the bridge between them, the keep's roof by its stair, the east tower's balcony and its outside stair (landing, top landing, first floor) are walked to.
        var pts = Features.Points!;
        (string Name, Vector3 At)[] twoB =
        [
            ("bridge west end", pts["bridge west end"]), ("bridge middle", pts["bridge middle"]), ("bridge east end", pts["bridge east end"]), ("twin east plate", pts["twin east plate"]),
            ("east balcony", pts["east balcony"]), ("east stair landing", pts["east stair landing"]), ("east stair top landing", pts["east stair top"]), ("east first floor", pts["east first floor"]),
        ];
        var twoBMissing = twoB.Where(r => !Walks(graph, start, r.At)).Select(r => r.Name).ToList();
        Assert.True(twoBMissing.Count == 0, "pass 2b places not reached on foot: " + string.Join(", ", twoBMissing));
        // The outside stair is a way up of its own: its foot walks to its top landing, and the first floor is reached from the landing.
        Assert.True(Walks(graph, pts["east stair foot"], pts["east stair top"]), "the outside stair's foot does not walk to its top landing");
        Assert.True(WalksAbove(graph, pts["east stair top"], pts["east first floor"], 5.5f), "the outside stair's top landing does not walk into the first floor through its gateway (review of pass 2b: a detour by the ground would hide a blocked gateway)");
        // The bridge joins the towers on foot: from the west tower's bridge end across to the east tower's plate, and the keep roof from the stair's foot.
        Assert.True(WalksAbove(graph, pts["bridge west end"], pts["twin east plate"], 11.5f), "the bridge does not join the twin towers at its own level (review of pass 2b: a detour down one core and up the other would hide a blocked gateway)");
        Assert.True(Walks(graph, V(34f, 162f, AshfallMap.BlockTop), V(0f, 190f, 18f)), "the keep roof is not reached from the stair's foot");
        // The pointed roofs cannot be stood on (no walk to a point on a slope), and the bare strips of the east tower's roof slab are solid tops a jump reaches.
        var levelMesh = AshfallContent.ToMapContent(Level).ToCollisionWorld().Character;
        (string Name, Vector3 At)[] slopes = [("gable slope", pts["east gable slope"]), ("spire slope", pts["east spire slope"])];
        Assert.All(slopes, r => Assert.True(levelMesh.Raycast(r.At + new Vector3(0f, 0f, 2f), -Vector3.UnitZ, 2.1f) is { Normal.Z: < 0.5f }, $"{r.Name} {r.At} is not on a steep face"));
        Assert.All(slopes, r => Assert.False(Walks(graph, start, r.At), $"{r.Name} was walked to"));
        (string Name, Vector3 At)[] flats = [("east roof strip west", pts["east roof strip west"]), ("east roof strip east", pts["east roof strip east"])];
        Assert.All(flats, r => Assert.True(levelMesh.Raycast(r.At + new Vector3(0f, 0f, 2f), -Vector3.UnitZ, 2.1f) is { Normal.Z: > 0.9f }, $"{r.Name} {r.At} is not on a solid flat top"));
        Assert.All(flats, r => Assert.False(Walks(graph, start, r.At), $"{r.Name} was walked to (should be jump-only)"));
        foreach (var t in Features.Towers.Where(t => t.Name.StartsWith("Twin tower")))
        {
            var roof = new Vector3((t.Footprint.X + t.Footprint.Z) / 2f, (t.Footprint.Y + t.Footprint.W) / 2f, 24f);
            Assert.True(levelMesh.Raycast(roof + new Vector3(0f, 0f, 2f), -Vector3.UnitZ, 2.1f) is not null, $"{t.Name}'s roof slab is missing");
            Assert.False(Walks(graph, start, roof), $"{t.Name}'s roof was walked to (should be jump-only)");
        }
        // Pass 3 (sketch 6): the ramparts, both stair towers' floors, the keep tower's gateway and floors, the keep bridge and the outerwork's terraces are all walked to from the Marine start.
        (string Name, Vector3 At)[] three =
        [
            ("rampart west", pts["rampart west"]), ("rampart south west", pts["rampart south west"]), ("rampart north", pts["rampart north"]), ("rampart east (18 m)", pts["rampart east"]), ("rampart south east (18 m)", pts["rampart south east"]),
            ("west rampart at the SW tower's gateway", pts["rampart west gate"]), ("east rampart at the SE tower's gateway", pts["rampart east gate"]), ("SW stair tower top floor (12 m)", pts["sw tower top"]), ("SE stair tower top floor (18 m)", pts["se tower top"]),
            ("keep tower gateway (12 m)", pts["keep tower gateway"]), ("keep tower top floor (24 m)", pts["keep tower top"]), ("keep bridge west", pts["keep bridge west"]), ("keep bridge middle", pts["keep bridge middle"]), ("keep bridge east", pts["keep bridge east"]),
            ("terrace foot", pts["terrace foot"]), ("terrace 1", pts["terrace 1"]), ("terrace 2", pts["terrace 2"]), ("terrace 3", pts["terrace 3"]),
        ];
        var threeMissing = three.Where(r => !Walks(graph, start, r.At)).Select(r => r.Name).ToList();
        Assert.True(threeMissing.Count == 0, "pass 3 places not reached on foot: " + string.Join(", ", threeMissing));
        // Each gateway is passable at its own level (a detour by the ground would hide a blocked one): stair tower to rampart (both), the keep bridge into the keep tower, and the whole twin-tower route (west tower, bridge, east tower, gateway, rampart) without leaving 12 m.
        Assert.True(WalksAbove(graph, pts["rampart west gate"], pts["sw tower top"], 11.5f), "the SW stair tower's gateway is not passable at 12 m");
        Assert.True(WalksAbove(graph, pts["rampart east gate"], pts["se tower top"], 17.5f), "the SE stair tower's gateway is not passable at 18 m");
        Assert.True(WalksAbove(graph, pts["keep bridge west"], pts["keep tower gateway"], 11.5f), "the keep bridge does not enter the keep tower at 12 m");
        Assert.True(WalksAbove(graph, pts["bridge west end"], pts["rampart west"], 11.5f), "the twin-tower bridge does not lead onto the west rampart at 12 m (west tower, bridge, east tower, gateway, rampart)");
        Assert.True(WalksAbove(graph, pts["rampart west"], pts["keep bridge middle"], 11.5f), "the west rampart does not lead onto the keep bridge");
        foreach (var t in Features.Towers.Where(t => t.Name is "Stair tower SW" or "Stair tower SE" or "Keep tower"))
            Assert.All(t.Floors, f => Assert.True(Walks(graph, start, f), $"{t.Name} floor {f} not reached on foot"));
        // The outerwork gives no way onto the wall: from the top terrace no walk keeps to terrace height (6 m or less) as far as the rampart.
        Assert.False(WalksAbove(graph, pts["terrace 3"], pts["rampart south west"], 5.9f), "a walk at terrace height leads from the outerwork to the wall top");
        (string Name, Vector3 At)[] perches =
        [
            ("A obelisk", V(-164f, 168f, 15f)), ("small obelisk", V(-92f, 0f, 12f)), ("arch top", V(92f, 0f, 14f)),
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


    // ---------------------------------------------------------------- pass 3: the seal

    /// <summary>
    /// A walking graph over the stronghold and its surroundings (x -135..105, y 84..232) of a variant of the level, seeded outside (the avenues, the west avenue by the twin towers) and in the courtyard: <paramref name="blockGate"/> walls the gate shut
    /// (a wall x -12..12, y 112..116, 40 m tall), <paramref name="cutBridge"/> removes the twin-tower bridge's deck. Walk and drop links only (no jump packs).
    /// </summary>
    static NavGraph SealGraph(bool blockGate, bool cutBridge)
    {
        // The bridge is its deck and its two parapets (the parapet tops are 0.5 m wide and a walk-off from the west tower's upper floor lands on them, so they go with it).
        var parts = Level.Parts.Where(p => !(cutBridge && Box2(p) is var b && ((p.Kind == AshfallKind.Floor && b.X0 == -109f && b.X1 == -83f) || (p.Kind == AshfallKind.Cover && b.Z0 == 12f && b.X0 == -108f && b.X1 == -84f)))).ToList();
        if (blockGate) parts.Add(new AshfallPart(AshfallKind.Wall, TfwMap.Triangles(new TfwSolid(new Vector3(-12f, 112f, 0f), new Vector3(12f, 116f, 40f)))));
        var world = AshfallContent.ToMapContent(Level with { Parts = parts }).ToCollisionWorld();
        Vector3[] seeds = [new(0f, 92f, 0f), new(-92f, 150f, 0f), new(92f, 150f, 0f), new(-60f, 92f, 0f), new(60f, 92f, 0f), new(-92f, 205f, 0f), new(92f, 205f, 0f), Level.Areas[1].Center];
        return NavGraph.Build(world.Character, world.KillVolumes, NavSettings.From(new SimConfig()), new NavArea(new Vector2(-135f, 84f), new Vector2(105f, 232f), seeds));
    }

    [Fact]
    [Trait("Speed", "Slow")]   // four local walking graphs (about 10 s each)
    public void Pass3_the_compound_is_entered_on_foot_only_through_the_gate_or_the_twin_tower_bridge()
    {
        var pts = Features.Points!;
        Vector3 outside = new(0f, 92f, 0f);
        (string Name, Vector3 At)[] inside =
        [
            ("courtyard", Level.Areas[1].Center), ("west rampart", pts["rampart west"]), ("south west rampart", pts["rampart south west"]), ("north rampart", pts["rampart north"]), ("east rampart (18 m)", pts["rampart east"]), ("south east rampart (18 m)", pts["rampart south east"]),
            ("SW stair tower top", pts["sw tower top"]), ("SE stair tower top", pts["se tower top"]), ("keep tower gateway", pts["keep tower gateway"]), ("keep tower top", pts["keep tower top"]), ("keep bridge middle", pts["keep bridge middle"]),
            ("twin tower east plate (12 m)", pts["twin east plate"]), ("keep roof", new Vector3(0f, 190f, 18f)), ("keep stair foot", new Vector3(34f, 162f, AshfallMap.BlockTop)),
        ];
        // Outside places that stay reachable in every variant (so "unreachable" below is a finding about the compound, not a broken graph): the avenue, the west tower's bridge end, the outerwork's terraces.
        (string Name, Vector3 At)[] outsidePlaces = [("avenue", new Vector3(-92f, 150f, 0f)), ("west twin tower bridge end", pts["bridge west end"]), ("terrace 3", pts["terrace 3"]), ("gate approach", new Vector3(0f, 108f, AshfallMap.BlockTop))];
        List<string> Reached(NavGraph g, (string Name, Vector3 At)[] list) => list.Where(r => Walks(g, outside, r.At)).Select(r => r.Name).ToList();

        var open = SealGraph(false, false);
        Assert.Empty(inside.Select(r => r.Name).Except(Reached(open, inside)));           // the gate and the bridge both there: all reached
        var noGate = SealGraph(true, false);
        Assert.Empty(inside.Select(r => r.Name).Except(Reached(noGate, inside)));         // the gate shut: all reached by the bridge (west tower, bridge, east tower, gateway, rampart, stair tower, courtyard)
        var noBridge = SealGraph(false, true);
        Assert.Empty(inside.Select(r => r.Name).Except(Reached(noBridge, inside)));       // the bridge cut: all reached through the gate (and from the courtyard up a stair tower, along the rampart, into the twin tower east)
        // The gate and the bridge both gone: nothing of the compound is reachable from outside (rampart, stair towers, keep tower, keep bridge, courtyard, twin tower east), while the outside places are.
        var sealedOff = SealGraph(true, true);
        Assert.Empty(Reached(sealedOff, inside));
        Assert.Equal(outsidePlaces.Length, Reached(sealedOff, outsidePlaces).Count);
        output.WriteLine($"seal check: open {open.NodeCount}, gate shut {noGate.NodeCount}, bridge cut {noBridge.NodeCount}, both {sealedOff.NodeCount} nodes");
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
