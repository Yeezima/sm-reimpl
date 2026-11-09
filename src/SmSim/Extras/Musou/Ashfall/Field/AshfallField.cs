using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Which generator builds Ashfall (owner, 2026-11-09: "full redesign - but keep the current version as a path to easily roll back to"). <see cref="Draft10"/> is the shipped layout (map <c>tfw_war</c>, hash pinned);
/// <see cref="Field"/> is the battlefield ruleset of docs/ashfall-layout-brief.md (map <c>tfw_war_field</c>; docs/musou-ashfall-graybox.md "Draft 11 battlefield layout").
/// </summary>
public enum AshfallLayout : byte { Draft10 = 0, Field = 1, City = 2 }

/// <summary>Draft 12: what the layouts share.</summary>
public static class AshfallLayouts
{
    /// <summary>Whether the layout is built from a plan (draft 11's field, draft 12's city): one flat ground, the fortress taken from draft 10, the plan's corridors as roadways.</summary>
    public static bool Planned(this AshfallLayout layout) => layout != AshfallLayout.Draft10;

    /// <summary>The default plan of a planned layout (null for draft 10).</summary>
    public static AshfallFieldPlan? PlanOf(this AshfallLayout layout) => layout switch { AshfallLayout.Field => AshfallField.Default, AshfallLayout.City => AshfallCity.Default, _ => null };

    /// <summary>The layout's name in the tools' and the client's switches (<c>--layout</c>, <c>--ashfall-layout</c>).</summary>
    public static AshfallLayout Parse(string? name) => name switch { "field" => AshfallLayout.Field, "draft10" => AshfallLayout.Draft10, "city" => AshfallLayout.City, _ => AshfallLayout.City };

    public static string Key(this AshfallLayout layout) => layout switch { AshfallLayout.Field => "field", AshfallLayout.City => "city", _ => "draft10" };
}

/// <summary>
/// The battlefield ruleset's tunable parameters (docs/ashfall-layout-brief.md "Generator controls"; spec "Ashfall battlefield layout (draft 11)"). Every value is ours, an estimate derived from the game's
/// metrics where one applies (named per value), for the owner's playtest; none is a universal level-design number. Changing any of them changes the level and its hash.
/// </summary>
public sealed record AshfallFieldParams
{
    /// <summary>The narrowest an army route may be after all collision is placed (m; the owner, 2026-11-09: "about 20 m"). 13 Ork files abreast at the army layout's 1.4 m file spacing with a Nob's 0.6 m body and 0.5 m edge clearance a side.</summary>
    public float MinArmyWidth { get; init; } = 20f;
    /// <summary>The width reserved for each army corridor before the kit is placed (m; the draft-10 avenue width): the 200-wave's 17 files (<c>TfwArmyLayout.Width</c>) at 1.4 m plus bodies.</summary>
    public float CorridorWidth { get; init; } = 24f;
    /// <summary>Extra keep-out round each corridor for the islands' graybox footprints (m; growth and the roadway margin then stay outside the corridor).</summary>
    public float RouteBuffer { get; init; } = 4f;
    /// <summary>Open ground kept round each capture zone (m beyond its radius): a ring for the fight round the point (two Ork ranks of 1.6 m deep, eight times over; ours).</summary>
    public float ObjectiveClear { get; init; } = 16f;
    /// <summary>Formation-capable approaches each ordinary point must have (the owner's brief: "at least two").</summary>
    public int ApproachCount { get; init; } = 2;
    /// <summary>Two approaches count as distinct when their entry bearings differ by at least this (degrees; ours).</summary>
    public float ApproachSeparation { get; init; } = 60f;
    /// <summary>The assembly area in front of each base's exit (m, width x depth): a 500-wave (27 files x 19 rows, <c>TfwArmyLayout</c>) stands in it with room round it.</summary>
    public Vector2 AssemblySize { get; init; } = new(80f, 40f);
    /// <summary>Obstacle density by district: the share of a district's free land the islands' drawn footprints may cover (ours; "mostly open, ruin islands").</summary>
    public float DensityCathedral { get; init; } = 0.16f;
    public float DensityManufactorum { get; init; } = 0.16f;
    public float DensityHab { get; init; } = 0.20f;
    public float DensityNoMansLand { get; init; } = 0.05f;
    /// <summary>Least open ground between two islands' footprints (m; ours: wide enough for a squad to walk round, so islands never fuse into accidental walls).</summary>
    public float IslandGap { get; init; } = 14f;
    /// <summary>Least gap between the buildings of one cluster (m, final size after the draft-6 growth; over the 2.4 m narrow-gap rule).</summary>
    public float ClusterGap { get; init; } = 6f;
    /// <summary>Tall sightline blockers (over this height, m) stand only within <see cref="EdgeBand"/> of the boundary or as authored landmarks.</summary>
    public float TallHeight { get; init; } = 16f;
    public float EdgeBand { get; init; } = 70f;
    /// <summary>A landmark must be seen from at least this many of the three points (ours: two of three).</summary>
    public int LandmarkVisibility { get; init; } = 2;
    /// <summary>High-ground coverage: shooting platforms in all (the pass adds them on free ground near points and routes; draft 10 had 48 in a denser city).</summary>
    public int PlatformTarget { get; init; } = 24;
    /// <summary>An added shooting platform may not stand where all three points lie within this range (m; ours: a rifle's useful reach on the open field), so no one platform overlooks every objective.</summary>
    public float HighGroundViewRange { get; init; } = 250f;
    /// <summary>Islands taller than this (m) keep out of a band this wide (m) along each landmark's sightline to its two nearest points (wayfinding: the landmark stays seen).</summary>
    public float SightBlockHeight { get; init; } = 4f;
    public float SightBand { get; init; } = 8f;
    /// <summary>No elevated position may see more than this many of the three points (the brief: "avoid one position dominating every objective").</summary>
    public int MaxPointsSeenFromHeight { get; init; } = 2;
    /// <summary>The longest a reinforcement may walk from the Imperial base to a point (s at the Guard army's 3.6 m/s) and an Ork wave from the stronghold (s at 3 m/s): the wave director's 90-150 s lulls (<c>TfwArmyRules</c>).</summary>
    public float MaxReinforceSeconds { get; init; } = 120f;
    public float MaxWaveSeconds { get; init; } = 120f;

