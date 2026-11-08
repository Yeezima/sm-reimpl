using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>A composed war map as both ends use it: the collision, the Stormbird's spawns, the start areas, the hash the handshake compares and the places of the battlefield.</summary>
public sealed record TfwMapData(CollisionWorld Collision, IReadOnlyList<SpawnPoint> Spawns, IReadOnlyList<StartArea> StartAreas, ulong Hash, TfwBattlefield Field);

/// <summary>One solid axis-aligned box of the Stormbird.</summary>
public readonly record struct TfwSolid(Vector3 Min, Vector3 Max);

/// <summary>A capture point of the battlefield: its centre, radius and half height, as the map's Seize Ground marker gives them (original asset loaded).</summary>
public readonly record struct TfwPoint(Vector3 Centre, float Radius, float HalfHeight);

/// <summary>
/// The Stormbird, the hub of Musou Mode: the inside of the campaign Thunderhawk in orbit (<see cref="TfwShip"/>: a corridor about 4.5 m wide and 30 m long with its ramp open), placed apart from the
/// battlefield by <see cref="Beyond"/>. <paramref name="Centre"/> is the floor point at the middle of the corridor (its z is the floor's). Stations and spawns follow from the centre (every place ours).
/// It is one world with the battlefield: the server cannot change maps (design section A).
/// </summary>
public sealed record TfwHub(Vector3 Centre)
{
    /// <summary>A point of <see cref="TfwShip"/>'s model space as the world's (the ship is turned half a circle: its ramp looks south, away from the battlefield).</summary>
    public Vector3 ToWorld(Vector3 model) => TfwShip.ToWorld(model, Centre);

    /// <summary>The model's matrix to the world's (row-vector).</summary>
    public Matrix4x4 ModelToWorld => TfwShip.ModelToWorld(Centre);

    /// <summary>A place of the model's (X, Y on the floor) as the world's, standing on the corridor's floor.</summary>
    public Vector3 OnFloor(Vector2 model) => ToWorld(new Vector3(model.X, model.Y, TfwShip.FloorZ));

    /// <summary>The start area a respawn and an extraction land in.</summary>
    public StartArea Area => new(Team.SpaceMarines, Centre, new Vector3(3f, 8f, 4f), 0f);

    /// <summary>Destination stands under the hanging monitor (<c>Monitor_Hub_01</c>), Equipment before the right tech panel.</summary>
    public Vector3 DestinationStation => OnFloor(TfwShip.Monitor);
    public Vector3 EquipmentStation => OnFloor(TfwShip.Panel);

    /// <summary>The Firing Range's button: on the left wall's tech panel between the other two stations (ours, spec "The Firing Range"); its pad is on the floor before it.</summary>
    public Vector3 RangeStation => OnFloor(RangeButtonModel);
    public Vector3 RangeButton => ToWorld(new Vector3(TfwShip.LeftPanel, RangeButtonModel.Y, TfwShip.FloorZ + 1.3f));
    static readonly Vector2 RangeButtonModel = new(-0.9f, 13.5f);

    /// <summary>Four Space Marine spawns in the middle of the corridor, facing the open ramp (south).</summary>
    public IReadOnlyList<SpawnPoint> Spawns => TfwShip.Spawns.Select(s => new SpawnPoint(OnFloor(s), TfwShip.RampYaw)).ToArray();

    /// <summary>Whether <paramref name="p"/> stands in the ship (flat: the corridor, the ramp and their walls).</summary>
    public bool Contains(Vector3 p) => MathF.Abs(p.X - Centre.X) <= TfwShip.HalfWidth && p.Y - Centre.Y >= -TfwShip.Aft && p.Y - Centre.Y <= TfwShip.Fore;

    /// <summary>The ship with its walls and its cap, for the wire's box.</summary>
    public MusouBox Box => new(Centre + new Vector3(-TfwShip.HalfWidth, -TfwShip.Aft, -TfwShip.Thickness - 1f), Centre + new Vector3(TfwShip.HalfWidth, TfwShip.Fore, TfwShip.Height + 1f));

    /// <summary>The boxes of the walls, floors and caps, in the world (the prisms of the stairs and the ceiling come with <see cref="Corners"/>).</summary>
    public IReadOnlyList<TfwSolid> Solids =>
        TfwShip.Boxes().Select(b =>
        {
            var a = ToWorld(b.Min);
            var c = ToWorld(b.Max);
            return new TfwSolid(Vector3.Min(a, c), Vector3.Max(a, c));
        }).ToArray();

