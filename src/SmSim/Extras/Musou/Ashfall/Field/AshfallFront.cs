using System.Globalization;
using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>A rule of a front gate (A on the empty frame, B on the final collision): its name, whether it passed, the value measured and the threshold.</summary>
public sealed record AshfallGateRule(string Name, bool Passed, string Value, string Threshold)
{
    public override string ToString() => $"[{(Passed ? "pass" : "FAIL")}] {Name}: {Value} (needs {Threshold})";
}

/// <summary>A failed front gate: the band, the gate and every rule that failed (the build stops here instead of making a bad map).</summary>
public sealed class AshfallGateException(string message) : InvalidOperationException(message);

/// <summary>
/// The Musou front pilot (the owner-approved plan of 2026-11-16; docs/musou-ashfall-graybox.md "Front pilot"): lane B's three positions as three bands, wasteland, outskirts and urban core, each its own 480 m battlefield.
/// One main avenue, 24 m wide, runs south to north at x = 0 through every band: through the Imperial base at the south edge (its gates opened to the avenue's width) and through the stronghold (a north gate, the keep split into two
/// wings) to the north edge. The order of a band's build: the frame (boundary, avenue anchors, base, stronghold), the skeleton (avenue, points, side corridors, links), gate A on the empty frame, then the fabric from the band's profile
/// (<see cref="AshfallBandProfile"/>; anything that meets a protected area, a corridor, a clear zone or a sightline band is refused), the kit pass with every standing rule, and gate B on the final collision. Every place and size ours.
/// </summary>
public static class AshfallFront
{
    /// <summary>The front's version (salts the bands' and the composed world's hashes; raised with any change of their geometry, points, areas or spawns).</summary>
    public const uint Version = 1;
    /// <summary>The bands of the front and the distance between their centres in the composed world (m, along x; the server cannot change maps, so the three battlefields stand side by side in one world).</summary>
    public const int BandCount = 3;
    public const float BandSpacing = 600f;
    /// <summary>The layout seed of the shipped test map (ours).</summary>
    public const uint DefaultSeed = 16;
    /// <summary>Deterministic retries of gate A (each moves the skeleton's free lines) before the build fails naming the rules.</summary>
    public const int GateRetries = 8;
    /// <summary>Half the main avenue's width plus the roadway rule's margin (m): an authored piece of the base or the stronghold that meets this strip is left out of the through variants.</summary>
    public const float ThroughHalf = 12.5f;
    /// <summary>The tallest a building with walkable upper floors is drawn (m; ours: the outskirts' 10-13 m kept their AC-051 flights, the core's 13-26 m lost most to the dead-end rule).</summary>
    public const float ShellHeight = 13f;
    /// <summary>The depth of a facade in the core's frontage row (m; ours: the city's facades were 3 m, a little more holds a facade model at street-wall height).</summary>
    public const float FacadeDepth = 5f;
    /// <summary>The core's street wall pieces between the frontage buildings (m; ours: over a Marine's head, so the street reads closed between its breaches; 2.4 m before F5).</summary>
    public const float StreetWallHeight = 6f;
    /// <summary>The frontage's margin from the reserved places (m; the owner, 2026-11-16: "denser instead of reducing reserved space": the corridors, assembly areas, plazas and clear zones stay; the frontage stands this close; the other fabric keeps the profile's route buffer, 2.5 to 4 m). Over the roadway rule's 0.5 m.</summary>
    public const float FrontBuffer = 1.5f;

    public static readonly Vector2 BaseCentre = new(0f, -188f), BaseGate = new(0f, -160f), StrongholdCentre = new(0f, 138f), StrongholdGate = new(0f, 112f);

    /// <summary>The band's x offset in the composed world.</summary>
    public static float OffsetOf(int band) => band * BandSpacing;

    /// <summary>
    /// The band the client draws (review of 2026-11-16): the band whose square (and 20 m beyond its boundary) holds the player at world <paramref name="at"/>, or -1 when the player stands outside every band (the Stormbird, the Firing Range,
    /// the flight) or <paramref name="cameraInHub"/> (a band's outside scenery reaches 1.2 km and would bury the hub and the range).
    /// </summary>
    public static int BandShown(Vector3 at, bool cameraInHub)
    {
        if (cameraInHub) return -1;
        int band = BandAt(at.X);
        return MathF.Abs(at.X - OffsetOf(band)) <= AshfallMap.Half + 20f && MathF.Abs(at.Y) <= AshfallMap.Half + 20f ? band : -1;
    }

    /// <summary>The band whose square holds world x (nearest centre, clamped).</summary>
    public static int BandAt(float x) => Math.Clamp((int)MathF.Round(x / BandSpacing), 0, BandCount - 1);

    /// <summary>The hash salt of band <paramref name="band"/>'s level ("W" in the high byte, the band in the next) and of the composed world.</summary>
    public static uint HashVersionOf(int band) => 0x57000000u | ((uint)band << 16) | Version;
    public const uint ComposedHashVersion = 0x57FF0000u | Version;

    public static AshfallLayout LayoutOf(int band) => band switch { 0 => AshfallLayout.FrontWasteland, 1 => AshfallLayout.FrontOutskirts, _ => AshfallLayout.FrontCore };

    /// <summary>The band of a front layout (-1 for any other).</summary>
    public static int BandOf(AshfallLayout layout) => layout switch { AshfallLayout.FrontWasteland => 0, AshfallLayout.FrontOutskirts => 1, AshfallLayout.FrontCore => 2, _ => -1 };

    public static bool IsFrontBand(this AshfallLayout layout) => BandOf(layout) >= 0;

    /// <summary>The run's band for a battle at war node <paramref name="node"/> (1 to 3; the final assault is node 3): lane B's positions 1, 2, 3 are the wasteland, the outskirts and the core; lanes A and C start at node 2, the outskirts.</summary>
    public static int BandForNode(int node) => Math.Clamp(node - 1, 0, BandCount - 1);

    /// <summary>Whether an authored piece's collision bounds meet the through road (the avenue's strip across the base and the stronghold).</summary>
    public static bool InThroughRoad(Vector3 min, Vector3 max) => min.X < ThroughHalf && max.X > -ThroughHalf && (max.Y < -150f || min.Y > 96f);

    static readonly Lazy<AshfallFieldPlan>[] Defaults = [.. Enumerable.Range(0, BandCount).Select(b => new Lazy<AshfallFieldPlan>(() => Plan(b, DefaultSeed), LazyThreadSafetyMode.ExecutionAndPublication))];

