using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// BR-02 (2026-10-30; every class since protocol 48, owner 2026-10-31): a charge fires nothing from the charge's start to its end: a trigger pressed just
/// before (a held charge, a delayed charged shot), during (Fire neither fires nor ends the charge) or on the tick the charge ends gives no shot, no ammunition use and no
/// heat; normal fire resumes after the charge. Every other class is unchanged (Fire ends its charge once the clip allows, and fires). Synthetic weapons and rush.
/// </summary>
public class NoFireInChargeTests(ITestOutputHelper output)
{
    const int Tactical = 0, Assault = 1;
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);

    // [0] melta (recharging cone), [1] rifle, [2] pistol, [3] plasma gun (charge with a fire delay), [4] lascannon (held charge).
    static SimConfig Config(string role) => new()
    {
        Weapons = [MeltaTests.Melta, Rifle, Pistol, ChargeTests.PlasmaGun, ChargeTests.Las],
        Classes =
        [
            new ClassLoadout("tactical", 1, 2, [Light]) { PrimaryChoices = [0, 1, 3, 4], PistolChoices = [2], MeleeRush = RushTests.Spec, Side = Team.SpaceMarines, Role = role },
            new ClassLoadout("assault", 1, 2, [Light]) { PrimaryChoices = [0, 1, 3, 4], PistolChoices = [2], MeleeRush = RushTests.Spec, Side = Team.SpaceMarines, Role = "assault" },
        ],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 1000f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static PlayerState Fresh(SimConfig c, int cls, int primary, bool pistol)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls, Primary = primary, Pistol = 2 }, c);
        if (pistol) s = s with { Slot = WeaponSlot.Pistol };
        return s;
    }

    static (PlayerState S, bool Fired) Tick(PlayerState s, SimConfig c, InputButtons b, float my = 1f)
    {
        var next = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out var ev);
        return (next, ev.Fired);
    }

    /// <summary>
    /// Walks 6 ticks with Fire held for <paramref name="holdBefore"/> of them, presses F on the next (Fire still held when <paramref name="fireAtStart"/>), then runs
    /// the charge with Fire pressed over <paramref name="fireFrom"/>..end. Returns the shots seen while charging and the state after the charge.
    /// </summary>
    static (int Shots, int Ticks, bool Ended, PlayerState Last, bool FiredAsItEnded) Run(SimConfig c, int cls, int primary, bool pistol, bool fireBefore, bool fireAtStart, int fireFrom)
    {
        var s = Fresh(c, cls, primary, pistol);
        for (int k = 0; k < 6; k++) s = Tick(s, c, fireBefore ? InputButtons.Fire : InputButtons.None).S;
        var (started, _) = Tick(s, c, InputButtons.MeleeHeavy | (fireAtStart ? InputButtons.Fire : InputButtons.None));
        Assert.Equal(RushPhase.Rush, started.Rush);
        s = started;
        int shots = 0, ticks = 1;
        bool endTick = false;
        for (int k = 1; k < 60 && s.Rush != RushPhase.None; k++, ticks++)
        {
            var (n, fired) = Tick(s, c, k >= fireFrom ? InputButtons.Fire : InputButtons.None, 0f);
            if (fired && n.Rush != RushPhase.None) shots++;
            else if (fired) endTick = true;   // the tick the charge ends is no longer the charge's
            s = n;
        }
        return (shots, ticks, s.Rush == RushPhase.None, s, endTick);
    }

    [Theory]
    [InlineData(0, false)]   // melta
    [InlineData(1, false)]   // rifle
    [InlineData(3, false)]   // plasma gun
    [InlineData(4, false)]   // lascannon
    [InlineData(1, true)]    // pistol
    public void A_tactical_fires_nothing_through_the_charge_whatever_was_pressed(int primary, bool pistol)
    {
        foreach (var (before, atStart, from) in new[] { (true, true, 99), (false, true, 99), (true, false, 99), (false, false, 2), (false, false, 15), (true, true, 1) })
        {
            var c = Config("tactical");
            var s0 = Fresh(c, Tactical, primary, pistol);
            var (shots, ticks, ended, last, _) = Run(c, Tactical, primary, pistol, before, atStart, from);
            output.WriteLine($"primary {primary} pistol {pistol} before {before} start {atStart} from {from}: {shots} shots, rush ran {ticks} ticks, ended {ended}, clip {last.Clip}");
            Assert.Equal(0, shots);
            Assert.True(ended);
            Assert.Equal(RushTests.Spec.Rush.End, ticks / 30f, 0.1f);   // Fire never cut it short: the clip plays to its end (1.0 s)
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(4)]
    public void A_trigger_held_into_the_charge_costs_no_ammunition_or_heat_while_it_lasts(int primary)
    {
        var c = Config("tactical");
        var s = Fresh(c, Tactical, primary, false);
        for (int k = 0; k < 6; k++) s = Tick(s, c, InputButtons.Fire | InputButtons.None).S;
        s = Tick(s, c, InputButtons.MeleeHeavy | InputButtons.Fire).S;
        Assert.Equal(RushPhase.Rush, s.Rush);
        var (clip, heat) = (s.Clip, s.Heat);
        while (s.Rush != RushPhase.None)
        {
            s = Tick(s, c, InputButtons.Fire, 0f).S;
            if (s.Rush != RushPhase.None) { Assert.Equal(clip, s.Clip); Assert.True(s.Heat <= heat + 1e-3f, $"heat {s.Heat} > {heat}"); Assert.False(s.Charging); Assert.Equal(0, s.PendingShot); }
        }
    }

    [Fact]
    public void Fire_pressed_after_the_charge_fires_normally()
    {
        var c = Config("tactical");
        var s = Fresh(c, Tactical, 1, false);
        for (int k = 0; k < 6; k++) s = Tick(s, c, InputButtons.None).S;
        s = Tick(s, c, InputButtons.MeleeHeavy).S;
        while (s.Rush != RushPhase.None) s = Tick(s, c, InputButtons.Fire, 0f).S;   // held through to the end
        int clip = s.Clip;
        // Held across the end: a full-automatic rifle goes on firing on the held trigger; released and pressed again always does.
        bool any = false;
        for (int k = 0; k < 5; k++) { var (n, f) = Tick(s, c, InputButtons.Fire); s = n; any |= f; }
        Assert.True(any);
        Assert.True(s.Clip < clip);
        s = Tick(s, c, InputButtons.None).S;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(4)]
    public void Every_class_fires_nothing_through_the_charge_and_fire_never_ends_it(int primary)
    {
        // sim/no-fire-charging-all (protocol 48): the Assault (a non-tactical) is held to the same rule: the clip plays to its end, no shot meanwhile.
        foreach (var from in new[] { 1, 2, 15, 99 })
        {
            var c = Config("tactical");
            var (shots, ticks, ended, last, _) = Run(c, Assault, primary, false, false, false, from);
            output.WriteLine($"assault primary {primary} from {from}: {shots} shots, {ticks} ticks, clip {last.Clip}");
            Assert.Equal(0, shots);
            Assert.True(ended);
            Assert.Equal(RushTests.Spec.Rush.End, ticks / 30f, 0.1f);
        }
    }

    [Fact]
    public void A_pistol_and_a_held_fire_give_no_shot_for_the_assault_either()
    {
        var c = Config("tactical");
        var (shots, _, ended, last, _) = Run(c, Assault, 1, true, false, true, 1);
        Assert.Equal(0, shots);
        Assert.True(ended);
        Assert.Equal(Fresh(c, Assault, 1, true).Clip, last.Clip);
    }

    [Fact]
    public void Aim_spam_during_the_charge_changes_nothing()
    {
        var c = Config("tactical");
        foreach (var cls in new[] { Tactical, Assault })
        {
            var s = Fresh(c, cls, 1, false);
            for (int k = 0; k < 6; k++) s = Tick(s, c, InputButtons.None).S;
            s = Tick(s, c, InputButtons.MeleeHeavy).S;
            Assert.Equal(RushPhase.Rush, s.Rush);
            int ticks = 1;
            for (int k = 1; k < 60 && s.Rush != RushPhase.None; k++, ticks++)
            {
                s = Tick(s, c, k % 2 == 0 ? InputButtons.Aim : InputButtons.None, 0f).S;
                if (s.Rush != RushPhase.None) Assert.False(s.Aiming);
            }
            Assert.Equal(RushTests.Spec.Rush.End, ticks / 30f, 0.1f);   // Aim neither ended it nor altered it
            var (after, _) = Tick(s, c, InputButtons.Aim);
            Assert.True(after.Aiming);   // aim works again once it ended
        }
    }

    [Fact]
    public void A_stick_that_ends_the_charge_early_drops_a_fire_press_of_that_tick_and_does_not_buffer_it()
    {
        var c = Config("tactical");
        var s = Fresh(c, Assault, 1, false);
        for (int k = 0; k < 6; k++) s = Tick(s, c, InputButtons.None).S;
        s = Tick(s, c, InputButtons.MeleeHeavy).S;
        int clip = s.Clip;
        bool shot = false;
        // S (stick back) ends the charge from its move-on time; Fire pressed on the same tick fires nothing, and released it is gone.
        for (int k = 1; k < 60 && s.Rush != RushPhase.None; k++)
        {
            var (n, f) = Tick(s, c, InputButtons.Fire, -1f);
            shot |= f; s = n;
        }
        Assert.False(shot);
        Assert.Equal(clip, s.Clip);
        var (idle, fired) = Tick(s, c, InputButtons.None);
        Assert.False(fired);
        Assert.Equal(clip, idle.Clip);
    }
}
