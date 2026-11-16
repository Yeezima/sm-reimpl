using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// The owner's melee changes of 2026-11-06 (protocol 59), every mode: sim/swing-step (every melee weapon's swings step forward × 1.5, the thunder hammer's and the
/// daemon maul's × 0.7) and sim/lock-reach (a locked attack's lunge closes 70 % of the gap to the separation). Distances measured in the simulation; synthetic clips.
/// </summary>
public class SwingStepLockReachTests(ITestOutputHelper output)
{
    static float T(float ticks) => ticks / 30f;

    static MeleeVariant Variant(float distance, float duration, float hit) => new(distance, Enumerable.Range(0, 61).Select(i =>
    {
        float t = duration * i / 60f;
        return MathF.Max(0f, distance - 1.5f) * Math.Clamp(t / hit, 0f, 1f) + 0.6f * Math.Clamp((t - hit) / (duration - hit), 0f, 1f);
    }).ToArray());

    /// <summary>MeleeCharge2Tests' attack: hit at tick 7, moves again at 22, over at 31; alignment over ticks 0..3; variants 1, 3.5, 7 m; start blend 1.</summary>
    static MeleeStep Reaching(float factor = 1f)
    {
        float duration = T(30.5f), hit = T(6.5f);
        return new MeleeStep(duration, hit, T(12.5f), T(18.5f), T(15.5f), T(21.5f), 0f, 0f, 3.5f)
        {
            Reach = new MeleeReach(0f, T(3f), 0f, hit, 3.5f, 1f, [Variant(1f, duration, hit), Variant(3.5f, duration, hit), Variant(7f, duration, hit)]),
            LungeFactor = factor,
        };
    }

    static SimConfig Config(float factor = 1f, float reach = 1f) => new()
    {
        MeleeSteps = [Reaching(factor), Reaching(factor), Reaching(factor)], MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f, MeleeLungeScale = 1.625f,
        MeleeLock = MeleeLockSpec.Default with { LungeReach = reach },
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    static List<PlayerState> Swing(SimConfig c, Vector3? target, int ticks)
    {
        var self = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);
        var view = target is { } t ? new Dictionary<int, PlayerState> { [2] = Combat.Respawned(new PlayerState(2, t, Vector3.Zero, 0f, true), c) } : null;
        var states = new List<PlayerState>();
        int locked = 0;
        for (int i = 0; i < ticks; i++)
        {
            var input = new PlayerInput((uint)i, 0, 0, 0f, i == 0 ? InputButtons.Melee : InputButtons.None);
            if (view is not null) { input = input with { MeleeTarget = MeleeLock.Choose(self, input, view, c, null, locked) }; locked = input.MeleeTarget; }
            self = Combat.StepPlayer(self, input, c, out _, null, view);
            states.Add(self);
        }
        return states;
    }

    // ---------------------------------------------------------------- 1, 2: the swings' step-forward

    [Fact]
    public void Every_melee_weapon_but_the_hammer_and_the_maul_steps_its_swings_half_again_as_far()
    {
        Assert.Equal(1.5f, MeleeTuning.OwnerSwingStep);
        foreach (var map in new[] { MeleeTuning.CombatKnife, MeleeTuning.PowerSword, MeleeTuning.PowerAxe, MeleeTuning.OrkChoppa, MeleeTuning.OrkChoppaSet2 })
            Assert.Equal(MeleeTuning.OwnerSwingStep, map.SwingStepFactor);
        foreach (string wargear in new[] { "sm_mp_melee_combat_knife", "csm_mp_melee_combat_knife", "sm_mp_melee_chainsword", "csm_mp_melee_chainsword", "sm_mp_melee_chainsword_gold",
                     "sm_mp_melee_powersword", "csm_mp_melee_powersword", "sm_mp_melee_power_axe", "csm_mp_melee_chain_axe" })
            Assert.Equal(MeleeTuning.OwnerSwingStep, MpClasses.MeleeMapFor(wargear)!.SwingStepFactor);
        // The heavy classes' kick is a core ability, not a melee weapon (ours): unchanged.
        Assert.Equal(1f, MeleeTuning.Displace.SwingStepFactor);
        Assert.Equal(1f, MpClasses.MeleeMapFor("sm_mp_core_abil_heavy_evade")!.SwingStepFactor);
    }