    /// <summary>The layout seed: islands, templates and their places (collision and routes).</summary>
    public uint LayoutSeed { get; init; } = 11;
    /// <summary>The dressing seed: which kit model stands for each structure (collision, never the corridors: the roadway rule keeps them clear).</summary>
    public uint DressSeed { get; init; } = 1;
    /// <summary>The surface seed: tile variants and turns (visual only).</summary>
    public uint SurfaceSeed { get; init; } = 1;

    /// <summary>The defaults (the level the map <c>tfw_war_field</c> loads).</summary>
    public static readonly AshfallFieldParams Default = new();
}

/// <summary>One army corridor: a straight centreline from <see cref="A"/> to <see cref="B"/>, <see cref="Width"/> wide (both ends extended by half the width, so corridors join cleanly).</summary>
public sealed record AshfallCorridor(string Name, Vector2 A, Vector2 B, float Width)
{
    public float Length => Vector2.Distance(A, B);

    /// <summary>The yaw step (x 15 degrees) of the centreline (a corridor runs along one of the 15-degree table's directions).</summary>
    public int YawStep
    {
        get
        {
            var d = Vector2.Normalize(B - A);
            int best = 0; float bestDot = float.MinValue;
            for (int k = 0; k < 12; k++) { var (s, c) = AshfallBuilder.Trig(k); float dot = MathF.Abs(d.X * c + d.Y * s); if (dot > bestDot) { bestDot = dot; best = k; } }
            return best;
        }
    }

    /// <summary>The corridor as a box on the ground plane, grown by <paramref name="grow"/> on every side (the roadway rule's strip).</summary>
    public AshfallBox Box(float grow = 0f, float z0 = -2f, float z1 = 1000f)
    {
        var m = (A + B) / 2f;
        return new AshfallBox(m.X, m.Y, z0, z1, Length + Width + 2f * grow, Width + 2f * grow, YawStep);
    }

    /// <summary>Whether a plan point lies inside the corridor grown by <paramref name="grow"/>.</summary>
    public bool Contains(Vector2 p, float grow = 0f) => AshfallAtmosphere.Inside(Box(grow), p, 0f);

