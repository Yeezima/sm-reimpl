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
        var lo = Vector3.Min(level.Character.Min, level.Weapons.Min);
        var hi = Vector3.Max(level.Character.Max, level.Weapons.Max);
        foreach (var k in level.KillVolumes)
        {
            float reach = new Vector2(k.HalfExtents.X, k.HalfExtents.Y).Length();
            lo = Vector3.Min(lo, k.Center - new Vector3(reach, reach, k.HalfExtents.Z));
            hi = Vector3.Max(hi, k.Center + new Vector3(reach, reach, k.HalfExtents.Z));
        }
        float y = MathF.Floor(lo.Y) - TfwMap.HubClearance - MathF.Ceiling(TfwShip.Fore);
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
/// battlefield (Manufactorum, read from the player's install: original asset loaded) and the Stormbird (generated from measures of the Thunderhawk's interior, <see cref="TfwShip"/>) are one world under one
/// name. <see cref="Compose"/> makes it from any level, so tests use synthetic ones; <c>SmContent.Extras.Musou.TfwWorld</c> feeds it the install's.
/// </summary>
public static class TfwMap
{
    /// <summary>The map's name (it travels in the Challenge).</summary>
    public const string Name = "tfw_war";
    /// <summary>The install's map every battle is fought on.</summary>
    public const string Battlefield = "mp_tdm_09";
    /// <summary>Raised with any change of how the world is composed (hashed into <see cref="TfwMapData.Hash"/>; 2: TF1's arena and Stormbird replace TF0's flat slab; 3: Manufactorum replaces the empty arena; 4: the Thunderhawk's interior replaces the Stormbird's box).</summary>
    public const uint Version = 4;
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
