using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/stab-release (protocol 88; the owner, 2026-11-20: "allow the player to act again as soon as the stab animation releases and the 'hit or miss' moment has occurred"): the power sword's stab
/// (<see cref="MeleeStep.Stab"/>: the real clip's 1.818 s, hit 0.636 s, shots from 0.455 s, move-on 0.909 s) ends at the later of its hit and its move-on branch (tick 28 at 30 Hz: 0.933 s;
/// the clip's end is tick 55). From that tick move, fire, aim, switch, grenade, melee act as from rest, the evade one tick later; the tick before it nothing does; the hit itself (tick 20) is unchanged.
/// Synthetic attacks and weapons (<see cref="StompUninterruptibleTests"/>' kit); the real numbers are the install's `smtool melee-steps` for the power sword.
/// </summary>
public class StabReleaseTests(ITestOutputHelper output)
{
    /// <summary>The stab with the install's timing (heavy 2 of the power sword, `smtool melee-steps`).</summary>
    static readonly MeleeStep Stab = new(1.818f, 0.636f, 0f, 0f, 0.455f, 0.909f, 100f, 0f, 3.5f) { Stab = true };
    /// <summary>A stab whose move-on branch lies before its hit: the release waits for the hit.</summary>
    static readonly MeleeStep EarlyMoveStab = Stab with { MoveAt = 0.3f };

