using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// M44a: how a test bot finds the other side's spawn room, its door and where to wait (<see cref="SpawnRoomWatch"/>; a test aid, ours). Synthetic level: a closed
/// room for Chaos on the west (x -20 to -4), a 4 m wide corridor east of it (x -4 to 0) sealed by a Chaos blocker box at x = -2 that stops Space Marines, and an
/// open yard (x 0 to 40) with the Space Marines' start at its east end. A ledge next to the room that only a jump reaches stands north of the yard.
/// </summary>
public class SpawnRoomWatchTests
{
    static readonly SimConfig Config = new();
    static readonly NavSettings Settings = NavSettings.From(Config);
    static readonly Vector3 ChaosStart = new(-15, 0, 0), MarinesStart = new(35, 0, 0);

    static CollisionWorld World(bool sealRoom = true, bool island = false)
    {
        var level = new NavigationTests.Level()
            .Floor(-20, -10, -4, 10)                    // the room
            .Floor(-4, -2, 0, 2)                        // the corridor
            .Floor(0, -20, 40, 20)                      // the yard
            .Wall(-20, -10, -20, 10).Wall(-20, 10, -4, 10).Wall(-20, -10, -4, -10)   // the room's walls
            .Wall(-4, 10, -4, 2).Wall(-4, -10, -4, -2)  // the room's east wall either side of the corridor
            .Wall(-4, 2, 0, 2).Wall(-4, -2, 0, -2);     // the corridor's walls
        if (island) level.Floor(60, 60, 70, 70);        // a patch nobody reaches
        var mesh = level.Mesh();
        SpawnBlocker[] blockers = sealRoom ? [new SpawnBlocker(Team.Chaos, new Vector3(-2, 0, 2), new Vector3(1, 4, 4), 0f)] : [];
        return new CollisionWorld(mesh, mesh, null, blockers);
    }

    static SpawnRoomWatch? Watch(CollisionWorld world, Team me = Team.SpaceMarines)
    {
        var enemy = MatchRules.Other(me);
        var graph = NavGraph.Build(world.CharacterFor(me), null, Settings);
        var enemyGraph = NavGraph.Build(world.CharacterFor(enemy), null, Settings);
        return SpawnRoomWatch.Build(graph, enemyGraph, me, me == Team.SpaceMarines ? MarinesStart : ChaosStart, me == Team.SpaceMarines ? ChaosStart : MarinesStart, world, Config.EyeHeight);
    }

    [Fact]
    public void The_room_is_the_floor_behind_the_blocker_and_the_door_is_just_outside_it()
    {
        var watch = Watch(World())!;
        Assert.NotNull(watch);
        Assert.Equal(Team.Chaos, watch.Enemy);
        Assert.NotNull(watch.Door);
        var door = watch.Door!.Value;
        Assert.InRange(door.X, -2f, 1.5f);               // east of the blocker (x = -2, half thickness 0.5, a body radius more)
        Assert.InRange(MathF.Abs(door.Y), 0f, 2f);
        Assert.True(watch.RoomCells > 500, $"room cells {watch.RoomCells}");
        // Inside: the room, the corridor up to the blocker. Outside: the corridor past it, the yard.
        Assert.True(watch.InRoom(new Vector3(-15, 0, 0)));
        Assert.True(watch.InRoom(new Vector3(-8, 5, 0)));
        Assert.True(watch.InRoom(new Vector3(-3, 0, 0)));
        Assert.False(watch.InRoom(new Vector3(0.5f, 0, 0)));
        Assert.False(watch.InRoom(new Vector3(10, 5, 0)));
        Assert.False(watch.InRoom(MarinesStart));
        // Not in the air above the room, and not on a different level below it.
        Assert.False(watch.InRoom(new Vector3(-15, 0, 6)));
        Assert.False(watch.InRoom(new Vector3(-15, 0, -6)));
    }

    [Fact]
    public void Hold_points_are_8_to_15_m_from_the_door_with_a_clear_line_to_it_and_never_in_the_room()
    {
        var world = World();
        var watch = Watch(world)!;
        Assert.NotEmpty(watch.Holds);
        Assert.True(watch.Holds.Count <= SpawnRoomWatch.MaxHolds);
        var door = watch.Door!.Value;
        foreach (var hold in watch.Holds)
        {
            float d = MathF.Sqrt((hold.X - door.X) * (hold.X - door.X) + (hold.Y - door.Y) * (hold.Y - door.Y));
            Assert.InRange(d, SpawnRoomWatch.HoldMin - 0.01f, SpawnRoomWatch.HoldMax + 0.01f);
            Assert.False(watch.InRoom(hold));
            var eye = hold + new Vector3(0, 0, Config.EyeHeight);
            var chest = door + new Vector3(0, 0, 1.2f);
            Assert.Null(world.Weapons.Raycast(eye, Vector3.Normalize(chest - eye), (chest - eye).Length() - 0.15f));
            Assert.True(world.Blockers.Where(b => b.Side == Team.Chaos).All(b => b.HorizontalDistance(hold) >= 3f));
        }
        // Spread out: no two hold points closer than 2.5 m.
        for (int i = 0; i < watch.Holds.Count; i++)
            for (int j = i + 1; j < watch.Holds.Count; j++)
                Assert.True(Vector3.Distance(watch.Holds[i], watch.Holds[j]) >= 2.5f);
    }

