using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/buffer-speed (protocol 33; the owner's request of 2026-10-29: "when approaching the end of any animation the player should be able to buffer
/// the next inputs ... hold space bar during the landing so that in the first frame possible after the recovery animation for the landing he jumps
/// immediately"). Rules ours (<see cref="InputBuffer"/>, spec "Input buffering"). Synthetic specs shaped like the install's; no original bytes.
/// </summary>
public class InputBufferTests(ITestOutputHelper output)
{
    const int Assault = 0, Tactical = 1;
    const float Dt = 1f / 30f;
    const float Window = 0.3f;   // GameplayTuning.OwnerInputBuffer

    static SimConfig On(SimConfig c) => c with { InputBufferWindow = Window };

    /// <summary>The M14 pack (no dash) with the feel/jump-pack air control and snappy slash (0.3 s recovery), as the content loader sets them.</summary>
    static readonly JumpPackSpec SlashPack = JumpPackTests.Pack with { AirSpeed = 8f, AirAcceleration = 15f, Slash = JumpPackFeelTests.Snappy, MaxPower = 300f };
    static readonly SimConfig PackConfig = JumpPackTests.Config with
    {
        JumpPacks = [SlashPack],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };
    /// <summary>The same with the jump-pack dash (the Assault / Raptor of M44: a tap dashes, a hold jumps).</summary>
    static readonly SimConfig DashConfig = JumpPackDashTests.Config with { JumpPacks = [SlashPack] };

    static PlayerState Fresh(SimConfig c, int cls) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c);

    static SelfEvents Step(ref PlayerState s, SimConfig c, InputButtons b, float mx = 0f, float my = 0f, float pitch = 0f)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, b, pitch), c, out var e);
        return e;
    }

    // ---------------------------------------------------------------- the owner's example: Space held through the slash's landing

    /// <summary>
    /// A jump, the slash pressed 3 ticks after the lift-off (aimed down), then from the landing on <paramref name="space"/> decides Jump per tick after the
    /// landing tick (0 = the landing). Returns the states from the landing tick on (index 0) and the index of the landing tick in the run.
    /// </summary>
    static List<PlayerState> SlashLanding(SimConfig c, Func<int, bool> space, int after = 30)
    {
        var s = Fresh(c, Assault);
        // Jump held past the dash's decision (a jump, not the dash), the slash pressed 3 ticks after the lift-off (low: below 8 m).
        for (int k = 0; k < 60 && s.Jump != JumpPhase.Ascent; k++) Step(ref s, c, InputButtons.Jump);
        for (int k = 0; k < 3; k++) Step(ref s, c, InputButtons.None);
        Step(ref s, c, InputButtons.Melee, pitch: -1.2f);
        Assert.Equal((JumpPhase.Dive, true), (s.Jump, s.JumpSlash));   // the slash (below 8 m)
        for (int k = 0; k < 120 && s.Jump == JumpPhase.Dive; k++) Step(ref s, c, InputButtons.None);
        Assert.Equal((JumpPhase.Recover, true), (s.Jump, s.JumpSlash));
        var states = new List<PlayerState> { s };
        for (int k = 1; k <= after; k++) { Step(ref s, c, space(k) ? InputButtons.Jump : InputButtons.None); states.Add(s); }
        return states;
    }

    static int First(List<PlayerState> states, Func<PlayerState, bool> what) => states.FindIndex(p => what(p));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Space_held_through_the_air_slash_landing_jumps_on_the_first_tick_the_recovery_allows(bool dash)
    {
        var c = dash ? DashConfig : PackConfig;
        // The tick the recovery ends: without the buffer the player is free (phase None) there and holding Space does nothing.
        var before = SlashLanding(c, k => k >= 1);
        int free = First(before, p => p.Jump == JumpPhase.None);
        Assert.Equal(9, free);   // 0.3 s after the landing tick (feel/jump-pack)
        Assert.All(before.Skip(free), p => Assert.Equal(JumpPhase.None, p.Jump));   // held, never a jump (a new press is needed)

        // With the buffer: pressed on the tick after the landing and held, the take-off starts on that very tick, and a hold is a jump (not the dash).
        var after = SlashLanding(On(c), k => k >= 1);
        Assert.Equal(JumpPhase.TakeOff, after[free].Jump);
        Assert.All(after.Take(free), p => Assert.Equal(JumpPhase.Recover, p.Jump));
        int liftOff = First(after, p => p.Jump == JumpPhase.Ascent);
        output.WriteLine($"{(dash ? "dash pack" : "plain pack")}: recovery ends at landing + {free} ticks; take-off on that tick (+0), lift-off at +{liftOff - free} ticks; without the buffer: none while held");
        Assert.Equal(6, liftOff - free);   // the take-off's 0.2 s
        Assert.Equal(0, after[liftOff].EvadeTicks);   // a jump, not the dash

        // Pressed late in the recovery (its last 0.3 s) and held: the same tick.
        var late = SlashLanding(On(c), k => k >= 5);
        Assert.Equal(JumpPhase.TakeOff, late[free].Jump);
    }

    [Fact]
    public void A_tap_in_the_recovery_is_kept_for_the_window_a_dash_class_dashes_and_an_earlier_tap_expires()
    {
        // The slash's 0.3 s recovery (9 ticks) lies inside the 0.3 s window: a one-tick tap anywhere in it acts at its end.
        foreach (int at in new[] { 1, 4, 8 })
        {
            var plain = SlashLanding(On(PackConfig), k => k == at);
            Assert.Equal(JumpPhase.TakeOff, plain[9].Jump);
            Assert.False(plain[9].JumpHolding && plain[10].JumpHolding);   // released: the short hop
            var dash = SlashLanding(On(DashConfig), k => k == at);
            Assert.Contains(dash.Skip(9), p => p.EvadeTicks > 0);   // a tap is the dash
        }
        // The pound's longer recovery (0.356 s, 11 ticks): a tap on the impact tick + 1 has run out (9 ticks) before it ends; a later one has not.
        var pound = On(PackConfig with { JumpPacks = [SlashPack with { Slash = null }] });
        List<PlayerState> Pound(int tap)
        {
            var s = Fresh(pound, Assault);
            for (int k = 0; k < 13; k++) Step(ref s, pound, InputButtons.Jump);
            for (int k = 0; k < 200 && s.Jump != JumpPhase.Hang; k++) Step(ref s, pound, InputButtons.None);
            Step(ref s, pound, InputButtons.Melee, pitch: -1.2f);
            Assert.Equal(JumpPhase.Dive, s.Jump);
            for (int k = 0; k < 200 && s.Jump == JumpPhase.Dive; k++) Step(ref s, pound, InputButtons.None);
            Assert.Equal(JumpPhase.Recover, s.Jump);
            var states = new List<PlayerState> { s };
            for (int k = 1; k <= 20; k++) { Step(ref s, pound, k == tap ? InputButtons.Jump : InputButtons.None); states.Add(s); }
            return states;
        }
        int end = First(Pound(-1), p => p.Jump == JumpPhase.None);
        Assert.Equal(11, end);
        Assert.Equal(JumpPhase.None, Pound(1)[end].Jump);       // expired at 1 + 9
        Assert.All(Pound(1), p => Assert.NotEqual(JumpPhase.TakeOff, p.Jump));
        Assert.Equal(JumpPhase.TakeOff, Pound(2)[end].Jump);    // 2 + 9 = 11: still kept
        Assert.Equal(JumpPhase.TakeOff, Pound(9)[end].Jump);
    }

    // ---------------------------------------------------------------- melee

    [Fact]
    public void A_melee_press_late_in_a_swing_starts_the_next_attack_on_the_tick_the_swing_ends()
    {
        // Placeholder knife: 1.5 s, combo window 0.5 to 0.8 s. A press after the window closes did nothing; buffered it starts attack 1 as the swing ends.
        int end = SwingEnd();
        Assert.InRange(end, 45, 46);   // 1.5 s (the summed tick lengths may land a tick late)
        foreach (var (c, expect) in new[] { (EvadeTests.Config, 0), (On(EvadeTests.Config), 1) })
        {
            var s = Swing(c, k => k == end - 6);   // 0.2 s before the end: inside the last 0.3 s
            Assert.Equal(expect, s[end].MeleeStep);
            Assert.Equal(1, s[end - 1].MeleeStep);
            if (expect == 1) Assert.Equal(0f, s[end].MeleeTime);
        }
        // A press 0.5 s before the end (outside the window) and released is gone by then; held, the hold rule keeps it.
        foreach (bool hold in new[] { false, true })
        {
            var s = Swing(On(EvadeTests.Config), k => k == end - 15 || (hold && k > end - 15));
            Assert.Equal(hold ? 1 : 0, s[end].MeleeStep);
        }
    }

    /// <summary>A light attack pressed at tick 0, then <paramref name="melee"/> decides the button per tick; the states by tick (index 0 = the press).</summary>
    static List<PlayerState> Swing(SimConfig c, Func<int, bool> melee, int ticks = 60)
    {
        var s = Fresh(c, Tactical);
        Step(ref s, c, InputButtons.Melee);
        var states = new List<PlayerState> { s };
        for (int k = 1; k <= ticks; k++) { Step(ref s, c, melee(k) ? InputButtons.Melee : InputButtons.None); states.Add(s); }
        return states;
    }

    /// <summary>The tick the placeholder swing ends without the buffer (its step back to 0).</summary>
    static int SwingEnd() => Swing(EvadeTests.Config, _ => false).FindIndex(p => p.MeleeStep == 0);

    [Fact]
    public void A_press_before_the_combo_window_opens_still_chains_at_its_opening_as_before()
    {
        foreach (var c in new[] { EvadeTests.Config, On(EvadeTests.Config) })
        {
            var s = Fresh(c, Tactical);
            Step(ref s, c, InputButtons.Melee);
            Step(ref s, c, InputButtons.None);
            Step(ref s, c, InputButtons.Melee);   // 0.067 s: queued (the combo's own buffer)
            Assert.True(s.MeleeQueued);
            Assert.Equal(BufferedAction.None, s.Buffered);   // used by the combo, not the input buffer
            int k = 2;
            while (s.MeleeStep == 1 && k < 100) { Step(ref s, c, InputButtons.None); k++; }
            Assert.Equal((2, 15), (s.MeleeStep, k));   // attack 2 at ComboOpen (0.5 s), unchanged
        }
    }

    // ---------------------------------------------------------------- evades, reactions, throws, switches and reloads

    [Fact]
    public void A_jump_press_late_in_a_roll_rolls_again_when_the_roll_allows_it()
    {
        // The roll: 1 s, the next evade from 1.0 s (NextAt), spacing 0.8 s. A press at 0.8 s was lost; buffered it rolls at 1.0 s.
        foreach (var (c, expect) in new[] { (EvadeTests.Config, false), (On(EvadeTests.Config), true) })
        {
            var s = Fresh(c, Tactical);
            Step(ref s, c, InputButtons.Jump);
            Assert.Equal(1, s.EvadeTicks);
            for (int k = 1; k < 30; k++) Step(ref s, c, k == 24 ? InputButtons.Jump : InputButtons.None);
            Step(ref s, c, InputButtons.None);   // the tick the roll's 1 s has played
            Assert.Equal(expect, s.EvadeTicks == 1);
        }
    }

    [Fact]
    public void A_jump_press_in_a_reaction_rolls_at_the_reactions_evade_branch()
    {
        // The light interrupt (front clip, 1.17 s): a Jump ends it from 0.467 s (EvadeAt), time alone at 0.98 s. A press at 0.267 s was lost;
        // buffered it rolls on the tick 0.467 s have played.
        foreach (var (c, expect) in new[] { (EvadeTests.Config, -1), (On(EvadeTests.Config), 15) })
        {
            var s = Fresh(c, Tactical) with { Reaction = ReactionKind.Interrupt, ReactionDirection = ReactionDirection.Front, ReactionTicks = 0 };
            int rolled = -1;
            for (int k = 1; k <= 30 && rolled < 0; k++)
            {
                Step(ref s, c, k == 8 ? InputButtons.Jump : InputButtons.None);
                if (s.EvadeTicks == 1) rolled = k;
            }
            Assert.Equal(expect, rolled);
        }
    }

    [Fact]
    public void A_grenade_press_late_in_a_swing_throws_when_it_ends_and_a_switch_press_late_in_a_throw_switches_when_it_ends()
    {
        var c = On(GrenadeTests.Config);
        var s = Combat.Respawned(Loadouts.Choose(c, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, -1, -1, -1, 0), c);
        Step(ref s, c, InputButtons.Melee);   // the knife: 1.0 s
        int threw = -1;
        for (int k = 1; k <= 40 && threw < 0; k++)
        {
            Step(ref s, c, k == 25 ? InputButtons.Grenade : InputButtons.None);
            if (s.Throwing && threw < 0) threw = k;
        }
        Assert.Equal(30, threw);   // the swing's 1.0 s
        var slot = s.Slot;
        int switched = -1;
        for (int k = 1; k <= 40 && switched < 0; k++)
        {
            Step(ref s, c, k == 15 ? InputButtons.Switch : InputButtons.None);
            if (s.Slot != slot) switched = k;
        }
        // The throw (0.667 s = 20 ticks from its press tick) ends after 20 ticks; the switch acts on the next tick, its first allowed one.
        output.WriteLine($"throw at swing tick {threw}, switch {switched} ticks after the throw's start");
        Assert.Equal(21, switched);
    }

    [Fact]
    public void A_reload_press_late_in_a_swing_reloads_when_it_ends_and_a_pointless_one_is_dropped()
    {
        var c = On(EvadeTests.Config);
        int end = SwingEnd();
        var s = Fresh(c, Tactical) with { Clip = 10 };
        Step(ref s, c, InputButtons.Melee);
        for (int k = 1; k <= end; k++) Step(ref s, c, k == end - 5 ? InputButtons.Reload : InputButtons.None);
        Assert.Equal(0, s.MeleeStep);
        Assert.True(s.ReloadLeft > 0f, "reloading from the swing's end");

        // A full clip: the replayed Reload meets a free player, does nothing and is dropped; a later shot does not start a reload.
        var f = Fresh(c, Tactical);
        Step(ref f, c, InputButtons.Melee);
        for (int k = 1; k <= end + 1; k++) Step(ref f, c, k == end - 5 ? InputButtons.Reload : InputButtons.None);
        Assert.Equal(BufferedAction.None, f.Buffered);
        for (int k = 0; k < 10; k++) Step(ref f, c, InputButtons.Fire);
        Assert.Equal(0f, f.ReloadLeft);
        // A press while free is never buffered (it acted, or had nothing to do).
        var g = Fresh(c, Tactical);
        Step(ref g, c, InputButtons.Reload);
        Assert.Equal(BufferedAction.None, g.Buffered);
    }

    // ---------------------------------------------------------------- the rules

    [Fact]
    public void One_buffered_action_a_newer_press_replaces_it_fire_is_never_buffered_and_death_clears_it()
    {
        var c = On(GrenadeTests.Config);
        var s = Combat.Respawned(Loadouts.Choose(c, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, -1, -1, -1, 0), c);
        Step(ref s, c, InputButtons.Melee);
        for (int k = 1; k < 25; k++) Step(ref s, c, InputButtons.None);
        Step(ref s, c, InputButtons.Switch);
        Assert.Equal((BufferedAction.Switch, 9), (s.Buffered, s.BufferTicks));
        Step(ref s, c, InputButtons.None);
        Assert.Equal((BufferedAction.Switch, 8), (s.Buffered, s.BufferTicks));
        Step(ref s, c, InputButtons.Grenade);   // newer: replaces the switch
        Assert.Equal((BufferedAction.Grenade, 9), (s.Buffered, s.BufferTicks));
        Step(ref s, c, InputButtons.Sprint | InputButtons.Aim);   // buttons that are not buffered do not replace it
        Assert.Equal(BufferedAction.Grenade, s.Buffered);
        var slot = s.Slot;
        for (int k = 0; k < 6; k++) Step(ref s, c, InputButtons.None);
        Assert.True(s.Throwing);
        Assert.Equal(slot, s.Slot);   // the replaced switch never happened
        Assert.Equal(BufferedAction.None, s.Buffered);

        var dead = Combat.Killed(s with { Buffered = BufferedAction.Jump, BufferTicks = 5 }, c);
        Assert.Equal((BufferedAction.None, 0), (dead.Buffered, dead.BufferTicks));
        Assert.Equal((BufferedAction.None, 0), (Combat.Respawned(s with { Buffered = BufferedAction.Jump, BufferTicks = 5 }, c) is var r ? (r.Buffered, r.BufferTicks) : default));
        Assert.Equal(InputButtons.None, InputBuffer.Button(BufferedAction.None));
        Assert.Equal(BufferedAction.None, InputBuffer.Pressed(s, InputButtons.Fire | InputButtons.Aim | InputButtons.Sprint | InputButtons.Interact, c));
    }

    [Fact]
    public void A_hold_buffer_lives_while_held_past_the_window_and_letting_go_after_it_cancels_it()
    {
        var c = On(EvadeTests.Config);
        int end = SwingEnd();
        // Pressed 15 ticks before the swing's end, held past the window (9 ticks) and let go 3 ticks before the end: cancelled, no restart.
        var s = Swing(c, k => k >= end - 15 && k < end - 3);
        Assert.Equal(BufferedAction.None, s[end - 3].Buffered);
        Assert.Equal(0, s[end].MeleeStep);
        // Held to the end: the restart.
        Assert.Equal(1, Swing(c, k => k >= end - 15)[end].MeleeStep);
        // Pressed inside the window and let go at once (a press): kept to the end.
        var t = Swing(c, k => k == end - 8 || k == end - 7);
        Assert.Equal((1, 0f), (t[end].MeleeStep, t[end].MeleeTime));
        // A replayed press leaves the real held flag: still holding after the restart, a further press needs a release.
        var u = Swing(c, k => k >= end - 6);
        Assert.Equal((1, true, BufferedAction.None), (u[end].MeleeStep, u[end].MeleeHeld, u[end].Buffered));
    }

    [Fact]
    public void A_jump_press_in_a_take_off_or_ascent_is_the_hold_and_never_buffered()
    {
        var c = On(PackConfig);
        var s = Fresh(c, Assault);
        var plain = Fresh(PackConfig, Assault);
        // Tap, let go, press again and hold through the whole flight: the jump is the unbuffered one tick for tick and no jump follows the landing.
        for (int k = 0; k < 120; k++)
        {
            var b = k == 0 || k >= 3 ? InputButtons.Jump : InputButtons.None;
            Step(ref s, c, b);
            Step(ref plain, PackConfig, b);
            Assert.Equal(plain.Position, s.Position);
            Assert.Equal(BufferedAction.None, s.Buffered);
        }
        Assert.Equal(JumpPhase.None, s.Jump);
    }

    [Fact]
    public void Without_a_window_nothing_is_buffered_and_every_tick_is_as_before()
    {
        // The synthetic configs' default (InputBufferWindow 0): a busy script steps identically with the rules compiled in.
        var c = EvadeTests.Config;
        Assert.Equal(0, c.InputBufferTicks);
        Assert.Equal(9, On(c).InputBufferTicks);
        Assert.Equal(15, (c with { InputBufferWindow = 2f }).InputBufferTicks);   // 4 bits in the own record
        var s = Fresh(c, Tactical);
        for (int k = 0; k < 600; k++)
        {
            var b = (InputButtons)((k * 7919) % 4096) & (InputButtons.Jump | InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Reload | InputButtons.Switch | InputButtons.Fire);
            Step(ref s, c, b, MathF.Sin(k * 0.1f), MathF.Cos(k * 0.07f));
            Assert.Equal((BufferedAction.None, 0), (s.Buffered, s.BufferTicks));
        }
    }

    // ---------------------------------------------------------------- review of 2026-10-29: the narrowed hold rule, the detonation latch, the countdown

    [Fact]
    public void A_held_button_does_not_break_out_of_a_reaction_but_a_press_in_its_last_window_does()
    {
        // The light interrupt (front clip): Jump ends it from 0.467 s (tick 15), time alone at 0.98 s. Space (or melee) pressed at tick 1 and held
        // to the end never acts (the hold rule does not apply in a reaction); pressed in the last 0.3 s before its branch (Jump 0.467 s, melee 0.793 s)
        // and held, it acts there.
        var c = On(EvadeTests.Config);
        foreach (var (button, from, expect) in new[] { (InputButtons.Jump, 1, false), (InputButtons.Melee, 1, false), (InputButtons.Jump, 8, true), (InputButtons.Melee, 18, true) })
        {
            var s = Fresh(c, Tactical) with { Reaction = ReactionKind.Interrupt, ReactionDirection = ReactionDirection.Front, ReactionTicks = 0 };
            bool acted = false;
            for (int k = 1; k <= 40; k++)
            {
                bool wasReacting = s.Reaction != ReactionKind.None;
                Step(ref s, c, k >= from ? button : InputButtons.None);
                if (wasReacting && (s.EvadeTicks == 1 || s.MeleeStep == 1)) acted = true;
            }
            Assert.True(acted == expect, $"{button} held from tick {from}: acted {acted}");
            if (!expect) Assert.Equal((0, 0), (s.EvadeTicks, s.MeleeStep));   // still held at the end, nothing started
        }
    }

    [Fact]
    public void Grenade_reload_and_switch_held_past_the_window_do_not_fire_but_melee_does()
    {
        var c = On(GrenadeTests.Config);
        PlayerState Knife() => Combat.Respawned(Loadouts.Choose(c, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, -1, -1, -1, 0), c) with { Clip = 5 };
        // The knife swing ends at tick 30 (1.0 s); each button pressed 10 ticks before (after the combo window) and held to the end.
        int end = 30;
        foreach (var button in new[] { InputButtons.Grenade, InputButtons.Reload, InputButtons.Switch, InputButtons.Melee })
        {
            var s = Knife();
            var slot = s.Slot;
            Step(ref s, c, InputButtons.Melee);
            for (int k = 1; k <= end + 2; k++)
            {
                Step(ref s, c, k >= end - 10 ? button : InputButtons.None);
                if (k == end - 1) Assert.Equal(button == InputButtons.Melee ? BufferedAction.Melee : BufferedAction.None, s.Buffered);   // only melee is kept past 0.3 s
            }
            bool acted = button switch
            {
                InputButtons.Grenade => s.Throwing || s.Grenades < Knife().Grenades,
                InputButtons.Reload => true,   // R held reloads by itself once the weapon is free (the M6 rule, not the buffer): only the buffer is checked
                InputButtons.Switch => s.Slot != slot,
                _ => s.MeleeStep == 1,
            };
            Assert.True(acted == (button is InputButtons.Melee or InputButtons.Reload), $"{button} held past the window: acted {acted}");
            // Pressed inside the window instead: each acts.
            var t = Knife();
            Step(ref t, c, InputButtons.Melee);
            for (int k = 1; k <= end + 2; k++) Step(ref t, c, k == end - 5 ? button : InputButtons.None);
            bool now = button switch
            {
                InputButtons.Grenade => t.Throwing,
                InputButtons.Reload => t.ReloadLeft > 0f,
                InputButtons.Switch => t.Slot != slot,
                _ => t.MeleeStep == 1,
            };
            Assert.True(now, $"{button} pressed 5 ticks before the end did not act");
        }
    }

    [Fact]
    public void Space_held_while_falling_does_not_roll_on_landing_but_a_late_press_does()
    {
        // A Tactical (no jump pack) dropping 10 m (1 s): Space pressed at the start and held lands without a roll; pressed in the last 0.3 s it rolls.
        var c = On(EvadeTests.Config);
        foreach (var (late, expect) in new[] { (false, false), (true, true) })
        {
            var s = Fresh(c, Tactical) with { Position = new Vector3(0f, 0f, 10f), Grounded = false };
            int landed = -1; bool rolled = false;
            for (int k = 0; k < 60; k++)
            {
                bool press = late ? k >= 25 : k >= 0;   // the body lands on tick 30 or 31
                Step(ref s, c, press ? InputButtons.Jump : InputButtons.None);
                if (landed < 0 && s.Grounded) landed = k;
                if (s.EvadeTicks == 1) rolled = true;
            }
            Assert.InRange(landed, 28, 33);
            Assert.Equal(expect, rolled);
        }
    }

    [Fact]
    public void A_buffered_reload_that_detonates_does_not_also_reload_while_r_stays_down()
    {
        // A remote-detonated launcher with grenades out, in a melee swing: R pressed late in the swing and held. The replay detonates as the swing
        // ends; R still down must not start a reload (the press was the detonation's), a release and a new press does.
        var launcher = EvadeTests.Config.Weapons[1] with
        {
            Name = "launcher", Delivery = DeliveryKind.Projectile, Projectile = GrenadeTests.FragBall with { MaxActive = 6 },
        };
        var c = On(EvadeTests.Config with { Weapons = [EvadeTests.Config.Weapons[0], launcher] });
        Assert.True(Shots.RemoteDetonated(launcher));
        int end = SwingEnd();
        var s = Fresh(c, Tactical) with { Clip = 3, ProjectilesOut = 2 };
        Step(ref s, c, InputButtons.Melee);
        bool detonated = false;
        for (int k = 1; k <= end + 10; k++)
        {
            var e = Step(ref s, c, k >= end - 5 ? InputButtons.Reload : InputButtons.None);
            detonated |= e.Detonate;
            if (detonated) Assert.Equal(0f, s.ReloadLeft);
        }
        Assert.True(detonated);
        Assert.True(s.DetonateHeld);
        Step(ref s, c, InputButtons.None);
        Step(ref s, c, InputButtons.Reload);
        Assert.True(s.ReloadLeft > 0f);
    }

    [Fact]
    public void A_buffered_press_does_not_cross_into_the_match_begins_countdown()
    {
        // World level: in the warm-up player 1 swings and presses melee late (buffered); player 2 joins, the countdown respawns everyone: the buffer is gone
        // and no attack starts during the countdown (its attack buttons are dropped, and nothing is replayed).
        ClassLoadout Tac(string name, Team side) => new(name, 0, 1, SimConfig.PlaceholderMeleeSteps)
            { PrimaryChoices = [0], PistolChoices = [1], EquipmentChoices = [0], Side = side, Role = "tactical", Evade = 0 };
        var c = On(new SimConfig
        {
            Weapons = EvadeTests.Config.Weapons, Thrown = [GrenadeTests.Frag], Evades = [EvadeTests.Roll],
            Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)],
            HealthMax = 1000f, ShieldMax = 0f, HealthRegenDelay = 1e6f,
        });
        var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 5, 0), 0f)])
            { Rules = new MatchRules { TimeLimit = 0f, CountdownTime = 1f, RestartDelay = 0.2f } };
        w.Spawn(1, 0);
        int end = SwingEnd();
        var buttons = InputButtons.Melee;
        for (int k = 0; k < end - 3; k++) { w.Step(id => new PlayerInput(0, 0f, 0f, 0f, buttons)); buttons = k == end - 6 ? InputButtons.Melee : InputButtons.None; }
        Assert.Equal(MatchState.Warmup, w.State);
        Assert.Equal(BufferedAction.Melee, w.Players[1].Buffered);
        w.Spawn(2, 1);
        w.Step(id => new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));
        Assert.Equal(MatchState.Countdown, w.State);
        for (int k = 0; k < 25 && w.State == MatchState.Countdown; k++)
        {
            Assert.Equal((BufferedAction.None, 0), (w.Players[1].Buffered, w.Players[1].MeleeStep));
            w.Step(id => new PlayerInput(0, 0f, 0f, 0f, id == 1 ? InputButtons.Melee | InputButtons.Grenade : InputButtons.None));   // dropped presses are not buffered either
        }
        Assert.Equal(BufferedAction.None, w.Players[1].Buffered);
    }

    [Fact]
    public void A_replayed_melee_press_picks_and_keeps_its_lock_like_a_fresh_press()
    {
        // feel/melee-lock merge: a melee press kept through a roll is replayed when the roll lets it attack (its MeleeAt, 0.7 s). On that tick the raw
        // input has no press, but the client's lock choice looks at the replayed tick (InputBuffer.Replayed), so it locks the enemy 3 m away 30 degrees
        // to the left and the attack turns to it in its alignment window, as a fresh press would.
        static float T(float ticks) => ticks / 30f;
        float duration = T(30.5f), hit = T(6.5f);
        var variant = new MeleeVariant(3.5f, Enumerable.Range(0, 61).Select(i => 2f * Math.Clamp(duration * i / 60f / hit, 0f, 1f)).ToArray());
        var step = new MeleeStep(duration, hit, T(12.5f), T(18.5f), T(15.5f), T(21.5f), 10f, 5f, 3.5f)
            { Reach = new MeleeReach(0f, T(3f), 0f, hit, 3.5f, 1f, [variant]) };
        var c = On(EvadeTests.Config with { Classes = [EvadeTests.Config.Classes[0], EvadeTests.Config.Classes[1] with { Melee = [step, step] }, EvadeTests.Config.Classes[2]] });
        float a = 30f * MathF.PI / 180f;
        var enemy = Combat.Respawned(new PlayerState(2, new Vector3(-MathF.Sin(a) * 3f, 3f * MathF.Cos(a) + 0f, 0f), Vector3.Zero, MathF.PI, true) { Class = 2, Team = Team.Chaos }, c);
        (PlayerState End, bool LockedOnReplay) Run(bool useLock)
        {
            var s = Fresh(c, Tactical) with { Team = Team.SpaceMarines };
            Step(ref s, c, InputButtons.Jump);   // a forward roll along +Y from the origin (the enemy moves with it: kept 3 m ahead, 30 degrees left)
            bool lockedOnReplay = false;
            for (int k = 1; k < 40; k++)
            {
                var input = new PlayerInput((uint)k, 0f, 0f, 0f, k == 15 ? InputButtons.Melee : InputButtons.None);
                var others = new Dictionary<int, PlayerState> { [2] = enemy with { Position = enemy.Position + new Vector3(s.Position.X, s.Position.Y, 0f) } };
                var (ls, li) = InputBuffer.Replayed(s, input, c);
                if (useLock && MeleeLock.Wants(ls, li))
                {
                    int target = MeleeLock.Choose(ls, li, others, c, null, 0);
                    if (s.MeleeStep == 0 && s.Buffered == BufferedAction.Melee && target == 2) lockedOnReplay = true;
                    input = input with { MeleeTarget = target };
                }
                if (s.MeleeStep == 0 && s.Buffered == BufferedAction.Melee) Assert.False(MeleeLock.Wants(s, input), "the raw input alone has no press");
                s = Combat.StepPlayer(s, input, c, out _, null, others);
                if (s.MeleeStep > 0 && s.MeleeTime > T(4f)) break;
            }
            Assert.True(s.MeleeStep > 0, "the buffered press attacked");
            return (s, lockedOnReplay);
        }
        var (locked, picked) = Run(true);
        var (free, _) = Run(false);
        output.WriteLine($"replayed press: locked {picked}; heading after the alignment {locked.Yaw * 180f / MathF.PI:F1} degrees (no lock {free.Yaw * 180f / MathF.PI:F1})");
        Assert.True(picked);
        Assert.InRange(locked.Yaw * 180f / MathF.PI, 25f, 35f);   // turned to the target
        Assert.InRange(free.Yaw * 180f / MathF.PI, -1f, 1f);     // the view's heading
    }

    // ---------------------------------------------------------------- wire and hash

    [Fact]
    public void The_own_record_carries_the_buffer_and_a_full_snapshot_stays_at_1200_bytes()
    {
        Assert.Equal(46, Protocol.Version);
        foreach (var action in Enum.GetValues<BufferedAction>())
            foreach (int ticks in action == BufferedAction.None ? new[] { 0 } : new[] { 0, 1, 4, 9, 15 })
            {
                var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true)
                    { Alive = true, Buffered = action, BufferTicks = ticks, RaiseTicks = Combat.MaxRaiseTicks, ReadyTicks = 300, GrenadeHeld = true, SlotPrimaryHeld = true };
                var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [me], Self: 3)))).Players[0];
                Assert.Equal(me, back);
                Assert.Equal((BufferedAction.None, 0), (Protocol.Slim(me).Buffered, Protocol.Slim(me).BufferTicks));   // others never see it
            }
        // The fullest snapshot (16 players with the buffer set, every block full) is still exactly the budget.
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(id => new PlayerState(id, new Vector3(1e4f), new Vector3(100f), 3f, true)
        {
            Alive = true, Health = 1e3f, Shield = 1e3f, Class = Protocol.MaxClasses - 1, Clip = 65535, Reserve = 65535, Grenades = 255, Equipment = 254,
            Buffered = BufferedAction.Reload, BufferTicks = InputBuffer.MaxTicks, RaiseTicks = Combat.MaxRaiseTicks, ReadyTicks = 300, GrenadeHeld = true,
        }).ToArray();
        var projectiles = Enumerable.Range(0, Protocol.MaxSnapshotProjectiles).Select(k => new ProjectileView(60000 + k, 254, 0, new Vector3(k, -k, 3), ProjectileFlags.Detonated)).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles, Match: new MatchView(MatchState.Playing, Team.None, uint.MaxValue, 65535, 65535),
            Kills: [.. Enumerable.Range(0, Protocol.MaxSnapshotKills).Select(k => new KillView(65535 - k, 65535, 1, KillCause.Thrown, 254))],
            Points: [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 1f, true), Protocol.MaxSnapshotPoints)], Shots: ShotTests.ManyShots(players, Protocol.MaxSnapshotShots)));
        output.WriteLine($"16 players, the own record's buffer set: {bytes.Length} bytes (protocol {Protocol.Version})");
        Assert.Equal(1194, Protocol.MaxSnapshotBytes);
        Assert.Equal(Protocol.MaxSnapshotBytes, bytes.Length);
        var read = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal((BufferedAction.Reload, InputBuffer.MaxTicks, Combat.MaxRaiseTicks), (read.Players[6].Buffered, read.Players[6].BufferTicks, read.Players[6].RaiseTicks));
    }

    [Fact]
    public void The_config_hash_covers_the_window_and_the_movement_scale_and_configs_without_them_hash_as_before()
    {
        var plain = new SimConfig();
        var seen = new HashSet<ulong> { plain.Hash() };
        Assert.Equal(plain.Hash(), (plain with { InputBufferWindow = 0f }).Hash());
        Assert.Equal(plain.Hash(), MovementScale.Apply(plain, 1f).Hash());
        Assert.True(seen.Add((plain with { InputBufferWindow = 0.3f }).Hash()));
        Assert.True(seen.Add((plain with { InputBufferWindow = 0.2f }).Hash()));
        Assert.True(seen.Add(MovementScale.Apply(plain, 0.9f).Hash()));
        Assert.True(seen.Add((plain with { MoveSpeedScale = 0.9f }).Hash()));   // the record alone changes it too
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(911)]
    [InlineData(912)]
    public void Prediction_matches_the_server_through_buffered_presses_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // The Assault with the slash pack and the dash, the Tactical with the roll; presses timed into swings, rolls, landings and recoveries.
        var config = On(EvadeTests.Config with
        {
            Evades = [EvadeTests.Roll, JumpPackDashTests.Dash],
            Classes = [EvadeTests.Config.Classes[0] with { Evade = 1 }, EvadeTests.Config.Classes[1]],
            JumpPacks = [SlashPack with { RechargeRate = 100f }],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        });
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 900, options: new ClientOptions { RequestedClass = Assault });
        h.AddClient(nonce: 901, options: new ClientOptions { RequestedClass = Tactical });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        InputButtons[] presses = [InputButtons.Jump, InputButtons.Melee, InputButtons.MeleeHeavy, InputButtons.Reload, InputButtons.Switch, InputButtons.Jump];
        PlayerInput Input(int i, uint t)
        {
            uint cycle = (t + (uint)i * 11) / 60, phase = (t + (uint)i * 11) % 60;
            var buttons = InputButtons.None;
            if (i == 0)
            {
                // A low jump, the slash after the lift-off, then Space pressed in the recovery (held some cycles, tapped others).
                if (phase < 3) buttons |= InputButtons.Jump;
                if (phase == 16) buttons |= InputButtons.Melee;
                if (cycle % 2 == 0 ? phase >= 24 && phase < 50 : phase == 26 + cycle % 5) buttons |= InputButtons.Jump;
            }
            else
            {
                // A swing or a roll, then one of the buttons pressed near its end.
                if (phase == 0) buttons |= cycle % 2 == 0 ? InputButtons.Melee : InputButtons.Jump;
                // Near the end: the swing's last 0.3 s (1.2 to 1.47 s) or the roll's (0.73 to 0.97 s).
                if (phase == (cycle % 2 == 0 ? 36 + cycle % 8 : 22 + cycle % 7)) buttons |= presses[(int)(cycle % (uint)presses.Length)];
            }
            return new PlayerInput(t, 0f, phase > 40 ? 0.6f : 0f, 0.3f * i + 0.01f * cycle, buttons, -1.2f);
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < 2; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int buffered = mine.Count(p => p.Buffered != BufferedAction.None);
            int fired = mine.Zip(mine.Skip(1)).Count(p => p.First.Buffered != BufferedAction.None && p.Second.Buffered == BufferedAction.None
                && (p.Second.Jump == JumpPhase.TakeOff || p.Second.EvadeTicks == 1 || p.Second.MeleeTime == 0f && p.Second.MeleeStep > 0 || p.Second.ReloadLeft > p.First.ReloadLeft || p.Second.Slot != p.First.Slot));
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {buffered} ticks with a buffered press, {fired} buffered actions started");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(buffered >= 40 && fired >= 8, $"client {i}: {buffered} buffered ticks, {fired} started");
            Assert.Empty(mismatches);
        }
    }

}
