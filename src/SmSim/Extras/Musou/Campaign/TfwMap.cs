using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>A composed war map as both ends use it: the collision, the Stormbird's spawns, the start areas, the hash the handshake compares and the places of the battlefield.</summary>
public sealed record TfwMapData(CollisionWorld Collision, IReadOnlyList<SpawnPoint> Spawns, IReadOnlyList<StartArea> StartAreas, ulong Hash, TfwBattlefield Field);

/// <summary>One solid axis-aligned box of the Stormbird.</summary>
public readonly record struct TfwSolid(Vector3 Min, Vector3 Max);

/// <summary>A capture point of the battlefield: its centre, radius and half height, as the map's Seize Ground marker gives them (original asset loaded).</summary>
public readonly record struct TfwPoint(Vector3 Centre, float Radius, float HalfHeight);

/// <summary>
/// The Stormbird: the closed room the run starts from, at <paramref name="Centre"/> (the middle of its floor), placed apart from the battlefield by <see cref="Beyond"/>. Stations and
/// spawns follow from the centre (every value ours). It is one world with the battlefield: the server cannot change maps (design section A).
/// </summary>
public sealed record TfwHub(Vector3 Centre)
{
    /// <summary>The start area a respawn and an extraction land in.</summary>
    public StartArea Area => new(Team.SpaceMarines, Centre, TfwMap.StormbirdSize, 0f);

    public Vector3 DestinationStation => Centre + new Vector3(-3.5f, 3.5f, 0f);
    public Vector3 EquipmentStation => Centre + new Vector3(3.5f, 3.5f, 0f);

    /// <summary>Four Space Marine spawns, facing north.</summary>
    public IReadOnlyList<SpawnPoint> Spawns =>
    [
        new(Centre + new Vector3(-2f, -3f, 0f), 0f), new(Centre + new Vector3(2f, -3f, 0f), 0f), new(Centre + new Vector3(-2f, -1f, 0f), 0f), new(Centre + new Vector3(2f, -1f, 0f), 0f),
    ];

    /// <summary>Whether <paramref name="p"/> stands in the Stormbird (flat, its walls included).</summary>
    public bool Contains(Vector3 p) => MathF.Abs(p.X - Centre.X) <= TfwMap.StormbirdSize.X / 2f + TfwMap.WallThickness && MathF.Abs(p.Y - Centre.Y) <= TfwMap.StormbirdSize.Y / 2f + TfwMap.WallThickness;

    /// <summary>The room with its shell, for the wire's box.</summary>
    public MusouBox Box
    {
        get
        {
            var half = new Vector3(TfwMap.StormbirdSize.X / 2f + TfwMap.WallThickness, TfwMap.StormbirdSize.Y / 2f + TfwMap.WallThickness, 0f);
            return new MusouBox(Centre - half - new Vector3(0f, 0f, TfwMap.WallThickness + 1f), Centre + half + new Vector3(0f, 0f, TfwMap.StormbirdSize.Z + TfwMap.WallThickness + 1f));
        }
    }

    /// <summary>Everything solid: the floor, four walls and the ceiling.</summary>
    public IReadOnlyList<TfwSolid> Solids
    {
        get
        {
            const float T = TfwMap.WallThickness;
            Vector3 c = Centre;
            float hx = TfwMap.StormbirdSize.X / 2f, hy = TfwMap.StormbirdSize.Y / 2f, top = c.Z + TfwMap.StormbirdSize.Z;
            return
            [
                new(new Vector3(c.X - hx - T, c.Y - hy - T, c.Z - T), new Vector3(c.X + hx + T, c.Y + hy + T, c.Z)),
                new(new Vector3(c.X - hx - T, c.Y - hy - T, c.Z), new Vector3(c.X + hx + T, c.Y - hy, top)),
                new(new Vector3(c.X - hx - T, c.Y + hy, c.Z), new Vector3(c.X + hx + T, c.Y + hy + T, top)),
                new(new Vector3(c.X - hx - T, c.Y - hy, c.Z), new Vector3(c.X - hx, c.Y + hy, top)),
                new(new Vector3(c.X + hx, c.Y - hy, c.Z), new Vector3(c.X + hx + T, c.Y + hy, top)),
                new(new Vector3(c.X - hx - T, c.Y - hy - T, top), new Vector3(c.X + hx + T, c.Y + hy + T, top + T)),
            ];
        }
    }

    /// <summary>The corners of every solid's triangles, three a triangle.</summary>
    public Vector3[] Corners() => Solids.SelectMany(TfwMap.Triangles).ToArray();

    /// <summary>
    /// The place for <paramref name="level"/>: at ground height (z = 0, not above: a blocker reaches over the level's top), south of everything solid or deadly, by whole metres, so the
    /// server and every client find the same place from the same map.
    /// </summary>
    public static TfwHub Beyond(CollisionWorld level)
    {
        var lo = Vector3.Min(level.Character.Min, level.Weapons.Min);
        var hi = Vector3.Max(level.Character.Max, level.Weapons.Max);
        foreach (var k in level.KillVolumes)
        {
            float reach = new Vector2(k.HalfExtents.X, k.HalfExtents.Y).Length();
            lo = Vector3.Min(lo, k.Center - new Vector3(reach, reach, k.HalfExtents.Z));
            hi = Vector3.Max(hi, k.Center + new Vector3(reach, reach, k.HalfExtents.Z));
        }
        float y = MathF.Floor(lo.Y) - TfwMap.HubClearance - TfwMap.StormbirdSize.Y / 2f - TfwMap.WallThickness;
        return new TfwHub(new Vector3(MathF.Round((lo.X + hi.X) / 2f), y, 0f));
    }
}

