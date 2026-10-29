using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Map edges (M18), synthetic geometry only: a thin panel in the character mesh stops a walking player at a ledge, shots
/// use the weapon mesh only, kill volumes kill (also a fall too fast to land inside a thin one) and are in the hash.
/// </summary>
public class MapEdgeTests
{
    static readonly SimConfig Config = new();

    /// <summary>A 20 × 20 m floor at z 0 ending at y = 10 (open beyond), optionally with a 3 m panel standing on that edge.</summary>
    static StaticMesh Ledge(bool panel)
    {
        var v = new List<Vector3> { new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0) };
        var i = new List<int> { 0, 1, 2, 0, 2, 3 };
        if (panel)
        {
            v.AddRange([new(-10, 10, 0), new(10, 10, 0), new(10, 10, 3), new(-10, 10, 3)]);
            i.AddRange([4, 5, 6, 4, 6, 7]);
        }
        return new StaticMesh([.. v], [.. i]);
    }

    static readonly KillVolume Below = new(new Vector3(0, 0, -10), new Vector3(125, 125, 0.5f), 0f);   // like Manufactorum's 250 × 250 × 1 box at z −10

    static World Walk(CollisionWorld level, int ticks, out bool diedAtTick)
    {
        var world = new World(Config, level, [new SpawnPoint(new Vector3(0, 5, 0), 0f)]);
        world.Spawn(1);
        diedAtTick = false;
        for (int t = 0; t < ticks && !diedAtTick; t++)
        {
            world.Step(_ => new PlayerInput(0, 0, 1, 0f, InputButtons.Sprint));   // yaw 0 walks toward +Y, off the ledge
            diedAtTick = !world.Players[1].Alive;
        }
        return world;
    }

    [Fact]
    public void A_panel_in_the_character_mesh_stops_a_player_at_the_ledge()
    {
        var world = Walk(new CollisionWorld(Ledge(panel: true), Ledge(panel: false)), 150, out bool died);
        Assert.False(died);
        var p = world.Players[1];
        Assert.True(p.Grounded);
        Assert.InRange(p.Position.Y, 9f, 10f);
        Assert.Equal(0f, p.Position.Z, 3);
    }

    [Fact]
    public void Without_the_panel_the_player_walks_off_and_a_kill_volume_below_kills()
    {
        var open = Ledge(panel: false);
        Walk(new CollisionWorld(open, open, [Below]), 300, out bool died);
        Assert.True(died);
        // Without it the fall goes on until KillDepth below the mesh, later.
        int killedWithVolume = TicksToDeath(new CollisionWorld(open, open, [Below]));
        int killedByDepth = TicksToDeath(new CollisionWorld(open, open));
        Assert.True(killedWithVolume < killedByDepth, $"{killedWithVolume} vs {killedByDepth}");
    }

    static int TicksToDeath(CollisionWorld level)
    {
        var world = new World(Config, level, [new SpawnPoint(new Vector3(0, 5, 0), 0f)]);
        world.Spawn(1);
        for (int t = 1; t <= 3000; t++)
        {
            world.Step(_ => new PlayerInput(0, 0, 1, 0f, InputButtons.Sprint));
            if (!world.Players[1].Alive) return t;
        }
        return int.MaxValue;
    }

    [Fact]
    public void Shots_use_the_weapon_mesh_so_a_character_only_panel_does_not_stop_them()
    {
        var level = new CollisionWorld(Ledge(panel: true), Ledge(panel: false));
        var from = new Vector3(0, 5, 1.5f);
        Assert.Null(level.Weapons.Raycast(from, Vector3.UnitY, 20f));
        Assert.NotNull(level.Character.Raycast(from, Vector3.UnitY, 20f));
    }

    [Fact]
    public void A_kill_volume_catches_a_fall_that_crosses_it_between_ticks_and_a_body_reaching_into_it()
    {
        var thin = new KillVolume(new Vector3(0, 0, -10), new Vector3(5, 5, 0.05f), 0f);
        Assert.False(thin.Contains(new Vector3(0, 0, -9)) || thin.Contains(new Vector3(0, 0, -11)));
        Assert.True(thin.Crosses(new Vector3(0, 0, -9), new Vector3(0, 0, -11)));        // swept through in one tick
        Assert.False(thin.Crosses(new Vector3(6, 0, -9), new Vector3(6, 0, -11)));       // beside it
        var level = new CollisionWorld(Ledge(false), Ledge(false), [thin]);
        Assert.True(level.KillsPlayer(new Vector3(0, 0, -9), new Vector3(0, 0, -11), 2f));
        Assert.True(level.KillsPlayer(new Vector3(0, 0, -11.5f), new Vector3(0, 0, -11.5f), 2f));   // feet below, body through it
        Assert.False(level.KillsPlayer(new Vector3(0, 0, -13f), new Vector3(0, 0, -13f), 2f));
        // Turned about Z: a 10 × 1 box turned 90° covers x ±0.5, y ±5.
        var turned = new KillVolume(Vector3.Zero, new Vector3(5, 0.5f, 1), MathF.PI / 2);
        Assert.True(turned.Contains(new Vector3(0.4f, 4.5f, 0)));
        Assert.False(turned.Contains(new Vector3(4.5f, 0.4f, 0)));
    }

    [Fact]
    public void Kill_volumes_are_part_of_the_level_hash()
    {
        var mesh = Ledge(false);
        ulong none = new CollisionWorld(mesh, mesh).ContentHash;
        Assert.Equal(none, new CollisionWorld(mesh, mesh, []).ContentHash);
        ulong one = new CollisionWorld(mesh, mesh, [Below]).ContentHash;
        Assert.NotEqual(none, one);
        Assert.Equal(one, new CollisionWorld(mesh, mesh, [Below]).ContentHash);
        Assert.NotEqual(one, new CollisionWorld(mesh, mesh, [Below with { Center = new Vector3(0, 0, -9) }]).ContentHash);
    }
}