    /// <summary>The corners of every solid's triangles, three a triangle.</summary>
    public Vector3[] Corners() => TfwShip.Corners(Centre);

    /// <summary>
    /// The place for <paramref name="level"/>: at ground height (z = 0, not above: a blocker reaches over the level's top), south of everything solid or deadly, by whole metres, so the
    /// server and every client find the same place from the same map.
    /// </summary>
    public static TfwHub Beyond(CollisionWorld level)
    {
        var (lo, hi) = Extent(level);
        float y = MathF.Floor(lo.Y) - TfwMap.HubClearance - MathF.Ceiling(TfwShip.Fore);
        return new TfwHub(new Vector3(MathF.Round((lo.X + hi.X) / 2f), y, 0f));
    }

    /// <summary>The level's solid and deadly extent.</summary>
    internal static (Vector3 Lo, Vector3 Hi) Extent(CollisionWorld level)
    {
        var lo = Vector3.Min(level.Character.Min, level.Weapons.Min);
        var hi = Vector3.Max(level.Character.Max, level.Weapons.Max);
        foreach (var k in level.KillVolumes)
        {
            float reach = new Vector2(k.HalfExtents.X, k.HalfExtents.Y).Length();
            lo = Vector3.Min(lo, k.Center - new Vector3(reach, reach, k.HalfExtents.Z));
            hi = Vector3.Max(hi, k.Center + new Vector3(reach, reach, k.HalfExtents.Z));
        }
        return (lo, hi);
    }
}

/// <summary>
/// The Firing Range (the owner, 2026-11-02): the first blank map concept, a flat walled square of <see cref="Size"/> m on a side, generated in the same world east of the level (the server cannot change maps),
/// a test ground for the artillery strike and the new features. <paramref name="Centre"/> is the floor's middle (z = 0). The player arrives at <see cref="Spawn"/> by the south wall and the way back is the pad <see cref="ReturnStation"/> beside it. Every number is ours.
/// </summary>
public sealed record TfwRange(Vector3 Centre)
{
    /// <summary>The floor's side, the walls' thickness and height, the gap to the level's east edge (metres).</summary>
    public const float Size = 100f, Thickness = 2f, WallHeight = 24f, Clearance = 60f;

    float Half => Size / 2f;

    /// <summary>The range east of <paramref name="level"/>, at ground height, level with its middle, by whole metres: the same place for the server and every client.</summary>
    public static TfwRange For(CollisionWorld level)
    {
        var (lo, hi) = TfwHub.Extent(level);
        return new TfwRange(new Vector3(MathF.Ceiling(hi.X) + Clearance + Size / 2f + Thickness, MathF.Round((lo.Y + hi.Y) / 2f), 0f));
    }

    /// <summary>Where a player arrives (a few metres inside the south wall, facing north) and the pad that brings them back.</summary>
    public SpawnPoint Spawn => new(Centre + new Vector3(0f, -Half + 8f, 0f), 0f);
    public Vector3 ReturnStation => Centre + new Vector3(-6f, -Half + 5f, 0f);

    /// <summary>Whether <paramref name="p"/> stands on the floor or in its walls.</summary>
    public bool Contains(Vector3 p) => MathF.Abs(p.X - Centre.X) <= Half + Thickness && MathF.Abs(p.Y - Centre.Y) <= Half + Thickness && p.Z > -Thickness && p.Z < WallHeight + 40f;

    /// <summary>The wire's box: the range with its walls and the air above them.</summary>
    public MusouBox Box => new(Centre + new Vector3(-Half - Thickness, -Half - Thickness, -Thickness - 1f), Centre + new Vector3(Half + Thickness, Half + Thickness, WallHeight + 30f));

    /// <summary>The floor and the four walls.</summary>
    public IReadOnlyList<TfwSolid> Solids
    {
        get
        {
            float h = Half, t = Thickness, w = WallHeight;
            var c = Centre;
            return
            [
                new(c + new Vector3(-h - t, -h - t, -t), c + new Vector3(h + t, h + t, 0f)),
                new(c + new Vector3(-h - t, -h - t, 0f), c + new Vector3(h + t, -h, w)),
                new(c + new Vector3(-h - t, h, 0f), c + new Vector3(h + t, h + t, w)),
                new(c + new Vector3(-h - t, -h, 0f), c + new Vector3(-h, h, w)),
                new(c + new Vector3(h, -h, 0f), c + new Vector3(h + t, h, w)),
            ];
        }
    }

