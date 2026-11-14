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

    sealed class Rig
    {
        public readonly MusouSim Sim;
        public readonly MusouTarget[] Targets;
        public readonly MusouBoss? Boss;
        public readonly ushort Id;
        public uint Tick;
        readonly List<MusouAttack> _attacks = new();
        readonly List<MusouBossStrike> _strikes = new();

        public Rig(MusouEnemyType type, float yaw, float slamFirst = 1000f, float chargeFirst = 1000f, float distance = 6f, int warm = 60)
        {
            var rules = MusouRules.Default with { Boss = MusouBossRules.Default with { SlamFirst = slamFirst, ChargeFirst = chargeFirst } };
            Sim = new MusouSim(null, rules) { TickRate = 30, War = TfwCombatRules.Default };
            Id = Sim.Spawn(type, Vector3.Zero, yaw, 4000f);
            if (type == MusouEnemyType.Warboss) Boss = new MusouBoss(Sim, null, Id, 4000f);
            Targets = [new MusouTarget(1, new Vector3(0f, distance, 0f), true)];
            for (int k = 0; k < warm; k++) Step();   // out of its spawn stand; it turns to face the player at +Y (yaw about 0)
        }

        public uint Step() { Sim.BeginTick(++Tick); _attacks.Clear(); _strikes.Clear(); Sim.Step(Targets, null, true, _attacks); Boss?.Step(Targets, true, _strikes); return Tick; }

        /// <summary>The player walks to the far side, so the boss's facing is turned away from it (a regression that turns a stunned boss toward its target shows).</summary>
        public Vector2 MovePlayerBehind()
        {
            float yaw = Sim.YawOf(Id);
            var forward = new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
            Targets[0] = new MusouTarget(1, new Vector3(-forward * 6f, 0f), true);
            return forward;
        }

        public MusouEnemy Me => Sim.TryGet(Id, out var e) ? e : default;
    }

    static readonly int Window = (int)MathF.Round(MusouRules.Default.Combat.StaggerSeconds * 30f);

    static void AssertHoldsFacing(Rig r, Vector2 forward, int ticks)
    {
        float before = r.Sim.YawOf(r.Id);
        for (int k = 0; k < ticks; k++)
        {
            r.Step();
            Assert.Equal(MusouEnemyState.Stunned, r.Me.State);
            Assert.Equal(before, r.Me.Yaw);   // the snapshot's yaw: not turned toward the player behind it, not turned away
            Assert.Equal(before, r.Sim.YawOf(r.Id));
        }
        Assert.True(Vector2.Dot(forward, new Vector2(-MathF.Sin(r.Me.Yaw), MathF.Cos(r.Me.Yaw))) > 0.999f);
    }

    [Fact]
    public void A_stunned_boss_does_not_turn_for_the_whole_stun_in_the_sim_or_the_snapshot()
    {
        foreach (var type in Bosses)
        {
            var r = new Rig(type, 0.7f);
            var forward = r.MovePlayerBehind();
            Assert.True(Vector2.Dot(forward, Vector2.Normalize(new Vector2(r.Targets[0].Position.X, r.Targets[0].Position.Y))) < -0.99f, "the player is behind the boss");
            Assert.True(r.Sim.StaggerMelee(r.Id, Vector2.UnitY, 2f, 1));
            AssertHoldsFacing(r, forward, Window - 1);
        }
    }

    [Fact]
    public void A_boss_stunned_in_its_slam_or_charge_does_not_turn_either()
    {
        foreach (var (slam, charge, wanted) in new[] { (0f, 1000f, MusouBossAction.SlamWindUp), (1000f, 0f, MusouBossAction.ChargeWindUp), (1000f, 0f, MusouBossAction.Charging) })
        {
            var r = new Rig(MusouEnemyType.Warboss, 0f, slam, charge, distance: charge == 0f ? 14f : 6f, warm: charge == 0f ? 0 : 60);   // the charge starts from 6 m and more
            int guard = 0;
            while (r.Boss!.Action != wanted && guard++ < 600) r.Step();
            Assert.Equal(wanted, r.Boss!.Action);   // both abilities call the sim's Turn while they run
            var forward = r.MovePlayerBehind();
            Assert.True(r.Sim.StaggerMelee(r.Id, Vector2.UnitY, 2f, 1));
            AssertHoldsFacing(r, forward, Window - 1);
            Assert.Equal(MusouBossAction.None, r.Boss.Action);
        }
    }

    [Fact]
    public void A_strike_that_restarts_the_stun_does_not_turn_the_boss_and_it_stays_dizzy_throughout()
    {
        var r = new Rig(MusouEnemyType.BossNob, 0.7f);
        var forward = r.MovePlayerBehind();
        r.Sim.StaggerMelee(r.Id, Vector2.UnitY, 2f, 1);
        float before = r.Sim.YawOf(r.Id);
        for (int k = 0; k < Window * 3; k++)
        {
            if (k % (Window - 2) == 0) Assert.True(r.Sim.StaggerMelee(r.Id, Vector2.UnitY, 2f, 1));   // each strike restarts the window
            r.Step();
            Assert.True(MusouDizzyRules.Dizzy(r.Me.Type, r.Me.State), $"tick {k}");
            Assert.Equal(before, r.Me.Yaw);
        }
        Assert.True(Vector2.Dot(forward, new Vector2(-MathF.Sin(r.Me.Yaw), MathF.Cos(r.Me.Yaw))) > 0.999f);
    }

    [Fact]
    public void The_dizzy_state_starts_with_the_stun_and_ends_the_tick_it_ends()
    {
        foreach (var type in Bosses)
        {
            var r = new Rig(type, 0f); var sim = r.Sim; var id = r.Id; Action step = () => r.Step();
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
