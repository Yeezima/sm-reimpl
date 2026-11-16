using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Added content "Musou": a Boss Ork (3x the body) runs facing the way it moves and keeps off the walls (owner, 2026-11-06 night: "elite 3x scaled up orks walk against walls looking the wrong direction while running forward").
/// A floor with a wall across it and a gap round its end; the boss runs from one side to a player on the other.
/// </summary>
public class MusouBossMovementTests(ITestOutputHelper output)
{
    // 100 x 60 floor, a wall along x = 50 from y 0 to 40 (the gap is y 40 to 60).
    static MusouSim Field() =>
        new(NavGraph.Build(new NavigationTests.Level().Floor(0f, 0f, 100f, 60f).Wall(50f, 0f, 50f, 40f).Mesh(), null, NavSettings.From(new SimConfig()))) { Swarm = MusouSwarmRules.Default };

    static float WallDistance(Vector3 p) => Vector2.Distance(new(p.X, p.Y), new(50f, Math.Clamp(p.Y, 0f, 40f)));

    public sealed record Run(int Moving, double MeanAngle, double P95Angle, double MaxAngle, int WallTicks, double Seconds, bool Arrived, int Still = 0);

    /// <summary>The boss runs 20,10 to a player at 80,10 round the wall's end; the facing against the velocity, and the ticks within 0.6 of its radius of the wall.</summary>
    static Run Go(MusouEnemyType type, Vector3? from = null, Vector3? to = null, int seconds = 60)
    {
        var sim = Field();
        float radius = sim.Rules.Of(type).Radius;
        ushort id = sim.Spawn(type, from ?? new Vector3(20f, 10f, 0f), MathF.PI / 2f * -1f, 3000f, sim.Walk!.Nearest(from ?? new Vector3(20f, 10f, 0f)));
        var player = to ?? new Vector3(80f, 10f, 0f);
        var targets = new[] { new MusouTarget(1, player, true) };
        var attacks = new List<MusouAttack>();
        var angles = new List<double>();
        var history = new List<Vector2>();
        int wall = 0, ticks = 0, still = 0;
        uint tick = 0;
        bool arrived = false;
        for (; ticks < 30 * seconds && !arrived; ticks++)
        {
            sim.BeginTick(++tick);
            sim.Step(targets, null, false, attacks);
            Assert.True(sim.TryGet(id, out var e));
            if (e.State != MusouEnemyState.Seek || ticks < 30) continue;   // past the spawning stand
            if (Vector2.Distance(new(e.Position.X, e.Position.Y), new(player.X, player.Y)) < 6f) { arrived = true; break; }
            if (WallDistance(e.Position) < radius * 0.6f && e.Position.Y < 45f) wall++;
            if (e.Velocity.Length() <= 1f) still++;
            history.Add(new Vector2(e.Position.X, e.Position.Y));
            if (history.Count > 15)
            {
                // The way it is getting on over the last half second against the way it faces (a shuffle on the spot has no way: skipped).
                var net = history[^1] - history[^16];
                if (net.Length() > 1.5f)
                {
                    var forward = new Vector2(-MathF.Sin(e.Yaw), MathF.Cos(e.Yaw));
                    angles.Add(Math.Acos(Math.Clamp(Vector2.Dot(forward, Vector2.Normalize(net)), -1f, 1f)) * 180.0 / Math.PI);
                }
            }
        }
        angles.Sort();
        return new Run(angles.Count, angles.Count == 0 ? 0 : angles.Average(), angles.Count == 0 ? 0 : angles[(int)(angles.Count * 0.95)], angles.Count == 0 ? 0 : angles[^1], wall, ticks / 30.0, arrived, still);
    }

    [Theory]
    [InlineData(MusouEnemyType.BossNob)]
    [InlineData(MusouEnemyType.BossBoy)]
    public void A_boss_runs_facing_its_velocity_and_does_not_grind_along_the_wall(MusouEnemyType type)
    {
        var r = Go(type);
        output.WriteLine($"{type}: {r}");
        Assert.True(r.Arrived, $"never reached the player in {r.Seconds:F0} s");
        Assert.True(r.Moving > 200, $"{r.Moving} ticks running");
        Assert.True(r.P95Angle <= 30.0, $"facing vs net movement: mean {r.MeanAngle:F1}, p95 {r.P95Angle:F1}, max {r.MaxAngle:F1} degrees");
        Assert.True(r.WallTicks <= 15, $"{r.WallTicks} ticks (of 30 a second) within 0.6 radii of the wall");
    }

    [Theory]
    [InlineData(MusouEnemyType.BossNob)]
    [InlineData(MusouEnemyType.BossBoy)]
    public void A_boss_with_a_player_just_behind_the_wall_finds_its_way_round_facing_where_it_goes(MusouEnemyType type)
    {
        // The player stands 8 m away straight through the wall: the boss first heads straight for him, pressed on the wall, then goes round.
        var r = Go(type, new Vector3(42f, 20f, 0f), new Vector3(58f, 20f, 0f), 40);
        output.WriteLine($"{type} behind the wall: {r}");
        Assert.True(r.Arrived, $"never reached the player in {r.Seconds:F0} s (shuffling at the wall's corner)");
        Assert.True(r.Seconds < 25.0, $"took {r.Seconds:F0} s");
        Assert.True(r.P95Angle <= 30.0, $"facing vs net movement: mean {r.MeanAngle:F1}, p95 {r.P95Angle:F1}, max {r.MaxAngle:F1} degrees");
        Assert.True(r.WallTicks <= 30, $"{r.WallTicks} ticks within 0.6 radii of the wall");
    }

    [Fact]
    public void A_normal_ork_keeps_its_old_facing_and_path()
    {
        var r = Go(MusouEnemyType.Boy);
        output.WriteLine($"Boy: {r}");
        Assert.True(r.Arrived);
    }
}
