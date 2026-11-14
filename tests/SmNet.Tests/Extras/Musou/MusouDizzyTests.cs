using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Owner, 2026-11-14 (Musou Mode): a stunned boss keeps its facing and stands dizzy (idle, a slow lean, stars circling above its head) for exactly as long as the stun lasts. The sim never turns a stunned boss and
/// its snapshot yaw is constant through the stun; the client draws it dizzy by <see cref="MusouDizzyRules.Dizzy"/> from the snapshot's type and state alone (no wire field).
/// </summary>
public class MusouDizzyTests
{
    static readonly MusouEnemyType[] Bosses =
        [MusouEnemyType.Warboss, MusouEnemyType.BossBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob];

    static (MusouSim Sim, MusouTarget[] Targets, ushort Id, Func<uint> Step) Arena(MusouEnemyType type, float yaw)
    {
        var rules = MusouRules.Default with { Boss = MusouBossRules.Default with { SlamFirst = 1000f, ChargeFirst = 1000f } };
        var sim = new MusouSim(null, rules) { TickRate = 30, War = TfwCombatRules.Default };
        ushort id = sim.Spawn(type, Vector3.Zero, yaw, 4000f);
        var targets = new[] { new MusouTarget(1, new Vector3(0f, 6f, 0f), true) };   // behind the boss's facing when yaw is 0: it would turn to it if it could
        uint tick = 0;
        var attacks = new List<MusouAttack>();
        uint Step() { sim.BeginTick(++tick); attacks.Clear(); sim.Step(targets, null, true, attacks); return tick; }
        for (int k = 0; k < 60; k++) Step();   // out of its spawn stand
        return (sim, targets, id, Step);
    }

    [Fact]
    public void A_stunned_boss_does_not_turn_for_the_whole_stun_in_the_sim_or_the_snapshot()
    {
        foreach (var type in Bosses)
        {
            var (sim, _, id, step) = Arena(type, 0.7f);
            float before = sim.YawOf(id);
            Assert.True(sim.StaggerMelee(id, Vector2.UnitY, 2f, 1));
            int window = (int)MathF.Round(MusouRules.Default.Combat.StaggerSeconds * 30f);
            for (int k = 0; k < window - 1; k++)
            {
                step();
                Assert.Equal(MusouEnemyState.Stunned, sim.TryGet(id, out var e) ? e.State : default);
                Assert.Equal(before, e.Yaw);   // the snapshot's yaw: not turned toward the player behind it, not turned away
                Assert.Equal(before, sim.YawOf(id));
            }
        }
    }

    [Fact]
    public void The_dizzy_state_starts_with_the_stun_and_ends_the_tick_it_ends()
    {
        foreach (var type in Bosses)
        {
            var (sim, _, id, step) = Arena(type, 0f);
            Assert.True(sim.TryGet(id, out var e));
            Assert.False(MusouDizzyRules.Dizzy(e.Type, e.State));   // standing free: not dizzy
            sim.StaggerMelee(id, Vector2.UnitY, 2f, 1);
            sim.TryGet(id, out e);
            Assert.True(MusouDizzyRules.Dizzy(e.Type, e.State));   // dizzy from the moment of the stun
            int window = (int)MathF.Round(MusouRules.Default.Combat.StaggerSeconds * 30f);
            for (int k = 0; k < window - 1; k++) { step(); sim.TryGet(id, out e); Assert.True(MusouDizzyRules.Dizzy(e.Type, e.State), $"{type} tick {k}"); }
            step();
            sim.TryGet(id, out e);
            Assert.False(MusouDizzyRules.Dizzy(e.Type, e.State));   // the stun ended: no stars, no sway
            Assert.False(sim.BossStunned(id));
        }
    }

    [Fact]
    public void Only_a_stunned_boss_is_dizzy_not_an_ordinary_Ork_nor_the_Weirdboys_broken_shield()
    {
        foreach (var type in Enum.GetValues<MusouEnemyType>())
        foreach (var state in Enum.GetValues<MusouEnemyState>())
        {
            bool boss = Bosses.Contains(type);
            Assert.Equal(boss && state == MusouEnemyState.Stunned, MusouDizzyRules.Dizzy(type, state));
        }
    }

    [Fact]
    public void The_stars_circle_evenly_and_the_lean_builds_up_then_goes_round()
    {
        Assert.Equal(3, MusouDizzyRules.StarCount);
        // evenly spaced, all going round together, once a turn every StarOrbitSeconds
        for (int k = 1; k < MusouDizzyRules.StarCount; k++)
            Assert.Equal(MathF.Tau / MusouDizzyRules.StarCount, MusouDizzyRules.StarAngle(k, 0.4f) - MusouDizzyRules.StarAngle(k - 1, 0.4f), 4);
        Assert.Equal(MathF.Tau, MusouDizzyRules.StarAngle(0, MusouDizzyRules.StarOrbitSeconds) - MusouDizzyRules.StarAngle(0, 0f), 4);
        // the lean: none at the start, full after the ramp, its direction a whole turn in SwaySeconds
        Assert.Equal(0f, MusouDizzyRules.Sway(0f).Angle);
        Assert.Equal(MusouDizzyRules.SwayDegrees * MathF.PI / 180f, MusouDizzyRules.Sway(MusouDizzyRules.SwayRampSeconds * 2f).Angle, 5);
        Assert.Equal(MathF.Tau, MusouDizzyRules.Sway(MusouDizzyRules.SwaySeconds).Toward - MusouDizzyRules.Sway(0f).Toward, 4);
        // the stars sit above the head: the Warboss's head is under his banner
        Assert.True(MusouDizzyRules.StarHeight(MusouEnemyType.Warboss) < MusouDizzyRules.StarHeight(MusouEnemyType.BossNob));
        Assert.True(MusouDizzyRules.StarHeight(MusouEnemyType.Warboss) > MusouDizzyRules.HeadFraction(MusouEnemyType.Warboss));
    }
}
