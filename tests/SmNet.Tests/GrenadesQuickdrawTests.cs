using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/grenades-quickdraw (config hash, no protocol change; the owner's requests of 2026-11-06, both **estimates** to review in a playtest): the quicker throw (the release at half the toss graph's
/// time) that frees the player at the release (<see cref="SimConfig.ThrowDuration"/> = the release), and the quickdraw switch (<see cref="SimConfig.QuickdrawSwitch"/>: the new
/// weapon is up when the 0.1 s switch ends, no raise). Synthetic specs; the install part checks the loader's values and is skipped without an install.
/// </summary>
public class GrenadesQuickdrawTests(ITestOutputHelper output)
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    /// <summary>A fact that is skipped unless the game install is found on this machine (never in CI).</summary>
    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (never in CI)";
        }
    }

    /// <summary>The throw as the install has it (0.2 s / 0.667 s) and as the owner's feel loads it (0.1 s, ending at the release).</summary>
    static readonly SimConfig Old = GrenadeTests.Config;
    static readonly SimConfig Quick = GrenadeTests.Config with { ThrowRelease = 0.1f, ThrowDuration = 0.1f };

    static PlayerState Fresh(SimConfig c) =>
        Combat.Respawned(Loadouts.Choose(c, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, -1, -1, -1, 0), c);

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, out SelfEvents e, float moveY = 0f) =>
        Combat.StepPlayer(s, new PlayerInput(0, 0f, moveY, 0f, b), c, out e);

    /// <summary>Presses Grenade and steps until the item leaves: the tick of the release (press tick = 0) and the state after it.</summary>
    static (int Tick, PlayerState State) ThrowToRelease(SimConfig c)
    {
        var s = Step(Fresh(c), c, InputButtons.Grenade, out var e);
        if (e.Threw) return (0, s);
        for (int k = 1; k < 60; k++)
        {
            s = Step(s, c, InputButtons.None, out e);
            if (e.Threw) return (k, s);
        }
        return (-1, s);
    }

    [Fact]
    public void The_grenade_leaves_the_hand_after_0_1_s_instead_of_0_2_s()
    {
        var (quick, after) = ThrowToRelease(Quick);
        var (old, _) = ThrowToRelease(Old);
        Assert.Equal(3, quick);   // 0.1 s at 30 Hz
        Assert.Equal(6, old);     // 0.2 s: the toss graph's Ability_Ranged_Fire
        Assert.False(after.Throwing);   // the throw ends with the release
        Assert.Equal((0, 1f), (after.Grenades, after.GrenadeCooldown));   // spent, its cooldown running from the release
        output.WriteLine($"press to release: {old} ticks ({old / 30f:0.000} s) -> {quick} ticks ({quick / 30f:0.000} s)");
    }

    [Theory]
    [InlineData("fire")]
    [InlineData("melee")]
    [InlineData("switch")]
    [InlineData("move")]
    [InlineData("aim")]
    public void Every_action_starts_on_the_tick_after_the_release(string action)
    {
        foreach (var (c, free) in new[] { (Quick, true), (Old, false) })
        {
            var (_, s) = ThrowToRelease(c);
            var buttons = action switch
            {
                "fire" => InputButtons.Fire, "melee" => InputButtons.Melee, "switch" => InputButtons.Switch, "aim" => InputButtons.Aim, _ => InputButtons.None,
            };
            var next = Step(s, c, buttons, out var e, action == "move" ? 1f : 0f);
            bool started = action switch
            {
                "fire" => e.Fired,
                "melee" => next.MeleeStep > 0,
                "switch" => next.SwitchLeft > 0f,
                "aim" => next.Aiming,
                _ => next.Velocity.Y > 0f,
            };
            if (action == "move") Assert.True(started);   // walking was never locked by a throw
            else Assert.Equal(free, started);
            output.WriteLine($"{action} on the tick after the release: {(started ? "starts" : "blocked")} ({(free ? "owner's feel" : "the data's 0.667 s")})");
        }
    }

    [Fact]
    public void A_second_throw_still_waits_for_the_items_cooldown()
    {
        var c = Quick;
        var s = Fresh(c) with { Grenades = 2 };
        s = Step(s, c, InputButtons.Grenade, out _);
        int second = -1;
        bool released = false;
        for (int k = 1; k < 90 && second < 0; k++)
        {
            s = Step(s, c, k % 2 == 0 ? InputButtons.Grenade : InputButtons.None, out var e);
            released |= e.Threw;
            if (released && s.Throwing) second = k;
        }
        Assert.InRange(second, 33, 35);   // 3 ticks to the release + the 1 s cooldown (30 ticks), on the next press edge
    }

    [Fact]
    public void The_toss_clip_opens_the_hand_on_the_release_tick()
    {
        // The wind-up (graph release 0.2 s) is played over the simulation's 0.1 s; after it the clip runs at its own speed.
        Assert.Equal(0f, ActionHandoff.TossClipTime(0f, 0.1f, 0.2f), 5);
        Assert.Equal(0.1f, ActionHandoff.TossClipTime(0.05f, 0.1f, 0.2f), 5);
        Assert.Equal(0.2f, ActionHandoff.TossClipTime(0.1f, 0.1f, 0.2f), 5);
        Assert.Equal(0.25f, ActionHandoff.TossClipTime(0.15f, 0.1f, 0.2f), 5);
        Assert.Equal(0.15f, ActionHandoff.TossClipTime(0.15f, 0.2f, 0.2f), 5);   // equal releases: the throw's own time
        Assert.Equal(0.15f, ActionHandoff.TossClipTime(0.15f, 0f, 0.2f), 5);
        Assert.Equal(0f, ActionHandoff.TossClipTime(float.NaN, 0.1f, 0.2f), 5);
    }

    // ---------------------------------------------------------------- the quickdraw switch

    static WeaponSpec Gun(string name, WeaponSlot slot, float raise) =>
        new WeaponSpec(name, slot, TriggerKind.SemiAuto, 10f, 0.1f, 30, 90, 1f, 250f, 245f, 250f, 1f, 1f, 1f, 1f, 1f) { RaiseDelay = raise };

    static SimConfig SwitchConfig(bool quickdraw, float switchTime) => new()
    {
        Weapons = [Gun("rifle", WeaponSlot.Ranged, 0.2f), Gun("pistol", WeaponSlot.Pistol, 1f / 6f)],
        Classes = [new ClassLoadout("t", 0, 1, SimConfig.PlaceholderMeleeSteps) { Evade = -1 }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        WeaponSwitchTime = switchTime, QuickdrawSwitch = quickdraw,
    };

    /// <summary>Ticks from the switch press (tick 0) to the new weapon's first shot with the trigger held from the press.</summary>
    static int SwitchToShot(SimConfig c, WeaponSlot from, InputButtons slotButton)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { Slot = from };
        s = Step(s, c, slotButton | InputButtons.Fire, out var e);
        if (e.Fired) return 0;
        for (int k = 1; k < 60; k++)
        {
            s = Step(s, c, (k % 2 == 0 ? InputButtons.Fire : InputButtons.None), out e);   // semi-auto: a fresh press every other tick
            if (e.Fired) return k;
        }
        return -1;
    }

    [Theory]
    [InlineData(WeaponSlot.Pistol, InputButtons.SlotPrimary)]
    [InlineData(WeaponSlot.Ranged, InputButtons.SlotPistol)]
    public void The_new_weapon_fires_0_1_s_after_the_switch_press_without_a_raise(WeaponSlot from, InputButtons button)
    {
        int quick = SwitchToShot(SwitchConfig(true, 0.1f), from, button);
        int old = SwitchToShot(SwitchConfig(false, 0.48f), from, button);
        Assert.InRange(quick, 3, 4);      // the switch's 3 ticks; the shot on the first press edge at or after them (presses every other tick)
        Assert.True(old >= 15 + 5, $"{old}");   // the 0.48 s switch (15 ticks) and then the raise (pistol 5 / rifle 6)
        output.WriteLine($"{from} -> other: switch press to first shot {old} ticks ({old / 30f:0.000} s) -> {quick} ticks ({quick / 30f:0.000} s)");
    }

    [Fact]
    public void The_weapon_is_up_when_the_switch_ends_and_a_sprint_or_melee_still_lowers_it()
    {
        var c = SwitchConfig(true, 0.1f);
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { Slot = WeaponSlot.Pistol };
        s = Step(s, c, InputButtons.SlotPrimary, out _);
        for (int k = 0; k < 5 && s.SwitchLeft > 0f; k++) s = Step(s, c, InputButtons.None, out _);
        Assert.Equal((WeaponSlot.Ranged, 0f), (s.Slot, s.SwitchLeft));
        Assert.True(s.ReadyTicks >= c.ReadyHoldTicks - 1, $"{s.ReadyTicks}");   // up (held through the switch, counting down from its end): the ready posture, the trigger fires at once
        Step(s, c, InputButtons.Fire, out var e);
        Assert.True(e.Fired);
        // A sprint during the switch: lowered at its end, the raise as before.
        var sprint = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { Slot = WeaponSlot.Pistol };
        sprint = Step(sprint, c, InputButtons.SlotPrimary | InputButtons.Sprint, out _, 1f);
        for (int k = 0; k < 5; k++) sprint = Step(sprint, c, InputButtons.Sprint, out _, 1f);
        Assert.Equal(0, sprint.ReadyTicks);
        // Off (the M32 rule): not up after the switch.
        var off = SwitchConfig(false, 0.1f);
        var o = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), off) with { Slot = WeaponSlot.Pistol };
        o = Step(o, off, InputButtons.SlotPrimary, out _);
        for (int k = 0; k < 5; k++) o = Step(o, off, InputButtons.None, out _);
        Assert.Equal(0, o.ReadyTicks);
    }

    [Fact]
    public void The_quickdraw_is_in_the_config_hash_and_off_by_default()
    {
        Assert.False(new SimConfig().QuickdrawSwitch);
        Assert.NotEqual(new SimConfig().Hash(), new SimConfig { QuickdrawSwitch = true }.Hash());
    }

    [InstallFact]
    public void The_install_loads_the_quick_throw_and_the_quickdraw_switch()
    {
        var c = GameplayTuning.Load(Game.Value!).Config;
        var data = GameplayTuning.Load(Game.Value!, ownerFeel: false).Config;
        Assert.Equal(data.ThrowRelease * GameplayTuning.OwnerThrowReleaseScale, c.ThrowRelease, 5);
        Assert.Equal(c.ThrowRelease, c.ThrowDuration, 5);
        Assert.Equal(GameplayTuning.OwnerQuickdrawTime, c.WeaponSwitchTime, 5);
        Assert.True(c.QuickdrawSwitch);
        Assert.False(data.QuickdrawSwitch);
        output.WriteLine($"throw release {data.ThrowRelease:0.000} s -> {c.ThrowRelease:0.000} s; throw end {data.ThrowDuration:0.000} s -> {c.ThrowDuration:0.000} s; " +
                         $"switch {data.WeaponSwitchTime:0.000} s (+ raise) -> {c.WeaponSwitchTime:0.000} s (no raise)");
    }
}
