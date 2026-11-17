using System.Numerics;
using SmSim.Extras.Musou.Ashfall;
using static SmSim.Extras.Musou.Ashfall.AshfallKind;

namespace SmSim.Extras.Warfront.Hq;

/// <summary>One Ork source (a place the assault's waves are fed from) and the defence stage it belongs to.</summary>
/// <param name="Name">The marker's name.</param>
/// <param name="Position">Where it stands.</param>
/// <param name="Stage">0: the assembly ground (all positions held); 1: the courtyard inside the gate (W has fallen); 2: the keep hall (K has fallen).</param>
public sealed record WarfrontHqSource(string Name, Vector3 Position, int Stage);

/// <summary>A place a defender (Guardsman) stands; <paramref name="DefencePosition"/> is the defence position it belongs to (0 W, 1 K, 2 V).</summary>
public sealed record WarfrontHqPost(string Name, Vector3 Position, int DefencePosition);

/// <summary>One named dimension of the map for its spec row (everything ours, an estimate).</summary>
public sealed record WarfrontHqDimension(string Name, float Value, string Units);

/// <summary>The HQ defense level and its named markers (<see cref="WarfrontHqMap.Build"/>).</summary>
public sealed record WarfrontHqBuild(
    AshfallLevel Level,
    Vector3 W, Vector3 K, Vector3 V, Vector3 LandingZone, Vector3 AssemblyCentre,
    IReadOnlyList<WarfrontHqSource> Sources, IReadOnlyList<WarfrontHqPost> Posts,
    IReadOnlyList<int> RoofParts, NavArea WalkArea)
{
    /// <summary>The three defence positions in the order they are held and fall: W, K, V.</summary>
    public IReadOnlyList<Vector3> Positions => [W, K, V];
}

/// <summary>
/// Warfront's HQ defense map (plan section 2, "Helm's Deep"; every size ours, an estimate): about 300 x 420 m, south to north: the Ork assembly ground; an approach ramp 30 m wide rising 12 m at about 8 degrees; a retaining
/// wall (12 m cliff, 14 m wall above it) with one gate 12 m wide and a walkable parapet reached by two stairs from the courtyard only; a courtyard and the keep (two 8 m doors in front, two 8 m openings behind); two roofed
/// cave tunnels (8 m wide, 8 m clear, joined by a cross passage halfway) into the last-stand chamber where the landing zone is. W (wall and gate), K (keep) and V (caves) are the three defence positions; each is a place defenders really stand and fight from.
/// Built with <see cref="AshfallBuilder"/> (read-only use), deterministic: no random numbers, no trigonometry, the same bits on every machine. The battle rules, the server wiring and the map name are W13b's.
/// </summary>
public static class WarfrontHqMap
{
    /// <summary>The level's version (hashed with the geometry): raise with any change of the generated geometry or markers.</summary>
    public const uint Version = 2;   // 2: the ramp's foot fans to 60 m and the wall's foot corners are chamfered (W13c)

    // ---- the plan (every number ours, an estimate; Dimensions lists them for spec/behavior.md)
    public const float HalfWidth = 150f, SouthEdge = -210f, NorthEdge = 210f;
    public const float AssemblyNorth = -130f;
    public const float RampWidth = 30f, RampRise = 12f, RampSouth = -130f, RampNorth = -46f;
    /// <summary>The ramp's lowest <see cref="FanLength"/> m fan out from <see cref="RampWidth"/> to <see cref="FanWidth"/> at the foot (two wedge ramps at the ramp's own slope; W13c, review 1 ruling 1; ours, an estimate).</summary>
    public const float FanWidth = 60f, FanLength = 25f, FanSideGradient = 0.5f, FanSkirtLength = 12f;
    /// <summary>The wall's foot corners (where the ramp's rim meets the fortress wall's south face) are cut off by a vertical diagonal face of this leg, so a queue at the wall slides to the gate instead of standing in a corner (W13c; ours, an estimate).</summary>
    public const float ChamferLeg = 6f;
    public const float PlateauZ = 12f;
    public const float WallSouth = -46f, WallNorth = -40f, WallTop = 26f, GateWidth = 12f, GateHeight = 10f;
    public const float StairRise = 14f, StairWidth = 4f, StairFootX = 40f, ParapetThickness = 0.5f, ParapetHeight = 1.2f;
    public const float CurtainX = 90f;
    public const float KeepHalfWidth = 50f, KeepSouth = 40f, KeepNorth = 100f, KeepWall = 4f, KeepTop = 26f, DoorWidth = 8f, DoorHeight = 8f;
    public const float TunnelWidth = 8f, TunnelClear = 8f, TunnelSouth = 100f, TunnelNorth = 150f, TunnelCentreX = 22f;
    public const float CrossSouth = 121f, CrossNorth = 129f, CrossClear = 8f;   // the passage between the two tunnels, halfway (review 1, item 5)
    public const float ChamberHalfWidth = 34f, ChamberNorth = 200f, ChamberClear = 10f, MassTop = 30f;

