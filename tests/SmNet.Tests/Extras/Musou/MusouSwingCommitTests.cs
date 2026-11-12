using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// An Ork's melee swing commits to a direction (MusouSim.Swing.cs; owner 2026-11-12): the wind-up tracks the target for its first 30 % at 540 degrees a second, then locks the facing, and the blow needs the
/// target within reach and the 60 degree cone of the locked facing. Measured with a player circling an Ork from the tick its wind-up starts; the restore switch (<see cref="MusouSim.OldSwingTracking"/>)
/// is the earlier behaviour, where the Ork faced the player every tick and the cone could never fail.
/// </summary>
public class MusouSwingCommitTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;

    /// <summary>One swing against a player standing <paramref name="radius"/> from the Ork who, from the tick the wind-up starts, circles it at <paramref name="speed"/> m/s. Hit or not, the angle between the Ork's facing and the player at the blow, and the wind-up's length in ticks.</summary>
    static (bool Hit, float OffDegrees, int WindUpTicks, int LockedAtBlow) Swing(MusouEnemyType type, float radius, float speed, bool old, int direction = 1)
    {
        var sim = new MusouSim(null) { Swarm = MusouSwarmRules.Default, OldSwingTracking = old };
        ushort id = sim.Spawn(type, Vector3.Zero, 0f, 1e6f);   // yaw 0 faces +y
        float phi = MathF.PI / 2f, w = direction * speed / radius;
        var attacks = new List<MusouAttack>();
        bool started = false, hit = false;
        int windUp = Math.Max(1, (int)MathF.Round(sim.Rules.Of(type).MeleeWindUp * 30f)), startTick = 0;
        float off = -1f;
        int locked = 0;
        for (uint t = 1; t <= 150; t++)
        {
            if (started) phi += w * Dt;
            var at = new Vector3(radius * MathF.Cos(phi), radius * MathF.Sin(phi), 0f);
            sim.BeginTick(t);
            attacks.Clear();
            sim.Step([new MusouTarget(1, at, true)], null, true, attacks);
            Assert.True(sim.TryGet(id, out var e));
            if (!started && e.State == MusouEnemyState.WindUp) { started = true; startTick = (int)t; }
            if (started && e.State == MusouEnemyState.WindUp && sim.SwingLocked(id, windUp)) locked++;
            if (started && e.State == MusouEnemyState.Strike)
            {
                hit = attacks.Any(a => a.Enemy == id);
                var to = new Vector2(at.X - e.Position.X, at.Y - e.Position.Y);
                var fwd = new Vector2(-MathF.Sin(sim.YawOf(id)), MathF.Cos(sim.YawOf(id)));
                off = MathF.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(to), fwd), -1f, 1f)) * 180f / MathF.PI;
                return (hit, off, (int)t - startTick, locked);
            }
        }
        Assert.Fail("the Ork never swung");
        return default;
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(4f)]
    [InlineData(6f)]
    [InlineData(8f)]
    public void Before_the_change_a_circling_player_is_always_hit(float speed)
    {
        var (hit, off, _, _) = Swing(MusouEnemyType.Boy, 1.2f, speed, old: true);
        Assert.True(hit, $"missed at {speed} m/s");
        Assert.True(off < 5f, $"the Ork faced the player to within {off} degrees at the blow");
    }

    [Fact]
    public void A_player_standing_still_is_still_hit()
    {
        var (hit, off, windUp, locked) = Swing(MusouEnemyType.Boy, 1.2f, 0f, old: false);
        Assert.True(hit);
        Assert.True(off < 5f, $"{off} degrees off");
        Assert.Equal(15, windUp);
        Assert.True(locked > 0);
    }

    [Theory]
    [InlineData(2f, true)]    // 95 degrees a second round a Boy: about 0.33 s locked is 31 degrees, inside the 60 degree cone
    [InlineData(3f, true)]    // 143 degrees a second: 43 degrees
    [InlineData(5f, false)]   // 238 degrees a second: 71 degrees, outside the cone
    [InlineData(7f, false)]
    [InlineData(-5f, false)]  // the other way round
    public void Circling_a_Boy_dodges_the_swing_from_a_run_but_not_a_walk(float speed, bool hit)
    {
        var (h, off, _, locked) = Swing(MusouEnemyType.Boy, 1.2f, MathF.Abs(speed), old: false, direction: speed < 0f ? -1 : 1);
        output.WriteLine($"Boy r 1.2 m, {speed} m/s: hit {h}, {off:0.0} degrees off the locked facing at the blow, {locked} locked ticks");
        Assert.Equal(hit, h);
        if (!hit) Assert.True(off > 60f);
    }

    [Fact]
    public void The_lowest_dodging_speed_round_a_Boy_is_measured()
    {
        float lowest = 0f;
        for (float v = 0.5f; v <= 10f; v += 0.25f)
            if (!Swing(MusouEnemyType.Boy, 1.2f, v, old: false).Hit) { lowest = v; break; }
        output.WriteLine($"Boy at 1.2 m: the swing is dodged from {lowest} m/s ({lowest / 1.2f * 180f / MathF.PI:0} degrees a second) on");
        Assert.InRange(lowest, 3.5f, 4.5f);
        for (float v = 0.5f; v <= 10f; v += 0.25f) Assert.False(Swing(MusouEnemyType.Boy, 1.2f, v, old: true).Hit == false, $"the old rule missed at {v} m/s");
    }

    [Fact]
    public void The_facing_follows_the_target_early_then_locks_for_the_rest_of_the_wind_up()
    {
        var sim = new MusouSim(null) { Swarm = MusouSwarmRules.Default };
        ushort id = sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 1e6f);
        var attacks = new List<MusouAttack>();
        var yaws = new List<float>();
        float phi = MathF.PI / 2f;
        bool started = false;
        for (uint t = 1; t <= 80 && yaws.Count < 16; t++)
        {
            if (started) phi += 5f / 1.2f * Dt;
            sim.BeginTick(t);
            attacks.Clear();
            sim.Step([new MusouTarget(1, new Vector3(1.2f * MathF.Cos(phi), 1.2f * MathF.Sin(phi), 0f), true)], null, true, attacks);
            sim.TryGet(id, out var e);
            if (!started && e.State == MusouEnemyState.WindUp) started = true;
            if (started) yaws.Add(sim.YawOf(id));
        }
        // Wind-up ticks 1..6 (the first 30 % of 15) may turn; 7 and on never do.
        Assert.True(yaws.Count >= 10);
        Assert.NotEqual(yaws[0], yaws[3]);                                  // still tracking in the first share
        for (int k = 5; k < 14; k++) Assert.Equal(yaws[4], yaws[k]);         // locked after it
        Assert.Equal(4, MusouSwingRules.TrackTicks(15));
    }

    [Theory]
    [InlineData(MusouEnemyType.Nob, 1.4f)]
    [InlineData(MusouEnemyType.Gretchin, 0.9f)]
    [InlineData(MusouEnemyType.BossBoy, 2.2f)]
    public void Every_melee_kind_commits_the_same_way(MusouEnemyType type, float radius)
    {
        var fast = Swing(type, radius, 9f, old: false);
        var oldFast = Swing(type, radius, 9f, old: true);
        var still = Swing(type, radius, 0f, old: false);
        output.WriteLine($"{type}: standing hit {still.Hit}; circling at 9 m/s new hit {fast.Hit} ({fast.OffDegrees:0} degrees off), old hit {oldFast.Hit}");
        Assert.True(still.Hit);
        Assert.True(oldFast.Hit);
        Assert.False(fast.Hit);
    }

    [Fact]
    public void The_Warboss_swing_commits_too_and_his_slam_and_charge_are_left_to_the_boss_controller()
    {
        // His ordinary swing goes through the same wind-up; at his 5.4 m reach a circle at 5 m is slow in degrees a second, so only a very fast player (or one stepping out) dodges: stated, not hidden.
        var still = Swing(MusouEnemyType.Warboss, 4.8f, 0f, old: false);
        var slow = Swing(MusouEnemyType.Warboss, 4.8f, 6f, old: false);
        var oldSlow = Swing(MusouEnemyType.Warboss, 4.8f, 6f, old: true);
        output.WriteLine($"Warboss r 4.8 m: standing hit {still.Hit}; at 6 m/s ({6f / 4.8f * 180f / MathF.PI:0} deg/s) new hit {slow.Hit} ({slow.OffDegrees:0} off), old hit {oldSlow.Hit}");
        Assert.True(still.Hit);
        Assert.True(oldSlow.Hit);
        Assert.True(slow.OffDegrees > 0f);
    }

    [Fact]
    public void The_5_wave_run_without_the_crowd_rules_keeps_its_facing_rule()
    {
        // No Swarm: the pool as the 5-wave run has it: the swing still faces its target to the blow.
        var sim = new MusouSim(null);
        ushort id = sim.Spawn(MusouEnemyType.Boy, Vector3.Zero, 0f, 1e6f);
        float phi = MathF.PI / 2f;
        var attacks = new List<MusouAttack>();
        bool started = false, hit = false;
        for (uint t = 1; t <= 150 && !hit; t++)
        {
            if (started) phi += 7f / 1.2f * Dt;
            sim.BeginTick(t);
            attacks.Clear();
            sim.Step([new MusouTarget(1, new Vector3(1.2f * MathF.Cos(phi), 1.2f * MathF.Sin(phi), 0f), true)], null, true, attacks);
            sim.TryGet(id, out var e);
            if (!started && e.State == MusouEnemyState.WindUp) started = true;
            hit = attacks.Any(a => a.Enemy == id);
        }
        Assert.True(hit);
    }

    [Fact]
    public void The_restore_switch_is_hashed_only_when_on_and_only_under_Musou()
    {
        var musou = new SimConfig { Extras = ContentExtras.Musou };
        Assert.NotEqual(musou.Hash(), (musou with { MusouOrkSwingTracking = true }).Hash());
        var extended = new SimConfig { Extras = ContentExtras.Orks };
        Assert.Equal(extended.Hash(), (extended with { MusouOrkSwingTracking = true }).Hash());
    }
}
