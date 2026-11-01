using System.Numerics;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// A synthetic battlefield for the war's tests (M1: the game's own is Manufactorum from the install, which tests never read): an empty walled square with three capture points and a start
/// area for each side, composed by the real <see cref="TfwMap.Compose"/>. Nothing here comes from the install.
/// </summary>
public static class TfwTestArena
{
    public const float ArenaHalf = 120f, WallHeight = 24f, CaptureRadius = 12f;
    public static readonly Vector3 LandingZone = new(0f, -100f, 0f), Position1 = new(-40f, -45f, 0f), Position2 = new(35f, 10f, 0f), Position3 = new(-25f, 60f, 0f), Hq = new(0f, 105f, 0f);
    public static readonly Vector3[] Sources = [new(-110f, -40f, 0f), new(110f, 15f, 0f), new(-110f, 70f, 0f), new(60f, 112f, 0f)];

    /// <summary>The floor slab and four walls.</summary>
    public static readonly TfwSolid[] Solids = BuildSolids();

    static TfwSolid[] BuildSolids()
    {
        const float H = ArenaHalf, T = TfwMap.WallThickness, O = H + T;
        return
        [
            new(new Vector3(-O, -O, -T), new Vector3(O, O, 0f)),
            new(new Vector3(-O, -O, 0f), new Vector3(O, -H, WallHeight)),
            new(new Vector3(-O, H, 0f), new Vector3(O, O, WallHeight)),
            new(new Vector3(-O, -H, 0f), new Vector3(-H, H, WallHeight)),
            new(new Vector3(H, -H, 0f), new Vector3(O, H, WallHeight)),
        ];
    }

    /// <summary>The level alone, before the Stormbird.</summary>
    public static CollisionWorld Level()
    {
        var corners = Solids.SelectMany(TfwMap.Triangles).ToArray();
        var mesh = new StaticMesh(corners, Enumerable.Range(0, corners.Length).ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    public static readonly ControlPoint[] Points = [new("a", Position1, CaptureRadius, 10f), new("b", Position2, CaptureRadius, 10f), new("c", Position3, CaptureRadius, 10f)];
    public static readonly StartArea[] Areas = [new(Team.SpaceMarines, LandingZone, new Vector3(10f, 8f, 5f), 0f), new(Team.Chaos, Hq, new Vector3(10f, 8f, 5f), MathF.PI)];

    /// <summary>The world as both ends compose it.</summary>
    public static TfwMapData Build() => TfwMap.Compose(Level(), Points, Areas, [LandingZone, Hq, .. Sources]);

    public static readonly TfwMapData Data = Build();
    public static TfwBattlefield Field => Data.Field;
    public static TfwHub Hub => Data.Field.Hub;
    public static IReadOnlyList<SpawnPoint> Spawns => Data.Spawns;

    /// <summary>The fixed layout of the old arena's tests, in order: the drawn one is <see cref="TfwSites"/>'s.</summary>
    public static TfwLayout Layout(TfwLane lane = TfwLane.A, int node = 1) =>
        new(LandingZone, [Position1, Position2, Position3], Hq, Sources, [CaptureRadius, CaptureRadius, CaptureRadius]);

    /// <summary>The server's map, as <c>GameServer.GeneratedMap</c> makes it from an install.</summary>
    public static GameMap ServerMap() => GameServer.TfwGameMap(Data);
}