    /// <summary>The widths of every opening on the Ork route, metres (the ramp, the gate, the doors, the openings behind the hall, the tunnels): each at least 8.</summary>
    public static IReadOnlyList<WarfrontHqDimension> RouteWidths { get; } =
    [
        new("approach ramp width", RampWidth, "m"), new("gate width", GateWidth, "m"), new("keep door width (two)", DoorWidth, "m"),
        new("hall opening width (two, to the tunnels)", TunnelWidth, "m"), new("tunnel width (two)", TunnelWidth, "m"),
    ];

    /// <summary>Every dimension of the map, for the spec rows.</summary>
    public static IReadOnlyList<WarfrontHqDimension> Dimensions { get; } =
    [
        new("map width (x -150..150)", HalfWidth * 2f, "m"), new("map length (y -210..210)", NorthEdge - SouthEdge, "m"),
        new("assembly ground depth (y -210..-130)", AssemblyNorth - SouthEdge, "m"),
        .. RouteWidths,
        new("ramp run (y -130..-46)", RampNorth - RampSouth, "m"), new("ramp rise", RampRise, "m"), new("ramp pitch (about)", 8.13f, "degrees"),
        new("ramp foot fan: width at the foot (two wedges, 25 m long, in the ramp's plane)", FanWidth, "m"), new("ramp foot fan: length", FanLength, "m"), new("ramp foot fan: outer side falls away at (rise per run)", FanSideGradient, "ratio"),
        new("wall foot corner chamfer: leg (a vertical diagonal face each side)", ChamferLeg, "m"),
        new("plateau height (courtyard and keep floor)", PlateauZ, "m"),
        new("retaining wall: cliff under the wall", PlateauZ, "m"), new("fortress wall height above the plateau", WallTop - PlateauZ, "m"),
        new("fortress wall thickness", WallNorth - WallSouth, "m"), new("gate clear height", GateHeight, "m"),
        new("parapet thickness", ParapetThickness, "m"), new("parapet height", ParapetHeight, "m"),
        new("wall walkway width between parapets", WallNorth - WallSouth - 2f * ParapetThickness, "m"),
        new("parapet stairs: rise (two, courtyard side only)", StairRise, "m"), new("parapet stairs: width", StairWidth, "m"),
        new("parapet stairs: run", StairRise / AshfallBuilder.Riser * AshfallBuilder.Tread, "m"),
        new("courtyard width (curtain walls at x +-90)", CurtainX * 2f, "m"), new("courtyard depth (wall to keep)", KeepSouth - WallNorth, "m"),
        new("keep width", KeepHalfWidth * 2f, "m"), new("keep depth (y 40..100)", KeepNorth - KeepSouth, "m"), new("keep wall thickness", KeepWall, "m"),
        new("keep top above the plateau", KeepTop - PlateauZ, "m"), new("door and opening clear height", DoorHeight, "m"),
        new("hall clear height", KeepTop - 2f - PlateauZ, "m"),
        new("tunnel clear height (a Boss Nob is about 6.9 m)", TunnelClear, "m"), new("cross passage (y 121..129, between the tunnels): width x clear height", CrossNorth - CrossSouth, "m"), new("cross passage clear height", CrossClear, "m"), new("tunnel length (y 100..150)", TunnelNorth - TunnelSouth, "m"), new("tunnel centre lines (x +-22)", TunnelCentreX, "m"),
        new("chamber width", ChamberHalfWidth * 2f, "m"), new("chamber depth (y 150..200)", ChamberNorth - TunnelNorth, "m"), new("chamber clear height", ChamberClear, "m"),
        new("position W marker radius (centre 0, -34, 20; half height 9)", 20f, "m"), new("position K marker radius (centre 0, 70)", 24f, "m"), new("position V marker radius (centre 0, 160)", 40f, "m"),
        new("landing zone marker (0, 188)", 188f, "m north"),
    ];