/// <summary>
/// The fixed places of the battlefield, read from the map once (design section A): the Stormbird, the Seize Ground points A, B and C (position 1, the depot, position 3), the Chaos start
/// area, where the commander stands, the Space Marine start area, one possible landing zone, and the map's own markers, which seed the enemies' graph (never the Stormbird).
/// </summary>
public sealed record TfwBattlefield(TfwHub Hub, IReadOnlyList<TfwPoint> Points, Vector3 Commander, Vector3 MarineStart, IReadOnlyList<Vector3> WalkSeeds, MusouBox Bounds)
{
    /// <summary>Where the enemies walk: around the map's own markers. No cell of the Stormbird: no marker is there and the box leaves it out.</summary>
    public NavArea WalkArea() => NavArea.Around(WalkSeeds, TfwMap.WalkMargin);

    /// <summary><paramref name="p"/> kept inside the battlefield (an order's point is the client's).</summary>
    public Vector3 Clamp(Vector3 p) => Vector3.Clamp(p, Bounds.Min, Bounds.Max);
}

/// <summary>
/// Musou Mode's one map (design section A; the owner, 2026-11-01: "use manufactorum as the battleground map for all musou mode nodes for now"): the server cannot change maps, so the
/// battlefield (Manufactorum, read from the player's install: original asset loaded) and the Stormbird (generated; every size ours) are one world under one name. <see cref="Compose"/> makes
/// it from any level, so tests use synthetic ones; <c>SmContent.Extras.Musou.TfwWorld</c> feeds it the install's.
/// </summary>
public static class TfwMap
{
    /// <summary>The map's name (it travels in the Challenge).</summary>
    public const string Name = "tfw_war";
    /// <summary>The install's map every battle is fought on.</summary>
    public const string Battlefield = "mp_tdm_09";
    /// <summary>Raised with any change of how the world is composed (hashed into <see cref="TfwMapData.Hash"/>; 2: TF1's arena and Stormbird replace TF0's flat slab; 3: Manufactorum replaces the empty arena).</summary>
    public const uint Version = 3;
    /// <summary>"tfw_war_", the hash's salt.</summary>
    const ulong HashSalt = 0x7466775F7761725FUL;

    /// <summary>The Stormbird's interior (its floor, walls and ceiling are <see cref="WallThickness"/> thick).</summary>
    public static readonly Vector3 StormbirdSize = new(10f, 10f, 5f);
    public const float WallThickness = 1f, StationRadius = 1.5f;
    /// <summary>The enemies' graph reaches this far around the map's markers (the bots' margin, <c>BotNavigation.Margin</c>).</summary>
    public const float WalkMargin = 30f;
    /// <summary>Metres between the Stormbird's shell and the level's edge: the graph's margin (<see cref="WalkMargin"/>) and 80 m more (estimate, spec "Musou Mode's map").</summary>
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
    /// spawn-room blockers go: the commander stands in the Chaos room and the war's Marines must reach him.
    /// </summary>
    public static (CollisionWorld World, TfwHub Hub, ulong Hash) WithHub(CollisionWorld level)
    {
        var hub = TfwHub.Beyond(level);
        var world = level.WithSolids(hub.Corners());
        return (world, hub, world.ContentHash ^ HashSalt ^ Version);
    }

    /// <summary>
    /// The war's world on <paramref name="level"/>; the same on every machine (no random numbers). <paramref name="points"/>: the map's Seize Ground points, in the map's order;
    /// <paramref name="areas"/>: its start areas; <paramref name="spawns"/>: its own spawn markers. An <see cref="InvalidDataException"/> for a map without three points or a start area for each side.
    /// </summary>
    public static TfwMapData Compose(CollisionWorld level, IReadOnlyList<ControlPoint> points, IReadOnlyList<StartArea> areas, IReadOnlyList<Vector3> spawns)
    {
        if (points.Count < PointsNeeded) throw new InvalidDataException($"the map has {points.Count} capture points; Musou Mode needs {PointsNeeded}");
        if (areas.FirstOrDefault(a => a.Side == Team.SpaceMarines) is not { } marines || areas.FirstOrDefault(a => a.Side == Team.Chaos) is not { } chaos)
            throw new InvalidDataException("the map has no start area for each side; Musou Mode needs them");
        var (world, hub, hash) = WithHub(level);
        var seeds = spawns.Concat(points.Select(p => p.Center)).Concat(areas.Select(a => a.Center)).ToArray();
        var margin = new Vector3(WalkMargin, WalkMargin, 0f);
        var low = seeds.Aggregate(seeds[0], Vector3.Min) - margin;
        var high = seeds.Aggregate(seeds[0], Vector3.Max) + margin;
        var bounds = new MusouBox(low with { Z = MathF.Min(low.Z, level.Character.Min.Z) }, high with { Z = MathF.Max(high.Z, level.Character.Max.Z) });
        var field = new TfwBattlefield(hub, points.Take(PointsNeeded).Select(p => new TfwPoint(p.Center, p.Radius, p.HalfHeight)).ToArray(), chaos.Center, marines.Center, seeds, bounds);
        return new TfwMapData(world, hub.Spawns, [hub.Area, chaos], hash, field);
    }
}
