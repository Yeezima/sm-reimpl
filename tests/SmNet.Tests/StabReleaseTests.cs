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

    // ---- Musou Mode and Warfront: the X,F thrust is released early too and keeps its finisher chain (owner 2026-11-20, "release early too") ----

    static readonly MeleeStep Thrust = Stab with { Stab = false, ThrustRelease = true, YNext = 2, YComboOpen = 0.873f, YComboClose = 1.418f };
    static readonly MeleeStep Finisher = new(1.5f, 0.4f, 0f, 0f, 0.3f, 0.8f, 80f, 0f, 3.5f);

    static SimConfig MusouMake(bool on = true) => StompUninterruptibleTests.Config with
    {
        StompRelease = true, StabRelease = on, Extras = ContentExtras.Musou,
        Classes = [new ClassLoadout("sword", 0, 1, [StompUninterruptibleTests.Kick]) { MeleeHeavy = [Thrust, Finisher], MeleeRush = RushTests.Spec, Evade = 0, EquipmentChoices = [0], Side = Team.SpaceMarines }],
    };

    static PlayerState ThrustAt(SimConfig c, int k)
    {
        var s = Fresh(c) with { Slot = WeaponSlot.Pistol, ReadyTicks = 300 };
        for (int i = 0; i < 5; i++) s = Step(c, s, InputButtons.None).S;
        s = Step(c, s, InputButtons.MeleeHeavy).S;
        Assert.Equal((1, true), (s.MeleeStep, s.MeleeHeavy));
        for (int i = 0; i < k; i++) s = Step(c, s, InputButtons.None).S;
        return s;
    }

    [Fact]
    public void Musou_thrust_another_action_at_the_release_ends_it_and_acts()
    {
        var c = MusouMake();
        var at27 = ThrustAt(c, 27);                                   // entering tick 28, the release
        Assert.Equal(1, at27.MeleeStep);
        Assert.True(Step(c, at27, InputButtons.Fire).E.Fired);
        Assert.True(Step(c, at27, InputButtons.Switch).S.SwitchLeft > 0f);
        Assert.True(Step(c, at27, InputButtons.Grenade).S.Throwing);
        Assert.True(Step(c, at27, InputButtons.Aim).S.Aiming);
        var swing = Step(c, Step(c, at27, InputButtons.Melee).S, InputButtons.None).S;
        Assert.True(swing.MeleeStep > 0 && !swing.MeleeHeavy, $"step {swing.MeleeStep} heavy {swing.MeleeHeavy}");
        var evade = ThrustAt(c, 27);
        int evadeAt = -1;
        for (int k = 28; k < 36 && evadeAt < 0; k++) { evade = Step(c, evade, k % 2 == 1 ? InputButtons.Jump : InputButtons.None).S; if (evade.EvadeTicks > 0) evadeAt = k; }
        output.WriteLine($"musou evade starts at {evadeAt}");
        Assert.Equal(31, evadeAt);   // the press of tick 29 ends the thrust (the release), the next press (tick 31) hops
    }

    [Fact]
    public void Musou_thrust_the_finisher_at_1_2_s_with_no_other_action_still_chains()
    {
        var c = MusouMake();
        var s = ThrustAt(c, 27);
        // The stick alone does not end the chain; the thrust carries on after its release while the window (0.873 to 1.418 s) is open.
        for (int k = 28; k < 36; k++) { s = Step(c, s, InputButtons.None, my: 1f).S; Assert.Equal(1, s.MeleeStep); }
        s = Step(c, s, InputButtons.MeleeHeavy, my: 1f).S;            // tick 36, 1.2 s
        Assert.Equal((2, true), (s.MeleeStep, s.MeleeHeavy));          // the finisher
    }

    [Fact]
    public void Musou_thrust_another_action_ends_the_chain_chance_and_the_window_closing_releases()
    {
        var c = MusouMake();
        var s = ThrustAt(c, 27);
        s = Step(c, s, InputButtons.Aim).S;                            // another action at the release ends the attack ...
        s = Step(c, s, InputButtons.None).S;
        for (int k = 0; k < 6; k++) s = Step(c, s, InputButtons.None).S;
        s = Step(c, s, InputButtons.MeleeHeavy).S;                     // ... so F now starts a fresh thrust, not the finisher
        Assert.Equal((1, true), (s.MeleeStep, s.MeleeHeavy));
        var w = ThrustAt(c, 42);                                       // 1.4 s: window still open (closes at 1.418 s)
        Assert.Equal(1, w.MeleeStep);
        w = Step(c, w, InputButtons.None).S;                           // tick 43, 1.433 s: closed, released well before the clip's end
        Assert.Equal(0, w.MeleeStep);
    }

    [Fact]
    public void Musou_thrust_with_the_switch_off_keeps_the_old_lock()
    {
        var c = MusouMake(on: false);
        Assert.Equal(1, ThrustAt(c, 45).MeleeStep);
    }

    // ---- review fixes (2026-11-20): stale queues, press edges, gating, death and stun ----

    /// <summary>A stab whose combo window (0.3 to 0.55 s) closes before its hit (0.636 s) and release: the shape that kept a queued press alive to the clip's end.</summary>
    static readonly MeleeStep WindowedStab = Stab with { ComboOpen = 0.3f, ComboClose = 0.55f };

    int FreeTick(SimConfig c, PlayerState s, int from, int to)
    {
        for (int k = from; k <= to; k++) { s = Step(c, s, InputButtons.None).S; output.WriteLine($"{k}: step {s.MeleeStep} heavy {s.MeleeHeavy}"); if (s.MeleeStep == 0) return k; }
        return -1;
    }

    [Fact]
    public void A_light_press_at_0_5_s_whose_combo_window_closes_unchained_does_not_lock_the_stab_to_the_clips_end()
    {
        var c = Make(WindowedStab);
        var s = StabAt(c, 14, out _);                                // 0.467 s
        s = Step(c, s, InputButtons.Melee).S;                        // tick 15, 0.5 s: the press queues (before the window closes)
        int free = FreeTick(c, s, 16, 54);
        output.WriteLine($"queued light press at tick 15: free at tick {free} (release 28, clip end 55)");
        Assert.InRange(free, 1, 28);                                 // at the release, never at the clip's end
    }

    [Fact]
    public void A_queued_press_whose_window_is_still_ahead_chains_as_before()
    {
        var c = Make(Stab with { ComboOpen = 0.3f, ComboClose = 0.8f });
        var s = StabAt(c, 14, out _);
        s = Step(c, s, InputButtons.Melee).S;
        for (int k = 16; k <= 21; k++) s = Step(c, s, InputButtons.None).S;
        Assert.True(s.MeleeStep == 1 && !s.MeleeHeavy, $"step {s.MeleeStep} heavy {s.MeleeHeavy}");   // the queued light swing starts once the hit has landed
    }

    static readonly MeleeStep WindowedThrust = Thrust with { ComboOpen = 0.3f, ComboClose = 0.55f };

    static SimConfig MusouMake(MeleeStep thrust) => MusouMake() with
    {
        Classes = [new ClassLoadout("sword", 0, 1, [StompUninterruptibleTests.Kick]) { MeleeHeavy = [thrust, Finisher], MeleeRush = RushTests.Spec, Evade = 0, EquipmentChoices = [0], Side = Team.SpaceMarines }],
    };

    [Fact]
    public void Musou_thrust_a_light_press_queued_at_0_5_s_starts_its_swing_when_the_hit_lands_so_the_thrust_never_locks()
    {
        var c = MusouMake(WindowedThrust);
        var s = ThrustAt(c, 14);
        s = Step(c, s, InputButtons.Melee).S;                        // tick 15, 0.5 s: queued (Musou melee spam starts it on the hit, whatever the window)
        int swing = -1;
        for (int k = 16; k <= 30 && swing < 0; k++) { s = Step(c, s, InputButtons.None).S; if (s.MeleeStep > 0 && !s.MeleeHeavy) swing = k; }
        output.WriteLine($"musou queued light press at tick 15: the swing starts at tick {swing} (hit tick 20, release 28)");
        Assert.InRange(swing, 20, 28);
    }

    [Fact]
    public void Musou_thrust_an_F_press_before_the_release_chains_into_the_finisher_and_never_locks()
    {
        var c = MusouMake();
        var s = ThrustAt(c, 14);
        s = Step(c, s, InputButtons.MeleeHeavy).S;                   // tick 15: queued for the finisher
        for (int k = 16; k <= 27; k++) s = Step(c, s, InputButtons.None).S;
        Assert.Equal((2, true), (s.MeleeStep, s.MeleeHeavy));        // chained when the Y window opened (0.873 s)
    }

    [Fact]
    public void Held_fire_through_the_musou_thrust_does_not_kill_the_finisher_chain()
    {
        var c = MusouMake();
        var s = Fresh(c) with { Slot = WeaponSlot.Pistol, ReadyTicks = 300 };
        for (int i = 0; i < 5; i++) s = Step(c, s, InputButtons.None).S;
        s = Step(c, s, InputButtons.Fire).S;                         // the trigger is pressed (and held) before the thrust starts
        s = Step(c, s, InputButtons.Fire | InputButtons.MeleeHeavy).S;
        Assert.Equal(1, s.MeleeStep);
        for (int k = 1; k < 36; k++) { s = Step(c, s, InputButtons.Fire).S; Assert.Equal(1, s.MeleeStep); }   // held through the release: no press, so the chain stays
    }

    [Fact]
    public void A_fire_press_at_the_release_still_ends_the_musou_thrust()
    {
        var c = MusouMake();
        Assert.True(Step(c, ThrustAt(c, 27), InputButtons.Fire).E.Fired);
    }

    [Fact]
    public void Classic_without_the_stab_keeps_the_stomp_finisher_unreleased()
    {
        // The content reader marks ThrustRelease only under WithoutSwordStab (Musou, Warfront): a Classic thrust without a stab (SM_OLD_SWORD_FF, no stab hit) is untouched.
        var plain = Stab with { Stab = false };
        Assert.False(plain.ThrustRelease);
        Assert.False(StabTuning.Released(plain, 1.0f, 1e-5f));
    }

    [Fact]
    public void An_evade_pressed_on_the_release_tick_itself_ends_the_stab_and_the_next_press_hops()
    {
        var c = Make(Stab);
        var s = StabAt(c, 27, out _);
        s = Step(c, s, InputButtons.Jump).S;                         // tick 28, the release: the press is not buffered by the sim (the input buffer above replays it)
        Assert.Equal((0, 0), (s.MeleeStep, s.EvadeTicks));
        s = Step(c, s, InputButtons.None).S;
        s = Step(c, s, InputButtons.Jump).S;                         // tick 30
        Assert.True(s.EvadeTicks > 0);
    }

    [Fact]
    public void Death_during_the_stab_leaves_a_clean_respawned_state()
    {
        var c = Make(Stab);
        var s = StabAt(c, 24, out _);
        var r = Combat.Respawned(s, c);
        Assert.Equal(0, r.MeleeStep);
        for (int k = 0; k < 60; k++) r = Step(c, r, InputButtons.None).S;
        Assert.Equal(0, r.MeleeStep);
    }

    [Fact]
    public void A_stun_inside_the_release_window_leaves_no_stuck_state()
    {
        foreach (int hitAt in new[] { 25, 27, 28 })
        {
            var c = Make(Stab);
            var s = StabAt(c, hitAt, out _) with { Reaction = ReactionKind.Stun, ReactionTicks = 12 };
            for (int k = 0; k < 120; k++) s = Step(c, s, InputButtons.None).S;
            Assert.Equal((0, ReactionKind.None), (s.MeleeStep, s.Reaction));
            bool fired = false;
            for (int k = 0; k < 40 && !fired; k++) { var (n, e) = Step(c, s, InputButtons.Fire); fired = e.Fired; s = n; }
            Assert.True(fired, $"stun at tick {hitAt}: a shot works after");
        }
    }

    [Fact]
    public void A_hit_exactly_on_or_a_hair_before_a_tick_boundary_does_not_move_the_release()
    {
        foreach (float hit in new[] { 28f / 30f, 28f / 30f - 1e-4f, 28f / 30f + 1e-4f })
        {
            var m = Stab with { HitTime = hit, MoveAt = 0.3f };
            var c = Make(m);
            var s = StabAt(c, 26, out _);
            s = Step(c, s, InputButtons.None).S;                     // tick 27 (0.9 s)
            Assert.Equal(StabTuning.Released(m, 27f / 30f, 1e-5f) ? 0 : 1, s.MeleeStep);
            s = Step(c, s, InputButtons.None).S;                     // tick 28 (0.933 s): released by here, the hit has landed
            Assert.Equal(hit <= 28f / 30f + 1e-5f ? 0 : 1, s.MeleeStep);
            s = Step(c, s, InputButtons.None).S; s = Step(c, s, InputButtons.None).S;
            Assert.Equal(0, s.MeleeStep);
        }
    }
}