    /// <summary>The level (deterministic): the same parts in the same order with the same bits on every machine.</summary>
    public static WarfrontHqBuild Build()
    {
        var b = new AshfallBuilder();
        var roofs = new List<int>();
        const float P = PlateauZ, H = HalfWidth;

        // ground: the assembly ground, the strips beside the ramp, the ramp (a heightfield wedge, 8 degrees), the plateau (courtyard and keep floor), the masses either side of the courtyard
        b.Box(Ground, -H, SouthEdge, -2f, H, AssemblyNorth, 0f);
        b.Box(Ground, -H, RampSouth, -2f, -RampWidth / 2f, RampNorth, 0f);
        b.Box(Ground, RampWidth / 2f, RampSouth, -2f, H, RampNorth, 0f);
        const float slope = RampRise / (RampNorth - RampSouth);
        b.Heightfield(Ground, -RampWidth / 2f, RampSouth, RampWidth / 2f, RampNorth, 2f, -1f, (_, y) => (y - RampSouth) * slope);
        // the foot's fan (W13c): on each side the ramp's plane goes on out to a diagonal that runs from FanWidth / 2 at the foot to the ramp's rim FanLength on. Where the ramp's plane is under a metre high (the first 7 m; the skirt runs on to FanSkirtLength, 12 m, to leave a wide entrance)
        // the edge is no kerb but a side slope (FanSideGradient), because a kerb under a metre is what the graph steps up and the body stops at (the first fan, a plain wedge, caught Orks there); beyond it the diagonal is a wall of
        // a metre and more (a wedge: the first run's best result, and a side slope there made a pocket where it met the cliff, which the second run caught Orks in).
        float fanRun = FanWidth / 2f - RampWidth / 2f, fanEdgeFall = fanRun / FanLength, skirtLen = FanSkirtLength;
        foreach (float sx in new[] { -1f, 1f })
        {
            b.Heightfield(Ground, sx < 0f ? -FanWidth / 2f : RampWidth / 2f, RampSouth, sx < 0f ? -RampWidth / 2f : FanWidth / 2f, RampSouth + skirtLen, 1f, -2f, (x, y) =>
            {
                float t = y - RampSouth, plane = t * slope, edge = FanWidth / 2f - t * fanEdgeFall, over = MathF.Max(0f, MathF.Abs(x) - edge);
                return MathF.Max(0f, plane - over * FanSideGradient);
            });
            float y0 = RampSouth + skirtLen, x0 = FanWidth / 2f - skirtLen * fanEdgeFall, x1 = RampWidth / 2f, z0 = skirtLen * slope, z1 = FanLength * slope;
            Vector3[] fan = [new(sx * x0, y0, z0), new(sx * x1, y0, z0), new(sx * x1, RampSouth + FanLength, z1),
                             new(sx * x0, y0, -2f), new(sx * x1, y0, -2f), new(sx * x1, RampSouth + FanLength, -2f)];
            b.Convex(Ground, fan, [[0, 1, 2], [3, 4, 5], [0, 1, 4, 3], [1, 2, 5, 4], [2, 0, 3, 5]]);
        }
        b.Box(Ground, -CurtainX, RampNorth, -2f, CurtainX, NorthEdge, P);
        foreach (float sx in new[] { -1f, 1f })   // the wall's foot: the corner between the ramp's rim and the wall face is cut by a diagonal (the queue slides along it to the gate)
        {
            float rx = RampWidth / 2f, c = ChamferLeg;
            Vector3[] cut = [new(sx * rx, RampNorth - c, -2f), new(sx * rx, RampNorth, -2f), new(sx * (rx - c), RampNorth, -2f),
                           new(sx * rx, RampNorth - c, WallTop), new(sx * rx, RampNorth, WallTop), new(sx * (rx - c), RampNorth, WallTop)];
            b.Convex(Wall, cut, [[0, 1, 2], [3, 4, 5], [0, 1, 4, 3], [1, 2, 5, 4], [2, 0, 3, 5]]);
        }
        b.Box(Wall, -H, RampNorth, -2f, -CurtainX, NorthEdge, 40f);
        b.Box(Wall, CurtainX, RampNorth, -2f, H, NorthEdge, 40f);

        // the fortress wall: one gate, a lintel over it so the parapet walk is continuous
        b.HoleWall(Wall, true, -CurtainX, CurtainX, WallSouth, WallNorth, P, WallTop, (-GateWidth / 2f, GateWidth / 2f, P, P + GateHeight));
        // parapets on both wall tops (the inner one stops at the stairs' tops), then the two stairs on the courtyard side
        const float ph = ParapetHeight;
        b.Box(Cover, -CurtainX, WallSouth, WallTop, CurtainX, WallSouth + ParapetThickness, WallTop + ph);
        b.Box(Cover, -CurtainX, WallNorth - ParapetThickness, WallTop, -72f, WallNorth, WallTop + ph);
        b.Box(Cover, -64f, WallNorth - ParapetThickness, WallTop, 64f, WallNorth, WallTop + ph);
        b.Box(Cover, 72f, WallNorth - ParapetThickness, WallTop, CurtainX, WallNorth, WallTop + ph);
        float stairY = WallNorth + StairWidth / 2f - AshfallBuilder.SideInset;   // flush with the wall's north face (the box steps stop a quarter metre inside their width)
        b.Stair(StairFootX, stairY, 1, 0, StairWidth, P, StairRise);
        b.Stair(-StairFootX, stairY, -1, 0, StairWidth, P, StairRise);

        // the keep: front wall with two doors, back wall with two openings to the tunnels, side walls, a roof slab, two pillars a side and low cover
        const float kx = KeepHalfWidth, kz = KeepTop;
        b.HoleWall(Stronghold, true, -kx, kx, KeepSouth, KeepSouth + KeepWall, P, kz, (-30f, -22f, P, P + DoorHeight), (22f, 30f, P, P + DoorHeight));
        b.HoleWall(Stronghold, true, -kx, kx, KeepNorth - KeepWall, KeepNorth, P, kz,
            (-TunnelCentreX - 4f, -TunnelCentreX + 4f, P, P + DoorHeight), (TunnelCentreX - 4f, TunnelCentreX + 4f, P, P + DoorHeight));
        b.Box(Stronghold, -kx, KeepSouth + KeepWall, P, -kx + KeepWall, KeepNorth - KeepWall, kz);
        b.Box(Stronghold, kx - KeepWall, KeepSouth + KeepWall, P, kx, KeepNorth - KeepWall, kz);
        roofs.Add(b.Parts.Count);
        b.Box(Floor, -kx, KeepSouth, kz - 2f, kx, KeepNorth, kz);
        foreach (float sx in new[] { -1f, 1f })
            foreach (float y in new[] { 58f, 82f })
                b.Box(Stronghold, sx * 36f - 1.5f, y - 1.5f, P, sx * 36f + 1.5f, y + 1.5f, kz - 2f);
        b.Box(Cover, -14f, 70f, P, -8f, 71.5f, P + 1.2f);   // low cover in the hall, off the doors' lines to the tunnels
        b.Box(Cover, 8f, 70f, P, 14f, 71.5f, P + 1.2f);

        // the caves: two tunnels (8 m wide, 6 m clear) in a rock mass, meeting in the last-stand chamber
        const float tn = TunnelNorth, ts = TunnelSouth;
        b.Box(Wall, -CurtainX, ts, P, -(TunnelCentreX + 4f), tn, MassTop);
        b.Box(Wall, -(TunnelCentreX - 4f), ts, P, TunnelCentreX - 4f, CrossSouth, MassTop);
        b.Box(Wall, -(TunnelCentreX - 4f), CrossNorth, P, TunnelCentreX - 4f, tn, MassTop);
        b.Box(Wall, TunnelCentreX + 4f, ts, P, CurtainX, tn, MassTop);
        roofs.Add(b.Parts.Count);
        b.Box(Wall, -(TunnelCentreX + 4f), ts, P + TunnelClear, -(TunnelCentreX - 4f), tn, MassTop);
        roofs.Add(b.Parts.Count);
        b.Box(Wall, TunnelCentreX - 4f, ts, P + TunnelClear, TunnelCentreX + 4f, tn, MassTop);
        roofs.Add(b.Parts.Count);
        b.Box(Wall, -(TunnelCentreX - 4f), CrossSouth, P + CrossClear, TunnelCentreX - 4f, CrossNorth, MassTop);   // the cross passage's roof
        b.Box(Cover, -16f, CrossNorth - 1.5f, P, -8f, CrossNorth - 0.2f, P + 1.2f);   // cover in the passage, out of the Ork line down the tunnels
        b.Box(Cover, 8f, CrossNorth - 1.5f, P, 16f, CrossNorth - 0.2f, P + 1.2f);
        b.Box(Wall, -CurtainX, tn, P, -ChamberHalfWidth, NorthEdge, MassTop);
        b.Box(Wall, ChamberHalfWidth, tn, P, CurtainX, NorthEdge, MassTop);
        b.Box(Wall, -ChamberHalfWidth, ChamberNorth, P, ChamberHalfWidth, NorthEdge, MassTop);
        roofs.Add(b.Parts.Count);
        b.Box(Wall, -ChamberHalfWidth, tn, P + ChamberClear, ChamberHalfWidth, ChamberNorth, MassTop);
        // chamber and courtyard cover (low walls off every route line)
        b.Box(Cover, -28f, 188f, P, -22f, 189.5f, P + 1.2f);
        b.Box(Cover, 22f, 188f, P, 28f, 189.5f, P + 1.2f);
        b.Box(Cover, -8f, 195f, P, 8f, 196.5f, P + 1.2f);
        foreach (float sx in new[] { -1f, 1f })
        {
            foreach (var (cx, cy) in new[] { (45f, -8f), (45f, 26f), (82f, -5f), (82f, 22f) })   // the second row (review 1, item 6): out of the 8 m corridors from the gate to the doors
                b.Box(Cover, sx * cx - 3f, cy, P, sx * cx + 3f, cy + 1.5f, P + 1.2f);
            b.Box(Cover, sx * 70f - 3f, -20f, P, sx * 70f + 3f, -18.5f, P + 1.2f);
            b.Box(Cover, sx * 70f - 3f, 10f, P, sx * 70f + 3f, 11.5f, P + 1.2f);
        }

        // the boundary (outside the map's box)
        const float O = H + 4f, T = 64f;
        b.Box(Boundary, -O, SouthEdge - 4f, -2f, O, SouthEdge, T);
        b.Box(Boundary, -O, NorthEdge, -2f, O, NorthEdge + 4f, T);
        b.Box(Boundary, -O, SouthEdge, -2f, -H, NorthEdge, T);
        b.Box(Boundary, H, SouthEdge, -2f, O, NorthEdge, T);

        // markers
        Vector3 w = new(0f, -34f, P), k = new(0f, 70f, P), v = new(0f, 160f, P), lz = new(0f, 188f, P), asm = new(0f, -170f, 0f);
        ControlPoint[] points = [new("W", new Vector3(0f, -34f, 20f), 20f, 9f), new("K", k, 24f, 8f), new("V", new Vector3(0f, 160f, P), 40f, 8f)];
        StartArea[] areas =
        [
            new(Team.SpaceMarines, lz, new Vector3(40f, 16f, 8f), 0f),
            new(Team.Chaos, asm, new Vector3(160f, 60f, 8f), 0f),
        ];
        WarfrontHqSource[] sources =
        [
            new("assembly west", new(-90f, -190f, 0f), 0), new("assembly west centre", new(-45f, -170f, 0f), 0), new("assembly centre", new(0f, -190f, 0f), 0),
            new("assembly east centre", new(45f, -170f, 0f), 0), new("assembly east", new(90f, -190f, 0f), 0), new("assembly front", new(0f, -150f, 0f), 0),
            new("courtyard west", new(-30f, -20f, P), 1), new("courtyard centre", new(0f, -8f, P), 1), new("courtyard east", new(30f, -20f, P), 1),
            new("hall west", new(-30f, 60f, P), 2), new("hall centre", new(0f, 52f, P), 2), new("hall east", new(30f, 60f, P), 2),
        ];
        WarfrontHqPost[] posts =
        [
            new("parapet west", new(-16f, -43f, WallTop), 0), new("parapet west gate", new(-8f, -43f, WallTop), 0), new("parapet east gate", new(8f, -43f, WallTop), 0), new("parapet east", new(16f, -43f, WallTop), 0),
            new("gate west", new(-12f, -30f, P), 0), new("gate east", new(12f, -30f, P), 0),
            new("hall west door", new(-18f, 58f, P), 1), new("hall east door", new(18f, 58f, P), 1), new("hall west", new(-18f, 76f, P), 1), new("hall east", new(18f, 76f, P), 1),
            new("cross passage west mouth", new(-14f, 125f, P), 2), new("cross passage east mouth", new(14f, 125f, P), 2), new("tunnel west", new(-22f, 140f, P), 2), new("tunnel east", new(22f, 140f, P), 2),
            new("chamber west", new(-20f, 176f, P), 2), new("chamber east", new(20f, 176f, P), 2),
        ];
        Vector3[] spawns = [asm, w, k, v, lz, .. sources.Select(s => s.Position), .. posts.Select(p => p.Position)];
        var area = new NavArea(new Vector2(-H, SouthEdge), new Vector2(H, NorthEdge), spawns);
        var level = new AshfallLevel(b.Parts, points, areas, spawns, MarkerHash(AshfallMap.HashOf(b.Parts, Version), points, areas, sources, posts, spawns));
        return new WarfrontHqBuild(level, w, k, v, lz, asm, sources, posts, roofs, area);
    }

