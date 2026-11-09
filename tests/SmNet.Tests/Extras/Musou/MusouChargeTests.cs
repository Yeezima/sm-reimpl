using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The owner, 2026-11-09: "make orks run 20% faster, and sprint to 'charge' the enemy when they reach within 30m of an enemy" (<see cref="MusouChargeRules"/>). On an open floor in a war's pool
/// (the crowd rules on): an Ork far from his enemy runs at 1.2x his speed, a melee Ork within 30 m sprints at the original sprint's ratio (at most 9.9 m/s), the gun kinds and the Warboss do not,
/// and the old switch keeps the speeds before.
/// </summary>
public class MusouChargeTests
{
    static MusouSim Floor() =>
        new(NavGraph.Build(new NavigationTests.Level().Floor(0f, -30f, 300f, 30f).Mesh(), null, NavSettings.From(new SimConfig()))) { Swarm = MusouSwarmRules.Default };

    /// <summary>The kind's ground speed (m/s) averaged over 0.5 s, from <paramref name="gap"/> m short of a player at x 280; whether it was charging then.</summary>
    static (float Speed, bool Charging) Pace(MusouEnemyType type, float gap, bool old = false)
    {
        {
            var sim = Floor();
            sim.OldOrkPace = old;
            var player = new Vector3(280f, 0f, 0f);
            var start = new Vector3(player.X - gap - 8f, 0f, 0f);   // 8 m of run-up, about a third of a second
            ushort id = sim.Spawn(type, start, -MathF.PI / 2f, 1e6f, sim.Walk!.Nearest(start));
            var targets = new[] { new MusouTarget(1, player, true) };
            var attacks = new List<MusouAttack>();
            uint tick = 0;
            for (int t = 0; t < 20; t++) { sim.BeginTick(++tick); sim.Step(targets, null, false, attacks); }   // past the spawning stand
            float sum = 0f;
            for (int t = 0; t < 15; t++)
            {
                sim.BeginTick(++tick); sim.Step(targets, null, false, attacks);
                Assert.True(sim.TryGet(id, out var e));
                sum += e.Velocity.Length();   // the ground speed (within 25 m he heads for his own place round the target, not straight at it)
            }
            return (sum / 15f, sim.Charging(id));
        }
    }

    static float Speed(MusouEnemyType type) => new MusouRules().Of(type).Speed;

    [Theory]
    [InlineData(MusouEnemyType.Boy)]
    [InlineData(MusouEnemyType.ShootaBoy)]
    [InlineData(MusouEnemyType.Nob)]
    [InlineData(MusouEnemyType.BossNob)]
    [InlineData(MusouEnemyType.Warboss)]
    public void Far_from_his_enemy_an_ork_runs_a_fifth_faster_and_the_old_switch_keeps_his_speed(MusouEnemyType type)
    {
        var (now, charging) = Pace(type, 80f);
        Assert.False(charging);
        Assert.Equal(Speed(type) * 1.2f, now, 0.15f);
        Assert.Equal(Speed(type), Pace(type, 80f, old: true).Speed, 0.15f);
    }

    [Theory]
    [InlineData(MusouEnemyType.Boy)]
    [InlineData(MusouEnemyType.ArdBoy)]
    [InlineData(MusouEnemyType.Nob)]
    [InlineData(MusouEnemyType.BossNob)]
    public void Within_thirty_metres_a_melee_ork_sprints_at_the_original_sprint_ratio(MusouEnemyType type)
    {
        var (now, charging) = Pace(type, 20f);
        Assert.True(charging);
        float want = MusouChargeRules.SprintSpeed(Speed(type));
        Assert.Equal(want, now, 0.3f);
        Assert.InRange(want, Speed(type) * 1.2f * 1.3f, MusouChargeRules.SprintMost + 0.01f);
        Assert.False(Pace(type, 20f, old: true).Charging);
    }

    [Theory]
    [InlineData(MusouEnemyType.ShootaBoy)]
    [InlineData(MusouEnemyType.BigShootaNob)]
    [InlineData(MusouEnemyType.BossShootaBoy)]
    [InlineData(MusouEnemyType.Warboss)]
    public void The_gun_kinds_and_the_warboss_never_charge(MusouEnemyType type)
    {
        Assert.False(MusouChargeRules.Charges(type));
        Assert.False(Pace(type, 25f).Charging);
    }

    [Fact]
    public void The_sprint_is_the_clips_ratio_capped_at_the_clips_speed()
    {
        Assert.Equal(9.91f / 5.98f, MusouChargeRules.SprintOverRun, 4);
        Assert.Equal(9.9f, MusouChargeRules.SprintSpeed(5.2f), 3);                 // a Boy: 6.24 x 1.66 = 10.3, capped
        Assert.Equal(4.4f * 1.2f * 9.91f / 5.98f, MusouChargeRules.SprintSpeed(4.4f), 3);   // a Nob or Boss Ork: 8.75
        Assert.False(MusouChargeRules.Paced(MusouEnemyType.BombSquig));
        Assert.False(MusouChargeRules.Paced(MusouEnemyType.ChaosMarine));
    }

    [Fact]
    public void The_client_plays_the_sprint_past_half_way_from_run_to_sprint_and_leaves_it_below_three_tenths()
    {
        float run = 6.24f, sprint = 9.9f;
        Assert.False(MusouGaitRules.Sprinting(7.5f, run, sprint, was: false));
        Assert.True(MusouGaitRules.Sprinting(8.2f, run, sprint, was: false));
        Assert.True(MusouGaitRules.Sprinting(7.5f, run, sprint, was: true));
        Assert.False(MusouGaitRules.Sprinting(7.0f, run, sprint, was: true));
        Assert.False(MusouGaitRules.Sprinting(12f, run, run, was: false));   // no sprint above the run: never
    }
}
