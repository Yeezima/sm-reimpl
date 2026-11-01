using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>A generated map as both ends use it: the collision, the spawn markers, the start areas and the hash the handshake compares.</summary>
public sealed record TfwMapData(CollisionWorld Collision, IReadOnlyList<SpawnPoint> Spawns, IReadOnlyList<StartArea> StartAreas, ulong Hash);

/// <summary>One solid axis-aligned box of the war's map; <see cref="Stormbird"/> tells the control room's from the arena's (the client draws them apart).</summary>
public readonly record struct TfwSolid(Vector3 Min, Vector3 Max, bool Stormbird);

/// <summary>
/// Musou Mode's one map (design section B, every value ours; the owner, 2026-11-01: for now an empty square box the size of Manufactorum and a small Stormbird control room):
/// the server cannot change maps, so the battle arena and the Stormbird are one generated map (game metres, Z up). Everything solid is one list of boxes, <see cref="Solids"/>: the
/// collision, the client's drawing and the client's ragdoll world are all made from it. Placeholder demonstration: nothing here comes from the install.
/// </summary>
public static class TfwMap
{
    /// <summary>The map's name (it travels in the Challenge).</summary>
    public const string Name = "tfw_war";
    /// <summary>Raised with any change of the geometry (hashed into <see cref="TfwMapData.Hash"/>; 2: TF1's arena and Stormbird replace TF0's flat slab).</summary>
    public const uint Version = 2;
    /// <summary>"tfw_war_", the hash's salt.</summary>
    const ulong HashSalt = 0x7466775F7761725FUL;

    // The arena: an empty square, x and y in [-ArenaHalf, ArenaHalf], floor top at z = 0; the walls stand outside that square.
    // 24 m, not the design's 12 m: its "jump apex 7.4 to 8.9 m" was measured under a spawn-room ceiling; in the open a full jump tops out at 17.54 m (spec "Measured apexes").
    public const float ArenaHalf = 120f, WallThickness = 1f, WallHeight = 24f;

    // The Stormbird: a closed room 80 m south of the arena (its size is the interior's; floor, walls and ceiling are WallThickness thick).
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

    /// <summary>Everything solid: the arena's floor slab and four walls, then the Stormbird's floor, four walls and ceiling.</summary>
    public static readonly IReadOnlyList<TfwSolid> Solids = BuildSolids();

    /// <summary>
    /// The box the wire quantises positions over (<see cref="World.StartTfw"/>, the server's fallback): the arena with its walls up to their top and the Stormbird with its shell,
    /// which the nav graph's own box leaves out (5 mm a step along the map's 328 m).
    /// </summary>
    public static readonly MusouBox WireBox = new(
        new Vector3(-ArenaHalf - WallThickness, StormbirdCentre.Y - StormbirdSize.Y / 2f - WallThickness, -WallThickness - 1f),
        new Vector3(ArenaHalf + WallThickness, ArenaHalf + WallThickness, WallHeight + 1f));

    static TfwSolid[] BuildSolids()
    {
        const float T = WallThickness;
        float outer = ArenaHalf + T;
        Vector3 c = StormbirdCentre;
        float hx = StormbirdSize.X / 2f, hy = StormbirdSize.Y / 2f, top = c.Z + StormbirdSize.Z;
        return
        [
            new(new Vector3(-outer, -outer, -T), new Vector3(outer, outer, 0f), false),
            new(new Vector3(-outer, -outer, 0f), new Vector3(outer, -ArenaHalf, WallHeight), false),
            new(new Vector3(-outer, ArenaHalf, 0f), new Vector3(outer, outer, WallHeight), false),
            new(new Vector3(-outer, -ArenaHalf, 0f), new Vector3(-ArenaHalf, ArenaHalf, WallHeight), false),
            new(new Vector3(ArenaHalf, -ArenaHalf, 0f), new Vector3(outer, ArenaHalf, WallHeight), false),

            new(new Vector3(c.X - hx - T, c.Y - hy - T, c.Z - T), new Vector3(c.X + hx + T, c.Y + hy + T, c.Z), true),
            new(new Vector3(c.X - hx - T, c.Y - hy - T, c.Z), new Vector3(c.X + hx + T, c.Y - hy, top), true),
            new(new Vector3(c.X - hx - T, c.Y + hy, c.Z), new Vector3(c.X + hx + T, c.Y + hy + T, top), true),
            new(new Vector3(c.X - hx - T, c.Y - hy, c.Z), new Vector3(c.X - hx, c.Y + hy, top), true),
            new(new Vector3(c.X + hx, c.Y - hy, c.Z), new Vector3(c.X + hx + T, c.Y + hy, top), true),
            new(new Vector3(c.X - hx - T, c.Y - hy - T, top), new Vector3(c.X + hx + T, c.Y + hy + T, top + T), true),
        ];
    }

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
    /// Where the enemies walk: the arena alone (design section B), every place of the battle a seed. No cell of the Stormbird: no walk or drop leads there and the box leaves it out.
    /// </summary>
    public static NavArea WalkArea() => new(new Vector2(-ArenaHalf), new Vector2(ArenaHalf), [LandingZone, Position1, Position2, Position3, Hq, .. Sources]);

    /// <summary>Builds the map; the same on every machine (no install, no random numbers).</summary>
    public static TfwMapData Build()
    {
        var corners = Solids.SelectMany(Triangles).ToArray();
        var mesh = new StaticMesh(corners, Enumerable.Range(0, corners.Length).ToArray());
        var collision = new CollisionWorld(mesh, mesh);
        return new TfwMapData(collision, Spawns, StartAreas, collision.ContentHash ^ HashSalt ^ Version);
    }
}