    /// <summary>The geometry hash with the markers folded in (points, areas, sources, posts, spawns: names, places, radii, stages), so a moved marker changes the level's hash.</summary>
    static ulong MarkerHash(ulong h, ControlPoint[] points, StartArea[] areas, WarfrontHqSource[] sources, WarfrontHqPost[] posts, Vector3[] spawns)
    {
        ulong F(ulong x, float f) => Geometry.Fnv(x, BitConverter.SingleToUInt32Bits(f));
        ulong V3(ulong x, Vector3 v) => F(F(F(x, v.X), v.Y), v.Z);
        ulong S(ulong x, string t) { foreach (char c in t) x = Geometry.Fnv(x, (uint)c); return x; }
        foreach (var p in points) h = F(F(V3(S(h, p.Name), p.Center), p.Radius), p.HalfHeight);
        foreach (var a in areas) h = F(V3(V3(Geometry.Fnv(h, (uint)a.Side), a.Center), a.Size), a.Yaw);
        foreach (var s in sources) h = Geometry.Fnv(V3(S(h, s.Name), s.Position), (uint)s.Stage);
        foreach (var q in posts) h = Geometry.Fnv(V3(S(h, q.Name), q.Position), (uint)q.DefencePosition);
        foreach (var v in spawns) h = V3(h, v);
        return h;
    }
}
