using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>A generated map as both ends use it: the collision, the spawn markers, the start areas and the hash the handshake compares.</summary>
public sealed record TfwMapData(CollisionWorld Collision, IReadOnlyList<SpawnPoint> Spawns, IReadOnlyList<StartArea> StartAreas, ulong Hash);

/// <summary>
/// The Three-Front War's one map (design section B, every value ours): the server cannot change maps, so the battle arena and the Stormbird control room are one generated map
/// (game metres, Z up). TF0 fixes the names and positions and builds a stub: one flat slab under both, placeholder demonstration. TF1 builds the real geometry behind
/// <see cref="Build"/>; the positions below are the contract and stay.
/// </summary>
public static class TfwMap
{
    /// <summary>The map's name (it travels in the Challenge).</summary>
    public const string Name = "tfw_war";
    /// <summary>Raised with any change of the geometry (hashed into <see cref="TfwMapData.Hash"/>).</summary>
    public const uint Version = 1;
    /// <summary>"tfw_war_", the hash's salt.</summary>
    const ulong HashSalt = 0x7466775F7761725FUL;

    // The arena: an empty square, x and y in [-ArenaHalf, ArenaHalf], floor top at z = 0.
    public const float ArenaHalf = 120f, WallThickness = 1f, WallHeight = 12f;

    // The Stormbird: a closed room 80 m south of the arena.
    public static readonly Vector3 StormbirdCentre = new(0f, -200f, 0f);
    public static readonly Vector3 StormbirdSize = new(10f, 10f, 5f);
    public const float StationRadius = 1.5f;
    public static readonly Vector3 DestinationStation = new(-3.5f, -196.5f, 0f);
    public static readonly Vector3 EquipmentStation = new(3.5f, -196.5f, 0f);

    // The battle layout (even nodes mirror x and lane C turns 90 degrees; TF1 and TF4).
    public static readonly Vector3 LandingZone = new(0f, -100f, 0f);
    public const float LandingRadius = 10f, CaptureRadius = 12f, HqRadius = 15f;
    public static readonly Vector3 Position1 = new(-40f, -45f, 0f);
    /// <summary>The supply depot (secured: +2 transport, +1 supply).</summary>
    public static readonly Vector3 Position2 = new(35f, 10f, 0f);
    public static readonly Vector3 Position3 = new(-25f, 60f, 0f);
    public static readonly Vector3 Hq = new(0f, 105f, 0f);
    /// <summary>The Ork spawn sources, each linked to the position of the same index; the fourth (the HQ's) stops only at victory.</summary>
    public static readonly Vector3[] Sources = [new(-110f, -40f, 0f), new(110f, 15f, 0f), new(-110f, 70f, 0f), new(60f, 112f, 0f)];

    /// <summary>Four Space Marine spawns in the Stormbird, facing north.</summary>
    public static readonly SpawnPoint[] Spawns =
    [
        new(new Vector3(-2f, -203f, 0f), 0f), new(new Vector3(2f, -203f, 0f), 0f), new(new Vector3(-2f, -201f, 0f), 0f), new(new Vector3(2f, -201f, 0f), 0f),
    ];

    /// <summary>The Space Marines' start area is the Stormbird (a respawn and an extraction land there); the Chaos one is the Ork HQ.</summary>
    public static readonly StartArea[] StartAreas =
    [
        new(Team.SpaceMarines, StormbirdCentre, StormbirdSize, 0f),
        new(Team.Chaos, new Vector3(0f, 108f, 0f), new Vector3(10f, 10f, 5f), MathF.PI),
    ];

    // The stub's slab, covering the arena and the Stormbird with a margin.
    public static readonly Vector2 StubFloorMin = new(-125f, -210f), StubFloorMax = new(125f, 125f);

    /// <summary>Builds the map; the same on every machine (no install, no random numbers).</summary>
    public static TfwMapData Build()
    {
        Vector3[] floor =
        [
            new(StubFloorMin.X, StubFloorMin.Y, 0f), new(StubFloorMax.X, StubFloorMin.Y, 0f), new(StubFloorMax.X, StubFloorMax.Y, 0f), new(StubFloorMin.X, StubFloorMax.Y, 0f),
        ];
        var mesh = new StaticMesh(floor, [0, 1, 2, 0, 2, 3]);
        var collision = new CollisionWorld(mesh, mesh);
        return new TfwMapData(collision, Spawns, StartAreas, collision.ContentHash ^ HashSalt ^ Version);
    }
}