    static SimConfig Make(MeleeStep stab, bool on = true) => StompUninterruptibleTests.Config with
    {
        StompRelease = true, StabRelease = on,
        Classes = [new ClassLoadout("sword", 0, 1, [StompUninterruptibleTests.Kick]) { MeleeHeavy = [stab], MeleeRush = RushTests.Spec, Evade = 0, EquipmentChoices = [0], Side = Team.SpaceMarines }],
    };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Team = Team.SpaceMarines, Class = 0 }, c);

    static (PlayerState S, SelfEvents E) Step(SimConfig c, PlayerState s, InputButtons b, float my = 0f)
    {
        var next = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out var e);
        return (next, e);
    }

    /// <summary>The state after the T press tick and <paramref name="k"/> more input-free ticks (tick k = k/30 s into the clip), the pistol in hand.</summary>
    static PlayerState StabAt(SimConfig c, int k, out int hitTick)
    {
        var s = Fresh(c) with { Slot = WeaponSlot.Pistol, ReadyTicks = 300 };
        for (int i = 0; i < 5; i++) s = Step(c, s, InputButtons.None).S;
        s = Step(c, s, InputButtons.Stab).S;
        Assert.Equal((1, true), (s.MeleeStep, s.MeleeHeavy));
        hitTick = -1;
        for (int i = 1; i <= k; i++)
        {
            var (n, e) = Step(c, s, InputButtons.None);
            if (e.MeleeHitStep != 0 && hitTick < 0) hitTick = i;
            s = n;
        }
        return s;
    }

    [Fact]
    public void The_stab_ends_at_its_move_on_branch_tick_28_not_at_the_clips_end_tick_55()
    {
        var c = Make(Stab);
        var s = StabAt(c, 27, out int hit);
        Assert.Equal(1, s.MeleeStep);                                // 0.9 s: still the stab
        Assert.Equal(20, hit);                                       // the hit lands at tick 20 (0.667 s) as before
        s = Step(c, s, InputButtons.None).S;
        output.WriteLine($"hit tick {hit}, tick 28: step {s.MeleeStep}");
        Assert.Equal(0, s.MeleeStep);                                // 0.933 s: released
        Assert.True(StabTuning.ReleaseAt(Stab) * 30f > hit);
    }

    [Fact]
    public void A_move_on_before_the_hit_waits_for_the_hit()
    {
        var c = Make(EarlyMoveStab);
        var s = StabAt(c, 19, out int hit);
        Assert.Equal((1, -1), (s.MeleeStep, hit));                   // tick 19 (0.633 s): before the hit, still locked
        var (n, e) = Step(c, s, InputButtons.None);
        Assert.NotEqual(0, e.MeleeHitStep);                              // tick 20: the hit lands ...
        Assert.Equal(0, n.MeleeStep);                                // ... and the stab is released with it, never before
    }

    [Fact]
    public void Every_next_action_works_from_the_release_tick_and_none_the_tick_before()
    {
        var c = Make(Stab);
        var at27 = StabAt(c, 27, out _);                             // the state entering tick 28 (the release)
        var at26 = StabAt(c, 26, out _);                             // the state entering tick 27 (0.9 s)
        // Before: aim, stick, switch, grenade, melee, charge all wait for the clip.
        var (n, e) = Step(c, at26, InputButtons.Aim | InputButtons.Switch | InputButtons.Grenade | InputButtons.Melee, my: 1f);   // (a shot ends the stab from its data's BRANCH_ToRanged, 0.455 s, as before)
        Assert.False(n.Aiming); Assert.False(n.Throwing); Assert.Equal(1, n.MeleeStep);
        Assert.Equal(0f, Step(c, at26, InputButtons.None, my: 1f).S.Velocity.Length(), 3);
        // From the release tick:
        Assert.True(Step(c, at27, InputButtons.Fire).E.Fired);                                       // a shot, the weapon up at once
        Assert.True(Step(c, at27, InputButtons.Aim).S.Aiming);
        Assert.True(Step(c, at27, InputButtons.None, my: 1f).S.Velocity.Length() > 0.1f);            // movement
        Assert.True(Step(c, at27, InputButtons.Switch).S.SwitchLeft > 0f);
        Assert.True(Step(c, at27, InputButtons.Grenade).S.Throwing);
        var swing = Step(c, Step(c, at27, InputButtons.Melee).S, InputButtons.None).S;               // a second melee, within two ticks
        Assert.True(swing.MeleeStep == 1 && !swing.MeleeHeavy, $"step {swing.MeleeStep} heavy {swing.MeleeHeavy}");
        var again = Step(c, at27, InputButtons.Stab).S;                                              // another stab, from rest
        Assert.Equal((1, true), (again.MeleeStep, again.MeleeHeavy));
    }

    [Fact]
    public void An_evade_press_waits_one_tick_after_the_release_then_starts_without_a_further_wait()
    {
        var c = Make(Stab);
        var s = StabAt(c, 27, out _);
        int evadeAt = -1;
        for (int k = 28; k < 36 && evadeAt < 0; k++)
        {
            s = Step(c, s, k % 2 == 1 ? InputButtons.Jump : InputButtons.None).S;
            if (s.EvadeTicks > 0) evadeAt = k;
        }
        output.WriteLine($"evade starts at tick {evadeAt} (release 28)");
        Assert.Equal(29, evadeAt);   // a press on the release tick itself would wait for the next (the evade starts from a state with no attack)
        Assert.Equal(0, s.MeleeStep);
        // Before the release the press never starts it (the stab is no evade branch).
        var locked = StabAt(c, 20, out _);
        for (int k = 21; k < 28; k++) { locked = Step(c, locked, k % 2 == 1 ? InputButtons.Jump : InputButtons.None).S; Assert.Equal(0, locked.EvadeTicks); }
    }

    [Fact]
    public void The_restore_switch_keeps_the_old_lock_to_the_clips_end()
    {
        var c = Make(Stab, on: false);
        var s = StabAt(c, 54, out _);
        Assert.Equal(1, s.MeleeStep);                                // tick 54, 1.8 s: still locked
        s = Step(c, s, InputButtons.None).S;
        s = Step(c, s, InputButtons.None).S;
        Assert.Equal(0, s.MeleeStep);                                // over at 1.818 s
        Assert.False(Make(Stab).Hash() == c.Hash());                 // the switch is in the config hash
    }

    [Fact]
    public void Only_a_marked_stab_is_released_other_heavy_strikes_keep_their_lock()
    {
        var c = Make(Stab with { Stab = false });
        var f = Fresh(c);
        for (int i = 0; i < 5; i++) f = Step(c, f, InputButtons.None).S;
        f = Step(c, f, InputButtons.MeleeHeavy).S;
        for (int i = 0; i < 40; i++) f = Step(c, f, InputButtons.None).S;
        Assert.Equal((1, true), (f.MeleeStep, f.MeleeHeavy));        // 1.33 s: an unmarked strike plays on
    }
}