    /// <summary>Distance from a plan point to the centreline segment.</summary>
    public float DistanceTo(Vector2 p)
    {
        var ab = B - A; float t = Math.Clamp(Vector2.Dot(p - A, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector2.Distance(p, A + ab * t);
    }
}

/// <summary>A named route through the corridors (plan waypoints), from a base or a point to a point; used for travel times, the clash tests and the plan's overlays.</summary>
public sealed record AshfallRoute(string Name, string From, string To, Vector2[] Path)
{
    public float Length { get { float l = 0f; for (int i = 1; i < Path.Length; i++) l += Vector2.Distance(Path[i - 1], Path[i]); return l; } }
}

/// <summary>A district: its name, the rectangles it covers, its island density and the families its islands draw on.</summary>
public sealed record AshfallDistrict(string Name, Vector4[] Rects, float Density, float Spacing);

/// <summary>One structure of an island, final size (after the draft-6 growth), standing on the ground; <see cref="Family"/> picks the kit pool.</summary>
public sealed record AshfallFieldStructure(string Name, string Family, Vector2 Centre, Vector3 Size, int YawStep, string District, bool Landmark = false);

/// <summary>
/// The battlefield plan (deterministic): protected areas first (bases, the stronghold, assembly areas, capture zones with their open rings, army corridors), then districts and their islands on what is left.
/// Built by <see cref="AshfallField.Plan"/>; the graybox is made from it (<c>AshfallMap.BuildFieldGraybox</c>) and the kit pass places models on that graybox under every standing rule.
/// </summary>
public sealed record AshfallFieldPlan(
    AshfallFieldParams Params,
    IReadOnlyList<ControlPoint> Points,
    IReadOnlyList<StartArea> Areas,
    IReadOnlyList<(string Name, Vector4 Rect)> Assembly,
    IReadOnlyList<AshfallCorridor> Corridors,
    IReadOnlyList<AshfallRoute> Routes,
    IReadOnlyList<AshfallDistrict> Districts,
    IReadOnlyList<AshfallFieldStructure> Structures,
    IReadOnlyList<(string Name, Vector4 Rect)> Protected,
    IReadOnlyList<Vector3> Spawns,
    IReadOnlyList<string> Notes,
    IReadOnlyList<(string Name, Vector4 Rect)> Aprons)
{
    /// <summary>Which layout the plan builds (draft 11: the field; draft 12: the city).</summary>
    public AshfallLayout Layout { get; init; } = AshfallLayout.Field;
    /// <summary>The version term the level's hash is salted with.</summary>
    public uint HashVersion { get; init; } = AshfallField.HashVersion;
    /// <summary>Draft 12: the city's blocks (name, buildable rectangle inside its roads), for the overlays; empty for the field.</summary>
    public IReadOnlyList<(string Name, Vector4 Rect)> Blocks { get; init; } = [];

    /// <summary>The open ring kept round each point (centre, radius).</summary>
    public IEnumerable<(string Name, Vector2 Centre, float Radius)> ClearZones => Points.Select(p => (p.Name, new Vector2(p.Center.X, p.Center.Y), p.Radius + Params.ObjectiveClear));

    /// <summary>The corridors that reach a point's clear zone (an approach each).</summary>
    public IEnumerable<AshfallCorridor> ApproachesOf(ControlPoint p)
    {
        var c = new Vector2(p.Center.X, p.Center.Y);
        float r = p.Radius + Params.ObjectiveClear;
        return Corridors.Where(k => k.DistanceTo(c) <= r);
    }
}

/// <summary>The battlefield generator's plan step (docs/musou-ashfall-graybox.md "Draft 11 battlefield layout"; every place and size ours, placeholder demonstration).</summary>
public static class AshfallField
{
    /// <summary>The field layout's version (salts its level hash; raised with any change of its geometry, points, areas or spawns).</summary>
    public const uint Version = 2;   // 2: the owner's stair pass (draft 12's stair rules: entry ramps, steepened flights, landing slabs, 1.6 m flights, the stair-access check enforced)

    /// <summary>The version term the field level's hash is salted with ("F" in the high byte, so it never equals a draft's version).</summary>
    public const uint HashVersion = 0x46000000u | Version;

    static readonly Lazy<AshfallFieldPlan> DefaultPlan = new(() => Plan(AshfallFieldParams.Default), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The plan of the default parameters (built once).</summary>
    public static AshfallFieldPlan Default => DefaultPlan.Value;

    // The fixed places: the Imperial base (draft 10's compound and start area) and the stronghold (draft 10's fortress, unchanged) stay; the points move.
    public static readonly Vector2 BaseCentre = new(0f, -188f), BaseGate = new(0f, -160f), StrongholdCentre = new(0f, 138f), StrongholdGate = new(0f, 112f);
    public static readonly Vector2 PointA = new(-150f, 80f), PointB = new(0f, -20f), PointC = new(150f, 80f);
    /// <summary>The junctions where the diagonals from the base meet the flank avenues and the B cross.</summary>
    public static readonly Vector2 WestJunction = new(-150f, -20f), EastJunction = new(150f, -20f), BaseFork = new(0f, -146f), StrongholdFork = new(0f, 80f);

    /// <summary>The plan for <paramref name="p"/> (deterministic: integer hashes, exact arithmetic, total-order sorts).</summary>
    public static AshfallFieldPlan Plan(AshfallFieldParams p)
    {
        var notes = new List<string>();
        const float top = AshfallMap.BlockTop;
        ControlPoint[] points = [new("A", new(PointA.X, PointA.Y, top), 14f, 8f), new("B", new(PointB.X, PointB.Y, top), 16f, 8f), new("C", new(PointC.X, PointC.Y, top), 14f, 8f)];
        StartArea[] areas =
        [
            new(Team.SpaceMarines, new(BaseCentre.X, BaseCentre.Y, top), new(64f, 48f, 8f), 0f),
            new(Team.Chaos, new(StrongholdCentre.X, StrongholdCentre.Y, top), new(40f, 36f, 8f), 0f),
        ];
        float w = p.CorridorWidth;
        var sw = new Vector2(-48f, -122f); var se = new Vector2(48f, -122f);   // the diagonals leave the base's assembly area here (clear of the compound's draft-10 walls): 45 degrees to the flank junctions (102 m each way)
        AshfallCorridor[] corridors =
        [
            new("base to B", new Vector2(0f, -140f), PointB, w),   // starts north of the compound's gate wall (its 16 m gate is narrower than a corridor)
            new("stronghold to B", new Vector2(0f, 94f), PointB, w),   // ends short of the gatehouse (the stronghold's gate is its authored exception)
            new("north cross west", StrongholdFork, PointA, w),
            new("north cross east", StrongholdFork, PointC, w),
            new("west avenue", WestJunction, PointA, w),
            new("east avenue", EastJunction, PointC, w),
            new("B west", PointB, WestJunction, w),
            new("B east", PointB, EastJunction, w),
            new("south-west diagonal", sw, WestJunction, w),
            new("south-east diagonal", se, EastJunction, w),
        ];
        var a0 = p.AssemblySize;
        (string, Vector4)[] assembly =
        [
            ("Imperial assembly", new(-a0.X / 2f, -150f, a0.X / 2f, -150f + a0.Y)),   // 10 m north of the compound's gate wall
            ("Ork assembly", new(-a0.X / 2f, 92f - a0.Y * 0.8f, a0.X / 2f, 92f)),        // on the north cross, short of the stronghold's apron (y 60..92)
        ];
        // The fortresses' aprons: draft 10's roadway strips round the stronghold and the Imperial compound (the north avenue, the perimeter road, the west and east avenues beside the ring, the gate lane; the paved road
        // north of the compound's gate and the perimeter road behind it). Kept as roadways, they settle the authored pieces exactly where draft 10 put them (the gatehouse, the terraces, the outer walls and the ring's
        // ends were pushed off those roads there), and they keep the islands off the fortresses' feet.
        (string, Vector4)[] aprons =
        [
            ("stronghold apron south", new(-110f, 80f, 110f, 104f)),
            ("stronghold apron north", new(-110f, 224f, 110f, 240f)),
            ("stronghold apron west", new(-104f, 76f, -80f, 240f)),
            ("stronghold apron east", new(80f, 76f, 104f, 240f)),
            ("stronghold gate lane", new(-6f, 104f, 6f, 110f)),
            ("compound road", new(-6f, -160f, 6f, -104f)),
            ("compound apron south", new(-60f, -240f, 60f, -224f)),
        ];
        Vector2 base0 = BaseCentre, sh = StrongholdCentre;
        AshfallRoute[] routes =
        [
            new("base to A", "base", "A", [base0, BaseGate, sw, WestJunction, PointA]),
            new("base to B", "base", "B", [base0, BaseGate, PointB]),
            new("base to C", "base", "C", [base0, BaseGate, se, EastJunction, PointC]),
            new("stronghold to A", "stronghold", "A", [sh, StrongholdGate, StrongholdFork, PointA]),
            new("stronghold to B", "stronghold", "B", [sh, StrongholdGate, PointB]),
            new("stronghold to C", "stronghold", "C", [sh, StrongholdGate, StrongholdFork, PointC]),
            new("A to B", "A", "B", [PointA, WestJunction, PointB]),
            new("B to C", "B", "C", [PointB, EastJunction, PointC]),
            new("A to C", "A", "C", [PointA, StrongholdFork, PointC]),
            new("A to B (north)", "A", "B", [PointA, StrongholdFork, PointB]),
            new("B to C (north)", "B", "C", [PointB, StrongholdFork, PointC]),
            new("base to A (via B)", "base", "A", [base0, BaseGate, PointB, WestJunction, PointA]),
            new("base to C (via B)", "base", "C", [base0, BaseGate, PointB, EastJunction, PointC]),
            new("stronghold to A (via B)", "stronghold", "A", [sh, StrongholdGate, PointB, WestJunction, PointA]),
            new("stronghold to C (via B)", "stronghold", "C", [sh, StrongholdGate, PointB, EastJunction, PointC]),
        ];

        // Protected areas (no island's graybox footprint may meet them): bases, the stronghold with its outerworks, assembly areas, clear zones (as squares round the ring: the disc test is separate), the boundary band.
        var prot = new List<(string, Vector4)>
        {
            ("Imperial base", new(-44f, -224f, 44f, -156f)),
            ("stronghold", new(-84f, 96f, 84f, 226f)),
            ("stronghold twin towers", new(-142f, 126f, -70f, 166f)),   // draft 10's twin towers and their bridge onto the west rampart (taken with the fortress)
        };
        prot.AddRange(assembly);
        prot.AddRange(aprons);

        var districts = new List<AshfallDistrict>
        {
            new("cathedral quarter", [new(-226f, 96f, -86f, 226f), new(-226f, 20f, -166f, 96f)], p.DensityCathedral, 14f),
            new("manufactorum district", [new(86f, 96f, 226f, 226f), new(166f, 20f, 226f, 96f)], p.DensityManufactorum, 14f),
            new("no-man's-land", [new(-138f, -8f, -14f, 68f), new(14f, -8f, 138f, 68f)], p.DensityNoMansLand, 16f),
            new("hab-block ruins", [new(-226f, -226f, -46f, -34f), new(46f, -226f, 226f, -34f), new(-226f, -34f, -166f, 20f), new(166f, -34f, 226f, 20f)], p.DensityHab, 14f),
        };

        var structures = new List<AshfallFieldStructure>();
        var islands = new List<Vector4>();   // final drawn footprints of placed islands (axis bounds)

        bool Free(Vector4 r, float gap)
        {
            if (r.X < -226f || r.Y < -226f || r.Z > 226f || r.W > 226f) return false;
            foreach (var (_, z) in prot) if (r.X < z.Z + p.RouteBuffer && r.Z > z.X - p.RouteBuffer && r.Y < z.W + p.RouteBuffer && r.W > z.Y - p.RouteBuffer) return false;
            foreach (var pt in points)
            {
                float rr = pt.Radius + p.ObjectiveClear + p.RouteBuffer;
                float dx = MathF.Max(MathF.Max(r.X - pt.Center.X, pt.Center.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - pt.Center.Y, pt.Center.Y - r.W), 0f);
                if (dx * dx + dy * dy < rr * rr) return false;
            }
            foreach (var k in corridors)
                if (RectMeetsBox(r, k.Box(p.RouteBuffer))) return false;
            foreach (var o in islands) if (AshfallPlacer.Gap(r, o) < gap) return false;
            return true;
        }

        // 1. Landmarks (authored places, one a district; the cathedral's and the manufactorum's at the edge so the tall blockers stay at the edges).
        (string District, string Name, string Family, Vector2 At, Vector3 Size)[] landmarks =
        [
            ("cathedral quarter", "cathedral spire", "tower", new(-196f, 196f), new(16f, 16f, 34f)),
            ("cathedral quarter", "cathedral statue", "monument", new(-182f, 136f), new(9f, 9f, 14f)),
            ("manufactorum district", "manufactorum chimney tower", "tower", new(196f, 196f), new(16f, 16f, 34f)),
            ("hab-block ruins", "hab tower", "tower", new(-196f, -196f), new(14f, 14f, 26f)),
            ("hab-block ruins", "hab tower east", "tower", new(196f, -196f), new(14f, 14f, 26f)),
            ("no-man's-land", "wrecked hulk", "wreck", new(-76f, 30f), new(14f, 9f, 6f)),
        ];
        foreach (var (d, name, fam, at, size) in landmarks)
        {
            var r = Footprint(at, size, 0);
            if (!Free(r, p.IslandGap)) { notes.Add($"landmark {name} does not fit at ({at.X}, {at.Y}) and is left out"); continue; }
            structures.Add(new(name, fam, at, size, 0, d, Landmark: true));
            islands.Add(r);
        }

        // Landmark sightlines: each tall landmark to its two nearest points (the segment, less the landmark's own 12 m).
        var bands = new List<(Vector2 A, Vector2 B)>();
        foreach (var s in structures.Where(s => s.Landmark && s.Size.Z >= 12f))
            foreach (var pt in points.OrderBy(q => Vector2.DistanceSquared(new Vector2(q.Center.X, q.Center.Y), s.Centre)).Take(2))
            {
                var a = new Vector2(pt.Center.X, pt.Center.Y); var dir = Vector2.Normalize(s.Centre - a);
                bands.Add((a, s.Centre - dir * 12f));
            }
        bool InSight(Vector4 r) => bands.Any(b => SegmentRectDistance(b.A, b.B, r) < p.SightBand / 2f);

        // 2. Islands, district by district, on a jittered grid in seeded order, until the district's density is met.
        uint n = 0;
        foreach (var d in districts)
        {
            float land = 0f, covered = 0f;
            foreach (var r in d.Rects) land += (r.Z - r.X) * (r.W - r.Y);
            foreach (var s in structures.Where(s => s.District == d.Name)) covered += s.Size.X * s.Size.Y;
            var cands = new List<(uint Key, Vector2 At)>();
            foreach (var r in d.Rects)
                for (float x = r.X + d.Spacing / 2f; x < r.Z; x += d.Spacing)
                    for (float y = r.Y + d.Spacing / 2f; y < r.W; y += d.Spacing)
                    {
                        uint h = AshfallBuilder.Mix(p.LayoutSeed * 0x9E37u + 17u, n++);
                        float jx = ((h % 13u) - 6f) * 1f, jy = (((h >> 8) % 13u) - 6f) * 1f;
                        cands.Add((h, new Vector2(x + jx, y + jy)));
                    }
            cands.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.At.X != b.At.X ? a.At.X.CompareTo(b.At.X) : a.At.Y.CompareTo(b.At.Y));
            int placedHere = 0, tried = 0;
            foreach (var (key, at) in cands)
            {
                if (covered >= d.Density * land) break;
                tried++;
                var group = Template(d.Name, key, at, p, placedHere);
                if (group.Count == 0) continue;
                var fp = group.Select(s => Footprint(s.Centre, s.Size, s.YawStep)).Aggregate(Union);
                bool tallInside = group.Any(s => s.Size.Z > p.TallHeight) && EdgeDistance(at) > p.EdgeBand;
                if (tallInside) group = group.Select(s => s.Size.Z > p.TallHeight ? s with { Size = s.Size with { Z = p.TallHeight }, Family = s.Family == "tower" ? "shell" : s.Family } : s).ToList();
                if (!Free(fp, d.Name == "no-man's-land" ? p.IslandGap + 6f : p.IslandGap)) continue;
                if (group.Max(s => s.Size.Z) > p.SightBlockHeight && InSight(fp)) continue;
                if (!d.Rects.Any(r => at.X >= r.X && at.X <= r.Z && at.Y >= r.Y && at.Y <= r.W)) continue;
                int k = 0;
                foreach (var s in group) structures.Add(s with { Name = $"{d.Name} island {placedHere} {s.Family} {k++}" });
                islands.Add(fp);
                covered += group.Sum(s => s.Size.X * s.Size.Y);
                placedHere++;
            }
            notes.Add($"{d.Name}: {placedHere} islands ({structures.Count(s => s.District == d.Name)} structures), drawn cover {covered / land:P0} of {land / 10000f:0.0} ha (target {d.Density:P0}; {tried} candidates tried)");
        }

        // Spawn markers: the enemies' graph seeds (open ground only: start areas, points, junctions, the forks, the districts' middles and the square's corners for the graph's bounds).
        Vector3 P(float x, float y) => new(x, y, top);
        Vector3[] spawns =
        [
            areas[0].Center, areas[1].Center, .. points.Select(q => q.Center),
            P(WestJunction.X, WestJunction.Y), P(EastJunction.X, EastJunction.Y), P(BaseFork.X, BaseFork.Y), P(StrongholdFork.X, StrongholdFork.Y),
            P(-87f, -83f), P(87f, -83f), P(-75f, 80f), P(75f, 80f), P(0f, -160f),
            P(-232f, -232f), P(232f, -232f), P(-232f, 232f), P(232f, 232f),
        ];
        return new AshfallFieldPlan(p, points, areas, assembly, corridors, routes, districts, structures, prot, spawns, notes, aprons);
    }

    // ---------------------------------------------------------------- island templates (final sizes; the graybox is drawn at 1 / 1.5 so the draft-6 growth brings them back)

    static List<AshfallFieldStructure> Template(string district, uint key, Vector2 at, AshfallFieldParams p, int index)
    {
        uint t = (key >> 4) % 100u;
        int yaw = (int)((key >> 12) % 2u) * 6;   // built pieces stand square to the plan grid (draft 9's urban rule)
        var list = new List<AshfallFieldStructure>();
        void Add(string fam, float dx, float dy, Vector3 size, int y = -1) => list.Add(new("", fam, at + new Vector2(dx, dy), size, y < 0 ? yaw : y, district));
        float g = p.ClusterGap;
        switch (district)
        {
            case "cathedral quarter":
                if (t < 40) { Add("shell", 0f, 0f, new(20f, 14f, 13f)); Add("rubble", 0f, -(7f + 5f), new(5f, 3f, 1.4f), 0); }                                   // a chapel ruin and its fallen stone
                else if (t < 70) { Add("shell", -(8f + g / 2f), 0f, new(16f, 12f, 11f)); Add("shell", 8f + g / 2f, 0f, new(16f, 12f, 11f)); }                   // a cloister pair
                else if (t < 85) { Add("facade", 0f, 0f, new(16f, 3f, 12f)); Add("cover", 0f, -7f, new(4f, 1.5f, 1.2f), 0); }                                    // a standing facade
                else { Add("monument", 0f, 0f, new(7f, 7f, 10f), 0); Add("cover", 7f, 0f, new(4f, 1.5f, 1.2f), 6); }                                              // a shrine
                break;
            case "manufactorum district":
                if (t < 35) { Add("shell", 0f, 0f, new(22f, 15f, 11f)); Add("wall", 0f, -(7.5f + 4f), new(12f, 1.5f, 2.2f), 0); }                                // a works hall
                else if (t < 60) { Add("platform", 0f, 0f, new(20f, 7f, 5f)); Add("cover", 0f, -6.5f, new(4f, 1.5f, 1.2f), 0); }                                 // a gantry platform
                else if (t < 80) { Add("bunker", 0f, 0f, new(18f, 15f, 7f)); }                                                                                    // a blockhouse
                else { Add("shell", -(8f + g / 2f), 0f, new(16f, 13f, 10f)); Add("shell", 8f + g / 2f, 0f, new(16f, 13f, 10f)); Add("rubble", 0f, 10f, new(5f, 3f, 1.4f), 0); }   // a works pair
                break;
            case "hab-block ruins":
                if (t < 45)
                {
                    // a cluster of hab blocks round a yard (2 x 2, the cluster gap between them)
                    float s = 14f;
                    foreach (var (dx, dy) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
                        if ((key >> (int)(10 + dx + 2 * dy + 3)) % 5u != 0u) Add("shell", dx * (s / 2f + g / 2f), dy * (s / 2f + g / 2f), new(s, s, 12f));
                }
                else if (t < 75) { Add("shell", 0f, 0f, new(18f, 14f, 12f)); Add("rubble", 9f + 5f, 0f, new(5f, 3f, 1.4f), 6); }
                else { Add("facade", 0f, 0f, new(15f, 3f, 11f)); Add("facade", 0f, 3f + g + 3f, new(15f, 3f, 11f)); }
                break;
            default:   // no-man's-land: low cover only, rubble and a wreck now and then (sightlines stay open)
                if (t < 50) { Add("cover", 0f, 0f, new(4f, 1.5f, 1.2f)); Add("cover", 5f + g, 2f, new(4f, 1.5f, 1.2f)); Add("rubble", -(4f + g), -1f, new(4f, 3f, 1.2f), 0); }
                else if (t < 80) { Add("cover", 0f, 0f, new(5f, 1.5f, 2.2f)); Add("rubble", 0f, -(2f + 3f), new(5f, 3f, 1.2f), 0); }
                else { Add("wreck", 0f, 0f, new(10f, 6f, 4.5f)); }
                break;
        }
        return list;
    }

    /// <summary>A structure's axis bounds (final size) at a yaw of 0 or a quarter turn.</summary>
    public static Vector4 Footprint(Vector2 c, Vector3 size, int yawStep)
    {
        var (s, co) = AshfallBuilder.Trig(yawStep);
        float ex = MathF.Abs(co) * size.X / 2f + MathF.Abs(s) * size.Y / 2f, ey = MathF.Abs(s) * size.X / 2f + MathF.Abs(co) * size.Y / 2f;
        return new Vector4(c.X - ex, c.Y - ey, c.X + ex, c.Y + ey);
    }

    /// <summary>Distance from a segment to an axis rectangle (0 when they meet; sampled every metre, ample for the plan's metre-scale bands).</summary>
    static float SegmentRectDistance(Vector2 a, Vector2 b, Vector4 r)
    {
        float len = Vector2.Distance(a, b), best = float.MaxValue;
        int n = Math.Max(1, (int)len);
        for (int i = 0; i <= n; i++)
        {
            var q = Vector2.Lerp(a, b, i / (float)n);
            float dx = MathF.Max(MathF.Max(r.X - q.X, q.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - q.Y, q.Y - r.W), 0f);
            best = MathF.Min(best, MathF.Sqrt(dx * dx + dy * dy));
        }
        return best;
    }

    static Vector4 Union(Vector4 a, Vector4 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.Z, b.Z), MathF.Max(a.W, b.W));

    static float EdgeDistance(Vector2 p) => AshfallMap.Half - MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y));