    /// <summary>The corners of every solid's triangles, three a triangle.</summary>
    public Vector3[] Corners() => Solids.SelectMany(TfwMap.Triangles).ToArray();

    /// <summary>
    /// Manufactorum's backdrop stands in the range: it has no collision, so the range's place (from the level's collision) falls on it. The owner, 2026-11-02: "add collision to the 2 large cylinders ... as well as the
    /// ramp". Measured from the render meshes <c>factory_tanks_lodh</c> and <c>factory_geo_lodh</c> in metres from <see cref="Centre"/> (each slope to the walls): two tanks and three slopes, all drawn by the level.
    /// </summary>
    public const float TankRadius = 10.7f, TankTop = 14.2f;
    static readonly Vector2[] Tanks = [new(-38.1f, 25.1f), new(-38.1f, 0.07f)];
    static readonly (float Y0, float Y1, Vector2[] Side)[] Slopes =
    [
        (-50f, 50f, [new(41.8f, 0f), new(50f, 0f), new(50f, 9.8f)]),                                  // the wide slope along the east wall
        (-17.5f, -9.9f, [new(33.4f, 0f), new(50f, 0f), new(50f, 19.7f)]),                             // the narrow ramp on it
        (-50f, -46f, [new(-2.5f, 0f), new(50f, 0f), new(50f, 42f), new(43.2f, 42f)]),                 // the ramp along the south wall
    ];

    /// <summary>Everything solid here, three corners a triangle: <see cref="Corners"/> (which the range draws) and the level's backdrop (which the level draws).</summary>
    public Vector3[] Collision()
    {
        var all = new List<Vector3>(Corners());
        foreach (var tank in Tanks)
        {
            const int sides = 24;
            var at = Centre + new Vector3(tank, 0f);
            Vector3 Rim(int k, float z) => at + new Vector3(MathF.Cos(k * MathF.Tau / sides) * TankRadius, MathF.Sin(k * MathF.Tau / sides) * TankRadius, z);
            for (int k = 0; k < sides; k++)
            {
                all.AddRange([Rim(k, 0f), Rim(k + 1, 0f), Rim(k + 1, TankTop), Rim(k, 0f), Rim(k + 1, TankTop), Rim(k, TankTop)]);
                all.AddRange([at + new Vector3(0f, 0f, TankTop), Rim(k, TankTop), Rim(k + 1, TankTop)]);
            }
        }
        foreach (var (y0, y1, side) in Slopes)
        {
            Vector3 P(Vector2 p, float y) => Centre + new Vector3(p.X, y, p.Y);
            for (int k = 0; k < side.Length; k++)
            {
                Vector2 a = side[k], b = side[(k + 1) % side.Length];
                all.AddRange([P(a, y0), P(b, y0), P(b, y1), P(a, y0), P(b, y1), P(a, y1)]);
            }
            for (int k = 1; k + 1 < side.Length; k++)
            {
                all.AddRange([P(side[0], y0), P(side[k + 1], y0), P(side[k], y0)]);
                all.AddRange([P(side[0], y1), P(side[k], y1), P(side[k + 1], y1)]);
            }
        }
        // The south ramp rises past the walls' 24 m: the walls go on above it, unseen, so nobody walks out over them.
        all.AddRange(TfwMap.Triangles(new TfwSolid(Centre + new Vector3(0f, -Half - Thickness, WallHeight), Centre + new Vector3(Half + Thickness, -Half, WallHeight + 40f))));
        all.AddRange(TfwMap.Triangles(new TfwSolid(Centre + new Vector3(Half, -Half, WallHeight), Centre + new Vector3(Half + Thickness, -40f, WallHeight + 40f))));
        return all.ToArray();
    }
}

