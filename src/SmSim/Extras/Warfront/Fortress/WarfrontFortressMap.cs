using System.Numerics;
using SmSim.Extras.Musou.Ashfall;
using static SmSim.Extras.Musou.Ashfall.AshfallKind;

namespace SmSim.Extras.Warfront.Fortress;

/// <summary>The fortress level and its named markers (<see cref="WarfrontFortressMap.Build"/>); it is an <see cref="IWarfrontArena"/>.</summary>
public sealed record WarfrontFortressBuild(
    AshfallLevel Level, Vector3 LandingZone, Vector3 BossSpot,
    IReadOnlyList<WarfrontArenaSource> Sources, IReadOnlyList<WarfrontArenaPost> Posts, IReadOnlyList<WarfrontArenaRoute> Routes,
    IReadOnlyList<int> RoofParts, IReadOnlyList<Vector3> Entries, NavArea WalkArea) : IWarfrontArena
{
    CollisionWorld? _collision;
    /// <inheritdoc />
    public CollisionWorld Collision => _collision ??= new CollisionWorld(
        new StaticMesh(Level.SolidCorners(), Enumerable.Range(0, Level.SolidCorners().Length).ToArray()),
        new StaticMesh(Level.WeaponCorners(), Enumerable.Range(0, Level.WeaponCorners().Length).ToArray()));
    /// <inheritdoc />
    public ulong Hash => Level.Hash;
    /// <inheritdoc />
    public string Name => "wf_fortress (generated)";
    /// <inheritdoc />
    public uint Version => WarfrontFortressMap.Version;
    /// <inheritdoc />
    public float ArenaRadius => WarfrontFortressMap.PlazaRadius;
    /// <inheritdoc />
    public IReadOnlyList<WarfrontArenaDimension> RouteWidths => WarfrontFortressMap.RouteWidthList;
}

/// <summary>
/// Warfront's Ork fortress (plan section 2, slot 11; every size ours, an estimate): about 300 x 320 m, south to north: the outer yard where the Marines land; the compound's south wall with a gate 12 m wide and a breach 8 m wide;
/// the outer streets (blocks with 12 m gaps and a 16 m cross street); a 19 m strip along the arena's south wall; the arena shell round a 90 x 90 m plaza (radius 45 m) for the Warboss, entered by a 16 m opening in the
/// south and a 12 m opening in each side from the flank lanes. Two entries (the gate and an 8 m breach on the west flank) and three routes: the centre and the east lane through the gate, the west lane through the breach. Every Ork opening is at least 8 m wide (a Boss Nob stands about 6.9 m); passages
/// for Marines are far wider than 3 m. Built with <see cref="AshfallBuilder"/> (read-only use), deterministic: no random numbers, no trigonometry, the same bits on every machine. The battle rules, the Warboss's spawn,
/// the server wiring and the map name are W14b's and W5's; a handmade map replaces this one through <see cref="IWarfrontArena"/>.
/// </summary>
public static class WarfrontFortressMap
{
    /// <summary>The level's version (hashed with the geometry): raise with any change of the generated geometry or markers.</summary>
    public const uint Version = 1;

    // ---- the plan (every number ours, an estimate; Dimensions lists them for spec/behavior.md)
    public const float HalfWidth = 150f, SouthEdge = -190f, NorthEdge = 130f;
    public const float CompoundHalf = 100f, GateSouth = -100f, GateNorth = -92f, WallTop = 16f, GateWidth = 12f, GateClear = 14f, BreachX0 = -68f, BreachX1 = -60f;
    public const float BlockTop = 14f, InnerBlockX0 = 24f, InnerBlockX1 = 60f, OuterBlockX0 = 72f, OuterBlockX1 = 96f;
    public const float RowSouthY0 = -84f, RowSouthY1 = -60f, RowNorthY0 = -44f, RowNorthY1 = -20f;
    public const float ShellHalf = 51f, ShellWall = 6f, ShellTop = 14f, ShellSouth = -1f, ShellNorth = 101f;
    public const float PlazaRadius = 45f, PlazaCentreY = 50f, SouthOpening = 16f, SideOpening = 12f;

