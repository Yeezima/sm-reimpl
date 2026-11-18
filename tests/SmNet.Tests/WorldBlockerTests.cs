using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// The map's invisible movement walls (<see cref="WorldBlocker"/>, classic-maps-fidelity gameplay 2026-11-18): solid for both
/// sides and for players with no side, in every way of moving, as a box (any rotation), a ceiling (jump-pack flight) and a
/// cylinder; shots pass through; a level without walls is untouched. Synthetic level only (no game data).
/// </summary>
public class WorldBlockerTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = EvadeTests.Config;
    static float R => Config.CapsuleRadius;
    const int Assault = 0, Tactical = 1;
    static readonly Team[] Everyone = [Team.SpaceMarines, Team.Chaos, Team.None];

    static StaticMesh Floor()
    {
        Vector3[] v = [new(-80, -80, 0), new(80, -80, 0), new(80, 80, 0), new(-80, 80, 0)];
        return new StaticMesh(v, [0, 1, 2, 0, 2, 3]);
    }

    static CollisionWorld Level(params WorldBlocker[] walls)
    {
        var mesh = Floor();
        return new CollisionWorld(mesh, mesh, null, null, null, walls);
    }

    /// <summary>A box of the given full size at the given centre, turned by the object's rotation (degrees about X, then Y, then Z).</summary>
    static WorldBlocker Box(Vector3 centre, Vector3 size, Vector3? degrees = null)
    {
        var r = degrees ?? Vector3.Zero;
        var q = Quaternion.Concatenate(Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, r.X * MathF.PI / 180f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Y * MathF.PI / 180f)),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Z * MathF.PI / 180f));
        return WorldBlocker.Box("wall", Matrix4x4.CreateScale(size) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(centre));
    }

    static PlayerState Fresh(Team side, int cls, Vector3 at, float yaw) =>
        Combat.Respawned(new PlayerState(1, at, Vector3.Zero, yaw, true) { Class = cls, Team = side }, Config);

    static float YawToward(Vector2 d) => MathF.Atan2(-d.X, d.Y);

    [Fact]
    public void Every_side_walking_and_sprinting_into_a_wall_is_stopped()
    {
        var level = Level(Box(new Vector3(3, 0, 10), new Vector3(1, 100, 40)));
        float east = YawToward(Vector2.UnitX);
        foreach (var team in Everyone)
        {
            var s = Fresh(team, Tactical, new Vector3(-6, 0, 0), east);
            float deepest = float.MinValue;
            for (int t = 0; t < 120; t++)
            {
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, east, t % 60 < 30 ? InputButtons.Sprint : InputButtons.None), Config, out _, level);
                deepest = MathF.Max(deepest, s.Position.X);
            }
            output.WriteLine($"{team}: end {s.Position}, deepest x {deepest:F3}");
            Assert.True(deepest <= 2.5f - R + 2e-3f, $"{team} reached x {deepest}");
            Assert.True(deepest >= 2.5f - R - 0.05f, $"{team} never reached the wall");
        }
    }

    [Fact]
    public void A_wall_turned_about_Y_and_Z_stops_every_side_too()
    {
        // 44 degrees about Y (as Shattered Bridge's everyone_blocker_01) and a quarter turn about Z, 30 m wide.
        var level = Level(Box(new Vector3(5, 0, 5), new Vector3(2, 60, 30), new Vector3(0, -44, 0)), Box(new Vector3(0, 20, 5), new Vector3(2, 30, 30), new Vector3(0, 0, 90)));
        foreach (var team in Everyone)
        {
            // Toward +Y from (0, 5): the Z-turned slab spans x -15..15 at y 19..21.
            var s = Fresh(team, Tactical, new Vector3(0, 5, 0), YawToward(Vector2.UnitY));
            for (int t = 0; t < 120; t++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, YawToward(Vector2.UnitY), InputButtons.Sprint), Config, out _, level);
            Assert.True(s.Position.Y <= 19f - R + 2e-3f, $"{team} reached y {s.Position.Y}");
            Assert.True(s.Position.Y > 17f, $"{team} stopped at y {s.Position.Y}");
        }
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(400f)]
    public void Gliding_flying_and_falling_fast_do_not_tunnel_through_a_wall(float speed)
    {
        var level = Level(Box(new Vector3(0, 0, 19), new Vector3(1, 24, 40)));
        float east = YawToward(Vector2.UnitX);
        foreach (var team in Everyone)
        {
            var glide = Fresh(team, Tactical, new Vector3(-3, 0, 0), east);
            var fly = Fresh(team, Tactical, new Vector3(-3, 0, 10), east) with { Grounded = false };
            var fall = Fresh(team, Tactical, new Vector3(-3, 0, 30), east) with { Grounded = false, Velocity = new Vector3(speed, 0, -speed) };
            for (int t = 0; t < 10; t++)
            {
                glide = Movement.Glide(glide, east, new Vector2(speed, 0f), Config, level);
                fly = Movement.Fly(fly, east, new Vector2(speed, 0f), 0f, 0f, Config, level);
                fall = Movement.Fly(fall, east, new Vector2(fall.Velocity.X, fall.Velocity.Y), fall.Velocity.Z, Config.Gravity, Config, level);
                foreach (var (what, p) in new[] { ("glide", glide.Position), ("flight", fly.Position), ("fall", fall.Position) })
                    Assert.True(p.X <= -0.5f - R + 2e-3f, $"{team} {what} at {speed} m/s reached {p}");
            }
        }
    }

    [Fact]
    public void A_ceiling_wall_keeps_a_jump_pack_under_it_for_both_sides()
    {
        // 200 x 200 x 2 slab whose underside is 9 m up (the Waste Management / Station Tertius ceilings), over a floor at 0:
        // the jump pack's highest lift (17.5 m) would clear it.
        var ceiling = Box(new Vector3(0, 0, 10), new Vector3(200, 200, 2));
        var open = Level();
        var level = Level(ceiling);
        float east = YawToward(Vector2.UnitX);
        foreach (var team in new[] { Team.SpaceMarines, Team.Chaos })
        {
            float openTop = 0f, top = 0f;
            var free = Fresh(team, Assault, new Vector3(0, 0, 0), east);
            var held = Fresh(team, Assault, new Vector3(0, 0, 0), east);
            for (int t = 0; t < 150; t++)
            {
                var buttons = t % 75 < 20 ? InputButtons.Jump : InputButtons.None;
                free = Combat.StepPlayer(free, new PlayerInput(0, 0, 0, east, buttons), Config, out _, open);
                held = Combat.StepPlayer(held, new PlayerInput(0, 0, 0, east, buttons), Config, out _, level);
                openTop = MathF.Max(openTop, free.Position.Z + Config.CapsuleHeight);
                top = MathF.Max(top, held.Position.Z + Config.CapsuleHeight);
            }
            output.WriteLine($"{team}: highest head {openTop:F2} m without the ceiling, {top:F2} m with it (underside 9 m)");
            Assert.True(openTop > 12f, $"the jump never rose above the ceiling's height ({openTop})");
            Assert.True(top <= 9f + 2e-3f, $"{team} head rose to {top}");
        }
    }

    [Fact]
    public void A_cylinder_wall_contains_the_true_cylinder_and_stops_every_side()
    {
        // Diameter 10 about (0, 0), 30 m tall: the 16-sided prism's faces touch the circle, so a ray in from any heading meets it at 15 m or less.
        var level = Level(WorldBlocker.Cylinder("pillar", Matrix4x4.CreateScale(10f, 10f, 30f) * Matrix4x4.CreateTranslation(0, 0, 14)));
        foreach (float degrees in new[] { 0f, 10f, 22.5f, 45f, 100f, 190f, 270f, 333f })
        {
            float a = degrees * MathF.PI / 180f;
            var from = new Vector3(20f * MathF.Cos(a), 20f * MathF.Sin(a), 1f);
            var hit = level.CharacterFor(Team.Chaos).Raycast(from, -new Vector3(MathF.Cos(a), MathF.Sin(a), 0f), 30f);
            Assert.NotNull(hit);
            Assert.InRange(hit.Value.Distance, 14.5f, 15f + 1e-3f);
        }
        foreach (var team in Everyone)
        {
            var s = Fresh(team, Tactical, new Vector3(-12, 0, 0), YawToward(Vector2.UnitX));
            float nearest = float.MaxValue;
            for (int t = 0; t < 90; t++)
            {
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, YawToward(Vector2.UnitX), InputButtons.Sprint), Config, out _, level);
                nearest = MathF.Min(nearest, MathF.Sqrt(s.Position.X * s.Position.X + s.Position.Y * s.Position.Y));
            }
            // It slides round the prism's corner (the heading meets a vertex) but never gets nearer than a radius off the true cylinder's side.
            output.WriteLine($"{team}: end {s.Position}, nearest to the axis {nearest:F3} m");
            Assert.True(nearest >= 5f + R - 0.02f, $"{team} came within {nearest} m of the axis");
            Assert.True(nearest <= 5f + R + 1f, $"{team} never reached the cylinder ({nearest})");
        }
    }

    [Fact]
    public void A_wall_top_is_not_ground_a_player_landing_on_it_slides_off_and_the_bot_graph_has_no_cell_there()
    {
        // 6 x 6 x 10 m wall on a floor at 0: its top is at z 10, flat. In the original there is no invisible ledge to stand on.
        var level = Level(Box(new Vector3(0, 0, 5), new Vector3(6, 6, 10)));
        foreach (var team in Everyone)
        {
            var s = Fresh(team, Tactical, new Vector3(0.3f, 0.2f, 12f), 0f) with { Grounded = false, Velocity = Vector3.Zero };
            bool stoodOnTop = false;
            for (int t = 0; t < 400; t++)
            {
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), Config, out _, level);
                if (s.Grounded && s.Position.Z > 5f) stoodOnTop = true;
            }
            output.WriteLine($"{team}: end {s.Position}, grounded {s.Grounded}");
            Assert.False(stoodOnTop, $"{team} stood on the wall's top");
            Assert.True(s.Position.Z < 1f && s.Grounded, $"{team} ended at {s.Position}, not on the floor");
        }
        var mesh = level.CharacterFor(Team.Chaos);
        Assert.NotNull(mesh.Raycast(new Vector3(0, 0, 20), -Vector3.UnitZ, 20f));
        Assert.True(mesh.IsNoStand(mesh.Raycast(new Vector3(0, 0, 20), -Vector3.UnitZ, 20f)!.Value.Triangle));
        Assert.False(level.Character.IsNoStand(0));
        var graph = NavGraph.Build(mesh, null, NavSettings.From(Config), NavArea.Around([new Vector3(-20, 0, 0), new Vector3(20, 0, 0)], 20f));
        for (int i = 0; i < graph.NodeCount; i++) Assert.True(graph.Position(i).Z < 1f, $"a bot cell at {graph.Position(i)}");
        var bareGraph = NavGraph.Build(Level().Character, null, NavSettings.From(Config), NavArea.Around([new Vector3(-20, 0, 0), new Vector3(20, 0, 0)], 20f));
        Assert.True(bareGraph.NodeCount > graph.NodeCount);
    }

    [Fact]
    public void Shots_pass_through_walls_and_a_level_without_them_is_untouched()
    {
        var wall = Box(new Vector3(3, 0, 10), new Vector3(1, 100, 40));
        var level = Level(wall);
        var bare = Level();
        Assert.Null(level.Weapons.Raycast(new Vector3(0, 0, 1), Vector3.UnitX, 20f));
        foreach (var team in Everyone) Assert.NotNull(level.CharacterFor(team).Raycast(new Vector3(0, 0, 1), Vector3.UnitX, 20f));
        Assert.Same(bare.Character, bare.CharacterFor(Team.None));
        Assert.Same(bare.Character, bare.CharacterFor(Team.Chaos));
        Assert.Empty(bare.Walls);
        Assert.NotEqual(bare.ContentHash, level.ContentHash);
        Assert.Equal(bare.ContentHash, new CollisionWorld(Floor(), Floor(), null, null, null, []).ContentHash);
        // Same walls, same hash; a wall moved by a centimetre, a different one.
        Assert.Equal(level.ContentHash, Level(Box(new Vector3(3, 0, 10), new Vector3(1, 100, 40))).ContentHash);
        Assert.NotEqual(level.ContentHash, Level(Box(new Vector3(3.01f, 0, 10), new Vector3(1, 100, 40))).ContentHash);
    }

    [Fact]
    public void Walls_and_spawn_room_blockers_work_together()
    {
        var marineRoom = new SpawnBlocker(Team.SpaceMarines, new Vector3(-10, 0, 19), new Vector3(1, 24, 40), 0f);
        var mesh = Floor();
        var level = new CollisionWorld(mesh, mesh, null, [marineRoom], null, [Box(new Vector3(10, 0, 10), new Vector3(1, 100, 40))]);
        float east = YawToward(Vector2.UnitX), west = YawToward(-Vector2.UnitX);
        var marine = Fresh(Team.SpaceMarines, Tactical, new Vector3(0, 0, 0), east);
        var chaos = Fresh(Team.Chaos, Tactical, new Vector3(0, 0, 0), west);
        for (int t = 0; t < 120; t++)
        {
            marine = Combat.StepPlayer(marine, new PlayerInput(0, 0, 1, east, InputButtons.Sprint), Config, out _, level);
            chaos = Combat.StepPlayer(chaos, new PlayerInput(0, 0, 1, west, InputButtons.Sprint), Config, out _, level);
        }
        Assert.True(marine.Position.X <= 9.5f - R + 2e-3f, $"marine at {marine.Position}");                // the wall stops it
        Assert.True(marine.Position.X > 5f, $"marine at {marine.Position}");
        Assert.True(chaos.Position.X >= -9.5f + R - 2e-3f, $"chaos at {chaos.Position}");                // the marine room's blocker stops it
    }
}