/// <summary>
/// The fixed places of the battlefield, read from the map once (design section A): the Stormbird, the Seize Ground points A, B and C (position 1, the depot, position 3), the Chaos start
/// area, where the commander stands, the Space Marine start area, one possible landing zone, and the map's own markers, which seed the enemies' graph (never the Stormbird).
/// <paramref name="BaseLanding"/>: every battle lands at the Space Marine start area (Ashfall Citadel's Imperial base in the south centre; the owner, 2026-11-08: "you should always spawn
/// in at the imperial base in the south center of the ashfall map in musou mode"); false (the Manufactorum composition), the landing zone is drawn as before (<see cref="TfwSites"/>).
/// </summary>
public sealed record TfwBattlefield(TfwHub Hub, IReadOnlyList<TfwPoint> Points, Vector3 Commander, Vector3 MarineStart, IReadOnlyList<Vector3> WalkSeeds, MusouBox Bounds, TfwRange? Range = null, bool BaseLanding = false)
{
    /// <summary>Where the enemies walk: around the map's own markers. No cell of the Stormbird: no marker is there and the box leaves it out.</summary>
    public NavArea WalkArea() => NavArea.Around(WalkSeeds, TfwMap.WalkMargin);

    /// <summary><paramref name="p"/> kept inside the battlefield (an order's point is the client's).</summary>
    public Vector3 Clamp(Vector3 p) => Vector3.Clamp(p, Bounds.Min, Bounds.Max);
}

/// <summary>
/// Musou Mode's one map (design section A; the owner, 2026-11-01: "use manufactorum as the battleground map for all musou mode nodes for now"): the server cannot change maps, so the
/// battlefield (Manufactorum, read from the player's install: original asset loaded) and the Stormbird (generated from measures of the Thunderhawk's interior, <see cref="TfwShip"/>) are one world under one
/// name. <see cref="Compose"/> makes it from any level, so tests use synthetic ones; <c>SmContent.Extras.Musou.TfwWorld</c> feeds it the install's.
/// </summary>
public static class TfwMap
{
    /// <summary>The map's name (it travels in the Challenge): Ashfall Citadel (G1, docs/musou-ashfall-graybox.md), generated in code.</summary>
    public const string Name = "tfw_war";
    /// <summary>The restore switch: the old war map, Manufactorum from the install with the Stormbird and the range (<see cref="ManufactorumVersion"/>'s composition).</summary>
    public const string ManufactorumName = "tfw_war_m09";
    /// <summary>Whether <paramref name="name"/> is one of the war's maps (<see cref="Name"/> or <see cref="ManufactorumName"/>).</summary>
    public static bool IsWarMap(string? name) => name is Name or ManufactorumName;
    /// <summary>The install's map every battle is fought on.</summary>
    public const string Battlefield = "mp_tdm_09";   // Manufactorum: the 5-wave test aid's map and the battlefield of ManufactorumName
    /// <summary>Raised with any change of how the world is composed (hashed into <see cref="TfwMapData.Hash"/>; 2: TF1's arena and Stormbird replace TF0's flat slab; 3: Manufactorum replaces the empty arena; 4: the Thunderhawk's interior replaces the Stormbird's box; 8: Ashfall Citadel replaces Manufactorum under <see cref="Name"/>; the Manufactorum composition keeps <see cref="ManufactorumVersion"/>).</summary>
    public const uint Version = 8;
    /// <summary>The version term of the Manufactorum composition's hash (<see cref="ManufactorumName"/>): it stays what it was, so that composition's hash is unchanged.</summary>
    public const uint ManufactorumVersion = 7;
    /// <summary>"tfw_war_", the hash's salt.</summary>
    const ulong HashSalt = 0x7466775F7761725FUL;

    /// <summary>The hub's walls and floors are this thick (<see cref="TfwShip.Thickness"/>); a station's ring is this wide.</summary>
    public const float WallThickness = TfwShip.Thickness, StationRadius = 1.5f;
    /// <summary>The enemies' graph reaches this far around the map's markers (the bots' margin, <c>BotNavigation.Margin</c>).</summary>
    public const float WalkMargin = 30f;
    /// <summary>Metres between the ship's north end (the cockpit's) and the level's edge: the graph's margin (<see cref="WalkMargin"/>) and 80 m more (estimate, spec "Musou Mode's map").</summary>
    public const float HubClearance = WalkMargin + 80f;

    /// <summary>The landing zone's ring and the commander's (ours; a capture point's radius is the map's).</summary>
    public const float LandingRadius = 10f, HqRadius = 15f;

    /// <summary>A map needs this many capture points: the three positions of a battle.</summary>
    public const int PointsNeeded = WarRules.Positions;