    /// <summary>The level's marker positions (the plaza's centre is the Warboss's spot).</summary>
    public static readonly Vector3 Landing = new(0f, -150f, 0f), Boss = new(0f, PlazaCentreY, 0f);

    /// <summary>The widths of every opening on an Ork route, metres: each at least 8.</summary>
    public static IReadOnlyList<WarfrontArenaDimension> RouteWidthList { get; } =
    [
        new("south gate width", GateWidth, "m"), new("south wall breach width (west flank, into the 12 m gap between the block columns)", BreachX1 - BreachX0, "m"), new("gap between the block columns (four)", OuterBlockX0 - InnerBlockX1, "m"),
        new("cross street width (two)", RowNorthY0 - RowSouthY1, "m"), new("central yard width inside the gate (x -24..24)", InnerBlockX0 * 2f, "m"),
        new("strip before the arena wall (y -20..-1)", ShellSouth - RowNorthY1, "m"),
        new("arena south opening width", SouthOpening, "m"), new("arena side opening width (two)", SideOpening, "m"),
        new("flank lane width (two)", CompoundHalf - ShellHalf, "m"),
    ];

    /// <summary>Every dimension of the map, for the spec rows.</summary>
    public static IReadOnlyList<WarfrontArenaDimension> Dimensions { get; } =
    [
        new("map width (x -150..150)", HalfWidth * 2f, "m"), new("map length (y -190..130)", NorthEdge - SouthEdge, "m"),
        new("outer yard depth (y -190..-100), the Marines' landing ground", GateSouth - SouthEdge, "m"),
        .. RouteWidthList,
        new("compound width (x +-100)", CompoundHalf * 2f, "m"), new("compound south wall thickness", GateNorth - GateSouth, "m"), new("compound wall height", WallTop, "m"),
        new("gate clear height (lintel 14 m up; the Warboss is about 13 m)", GateClear, "m"),
        new("street block height", BlockTop, "m"), new("street blocks: two columns x four per side, columns x 24..60 and 72..96", 8f, "blocks"),
        new("block depth (y -84..-60 and -44..-20)", RowSouthY1 - RowSouthY0, "m"),
        new("arena shell: outer width (x +-51)", ShellHalf * 2f, "m"), new("arena shell wall thickness", ShellWall, "m"), new("arena shell height", ShellTop, "m"),
        new("arena openings: no lintel (open to the sky)", 0f, "m"),
        new("arena plaza width and depth (inside the shell)", PlazaRadius * 2f, "m"), new("arena plaza radius (round the Warboss, ring 3.4 m)", PlazaRadius, "m"),
        new("plaza pillars (four, 3 m square, 12 m high)", 4f, "pillars"), new("plaza cover walls (four, 6 m x 1.5 m x 1.2 m)", 4f, "walls"),
        new("landing zone distance south of the origin (0, -150)", 150f, "m"), new("Warboss spot distance north of the origin (0, 50)", 50f, "m"),
        new("rock walls beside the compound (x 100..150) height", 40f, "m"),
    ];