    /// <summary>Whether an axis rectangle meets a turned box (separating axes: the rectangle's two and the box's two).</summary>
    internal static bool RectMeetsBox(Vector4 r, AshfallBox b)
    {
        var rc = new Vector2((r.X + r.Z) / 2f, (r.Y + r.W) / 2f); var rh = new Vector2((r.Z - r.X) / 2f, (r.W - r.Y) / 2f);
        var bc = new Vector2(b.Cx, b.Cy); var ax = b.AxisX; var ay = b.AxisY; float hx = b.Length / 2f, hy = b.Depth / 2f;
        var d = bc - rc;
        // the rectangle's axes
        if (MathF.Abs(d.X) > rh.X + MathF.Abs(ax.X) * hx + MathF.Abs(ay.X) * hy) return false;
        if (MathF.Abs(d.Y) > rh.Y + MathF.Abs(ax.Y) * hx + MathF.Abs(ay.Y) * hy) return false;
        // the box's axes
        if (MathF.Abs(Vector2.Dot(d, ax)) > hx + rh.X * MathF.Abs(ax.X) + rh.Y * MathF.Abs(ax.Y)) return false;
        if (MathF.Abs(Vector2.Dot(d, ay)) > hy + rh.X * MathF.Abs(ay.X) + rh.Y * MathF.Abs(ay.Y)) return false;
        return true;
    }
}