    /// <summary>The plan of band <paramref name="band"/> with the default seed (built once).</summary>
    public static AshfallFieldPlan Default(int band) => Defaults[band].Value;

    // ---------------------------------------------------------------- the frame and the skeleton

    /// <summary>The skeleton's free lines (m, band frame): the flank avenues' x, the south avenue's y, the cross avenue's y (B on it), the points A and C's y on the flanks, the north cross's y.</summary>
    public sealed record Skeleton(int Band, int Attempt, float FlankX, float SouthY, float CrossY, float PointY, float NorthY)
    {
        public Vector2 A => new(-FlankX, PointY);
        public Vector2 B => new(0f, CrossY);
        public Vector2 C => new(FlankX, PointY);
    }

    /// <summary>The skeleton of a band for a seed and an attempt (attempt 0 is the band's own lines; each retry moves them by a seeded step).</summary>
    public static Skeleton SkeletonOf(int band, uint seed, int attempt)
    {
        var (f, s, c, p, n) = band switch
        {
            0 => (150f, -96f, 20f, -20f, 84f),   // the wasteland: B north of the middle, A and C on the flanks south of it (long views across the trench field)
            1 => (150f, -96f, 0f, 50f, 84f),     // the outskirts: B at the crossing, A and C up the flanks
            _ => (140f, -96f, 10f, 84f, 84f),    // the core: A and C where the north cross meets the flanks (three plazas); B north of the middle so the avenue south of it runs between street walls (F5)
        };
        if (attempt == 0 && seed == DefaultSeed) return new(band, 0, f, s, c, p, n);
        uint h = AshfallBuilder.Mix(seed * 0x9E3779B1u + (uint)band * 0x85EBCA6Bu, (uint)attempt);
        float df = new[] { -10f, 0f, 10f }[h % 3u], dc = new[] { -10f, -5f, 0f, 5f, 10f }[(h >> 4) % 5u], dp = new[] { -10f, 0f, 10f }[(h >> 8) % 3u], dn = new[] { -4f, 0f }[(h >> 12) % 2u];
        float north = n + dn;
        return new(band, attempt, f + df, s, c + dc, band == 2 ? north : p + dp, north);
    }

    /// <summary>The fixed protected areas of every band (the base, the stronghold, the twin towers) and the fortresses' aprons (draft 13's, the through road's strips inside them).</summary>
    static readonly (string, Vector4)[] FixedProtected =
    [
        ("Imperial base", new(-44f, -224f, 44f, -156f)),
        ("stronghold", new(-84f, 96f, 84f, 226f)),
        ("stronghold twin towers", new(-142f, 126f, -70f, 166f)),
    ];

    static readonly (string, Vector4)[] Aprons =
    [
        ("stronghold apron north", new(-110f, 224f, 110f, 240f)),
        ("stronghold apron west", new(-104f, 211f, -80f, 240f)),
        ("stronghold apron east", new(80f, 211f, 104f, 240f)),
        ("compound apron south", new(-60f, -240f, 60f, -224f)),
    ];

    /// <summary>The empty frame's plan for a skeleton: points, start areas, assembly areas, corridors, routes, protected areas and spawns; no fabric.</summary>
    public static AshfallFieldPlan FramePlan(AshfallBandProfile profile, Skeleton k, uint seed)
    {
        const float top = AshfallMap.BlockTop;
        float w = profile.CorridorWidth, F = k.FlankX;
        ControlPoint[] points = [new("A", new(k.A.X, k.A.Y, top), 14f, 8f), new("B", new(k.B.X, k.B.Y, top), 16f, 8f), new("C", new(k.C.X, k.C.Y, top), 14f, 8f)];
        StartArea[] areas =
        [
            new(Team.SpaceMarines, new(BaseCentre.X, BaseCentre.Y, top), new(64f, 48f, 8f), 0f),
            new(Team.Chaos, new(StrongholdCentre.X, StrongholdCentre.Y, top), new(40f, 36f, 8f), 0f),
        ];
        Vector2 sj = new(0f, k.SouthY), nj = new(0f, k.NorthY), sw = new(-F, k.SouthY), se = new(F, k.SouthY), cw = new(-F, k.CrossY), ce = new(F, k.CrossY), nw = new(-F, k.NorthY), ne = new(F, k.NorthY);
        var corridors = new List<AshfallCorridor>
        {
            new("main avenue through the base", new(0f, -227f), sj, w),        // from the south edge (the box ends 1 m short of the boundary wall) through the Imperial compound (its gates opened to the avenue's width)
            new("main avenue south", sj, k.B, w),
            new("main avenue north", k.B, nj, w),
            new("main avenue through the stronghold", nj, new(0f, 227f), w),   // through the stronghold (the south and north gates, between the keep's two wings) to the north edge
            new("south avenue west", sj, sw, w), new("south avenue east", sj, se, w),
            new("north cross west", nj, nw, w), new("north cross east", nj, ne, w),
            new("cross avenue west", k.B, cw, w), new("cross avenue east", k.B, ce, w),
        };
        // The flank avenues from the south avenue to the north cross, split at the points (and at the cross avenue where it meets them below or above the points).
        foreach (var (side, x) in new[] { ("west", -F), ("east", F) })
        {
            var ys = new SortedSet<float> { k.SouthY, k.PointY, k.CrossY, k.NorthY }.ToArray();
            for (int i = 0; i + 1 < ys.Length; i++)
                if (ys[i + 1] - ys[i] > 1f) corridors.Add(new($"{side} flank {i}", new(x, ys[i]), new(x, ys[i + 1]), w));
        }
        var a0 = new Vector2(80f, 40f);
        (string, Vector4)[] assembly =
        [
            ("Imperial assembly", new(-a0.X / 2f, -150f, a0.X / 2f, -150f + a0.Y)),   // north of the compound's north wall, on the avenue
            ("Ork assembly", new(-a0.X / 2f, 92f - a0.Y, a0.X / 2f, 92f)),        // in front of the stronghold's south gate (the full 80 x 40 m; drafts 11 to 13 had 80 x 32)
        ];
        Vector2 b0 = BaseCentre, sh = StrongholdCentre;
        var routes = new List<AshfallRoute>
        {
            new("base to A", "base", "A", [b0, BaseGate, sj, sw, k.A]),
            new("base to B", "base", "B", [b0, BaseGate, k.B]),
            new("base to C", "base", "C", [b0, BaseGate, sj, se, k.C]),
            new("base to A (cross avenue)", "base", "A", [b0, BaseGate, k.B, cw, k.A]),
            new("base to C (cross avenue)", "base", "C", [b0, BaseGate, k.B, ce, k.C]),
            new("stronghold to A", "stronghold", "A", [sh, StrongholdGate, nj, nw, k.A]),
            new("stronghold to B", "stronghold", "B", [sh, StrongholdGate, k.B]),
            new("stronghold to C", "stronghold", "C", [sh, StrongholdGate, nj, ne, k.C]),
            new("A to B", "A", "B", [k.A, cw, k.B]),
            new("B to C", "B", "C", [k.B, ce, k.C]),
            new("A to C", "A", "C", [k.A, nw, ne, k.C]),
        };
        // a point on a junction (the core's A and C on the north cross) makes a waypoint twice: once is enough
        routes = [.. routes.Select(r => r with { Path = [.. r.Path.Where((q, i) => i == 0 || Vector2.DistanceSquared(q, r.Path[i - 1]) > 1e-4f)] })];
        var prot = new List<(string, Vector4)>(FixedProtected);
        prot.AddRange(assembly);
        prot.AddRange(Aprons);
        Vector3 P(Vector2 q) => new(q.X, q.Y, top);
        Vector3[] spawns =
        [
            areas[0].Center, areas[1].Center, .. points.Select(q => q.Center),
            P(sj), P(nj), P(sw), P(se), P(cw), P(ce), P(nw), P(ne), P(new(0f, -160f)), P(new(0f, -60f)), P(new(0f, 50f)),
            P(new(-232f, -232f)), P(new(232f, -232f)), P(new(-232f, 232f)), P(new(232f, 232f)),
        ];
        var param = ParamsOf(profile, seed);
        var districts = new List<AshfallDistrict> { new(profile.Name, [new(-226f, -226f, 226f, 226f)], profile.CoverageMax, profile.Spacing) };
        int band = (int)profile.Kind;
        return new AshfallFieldPlan(param, points, areas, assembly, corridors, routes, districts, [], prot, spawns, [], Aprons)
        { Layout = LayoutOf(band), HashVersion = HashVersionOf(band), Band = band };
    }