    /// <summary>The level (deterministic): the same parts in the same order with the same bits on every machine.</summary>
    public static WarfrontFortressBuild Build()
    {
        var b = new AshfallBuilder();
        var roofs = new List<int>();
        const float H = HalfWidth, C = CompoundHalf;
        void Lintel(float x0, float y0, float x1, float y1, float zLo, float zHi) { roofs.Add(b.Parts.Count); b.Box(Wall, x0, y0, zLo, x1, y1, zHi); }

        // ground and the rock beside and behind the compound
        b.Box(Ground, -H, SouthEdge, -2f, H, NorthEdge, 0f);
        b.Box(Wall, -H, GateSouth, 0f, -C, NorthEdge, 40f);
        b.Box(Wall, C, GateSouth, 0f, H, NorthEdge, 40f);
        b.Box(Wall, -C, ShellNorth, 0f, C, NorthEdge, WallTop);

        // the compound's south wall: the gate (lintel 14 m up, a roof part) and an 8 m breach on the west flank (open)
        b.Box(Wall, -C, GateSouth, 0f, BreachX0, GateNorth, WallTop);
        b.Box(Wall, BreachX1, GateSouth, 0f, -GateWidth / 2f, GateNorth, WallTop);
        b.Box(Wall, GateWidth / 2f, GateSouth, 0f, C, GateNorth, WallTop);
        Lintel(-GateWidth / 2f, GateSouth, GateWidth / 2f, GateNorth, GateClear, WallTop);

        // street blocks (two columns, two rows, both sides) with a 12 m gap between the columns and a 16 m cross street between the rows
        foreach (float sx in new[] { -1f, 1f })
            foreach (var (x0, x1) in new[] { (InnerBlockX0, InnerBlockX1), (OuterBlockX0, OuterBlockX1) })
                foreach (var (y0, y1) in new[] { (RowSouthY0, RowSouthY1), (RowNorthY0, RowNorthY1) })
                    b.Box(Stronghold, MathF.Min(sx * x0, sx * x1), y0, 0f, MathF.Max(sx * x0, sx * x1), y1, BlockTop);

        // the arena shell: south wall (16 m opening), north wall, side walls (12 m openings at y 44..56), open gaps
        const float S = ShellHalf, T = ShellTop;
        b.Box(Stronghold, -S, ShellSouth, 0f, -SouthOpening / 2f, ShellSouth + ShellWall, T);
        b.Box(Stronghold, SouthOpening / 2f, ShellSouth, 0f, S, ShellSouth + ShellWall, T);
        b.Box(Stronghold, -S, ShellNorth - ShellWall, 0f, S, ShellNorth, T);
        float wy0 = PlazaCentreY - SideOpening / 2f, wy1 = PlazaCentreY + SideOpening / 2f, py0 = ShellSouth + ShellWall, py1 = ShellNorth - ShellWall;
        foreach (float sx in new[] { -1f, 1f })
        {
            float xa = MathF.Min(sx * S, sx * (S - ShellWall)), xb = MathF.Max(sx * S, sx * (S - ShellWall));
            b.Box(Stronghold, xa, py0, 0f, xb, wy0, T);
            b.Box(Stronghold, xa, wy1, 0f, xb, py1, T);
        }

        // plaza: four pillars, four low cover walls off the openings' lines and clear of the Warboss's ring
        foreach (float sx in new[] { -1f, 1f })
        {
            foreach (float y in new[] { 28f, 72f }) b.Box(Stronghold, sx * 25f - 1.5f, y - 1.5f, 0f, sx * 25f + 1.5f, y + 1.5f, 12f);
            foreach (float y in new[] { 38f, 61f }) b.Box(Cover, MathF.Min(sx * 30f, sx * 36f), y, 0f, MathF.Max(sx * 30f, sx * 36f), y + 1.5f, 1.2f);
        }
        // cover in the yard, the streets and the lanes (low walls out of the lines through the openings)
        foreach (float sx in new[] { -1f, 1f })
        {
            b.Box(Cover, MathF.Min(sx * 30f, sx * 40f), -135f, 0f, MathF.Max(sx * 30f, sx * 40f), -133.5f, 1.2f);
            b.Box(Cover, MathF.Min(sx * 8f, sx * 18f), -105f, 0f, MathF.Max(sx * 8f, sx * 18f), -103.5f, 1.2f);
            b.Box(Cover, MathF.Min(sx * 80f, sx * 90f), 20f, 0f, MathF.Max(sx * 80f, sx * 90f), 21.5f, 1.2f);
            b.Box(Cover, MathF.Min(sx * 60f, sx * 70f), 80f, 0f, MathF.Max(sx * 60f, sx * 70f), 81.5f, 1.2f);
            b.Box(Cover, MathF.Min(sx * 14f, sx * 22f), -14f, 0f, MathF.Max(sx * 14f, sx * 22f), -12.5f, 1.2f);
        }

        // the boundary (outside the map's box)
        const float BO = H + 4f, BT = 64f;
        b.Box(Boundary, -BO, SouthEdge - 4f, -2f, BO, SouthEdge, BT);
        b.Box(Boundary, -BO, NorthEdge, -2f, BO, NorthEdge + 4f, BT);
        b.Box(Boundary, -BO, SouthEdge, -2f, -H, NorthEdge, BT);
        b.Box(Boundary, H, SouthEdge, -2f, BO, NorthEdge, BT);

        // markers
        ControlPoint[] points = [new("Arena", Boss, PlazaRadius, 12f)];
        StartArea[] areas =
        [
            new(Team.SpaceMarines, Landing, new Vector3(60f, 24f, 8f), 0f),
            new(Team.Chaos, Boss, new Vector3(80f, 80f, 8f), 0f),
        ];
        WarfrontArenaSource[] sources =
        [
            new("street centre", new(0f, -50f, 0f), 0), new("street west cross", new(-40f, -52f, 0f), 0), new("street east cross", new(40f, -52f, 0f), 0),
            new("street far west", new(-84f, -52f, 0f), 0), new("street far east", new(84f, -52f, 0f), 0), new("street inner", new(0f, -38f, 0f), 0),
            new("strip centre", new(0f, -10f, 0f), 1), new("strip west", new(-30f, -10f, 0f), 1), new("strip east", new(30f, -10f, 0f), 1),
            new("lane west south", new(-75f, 10f, 0f), 1), new("lane east south", new(75f, 10f, 0f), 1),
            new("lane west north", new(-75f, 70f, 0f), 1), new("lane east north", new(75f, 70f, 0f), 1),
            new("plaza west", new(-28f, 50f, 0f), 2), new("plaza east", new(28f, 50f, 0f), 2), new("plaza south", new(0f, 18f, 0f), 2), new("plaza north", new(0f, 82f, 0f), 2),
        ];
        WarfrontArenaPost[] posts =
        [
            new("landing west", new(-24f, -150f, 0f), "landing"), new("landing east", new(24f, -150f, 0f), "landing"), new("landing centre", new(0f, -136f, 0f), "landing"),
            new("landing south west", new(-40f, -164f, 0f), "landing"), new("landing south east", new(40f, -164f, 0f), "landing"),
            new("gate west", new(-16f, -86f, 0f), "gate"), new("gate east", new(16f, -86f, 0f), "gate"), new("breach", new(-64f, -86f, 0f), "gate"),
            new("plaza gate west", new(-20f, 12f, 0f), "plaza"), new("plaza gate east", new(20f, 12f, 0f), "plaza"),
            new("elite street west", new(-22f, -52f, 0f), "elite"), new("elite street east", new(22f, -52f, 0f), "elite"),
            new("elite gap west", new(-66f, -14f, 0f), "elite"), new("elite gap east", new(66f, -14f, 0f), "elite"),
            new("elite lane west", new(-75f, 40f, 0f), "elite"), new("elite lane east", new(75f, 40f, 0f), "elite"),
            new("elite strip west", new(-24f, -8f, 0f), "elite"), new("elite strip east", new(24f, -8f, 0f), "elite"),
        ];
        WarfrontArenaRoute[] routes =
        [
            new("centre (gate, then straight to the south opening)", [new(0f, -30f, 0f), new(0f, -10f, 0f)]),
            new("west breach (breach, gap column, west lane)", [new(-64f, -96f, 0f), new(-66f, -30f, 0f), new(-75f, 10f, 0f)]),
            new("east lane (gate, gap column, east lane)", [new(0f, -96f, 0f), new(66f, -30f, 0f), new(75f, 10f, 0f)]),
        ];
        Vector3[] entries = [new(0f, -96f, 0f), new(-64f, -96f, 0f)];
        Vector3[] spawns = [Landing, Boss, .. sources.Select(s => s.Position), .. posts.Select(p => p.Position)];
        var area = new NavArea(new Vector2(-H, SouthEdge), new Vector2(H, NorthEdge), spawns);
        var level = new AshfallLevel(b.Parts, points, areas, spawns,
            WarfrontArenaGates.MarkerHash(WarfrontArenaGates.GeometryHash(b.Parts, Version), points, areas, sources, posts, routes, spawns));
        return new WarfrontFortressBuild(level, Landing, Boss, sources, posts, routes, roofs, entries, area);
    }
}