    [Fact]
    public void The_hammer_and_the_daemon_maul_step_their_swings_thirty_percent_less()
    {
        Assert.Equal(0.7f, MeleeTuning.OwnerHammerSwingStep);
        Assert.Equal(MeleeTuning.OwnerHammerSwingStep, MeleeTuning.ThunderHammer.SwingStepFactor);
        Assert.Equal(MeleeTuning.OwnerHammerSwingStep, MpClasses.MeleeMapFor("sm_mp_melee_2hand_thunder_hammer")!.SwingStepFactor);
        Assert.Equal(MeleeTuning.OwnerHammerSwingStep, MpClasses.MeleeMapFor("csm_mp_melee_2hand_power_mace")!.SwingStepFactor);   // the maul: our assumption
        Assert.Equal(MeleeTuning.HammerLightLungeFactor, MeleeTuning.ThunderHammer.LightLungeFactor);                              // the 2026-11-01 trim stays under it
    }

    [Theory]
    [InlineData(1.5f)]                   // every melee weapon's swing
    [InlineData(0.82f * 0.7f)]           // the hammer's: its 2026-11-01 trim × the new 0.7
    public void The_free_swing_moves_by_the_factor_in_the_simulation(float factor)
    {
        float before = Swing(Config(), null, 22)[^1].Position.Y, after = Swing(Config(factor), null, 22)[^1].Position.Y;
        output.WriteLine($"no target, to the move gate: {before:F3} m → {after:F3} m (×{factor:F3})");
        Assert.True(before > 0.3f);
        Assert.Equal(factor * before, after, 3);
    }

    [Fact]
    public void The_swing_factor_is_in_the_hash()
    {
        Assert.NotEqual(Config().Hash(), Config(1.5f).Hash());
        Assert.NotEqual(Config(1.5f).Hash(), Config(0.7f).Hash());
    }

    // ---------------------------------------------------------------- 3: the lock-on's lunge

    [Theory]
    [InlineData(3.5f)]
    [InlineData(5f)]
    [InlineData(6.5f)]
    public void A_locked_lunge_closes_seventy_percent_of_the_gap(float distance)
    {
        var target = new Vector3(0f, distance, 0f);
        float separation = MeleeLockSpec.Default.Separation;
        var before = Swing(Config(), target, 31);
        var after = Swing(Config(reach: 0.7f), target, 31);
        float movedBefore = before[^1].Position.Y, movedAfter = after[^1].Position.Y;
        output.WriteLine($"target {distance} m: lunge {movedBefore:F3} m → {movedAfter:F3} m (gap at the end {distance - movedBefore:F3} → {distance - movedAfter:F3} m)");
        Assert.Equal(distance - separation, movedBefore, 1);                    // before: the whole gap, to the separation
        // now: 70 % of it, a little less where the burst's 28 m/s cap binds (the stateless profile then settles short; this clip's hit is early, 0.22 s)
        Assert.InRange(movedAfter, 0.6f * (distance - separation), 0.7f * (distance - separation) + 0.02f);
        if (distance <= 3.5f) Assert.Equal(0.7f * (distance - separation), movedAfter, 1);   // the cap does not bind on a short gap
        Assert.All(after, s => Assert.True(s.Position.Y >= -1e-3f, "no step goes backwards"));
    }

    [Fact]
    public void The_lock_reach_applies_with_any_swing_factor_and_its_default_is_the_old_rule()
    {
        var target = new Vector3(0f, 5f, 0f);
        float knife = Swing(Config(1.5f, 0.7f), target, 31)[^1].Position.Y, hammer = Swing(Config(0.82f * 0.7f, 0.7f), target, 31)[^1].Position.Y;
        output.WriteLine($"target 5 m, reach 0.7: swing ×1.5 {knife:F3} m, hammer swing {hammer:F3} m");
        Assert.InRange(knife, 0.6f * 4f, 0.7f * 4f + 0.02f);
        Assert.InRange(hammer, 0.6f * 4f, 0.7f * 4f + 0.02f);
        Assert.Equal(1f, MeleeLockSpec.Default.LungeReachApplied);
        Assert.Equal(1f, (MeleeLockSpec.Default with { LungeReach = float.NaN }).LungeReachApplied);
        Assert.Equal(1f, (MeleeLockSpec.Default with { LungeReach = 1.4f }).LungeReachApplied);
        Assert.Equal(new SimConfig().Hash(), (new SimConfig() with { MeleeLock = MeleeLockSpec.Default with { LungeReach = 1f } }).Hash());
        Assert.NotEqual(new SimConfig().Hash(), (new SimConfig() with { MeleeLock = MeleeLockSpec.Default with { LungeReach = 0.7f } }).Hash());
        Assert.Equal(0.7f, GameplayTuning.OwnerLockLungeReach);
    }
}