    /// <summary>The field parameters a band's plan carries (the kit pass and the battlefield measure read them).</summary>
    public static AshfallFieldParams ParamsOf(AshfallBandProfile profile, uint seed) => new()
    {
        MinArmyWidth = profile.MinArmyWidth, CorridorWidth = profile.CorridorWidth, RouteBuffer = profile.RouteBuffer, ObjectiveClear = profile.ObjectiveClear,
        ApproachCount = profile.Approaches, ApproachSeparation = profile.ApproachSeparation, AssemblySize = new(80f, 40f),
        DensityCathedral = profile.CoverageMax, DensityManufactorum = profile.CoverageMax, DensityHab = profile.CoverageMax, DensityNoMansLand = profile.CoverageMax,
        IslandGap = profile.IslandGap, ClusterGap = profile.ClusterGap, TallHeight = profile.TallHeight, PlatformTarget = profile.PlatformTarget,
        LayoutSeed = seed, DressSeed = 1, SurfaceSeed = 1,
    };

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AshfallLevel, AshfallFieldPlan> Frames = new();

    /// <summary>
    /// The frame plan of a built band level, read back from it (no gate A, no fabric, no draft-10 build: the client calls this on its main thread after a join from the layout cache): the skeleton's lines from the points
    /// (A at (-F, PointY), B at (0, CrossY)) and the frame's spawn markers (the sixth is the south junction, the seventh the north one), any seed. Its corridors, aprons, assembly areas and protected areas are the band's;
    /// its fabric is empty and its layout seed the default (neither is read from a built level). Null for a level that is not a front band.
    /// </summary>
    public static AshfallFieldPlan? FrameOf(AshfallLevel level)
    {
        int band = BandOf(level.Layout);
        if (band < 0 || level.Points.Count < 3 || level.Spawns.Count < 7) return null;
        return Frames.GetValue(level, l =>
        {
            var k = new Skeleton(band, -1, -l.Points[0].Center.X, l.Spawns[5].Y, l.Points[1].Center.Y, l.Points[0].Center.Y, l.Spawns[6].Y);
            return FramePlan(AshfallBandProfile.Front[band], k, DefaultSeed) with { Skeleton = k };
        });
    }

    // ---------------------------------------------------------------- gate A (the empty frame)