    /// <summary>The 12 outward-facing triangles of <paramref name="solid"/> (36 corners, three a triangle): (b - a) x (c - a) points out of the box.</summary>
    public static Vector3[] Triangles(TfwSolid solid)
    {
        Vector3 l = solid.Min, h = solid.Max;
        Vector3[] p =
        [
            new(l.X, l.Y, l.Z), new(h.X, l.Y, l.Z), new(h.X, h.Y, l.Z), new(l.X, h.Y, l.Z),
            new(l.X, l.Y, h.Z), new(h.X, l.Y, h.Z), new(h.X, h.Y, h.Z), new(l.X, h.Y, h.Z),
        ];
        int[][] faces = [[3, 2, 1, 0], [4, 5, 6, 7], [0, 1, 5, 4], [2, 3, 7, 6], [3, 0, 4, 7], [1, 2, 6, 5]];
        var corners = new Vector3[36];
        for (int f = 0; f < 6; f++)
        {
            int[] q = faces[f];
            ReadOnlySpan<int> order = [q[0], q[1], q[2], q[0], q[2], q[3]];
            for (int k = 0; k < 6; k++) corners[f * 6 + k] = p[order[k]];
        }
        return corners;
    }

    /// <summary>
    /// <paramref name="level"/> with the Stormbird beside it and, for the handshake, the hash of the whole (<c>collision ^ salt ^ version</c>). The kill volumes and props stay. The
    /// spawn-room blockers go: the commander stands in the Chaos room and the war's Marines must reach him. <paramref name="manufactorum"/>: the level is Manufactorum, whose backdrop
    /// (tanks and slopes) stands in the Firing Range's place and gets collision there (<see cref="TfwRange.Collision"/>, hash term <see cref="ManufactorumVersion"/>, as before); false (Ashfall
    /// Citadel): the range has its own floor and walls only (hash term <see cref="Version"/>).
    /// </summary>
    public static (CollisionWorld World, TfwHub Hub, ulong Hash) WithHub(CollisionWorld level, bool manufactorum)
    {
        var hub = TfwHub.Beyond(level);
        var range = TfwRange.For(level);
        var world = level.WithSolids([.. hub.Corners(), .. (manufactorum ? range.Collision() : range.Corners())]);   // the Firing Range stands beside the level
        return (world, hub, world.ContentHash ^ HashSalt ^ (manufactorum ? ManufactorumVersion : Version));
    }

    /// <summary>
    /// The war's world on <paramref name="level"/>; the same on every machine (no random numbers). <paramref name="points"/>: the map's Seize Ground points, in the map's order;
    /// <paramref name="areas"/>: its start areas; <paramref name="spawns"/>: its own spawn markers; <paramref name="manufactorum"/>: see <see cref="WithHub"/>. An <see cref="InvalidDataException"/> for a map without three points or a start area for each side.
    /// </summary>
    public static TfwMapData Compose(CollisionWorld level, IReadOnlyList<ControlPoint> points, IReadOnlyList<StartArea> areas, IReadOnlyList<Vector3> spawns, bool manufactorum = true)
    {
        if (points.Count < PointsNeeded) throw new InvalidDataException($"the map has {points.Count} capture points; Musou Mode needs {PointsNeeded}");
        if (areas.FirstOrDefault(a => a.Side == Team.SpaceMarines) is not { } marines || areas.FirstOrDefault(a => a.Side == Team.Chaos) is not { } chaos)
            throw new InvalidDataException("the map has no start area for each side; Musou Mode needs them");
        var (world, hub, hash) = WithHub(level, manufactorum);
        var seeds = spawns.Concat(points.Select(p => p.Center)).Concat(areas.Select(a => a.Center)).ToArray();
        var margin = new Vector3(WalkMargin, WalkMargin, 0f);
        var low = seeds.Aggregate(seeds[0], Vector3.Min) - margin;
        var high = seeds.Aggregate(seeds[0], Vector3.Max) + margin;
        var bounds = new MusouBox(low with { Z = MathF.Min(low.Z, level.Character.Min.Z) }, high with { Z = MathF.Max(high.Z, level.Character.Max.Z) });
        var range = TfwRange.For(level);
        var field = new TfwBattlefield(hub, points.Take(PointsNeeded).Select(p => new TfwPoint(p.Center, p.Radius, p.HalfHeight)).ToArray(), chaos.Center, marines.Center, [.. seeds, range.Centre], bounds, range, BaseLanding: !manufactorum);   // the range's seed last: its own part of the graph; Ashfall always lands at its Imperial base (owner, 2026-11-08)
        return new TfwMapData(world, hub.Spawns, [hub.Area, chaos], hash, field);
    }
}