    [Fact]
    public void Without_blockers_the_room_has_no_door_and_nobody_is_in_it()
    {
        var watch = Watch(World(sealRoom: false))!;
        // The bot reaches the room too (nothing seals it): the walk from the enemy's start comes out at once among reachable floor.
        Assert.False(watch.InRoom(new Vector3(-15, 0, 0)));
        Assert.False(watch.InRoom(new Vector3(10, 0, 0)));
    }

    [Fact]
    public void A_ledge_that_only_a_jump_reaches_is_not_part_of_the_room()
    {
        // A raised platform north of the yard, outside the room: the bot's graph has it as unreachable cells (no jump links in a plain graph), but the enemy's walk from its
        // own start never gets there, so it is not "in the room".
        var level = new NavigationTests.Level()
            .Floor(-20, -10, -4, 10).Floor(-4, -2, 0, 2).Floor(0, -20, 40, 20)
            .Wall(-20, -10, -20, 10).Wall(-20, 10, -4, 10).Wall(-20, -10, -4, -10).Wall(-4, 10, -4, 2).Wall(-4, -10, -4, -2).Wall(-4, 2, 0, 2).Wall(-4, -2, 0, -2)
            .Box(new Vector3(20, 12, 0), new Vector3(30, 18, 5))   // a block standing in the yard with a flat top
            ;
        var mesh = level.Mesh();
        var world = new CollisionWorld(mesh, mesh, null, [new SpawnBlocker(Team.Chaos, new Vector3(-2, 0, 2), new Vector3(1, 4, 4), 0f)]);
        var watch = Watch(world)!;
        Assert.NotNull(watch.Door);
        Assert.False(watch.InRoom(new Vector3(25, 15, 5)));   // on top of the block
        Assert.True(watch.InRoom(new Vector3(-15, 0, 0)));
    }

    [Fact]
    public void An_unreachable_enemy_start_gives_no_watch_and_a_cancelled_build_throws()
    {
        var world = World(island: true);
        var graph = NavGraph.Build(world.CharacterFor(Team.SpaceMarines), null, Settings);
        var enemyGraph = NavGraph.Build(world.CharacterFor(Team.Chaos), null, Settings);
        // The enemy start on the island: its walk never comes out among floor the bot reaches, so there is no door and no room.
        var island = SpawnRoomWatch.Build(graph, enemyGraph, Team.SpaceMarines, MarinesStart, new Vector3(65, 65, 0), world, Config.EyeHeight)!;
        Assert.Null(island.Door);
        Assert.False(island.InRoom(new Vector3(65, 65, 0)));
        Assert.Empty(island.Holds);
        // No opponent side (free-for-all): nothing to watch.
        Assert.Null(SpawnRoomWatch.Build(graph, enemyGraph, Team.None, MarinesStart, ChaosStart, world, Config.EyeHeight));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SpawnRoomWatch.Build(graph, enemyGraph, Team.SpaceMarines, MarinesStart, ChaosStart, world, Config.EyeHeight, cancelled.Token));
    }

    [Fact]
    public void Chaos_bots_see_the_Space_Marines_room_the_same_way()
    {
        // Mirror: the same level with the blocker belonging to Space Marines and the sides swapped.
        var level = new NavigationTests.Level()
            .Floor(-20, -10, -4, 10).Floor(-4, -2, 0, 2).Floor(0, -20, 40, 20)
            .Wall(-20, -10, -20, 10).Wall(-20, 10, -4, 10).Wall(-20, -10, -4, -10).Wall(-4, 10, -4, 2).Wall(-4, -10, -4, -2).Wall(-4, 2, 0, 2).Wall(-4, -2, 0, -2);
        var mesh = level.Mesh();
        var world = new CollisionWorld(mesh, mesh, null, [new SpawnBlocker(Team.SpaceMarines, new Vector3(-2, 0, 2), new Vector3(1, 4, 4), 0f)]);
        var graph = NavGraph.Build(world.CharacterFor(Team.Chaos), null, Settings);
        var enemyGraph = NavGraph.Build(world.CharacterFor(Team.SpaceMarines), null, Settings);
        var watch = SpawnRoomWatch.Build(graph, enemyGraph, Team.Chaos, MarinesStart, ChaosStart, world, Config.EyeHeight)!;
        Assert.Equal(Team.SpaceMarines, watch.Enemy);
        Assert.NotNull(watch.Door);
        Assert.True(watch.InRoom(new Vector3(-15, 0, 0)));
        Assert.False(watch.InRoom(new Vector3(20, 0, 0)));
    }
}