    /// <summary>
    /// Gate A on the empty frame (before any fabric): the plan's own rules (the avenue 24 m at x = 0 at both edges, every corridor at least the army's width, two planned approaches 60 degrees apart at each point, clear zones off the frame,
    /// assembly areas of 80 x 40 m, planned travel within 120 s), then the battlefield measure on the frame's collision (the boundary, the through base and the through stronghold with nothing else): army widths, approaches, protected areas
    /// and travel along real walks.
    /// </summary>
    public static List<AshfallGateRule> GateA(AshfallFieldPlan frame, AshfallLevel frameLevel)
    {
        var rules = new List<AshfallGateRule>();
        var p = frame.Params;
        string M(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        // The avenue at both edges.
        foreach (float edge in new[] { -AshfallMap.Half + 2f, AshfallMap.Half - 2f })
        {
            var at = new Vector2(0f, edge);
            var hit = frame.Corridors.Where(k => k.Contains(at)).ToList();
            float width = hit.Count == 0 ? 0f : hit.Max(k => k.Width);
            bool centred = hit.Any(k => MathF.Abs(k.A.X) < 1e-3f && MathF.Abs(k.B.X) < 1e-3f);
            rules.Add(new($"main avenue at the {(edge < 0f ? "south" : "north")} edge (x = 0)", centred && width >= 24f, $"{M(width)} m{(centred ? "" : ", not at x = 0")}", ">= 24.0 m at x = 0"));
        }
        foreach (var k in frame.Corridors) if (k.Width < p.MinArmyWidth) rules.Add(new($"corridor {k.Name} width", false, $"{M(k.Width)} m", $">= {M(p.MinArmyWidth)} m"));
        rules.Add(new("every corridor at least the army's width", frame.Corridors.All(k => k.Width >= p.MinArmyWidth), $"narrowest {M(frame.Corridors.Min(k => k.Width))} m", $">= {M(p.MinArmyWidth)} m"));
        // Planned approaches.
        foreach (var pt in frame.Points)
        {
            var c = new Vector2(pt.Center.X, pt.Center.Y);
            var bearings = new List<float>();
            foreach (var k in frame.ApproachesOf(pt))
            {
                foreach (var end in new[] { k.A, k.B })
                {
                    if (Vector2.Distance(end, c) < pt.Radius + p.ObjectiveClear) continue;
                    var d = end - c;
                    bearings.Add((MathF.Atan2(d.Y, d.X) * 180f / MathF.PI + 360f) % 360f);
                }
            }
            var picked = new List<float>();
            foreach (float b in bearings.OrderBy(x => x)) if (picked.All(q => Sep(q, b) >= p.ApproachSeparation)) picked.Add(b);
            rules.Add(new($"point {pt.Name}: planned approaches", picked.Count >= p.ApproachCount, $"{picked.Count} (bearings {string.Join(", ", picked.Select(b => b.ToString("0", CultureInfo.InvariantCulture)))})", $">= {p.ApproachCount}, {p.ApproachSeparation:0} degrees apart"));
        }
        // Clear zones off the frame's fixed pieces and inside the square.
        foreach (var (name, c, r) in frame.ClearZones)
        {
            var bad = frame.Protected.Where(z => DiscMeetsRect(c, r, z.Rect)).Select(z => z.Name).ToList();
            bool inside = MathF.Abs(c.X) + r <= 226f && MathF.Abs(c.Y) + r <= 226f;
            rules.Add(new($"point {name}: clear zone off the frame and the protected areas", bad.Count == 0 && inside, bad.Count == 0 ? (inside ? $"r {M(r)} m clear" : "outside the square") : "meets " + string.Join(", ", bad), "no overlap, inside the 226 m band"));
        }
        foreach (var (name, r) in frame.Assembly)
            rules.Add(new($"{name} size", r.Z - r.X >= 80f - 1e-3f && r.W - r.Y >= 40f - 1e-3f, $"{M(r.Z - r.X)} x {M(r.W - r.Y)} m", ">= 80.0 x 40.0 m"));
        // Planned travel (the shortest planned route from each base to each point).
        foreach (var to in new[] { "A", "B", "C" })
        {
            float bl = frame.Routes.Where(r => r.From == "base" && r.To == to).Min(r => r.Length), sl = frame.Routes.Where(r => r.From == "stronghold" && r.To == to).Min(r => r.Length);
            rules.Add(new($"planned reinforcement base to {to} (Guard army, {AshfallBattlefield.GuardArmySpeed} m/s)", bl / AshfallBattlefield.GuardArmySpeed <= p.MaxReinforceSeconds, $"{bl / AshfallBattlefield.GuardArmySpeed:0} s ({bl:0} m)", $"<= {p.MaxReinforceSeconds:0} s"));
            rules.Add(new($"planned wave stronghold to {to} (Ork army, {AshfallBattlefield.OrkArmySpeed} m/s)", sl / AshfallBattlefield.OrkArmySpeed <= p.MaxWaveSeconds, $"{sl / AshfallBattlefield.OrkArmySpeed:0} s ({sl:0} m)", $"<= {p.MaxWaveSeconds:0} s"));
        }
        // The measure on the frame's own collision.
        var bf = new AshfallBattlefield(frame.Layout, frameLevel, new AshfallDressing([], [], 0u), frame);
        foreach (var c in bf.Constraints.Where(IsFormationRule)) rules.Add(new("frame: " + c.Name, c.Passed, c.Value, c.Threshold));
        rules.AddRange(ThroughRoad(bf.Grid, frame.Params.MinArmyWidth).Select(r => r with { Name = "frame: " + r.Name }));
        return rules;
    }

    /// <summary>Whether a battlefield constraint is one of the formation rules the gates enforce (widths, approaches, protected areas, travel); the rest (landmarks, high ground, scouting, density) are reported.</summary>
    public static bool IsFormationRule(AshfallBattlefield.Constraint c) =>
        c.Name.StartsWith("army route ", StringComparison.Ordinal) || c.Name.StartsWith("route ", StringComparison.Ordinal) || c.Name.StartsWith("protected: ", StringComparison.Ordinal)
        || c.Name.StartsWith("reinforcement time ", StringComparison.Ordinal) || c.Name.StartsWith("wave time ", StringComparison.Ordinal) || c.Name.EndsWith(": distinct formation-capable approaches", StringComparison.Ordinal);

    /// <summary>The through road's width along x = 0 from edge to edge (every metre, inside the base and the stronghold too): its narrowest point.</summary>
    public static List<AshfallGateRule> ThroughRoad(AshfallGrid grid, float minWidth)
    {
        float min = float.MaxValue, at = 0f, baseMin = float.MaxValue, holdMin = float.MaxValue;
        for (float y = -AshfallMap.Half + 2f; y <= AshfallMap.Half - 2f; y += 1f)
        {
            float wdt = grid.WidthAcross(new Vector2(0f, y), Vector2.UnitY);
            if (wdt < min) { min = wdt; at = y; }
            if (y < -150f) baseMin = MathF.Min(baseMin, wdt);
            if (y > 96f) holdMin = MathF.Min(holdMin, wdt);
        }
        string M(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        return
        [
            new("through road (x = 0, edge to edge): width at its narrowest point", min >= minWidth, $"{M(min)} m at y {at:0}", $">= {M(minWidth)} m"),
            new("through road through the Imperial base", baseMin >= minWidth, $"{M(baseMin)} m", $">= {M(minWidth)} m"),
            new("through road through the stronghold (south gate, keep wings, north gate)", holdMin >= minWidth, $"{M(holdMin)} m", $">= {M(minWidth)} m"),
        ];
    }

    // ---------------------------------------------------------------- the plan

    /// <summary>
    /// The plan of band <paramref name="band"/> for <paramref name="seed"/> (deterministic: integer hashes, exact arithmetic, total-order sorts): the frame and its skeleton until gate A passes (up to <see cref="GateRetries"/> attempts; then an
    /// <see cref="AshfallGateException"/> naming the failed rules), then the fabric from the band's profile.
    /// </summary>
    public static AshfallFieldPlan Plan(int band, uint seed)
    {
        var profile = AshfallBandProfile.Front[band];
        var tried = new List<string>();
        for (int attempt = 0; attempt <= GateRetries; attempt++)
        {
            var k = SkeletonOf(band, seed, attempt);
            var frame = FramePlan(profile, k, seed);
            var rules = GateA(frame, AshfallMap.BuildFrontFrame(frame));
            var failed = rules.Where(r => !r.Passed).ToList();
            if (failed.Count == 0)
            {
                var plan = Fabric(profile, frame, seed);
                var notes = new List<string>(plan.Notes);
                notes.Insert(0, $"front band {band} ({profile.Name}), seed {seed}: skeleton attempt {attempt} (flanks x = +-{k.FlankX:0}, south avenue y = {k.SouthY:0}, cross avenue y = {k.CrossY:0}, A and C y = {k.PointY:0}, north cross y = {k.NorthY:0}); gate A passed {rules.Count} rules" + (tried.Count > 0 ? "; earlier attempts failed: " + string.Join("; ", tried) : ""));
                return plan with { Notes = notes, GateA = rules, Skeleton = k };
            }
            tried.Add($"attempt {attempt}: {string.Join(", ", failed.Select(f => f.Name + " " + f.Value))}");
        }
        throw new AshfallGateException($"front band {band} ({profile.Name}), seed {seed}: gate A (the empty frame) failed after {GateRetries} retries: {string.Join(" | ", tried)}");
    }

    /// <summary>The fabric on a frame that passed gate A: landmarks first (their sightlines then protected), then islands or rows from the profile, the last fifth to the north blending toward the next band.</summary>
    static AshfallFieldPlan Fabric(AshfallBandProfile profile, AshfallFieldPlan frame, uint seed)
    {
        var notes = new List<string>();
        var p = frame.Params;
        var structures = new List<AshfallFieldStructure>();
        var placed = new List<Vector4>();
        var why = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var points = frame.Points;
        var plazas = profile.Plazas > 0 ? points.Take(profile.Plazas).Select(q => (new Vector2(q.Center.X, q.Center.Y), profile.PlazaRadius)).ToList() : [];

        string? FreeWhy(Vector4 r, float gap, float buf)
        {
            if (r.X < -226f || r.Y < -226f || r.Z > 226f || r.W > 226f) return "boundary band";
            foreach (var (zn, z) in frame.Protected) if (r.X < z.Z + buf && r.Z > z.X - buf && r.Y < z.W + buf && r.W > z.Y - buf) return "protected (" + zn + ")";
            foreach (var pt in points)
                if (DiscMeetsRect(new(pt.Center.X, pt.Center.Y), pt.Radius + p.ObjectiveClear + buf, r)) return "a point's clear zone";
            foreach (var (c, rad) in plazas) if (DiscMeetsRect(c, rad, r)) return "a plaza";
            foreach (var k in frame.Corridors) if (AshfallField.RectMeetsBox(r, k.Box(buf))) return "a corridor";
            foreach (var o in placed) if (AshfallPlacer.Gap(r, o) < gap) return "a neighbour";
            return null;
        }
        // the fabric keeps the profile's route buffer off the reserved places; the frontage stands closer (FrontBuffer: the reserved places themselves are unchanged)
        bool Free(Vector4 r, float gap, float buf = -1f)
        {
            string? w = FreeWhy(r, gap, buf < 0f ? p.RouteBuffer : buf);
            if (w is not null) why[w] = why.GetValueOrDefault(w) + 1;
            return w is null;
        }
        void Add(AshfallFieldStructure s)
        {
            if (s.Family == "shell" && s.Size.Z > ShellHeight) s = s with { Size = s.Size with { Z = ShellHeight } };   // a model with walkable upper floors keeps a height its kit stairs reach (draft 13: taller AC-051s put their flight tops on a wall)
            structures.Add(s); placed.Add(AshfallField.Footprint(s.Centre, s.Size, s.YawStep));
        }

        // 1. The through variants' authored pieces (outside every rule but their own: the avenue's strip is kept clear by the roadway rule): the compound's walls with 24 m gates north and south, the stronghold's north gate jamb and the
        // keep's two wings, the avenue between them.
        foreach (var s in ThroughPieces()) Add(s);

        // 2. Landmarks (the profile's slots).
        foreach (var (name, fam, at, size) in profile.Landmarks)
        {
            var r = AshfallField.Footprint(at, size, 0);
            if (!Free(r, p.ClusterGap)) { notes.Add($"landmark {name} does not fit at ({at.X}, {at.Y}) and is left out"); continue; }
            Add(new(name, fam, at, size, 0, profile.Name, Landmark: true));
        }
        // Landmark sightlines to the two nearest points: a structure that would rise into the line from the point's eye to the landmark's top is refused.
        var bands = new List<(Vector2 A, Vector2 B, float Top)>();
        foreach (var s in structures.Where(s => s.Landmark && s.Size.Z >= 12f))
            foreach (var pt in points.OrderBy(q => Vector2.DistanceSquared(new Vector2(q.Center.X, q.Center.Y), s.Centre)).ThenBy(q => q.Name, StringComparer.Ordinal).Take(2))
            {
                var a = new Vector2(pt.Center.X, pt.Center.Y); var dir = Vector2.Normalize(s.Centre - a);
                bands.Add((a, s.Centre - dir * MathF.Max(s.Size.X, s.Size.Y), AshfallMap.BlockTop + s.Size.Z * 0.85f));
            }
        bool Sight(Vector4 r, float height)
        {
            if (height <= p.SightBlockHeight || !bands.Any(b => SightBlocked(b.A, b.B, b.Top, r, height, p.SightBand / 2f))) return false;
            why["a landmark's sightline"] = why.GetValueOrDefault("a landmark's sightline") + 1;
            return true;
        }

        // The land the coverage is measured against: the square less the protected areas, the corridors and the clear zones (sampled every 4 m).
        float land = 0f;
        for (float x = -224f; x < 226f; x += 4f)
            for (float y = -224f; y < 226f; y += 4f)
                if (FreeWhy(new Vector4(x - 0.01f, y - 0.01f, x + 0.01f, y + 0.01f), -1f, p.RouteBuffer) is null) land += 16f;
        float covered = 0f;
        float Cover(IEnumerable<AshfallFieldStructure> g) => g.Sum(s => s.Size.X * s.Size.Y);
        // the band-wide cover a new piece may still add to: the profile's, toward the next band's in the north fifth, but never past the profile's own ceiling outside the core (F6: the frontage fills the south and middle first)
        float Target(float y) => MathF.Min(profile.Coverage + (profile.Next.Coverage - profile.Coverage) * AshfallBandProfile.BlendAt(y), profile.Fabric == AshfallFabric.Rows ? 1f : profile.CoverageMax - 0.005f);

        // 3. Frontage (F5, F6: the owner, 2026-11-16, "Make the buildings around the roads denser instead of reducing reserved space"): rows of buildings on every corridor's edges, then round the assembly areas and the plazas (or the
        // points' clear zones), FrontBuffer off them. The core's frontage is a street wall (facades and wall pieces between them, a breach every 30 to 50 m, back rows behind 6 m alleys); the outskirts' a looser row (buildings with upper
        // floors, a few gaps). The reserved places (the corridors, assembly areas, plazas and clear zones) are as before; only the margin the frontage keeps from them is smaller.
        int rows = 0, breaches = 0, walls = 0, row = 0;
        bool street = profile.Fabric == AshfallFabric.Rows;
        void Frontage(string name, Vector2 a, Vector2 b, Vector2 outward, float baseLine, int tiers, float trim)
        {
            var dir = Vector2.Normalize(b - a);
            bool alongX = MathF.Abs(dir.X) > 0.5f;
            int yaw = alongX ? 0 : 6;
            float total = Vector2.Distance(a, b);
            for (int tier = 0; tier < tiers; tier++)
            {
                uint key = AshfallBuilder.Mix(seed * 0x9E37u + 41u, (uint)(row++));
                float line = baseLine + tier * (18f + 6f);
                float t = trim, len = total - trim, sinceBreach = 0f, nextBreach = profile.BreachMin + key % (uint)(profile.BreachMax - profile.BreachMin + 1f);
                int n = 0;
                rows++;
                while (t < len - 6f)
                {
                    uint h = AshfallBuilder.Mix(key, (uint)n++);
                    var at = a + dir * t;
                    var prof = Pick(profile, at.Y, h);
                    float wdt = new[] { 18f, 22f, 26f }[h % 3u], depth = new[] { 14f, 16f, 18f }[(h >> 4) % 3u];
                    if (t + wdt > len) wdt = len - t;
                    if (wdt < 10f) break;
                    float height = Height(prof, h, at);
                    string fam = Family(prof, h);
                    // the core's frontage: its stairless buildings are facades (the street wall reads as one: the kit's stairless masses are round heaps); the back rows keep the masses
                    if (street && tier == 0 && fam == "block") { fam = "facade"; depth = FacadeDepth; }
                    if (!street && ((h >> 19) ^ (h >> 7)) % 100u < (uint)(profile.FrontageGapShare * 100f)) { t += wdt; continue; }   // the outskirts: a gap in the frontage
                    if (!street && covered >= profile.Coverage * land) break;   // the outskirts keep their 20-24 %: the fabric goes to the roads first (to the band's mid target), the rest of the land stays open
                    var centre = a + dir * (t + wdt / 2f) + outward * (line + depth / 2f);
                    var size = new Vector3(wdt, depth, height);
                    var fp = AshfallField.Footprint(centre, size, yaw);
                    if (Free(fp, tier == 0 ? 2.45f : 6f, tier == 0 ? FrontBuffer : p.RouteBuffer) && !Sight(fp, height))
                    {
                        Add(new($"{name} {(tier == 0 ? "row" : tier == 1 ? "back row" : "second back row")} {fam} {n}", fam, centre, size, yaw, profile.Name));
                        covered += wdt * depth;
                        sinceBreach += wdt;
                    }
                    else { t += 4f; continue; }
                    t += wdt;
                    if (tier >= 1 || !street) { t += street ? 6f : 2.5f + 1.5f * ((h >> 14) % 3u); continue; }   // the back rows and the outskirts: alleys only (the outskirts' 2.5 to 5.5 m)
                    if (sinceBreach >= nextBreach)
                    {
                        // a breach gap in the street wall (30 to 50 m apart): open ground through the row into the block behind
                        t += profile.BreachWidth; breaches++; sinceBreach = 0f;
                        nextBreach = profile.BreachMin + (h >> 8) % (uint)(profile.BreachMax - profile.BreachMin + 1f);
                        continue;
                    }
                    // a street wall piece between two buildings, 2.5 m breaks each side (over the 2.4 m narrow-gap rule)
                    float wl = 6f + 2f * ((h >> 12) % 3u);
                    var wc = a + dir * (t + 2.5f + wl / 2f) + outward * (line + 0.75f);
                    var wsize = new Vector3(wl, 1.5f, StreetWallHeight);
                    var wfp = AshfallField.Footprint(wc, wsize, yaw);
                    if (t + 2.5f + wl + 2.5f < len && Free(wfp, 2.45f, FrontBuffer)) { Add(new($"{name} street wall {walls++}", "wall", wc, wsize, yaw, profile.Name)); t += 2.5f + wl + 2.5f; sinceBreach += wl; }
                    else t += 6f;
                }
            }
        }
        // the four outward edges of a rectangle (x0, y0, x1, y1), lined from outside
        void Around(string name, Vector4 r, float off, int tiers)
        {
            Frontage($"{name} south", new(r.X, r.Y), new(r.Z, r.Y), new(0f, -1f), off, tiers, 0f);
            Frontage($"{name} north", new(r.X, r.W), new(r.Z, r.W), new(0f, 1f), off, tiers, 0f);
            Frontage($"{name} west", new(r.X, r.Y), new(r.X, r.W), new(-1f, 0f), off, tiers, 0f);
            Frontage($"{name} east", new(r.Z, r.Y), new(r.Z, r.W), new(1f, 0f), off, tiers, 0f);
        }
        if (profile.FrontageTiers > 0)
        {
            // the assembly areas' and the plazas' (or clear zones') edges: the base exit and B framed by buildings
            foreach (var (name, r) in frame.Assembly) Around(name, r, FrontBuffer + 0.1f, 1);
            foreach (var pt in points)
            {
                float rad = plazas.Count > 0 ? MathF.Max(profile.PlazaRadius, pt.Radius + p.ObjectiveClear) : pt.Radius + p.ObjectiveClear;
                var c = new Vector2(pt.Center.X, pt.Center.Y);
                Around($"point {pt.Name}", new Vector4(c.X - rad, c.Y - rad, c.X + rad, c.Y + rad), FrontBuffer + 0.1f, 1);
            }
            foreach (var k in frame.Corridors.Where(k => !k.Name.StartsWith("main avenue through", StringComparison.Ordinal)))
            {
                var dir = Vector2.Normalize(k.B - k.A); var left = new Vector2(-dir.Y, dir.X);
                Frontage($"{k.Name} left", k.A, k.B, left, k.Width / 2f + FrontBuffer + 0.1f, profile.FrontageTiers, 8f);
                Frontage($"{k.Name} right", k.A, k.B, -left, k.Width / 2f + FrontBuffer + 0.1f, profile.FrontageTiers, 8f);
            }
            notes.Add($"frontage: {rows} rows along the corridors and round the assembly areas and the points, {walls} street wall pieces, {breaches} breach gaps ({profile.BreachMin:0}-{profile.BreachMax:0} m apart, {profile.BreachWidth:0} m wide), {FrontBuffer} m off the reserved places");
        }

        // 4. Islands on a jittered grid in seeded order, until the coverage target (the last fifth to the north blends toward the next band's profile and coverage).
        var cands = new List<(uint Key, Vector2 At)>();
        uint ci = 0;
        for (float x = -226f + profile.Spacing / 2f; x < 226f; x += profile.Spacing)
            for (float y = -226f + profile.Spacing / 2f; y < 226f; y += profile.Spacing)
            {
                uint h = AshfallBuilder.Mix(seed * 0x9E37u + 17u + (uint)profile.Kind, ci++);
                cands.Add((h, new Vector2(x + ((h % 9u) - 4f), y + (((h >> 8) % 9u) - 4f))));
            }
        cands.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.At.X != b.At.X ? a.At.X.CompareTo(b.At.X) : a.At.Y.CompareTo(b.At.Y));
        int islands = 0, breached = 0;
        // Two passes: the profile's templates, then (still under the target) single pieces that fit the gaps the first pass left.
        // the fill pass also tries a grid offset by half the spacing (the gaps between the first grid's places)
        var offset = cands.Select(c => (Key: AshfallBuilder.Mix(c.Key, 0xF111u), At: c.At + new Vector2(profile.Spacing / 2f, profile.Spacing / 2f))).Where(c => MathF.Abs(c.At.X) < 226f && MathF.Abs(c.At.Y) < 226f).ToList();
        foreach (var (key, at, fill) in cands.Select(c => (c.Key, c.At, false)).Concat(cands.Concat(offset).Select(c => (c.Key, c.At, true))))
        {
            if (covered >= Target(at.Y) * land) continue;
            var prof = Pick(profile, at.Y, key);
            bool breachedBlock = !fill && profile.Fabric == AshfallFabric.Rows && breached < profile.BreachedBlocks && MathF.Abs(at.X) >= AshfallBandProfile.BreachedBlockOffset + 20f && ((key >> 20) % 3u) == 0u;
            var group = breachedBlock ? BreachedBlock(prof, key, at) : fill ? FillPiece(prof, key, at) : Template(prof, key, at);
            if (group.Count == 0) continue;
            var fp = group.Select(s => AshfallField.Footprint(s.Centre, s.Size, s.YawStep)).Aggregate(Union);
            float gap = fill ? prof.FillGap : prof.Fabric == AshfallFabric.Rows ? 6f : prof.IslandGap;   // the fill's single pieces may stand closer (a side street or an alley)
            if (!Free(fp, gap) || Sight(fp, group.Max(s => s.Size.Z))) continue;
            int q = 0;
            foreach (var s in group) Add(s with { Name = $"{prof.Name} {(breachedBlock ? "breached block" : "island")} {islands} {s.Family} {q++}", District = profile.Name });
            covered += Cover(group);
            islands++;
            if (breachedBlock) breached++;
        }
        notes.Add($"{profile.Name}: {islands} islands ({breached} walk-through breached blocks, never within {AshfallBandProfile.BreachedBlockOffset:0} m of the main avenue), {structures.Count} structures in all; drawn cover {covered / land:P0} of {land / 10000f:0.0} ha of free land (target {profile.CoverageMin:P0}-{profile.CoverageMax:P0}, the north fifth toward {profile.Next.Name}'s {profile.Next.Coverage:P0})");
        notes.Add($"refused places: {string.Join(", ", why.Select(kv => $"{kv.Key} {kv.Value}"))}");
        return frame with { Structures = structures, Notes = notes, FreeLand = land, Coverage = covered / land };
    }

    /// <summary>The profile a structure at <paramref name="y"/> takes: the band's own, or in the north fifth the next band's with the blend's share.</summary>
    static AshfallBandProfile Pick(AshfallBandProfile profile, float y, uint h)
    {
        float t = AshfallBandProfile.BlendAt(y);
        return t > 0f && ((h >> 24) % 100u) < (uint)(t * 100f) ? profile.Next : profile;
    }

    static float Height(AshfallBandProfile prof, uint h, Vector2 at)
    {
        float edge = AshfallMap.Half - MathF.Max(MathF.Abs(at.X), MathF.Abs(at.Y));
        if (edge < 70f && (h >> 16) % 7u == 0u) return prof.TallHeight;
        return prof.HeightMin + (prof.HeightMax - prof.HeightMin) * ((h >> 10) % 5u) / 4f;
    }

    /// <summary>A building's family: with the profile's upper-floor share a model with walkable upper floors (its kit stairs pass the stair rules or it is replaced), else a stairless mass.</summary>
    static string Family(AshfallBandProfile prof, uint h) => ((h >> 18) % 100u) < (uint)(prof.UpperFloorShare * 100f) ? "shell" : "block";

    static List<AshfallFieldStructure> Template(AshfallBandProfile prof, uint key, Vector2 at)
    {
        uint t = (key >> 4) % 100u;
        int yaw = (int)((key >> 12) % 2u) * 6;
        var list = new List<AshfallFieldStructure>();
        void Add(string fam, float dx, float dy, Vector3 size, int y = -1) => list.Add(new("", fam, at + new Vector2(dx, dy), size, y < 0 ? yaw : y, prof.Name));
        float low(uint s) => prof.LowMin + (prof.LowMax - prof.LowMin) * (s % 3u) / 2f;
        float g = prof.ClusterGap;
        switch (prof.Kind)
        {
            case AshfallBandKind.Wasteland:
                if (t < 35)
                {
                    // a trench line: two to four low wall segments in a row, 3 m breaks (over the narrow-gap rule)
                    int n = 2 + (int)((key >> 16) % 3u); float seg = 10f + 2f * ((key >> 18) % 3u), h = low(key >> 20);
                    bool x = yaw == 0;
                    for (int i = 0; i < n; i++) { float o = (i - (n - 1) / 2f) * (seg + 3f); Add("wall", x ? o : 0f, x ? 0f : o, new(seg, 1.5f, h)); }
                }
                else if (t < 55) { Add("berm", 0f, 0f, new(8f, 4f, 1.4f), 0); Add("berm", 8f + g, 2f, new(6f, 3f, 1.2f), 0); }                                   // a berm
                else if (t < 80) { float h = prof.HeightMin + (prof.HeightMax - prof.HeightMin) * ((key >> 20) % 3u) / 2f; Add("block", 0f, 0f, new(12f, 9f, h)); Add("rubble", 6f + 3f + g, 0f, new(5f, 3f, 1.4f), 0); }   // a low ruin and its fallen stone
                else if (t < 92) { Add("cover", 0f, 0f, new(4f, 1.5f, 1.2f)); Add("cover", 5f + g, 2f, new(4f, 1.5f, 1.2f)); }
                else Add("wreck", 0f, 0f, new(10f, 6f, 4.5f));
                break;
            case AshfallBandKind.Outskirts:
                if (t < 40)
                {
                    // a courtyard: two to four buildings round a 10 m yard
                    float s = 16f, o = s / 2f + 5f;
                    int k = 0;
                    foreach (var (dx, dy) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
                    {
                        uint b = AshfallBuilder.Mix(key, (uint)k++);
                        if (k > 2 && b % 3u == 0u) continue;
                        Add(Family(prof, b), dx * o, dy * o, new(s, 14f, prof.HeightMin + (prof.HeightMax - prof.HeightMin) * (b % 4u) / 3f));
                    }
                }
                else if (t < 70) { Add(Family(prof, key), 0f, 0f, new(22f, 16f, prof.HeightMin + (prof.HeightMax - prof.HeightMin) * ((key >> 20) % 4u) / 3f)); Add("rubble", 9f + 3f + g, 0f, new(5f, 3f, 1.4f), 0); }
                else if (t < 85) { Add("block", 0f, 0f, new(14f, 12f, 7f)); Add("cover", 0f, -(6f + 2f + g), new(4f, 1.5f, 1.2f), 0); }
                else { Add("block", 0f, 0f, new(15f, 3f, 10f)); Add("block", 0f, 3f + g + 3f, new(15f, 3f, 10f)); }
                break;
            default:   // the core between its rows: blocks behind the frontage
                if (t < 60) Add(Family(prof, key), 0f, 0f, new(24f, 18f, Height(prof, key, at)));
                else if (t < 85) { Add(Family(prof, key), -(8f + g / 2f), 0f, new(16f, 14f, Height(prof, key, at))); Add(Family(prof, key >> 3), 8f + g / 2f, 0f, new(16f, 14f, Height(prof, key >> 5, at))); }
                else { Add("rubble", 0f, 0f, new(6f, 4f, 1.4f), 0); Add("cover", 6f + g, 0f, new(5f, 1.5f, 1.2f), 0); }
                break;
        }
        return list;
    }

    /// <summary>The second pass's piece: one building (or, in the wasteland, one trench wall) sized to the gaps the templates left.</summary>
    static List<AshfallFieldStructure> FillPiece(AshfallBandProfile prof, uint key, Vector2 at)
    {
        int yaw = (int)((key >> 13) % 2u) * 6;
        return prof.Kind == AshfallBandKind.Wasteland
            ? [new("", "wall", at, new(10f, 1.5f, prof.LowMin + (prof.LowMax - prof.LowMin) * ((key >> 21) % 3u) / 2f), yaw, prof.Name)]
            : [new("", Family(prof, key >> 2), at, new(14f, 12f, prof.Kind == AshfallBandKind.Core ? Height(prof, key, at) : prof.HeightMin + (prof.HeightMax - prof.HeightMin) * ((key >> 21) % 4u) / 3f), yaw, prof.Name)];
    }

    /// <summary>A walk-through breached block (the core): two buildings with a 6 m passage between them and fallen stone at its mouth, never on the main route.</summary>
    static List<AshfallFieldStructure> BreachedBlock(AshfallBandProfile prof, uint key, Vector2 at)
    {
        int yaw = (int)((key >> 12) % 2u) * 6;
        bool x = yaw == 0;
        float h = Height(prof, key, at);
        return
        [
            new("", Family(prof, key), at + (x ? new Vector2(-14f, 0f) : new Vector2(0f, -14f)), new(22f, 16f, h), yaw, prof.Name),
            new("", Family(prof, key >> 7), at + (x ? new Vector2(14f, 0f) : new Vector2(0f, 14f)), new(22f, 16f, h), yaw, prof.Name),
        ];
    }

    /// <summary>
    /// The through variants' pieces (final size, band frame; authored, outside the fabric's rules): the compound's north and south walls rebuilt with 24 m gates on the avenue (draft 10's walls there met the avenue's strip) and the
    /// keep's two wings. The ring's north leg loses its middle to the avenue: the north gate is about 34 m (x -20 to 14), the owner's chosen width (2026-11-16; a jamb narrowing it to 24 m met the roadway rule) (draft 10's keep, 64 m across the avenue, is left out; the wings stand either side of the road, the keep tower against the west one).
    /// </summary>
    public static IEnumerable<AshfallFieldStructure> ThroughPieces()
    {
        const string d = "through";
        yield return new("through compound north wall west", "wallrun", new(-24.25f, -160.5f), new(23.5f, 3.4f, 3.8f), 0, d);
        yield return new("through compound north wall east", "wallrun", new(24.25f, -160.5f), new(23.5f, 3.4f, 3.8f), 0, d);
        yield return new("through compound south wall west", "wallrun", new(-17.75f, -215.5f), new(10.5f, 4.5f, 3.8f), 0, d);
        yield return new("through compound south wall east", "wallrun", new(17.75f, -215.5f), new(10.5f, 4.5f, 3.8f), 0, d);
        yield return new("through keep wing west", "keepwing", new(-21f, 175.5f), new(16f, 31f, 18f), 0, d);
        yield return new("through keep wing east", "keepwing", new(23.5f, 187.5f), new(21f, 55f, 18f), 0, d);
    }

    // ---------------------------------------------------------------- helpers

    static float Sep(float a, float b) { float d = MathF.Abs(a - b) % 360f; return d > 180f ? 360f - d : d; }

    static bool DiscMeetsRect(Vector2 c, float r, Vector4 z)
    {
        float dx = MathF.Max(MathF.Max(z.X - c.X, c.X - z.Z), 0f), dy = MathF.Max(MathF.Max(z.Y - c.Y, c.Y - z.W), 0f);
        return dx * dx + dy * dy < r * r;
    }

    static Vector4 Union(Vector4 a, Vector4 b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.Z, b.Z), MathF.Max(a.W, b.W));

    static bool SightBlocked(Vector2 a, Vector2 b, float topZ, Vector4 r, float height, float half)
    {
        float len = Vector2.Distance(a, b);
        int n = Math.Max(1, (int)len);
        float eye = AshfallMap.BlockTop + 1.6f, roof = AshfallMap.BlockTop + height + 1f;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            var q = Vector2.Lerp(a, b, t);
            float dx = MathF.Max(MathF.Max(r.X - q.X, q.X - r.Z), 0f), dy = MathF.Max(MathF.Max(r.Y - q.Y, q.Y - r.W), 0f);
            if (dx * dx + dy * dy < half * half && roof > eye + (topZ - eye) * t) return true;
        }
        return false;
    }
}
