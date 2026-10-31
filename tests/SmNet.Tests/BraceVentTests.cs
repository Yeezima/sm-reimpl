using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Fidelity gap 7: the heavy bolter's braced stance (entry and exit times, braced cooldown and heat, what the stance blocks, reactions,
/// death) and the plasma vent (rate, overheat multiplier, end rules, automatic vent while overheated), the plasma cannon's overheat
/// slow, the reader of all three, protocol 29's bits and exact prediction through brace and vent at 150 ms RTT. Synthetic data only.
/// </summary>
public class BraceVentTests(ITestOutputHelper output)
{
    /// <summary>Heavy-bolter-like: 0.233 s, 3 heat per shot (100, recovery 20, 25/s after 0.3 s); braced 0.1 s and 1.8 heat; the install's entry and exit times.</summary>
    static readonly WeaponSpec HeavyBolter = new("test_heavy_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto,
        Damage: 35f, ShotCooldown: 0.233f, ClipSize: 400, ReserveSize: 0, ReloadTime: 1.5f, MaxRange: 250f,
        FallOffStart: 250f, FallOffEnd: 250f, FallOffEndMultiplier: 1f, HeadMultiplier: 1f, BodyMultiplier: 1f, ShieldHeadMultiplier: 0.7f, ShieldBodyMultiplier: 0.7f)
    {
        Heat = new HeatSpec(3f, 100f, 75f, 20f, 25f, 0.3f, 0f),
        // The clips' frames: 45 and 41, branches at frames 43 and 39; the exit at the entry's speed 1 (owner's recollection: Expertise speeds both).
        Brace = new BraceSpec(0.1f, 1.8f) { EnterTime = 1.5f * (43f / 45f), ExitTime = 41f / 30f * (39f / 41f), FireMode = "mp_fire_mode_heavy_bolter_braced_fire" },
        AlwaysReady = true,
    };
    static readonly VentSpec Vent45 = new([new(0.1f, 45f), new(0.25f, 45f), new(0.5f, 45f), new(0.75f, 45f), new(1f, 45f)], 1f, 0.01f, 0.5f);
    /// <summary>Plasma-gun-like: semi auto, 25 heat per shot (so 4 shots overheat it), recovery 0.1, cools 4/s after 0.35 s (1 s after an overheat); vents 45/s.</summary>
    static readonly WeaponSpec PlasmaGun = new("test_plasma_gun", WeaponSlot.Ranged, TriggerKind.SemiAuto,
        Damage: 40f, ShotCooldown: 0.1f, ClipSize: 1000, ReserveSize: 0, ReloadTime: 1f, MaxRange: 100f,
        FallOffStart: 100f, FallOffEnd: 100f, FallOffEndMultiplier: 1f, HeadMultiplier: 1f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f)
    { Heat = new HeatSpec(25f, 100f, 80f, 0.1f, 4f, 0.35f, 1f), Vent = Vent45 };
    /// <summary>Plasma-cannon-like: overheat slows the carrier to 0.5 for 1.5 s; vents at 0.6 × 45 while overheated.</summary>
    static readonly WeaponSpec PlasmaCannon = PlasmaGun with
    {
        Name = "test_plasma_cannon", Heat = new HeatSpec(50f, 100f, 80f, 0.1f, 3f, 0.55f, 0f) { OverheatSpeed = 0.5f, OverheatSpeedTime = 1.5f },
        Vent = Vent45 with { OverheatMultiplier = 0.6f },
    };
    static readonly WeaponSpec Pistol = new("test_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto,
        Damage: 30f, ShotCooldown: 0.02f, ClipSize: 8, ReserveSize: 24, ReloadTime: 0.5f, MaxRange: 20f,
        FallOffStart: 20f, FallOffEnd: 20f, FallOffEndMultiplier: 1f, HeadMultiplier: 3f, BodyMultiplier: 1.5f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 0.5f);
    static readonly MeleeStep Knife = new(1.0f, 0.25f, 0.4f, 0.6f, 0.5f, 0.7f, 40f, 20f, 3.5f);

    const int Devastator = 0, Tactical = 1, Cannoneer = 2;
    static readonly SimConfig Config = new()
    {
        Weapons = [HeavyBolter, PlasmaGun, PlasmaCannon, Pistol],
        Classes =
        [
            new ClassLoadout("test - devastator", 0, 3, [Knife]) { PrimaryChoices = [0], PistolChoices = [3] },
            new ClassLoadout("test - tactical", 1, 3, [Knife]) { PrimaryChoices = [1], PistolChoices = [3] },
            new ClassLoadout("test - cannon", 2, 3, [Knife]) { PrimaryChoices = [2], PistolChoices = [3] },
        ],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.0001f, MaxAcceleration = 1e4f,
    };
    static float Dt => Config.TickSeconds;

    static PlayerState Fresh(int playerClass) =>
        Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = playerClass }, Config);

    static bool Step(ref PlayerState s, InputButtons buttons, float moveY = 0f, float yaw = 0f)
    {
        s = Combat.StepSelf(s, new PlayerInput(0, 0f, moveY, yaw, buttons), Config, out bool fired, out _);
        return fired;
    }

    static int Ticks(float seconds) => Brace.TicksFor(seconds, Dt);

    // ---------------------------------------------------------------- the brace

    [Fact]
    public void Reload_enters_the_brace_after_its_entry_time_and_braced_fire_uses_the_braced_cooldown_and_heat()
    {
        var s = Fresh(Devastator);
        // Unbraced: 0.233 s between shots, 3 heat each.
        int unbraced = 0;
        for (int i = 0; i < 30; i++) unbraced += Step(ref s, InputButtons.Fire) ? 1 : 0;
        Assert.Equal(5, unbraced);   // ticks 0, 7, 14, 21, 28 (0.233 s ≈ 7 ticks)
        s = Fresh(Devastator);
        Assert.False(Step(ref s, InputButtons.Reload | InputButtons.Fire));   // the press starts the entry; no shot
        Assert.Equal((BracePhase.Entering, 1), (s.Brace, s.BraceTicks));
        int entryTicks = Ticks(HeavyBolter.Brace!.EnterTime);
        Assert.Equal(43, entryTicks);   // 1.433 s at 30 Hz
        for (int i = 2; i < entryTicks; i++)
        {
            Assert.False(Step(ref s, InputButtons.Fire), $"tick {i}: no shot while entering");
            Assert.Equal((BracePhase.Entering, i), (s.Brace, s.BraceTicks));
        }
        Step(ref s, InputButtons.None);
        Assert.Equal((BracePhase.Braced, 0), (s.Brace, s.BraceTicks));
        float before = s.Heat;
        int braced = 0;
        for (int i = 0; i < 30; i++) braced += Step(ref s, InputButtons.Fire) ? 1 : 0;
        Assert.Equal(10, braced);   // 0.1 s = 3 ticks
        Assert.True(s.Heat > before);
        // Heat per braced shot: one more shot from a fresh braced state adds exactly 1.8 (no cooling in its tick: the delay restarts).
        var t = s with { Heat = 50f, SinceHeat = 0f, Cooldown = 0f };
        Assert.True(Step(ref t, InputButtons.Fire));
        Assert.Equal(51.8f, t.Heat, 3);
        // The Coolant perk's 0.7 is the brace's own heat (Brace.Braced replaces PerShot only).
        Assert.Equal((0.1f, 0.7f, 35f), (Brace.Braced(HeavyBolter with { Brace = HeavyBolter.Brace with { HeatPerShot = 0.7f } }).ShotCooldown,
            Brace.Braced(HeavyBolter with { Brace = HeavyBolter.Brace with { HeatPerShot = 0.7f } }).Heat!.PerShot, Brace.Braced(HeavyBolter).Damage));
    }

    static PlayerState Braced()
    {
        var s = Fresh(Devastator);
        Step(ref s, InputButtons.Reload);
        for (int i = 0; i < 60 && s.Brace != BracePhase.Braced; i++) Step(ref s, InputButtons.None);
        Assert.Equal(BracePhase.Braced, s.Brace);
        return s;
    }

    [Fact]
    public void Entering_and_braced_players_do_not_walk_sprint_evade_melee_or_switch_but_turn_and_aim()
    {
        var s = Fresh(Devastator);
        Step(ref s, InputButtons.Reload);
        var start = s.Position;
        for (int i = 0; i < 20; i++) Step(ref s, InputButtons.Sprint | InputButtons.Jump | InputButtons.Melee | InputButtons.Switch | InputButtons.Aim, moveY: 1f, yaw: 0.5f);
        Assert.Equal(BracePhase.Entering, s.Brace);
        Assert.Equal(start, s.Position);
        Assert.Equal((0, WeaponSlot.Ranged, false, 0), (s.MeleeStep, s.Slot, s.Aiming, s.EvadeTicks));   // entering: no aim mode either
        Assert.Equal(0.5f, s.Yaw, 4);   // the body turns

        s = Braced();
        start = s.Position;
        for (int i = 0; i < 30; i++)
        {
            Step(ref s, (i % 2 == 0 ? InputButtons.Melee | InputButtons.Switch | InputButtons.Jump : InputButtons.None) | InputButtons.Sprint | InputButtons.Aim, moveY: 1f, yaw: -1f);
            Assert.Equal((start, 0, WeaponSlot.Ranged, 0, BracePhase.Braced), (s.Position, s.MeleeStep, s.Slot, s.EvadeTicks, s.Brace));
        }
        Assert.True(s.Aiming);   // aim mode works braced
        Assert.Equal(-1f, s.Yaw, 4);
    }

    [Fact]
    public void A_melee_key_held_through_the_stance_does_not_attack_when_it_ends_a_new_press_does()
    {
        var s = Braced();
        // Pressed while braced: no attack, but the key counts as held (the held flags follow the real buttons, as in a reaction).
        Step(ref s, InputButtons.Melee);
        Assert.Equal((0, true, BracePhase.Braced), (s.MeleeStep, s.MeleeHeld, s.Brace));
        Step(ref s, InputButtons.Melee | InputButtons.Reload);   // the exit, the key still down
        for (int i = 0; i < 45; i++) Step(ref s, InputButtons.Melee);   // the 1.3 s exit
        Assert.Equal(BracePhase.None, s.Brace);
        Assert.Equal(0, s.MeleeStep);   // no press edge after the stance: still no attack
        Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.Melee);
        Assert.Equal(1, s.MeleeStep);   // a new press attacks
    }

    // ---------------------------------------------------------------- the exit's evade and the jump-pack guard

    static readonly SimConfig EvadeConfig = Config with
    {
        Classes = [Config.Classes[Devastator] with { Evade = 0 }, Config.Classes[Tactical], Config.Classes[Cannoneer],
            Config.Classes[Devastator] with { Name = "test - pack devastator", JumpPack = 0 }],
        Evades = [EvadeTests.Hop],
        JumpPacks = [JumpPackTests.Pack],
    };

    static PlayerState Exiting(SimConfig c, int cls = Devastator)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c);
        s = Combat.StepSelf(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Reload), c, out _, out _);
        for (int i = 0; i < 60 && s.Brace != BracePhase.Braced; i++) s = Combat.StepSelf(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _, out _);
        Assert.Equal(BracePhase.Braced, s.Brace);
        s = Combat.StepSelf(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Reload), c, out _, out _);
        s = Combat.StepSelf(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _, out _);
        Assert.Equal((BracePhase.Exiting, 2), (s.Brace, s.BraceTicks));
        return s;
    }

    [Fact]
    public void Jump_ends_the_exit_only_when_an_evade_starts_that_tick()
    {
        // A class with the heavy hop: Jump during the exit ends it and the hop starts in the same tick.
        var s = Exiting(EvadeConfig);
        s = Combat.StepSelf(s, new PlayerInput(0, 1f, 0f, 0f, InputButtons.Jump), EvadeConfig, out _, out _);
        Assert.Equal((BracePhase.None, 1), (s.Brace, s.EvadeTicks));
        // The evade's spacing not over: no evade, so the exit runs on.
        s = Exiting(EvadeConfig) with { EvadeLock = 5 };
        s = Combat.StepSelf(s, new PlayerInput(0, 1f, 0f, 0f, InputButtons.Jump), EvadeConfig, out _, out _);
        Assert.Equal((BracePhase.Exiting, 3, 0), (s.Brace, s.BraceTicks, s.EvadeTicks));
        // A class without an evade: Jump does nothing to the exit.
        s = Exiting(Config);
        s = Combat.StepSelf(s, new PlayerInput(0, 1f, 0f, 0f, InputButtons.Jump), Config, out _, out _);
        Assert.Equal((BracePhase.Exiting, 3), (s.Brace, s.BraceTicks));
        // Jump with a melee press in the same tick starts no evade either.
        s = Exiting(EvadeConfig);
        s = Combat.StepSelf(s, new PlayerInput(0, 1f, 0f, 0f, InputButtons.Jump | InputButtons.Melee), EvadeConfig, out _, out _);
        Assert.Equal((BracePhase.Exiting, 0), (s.Brace, s.EvadeTicks));
    }

    [Fact]
    public void A_class_with_a_jump_pack_never_braces()
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 3 }, EvadeConfig);
        Assert.NotNull(Loadouts.InHand(EvadeConfig, s)?.Brace);
        Assert.False(Brace.CanStart(s, EvadeConfig));
        Assert.True(Brace.CanStart(s));   // the state alone would allow it
        s = Combat.StepSelf(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Reload), EvadeConfig, out _, out _);
        Assert.Equal(BracePhase.None, s.Brace);
    }

    [Fact]
    public void A_weapon_without_brace_vent_or_overheat_slow_hashes_as_before()
    {
        // The new fields are written behind a marker only when present: the bytes of a heat weapon with a brace are its plain bytes with
        // one block (starting 0xB7) inserted, so the plain weapon writes exactly what it wrote before (the pinned config hashes, OrkSimTests).
        var plain = HeavyBolter with { Brace = null };
        byte[] without = Variants.Bytes(plain), with = Variants.Bytes(HeavyBolter);
        int k = 0;
        while (k < without.Length && without[k] == with[k]) k++;
        Assert.True(with.Length > without.Length);
        Assert.Equal(0xB7, with[k]);
        Assert.Equal(without[k..], with[(with.Length - (without.Length - k))..]);
        Assert.Equal(without, Variants.Bytes(plain with { Heat = plain.Heat! with { OverheatSpeed = 1f, OverheatSpeedTime = 0f } }));
        Assert.NotEqual(without, Variants.Bytes(plain with { Heat = plain.Heat! with { OverheatSpeed = 0.5f, OverheatSpeedTime = 1.5f } }));
    }

    [Fact]
    public void Reload_while_braced_exits_after_the_exit_time_the_player_walks_meanwhile_and_fires_unbraced_after()
    {
        var s = Braced();
        Step(ref s, InputButtons.Reload);
        Assert.Equal((BracePhase.Exiting, 1), (s.Brace, s.BraceTicks));
        int exitTicks = Ticks(HeavyBolter.Brace!.ExitTime);
        Assert.Equal(39, exitTicks);   // 1.3 s
        var start = s.Position;
        for (int i = 2; i < exitTicks; i++)
        {
            Assert.False(Step(ref s, InputButtons.Fire, moveY: 1f), "no shot while exiting");
            Assert.Equal((BracePhase.Exiting, i), (s.Brace, s.BraceTicks));
        }
        Assert.NotEqual(start, s.Position);   // the legs walk during the exit
        // The tick that reaches the exit time leaves the stance and may fire; then the primary fires at 0.233 s.
        Assert.True(Step(ref s, InputButtons.Fire));
        Assert.Equal(BracePhase.None, s.Brace);
        int shots = 0;
        for (int i = 0; i < 30; i++) shots += Step(ref s, InputButtons.Fire) ? 1 : 0;
        Assert.Equal(4, shots);

        // A Jump press during the exit of a class without an evade changes nothing (Jump_ends_the_exit_only_when_an_evade_starts_that_tick).
        s = Braced();
        Step(ref s, InputButtons.Reload);
        Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.Jump);
        Assert.Equal((BracePhase.Exiting, 3), (s.Brace, s.BraceTicks));
        // A Reload press during the entry is ignored (the stance's own transition runs on).
        s = Fresh(Devastator);
        Step(ref s, InputButtons.Reload);
        Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.Reload);
        Assert.Equal((BracePhase.Entering, 3), (s.Brace, s.BraceTicks));
    }

    [Fact]
    public void No_reaction_knocks_a_player_out_of_any_brace_phase_and_death_clears_it()
    {
        // Owner's recollection (2026-10-28): stuns and hits did not knock you out of the brace; entering and exiting are immune too (ours).
        var s = Fresh(Devastator);
        Assert.True(Reaction.CanReact(s, Config));
        Step(ref s, InputButtons.Reload);
        Assert.Equal(BracePhase.Entering, s.Brace);
        Assert.False(Reaction.CanReact(s, Config));
        s = Braced();
        Assert.False(Reaction.CanReact(s, Config));
        Step(ref s, InputButtons.Reload);
        Assert.Equal(BracePhase.Exiting, s.Brace);
        Assert.False(Reaction.CanReact(s, Config));
        // The entry cannot be cancelled: Reload, Jump, melee and a switch press change nothing until the stance is reached.
        s = Fresh(Devastator);
        Step(ref s, InputButtons.Reload);
        for (int i = 2; i < Ticks(HeavyBolter.Brace!.EnterTime); i++)
        {
            Step(ref s, i % 2 == 0 ? InputButtons.Reload | InputButtons.Jump | InputButtons.Melee | InputButtons.Switch : InputButtons.None);
            Assert.Equal((BracePhase.Entering, i), (s.Brace, s.BraceTicks));
        }
        Step(ref s, InputButtons.None);
        Assert.Equal(BracePhase.Braced, s.Brace);

        var dead = Combat.Killed(Braced(), Config);
        Assert.Equal((BracePhase.None, 0, false), (dead.Brace, dead.BraceTicks, dead.Venting));
        var back = Combat.Respawned(Braced() with { Venting = true }, Config);
        Assert.Equal((BracePhase.None, 0, false), (back.Brace, back.BraceTicks, back.Venting));
        // Not from the air, a switch or with no brace weapon in hand.
        Assert.False(Brace.CanStart(Fresh(Devastator) with { Grounded = false }));
        Assert.False(Brace.CanStart(Fresh(Devastator) with { SwitchLeft = 0.2f }));
        var pistol = Fresh(Devastator);
        Step(ref pistol, InputButtons.Switch);
        for (int i = 0; i < 30; i++) Step(ref pistol, InputButtons.None);
        Step(ref pistol, InputButtons.Reload);
        Assert.Equal(BracePhase.None, pistol.Brace);
    }

    // ---------------------------------------------------------------- the vent

    [Fact]
    public void Reload_vents_at_45_per_second_until_heat_0_and_fire_ends_a_vent()
    {
        var s = Fresh(Tactical) with { Heat = 60f, SinceHeat = 0f };
        Step(ref s, InputButtons.Reload);
        Assert.True(s.Venting);
        Assert.Equal(60f - 45f * Dt, s.Heat, 3);   // vented; no cooling yet (its 0.35 s delay since the last heat gain)
        int ticks = 1;
        while (s.Venting && ticks < 100) { Step(ref s, InputButtons.None); ticks++; }
        Assert.Equal(0f, s.Heat);
        // The vent adds to the cooling (owner's recollection): 1.5 a tick, plus 4/s once the delay has passed: 38 ticks instead of 40.
        Assert.Equal(38, ticks);
        Assert.False(s.Venting);

        // Fire ends the vent and shoots in the same tick.
        s = Fresh(Tactical) with { Heat = 60f, SinceHeat = 0f };
        Step(ref s, InputButtons.Reload);
        Step(ref s, InputButtons.None);
        Assert.True(Step(ref s, InputButtons.Fire));
        Assert.False(s.Venting);
        // Sprint ends it too (a vent never starts while sprinting forward).
        s = Fresh(Tactical) with { Heat = 60f, SinceHeat = 0f };
        Step(ref s, InputButtons.Reload);
        Step(ref s, InputButtons.Sprint, moveY: 1f);
        Assert.False(s.Venting);
        // Nothing to vent: no vent.
        s = Fresh(Tactical);
        Step(ref s, InputButtons.Reload);
        Assert.False(s.Venting);
        // A weapon without a vent (the heavy bolter) never vents.
        var hb = Fresh(Devastator) with { Heat = 60f, Overheated = true };
        Step(ref hb, InputButtons.None);
        Assert.False(hb.Venting);
    }

    [Fact]
    public void Venting_goes_on_only_while_standing_or_walking()
    {
        // Owner's recollection: everything but walking at normal walk speed stopped venting; standing still counts (ours).
        PlayerState Venting()
        {
            var v = Fresh(Tactical) with { Heat = 90f, SinceHeat = 0f };
            Step(ref v, InputButtons.Reload);
            Assert.True(v.Venting);
            return v;
        }
        var s = Venting();
        for (int i = 0; i < 10; i++) Step(ref s, InputButtons.None);
        Assert.True(s.Venting);                                   // standing
        s = Venting();
        for (int i = 0; i < 10; i++) Step(ref s, InputButtons.None, moveY: 1f);
        Assert.True(s.Venting);                                   // walking (forward, no Sprint)
        Assert.True(new Vector2(s.Velocity.X, s.Velocity.Y).Length() > 1f);
        foreach (var (what, buttons, moveY) in new (string, InputButtons, float)[]
        {
            ("sprint", InputButtons.Sprint, 1f), ("fire", InputButtons.Fire, 0f), ("aim", InputButtons.Aim, 0f), ("melee", InputButtons.Melee, 0f),
            ("switch", InputButtons.Switch, 0f), ("grenade", InputButtons.Grenade, 0f),
        })
        {
            s = Venting();
            Step(ref s, buttons, moveY: moveY);
            Assert.False(s.Venting, what);
        }
        // The evade (a class with the hop) stops it too.
        var e = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Tactical }, VentEvadeConfig) with { Heat = 90f, SinceHeat = 0f };
        e = Combat.StepSelf(e, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Reload), VentEvadeConfig, out _, out _);
        Assert.True(e.Venting);
        e = Combat.StepSelf(e, new PlayerInput(0, 1f, 0f, 0f, InputButtons.Jump), VentEvadeConfig, out _, out _);
        Assert.Equal((false, 1), (e.Venting, e.EvadeTicks));
    }

    static readonly SimConfig VentEvadeConfig = Config with
    {
        Classes = [Config.Classes[Devastator], Config.Classes[Tactical] with { Evade = 0 }, Config.Classes[Cannoneer]],
        Evades = [EvadeTests.Hop],
    };

    [Fact]
    public void An_overheated_weapon_vents_by_itself_fire_does_not_end_it_and_the_overheat_ends_at_recovery()
    {
        var s = Fresh(Tactical);
        for (int i = 0; i < 4; i++) { Assert.True(Step(ref s, InputButtons.Fire)); Step(ref s, InputButtons.None); Step(ref s, InputButtons.None); Step(ref s, InputButtons.None); }
        // The fourth shot overheated it; the ticks after it vented by themselves.
        Assert.True(s.Venting);
        Assert.True(s.Overheated);
        int lockTicks = 0;
        while (s.Overheated && lockTicks < 200) { Assert.False(Step(ref s, InputButtons.Fire)); lockTicks++; }
        Assert.InRange(lockTicks, 58, 66);   // about 2.1 s: 45/s vented, plus 4/s cooling after 1 s (passive cooling alone: 26 s)
        Assert.True(s.Heat <= PlasmaGun.Heat!.Recovery);
        output.WriteLine($"plasma gun overheat lock with the vent: {(lockTicks + 3) * Dt:0.00} s");

        // The cannon vents at 0.6 × 45 while overheated.
        var c = Fresh(Cannoneer) with { Heat = 100f, Overheated = true, SinceHeat = 0f };
        Step(ref c, InputButtons.None);
        Assert.True(c.Venting);
        Assert.Equal(100f - (3f + 45f * 0.6f) * Dt, c.Heat, 3);   // its cooling (3/s, no delay after an overheat) plus the vent
    }

    [Fact]
    public void Vent_rate_table_and_stage_rules()
    {
        var v = new VentSpec([new(0.25f, 10f), new(0.5f, 20f), new(1f, 40f)], 0.5f, 0.01f, 0.5f);
        Assert.Equal((10f, 10f, 20f, 20f, 40f, 40f), (Vent.RateAt(v, 0f), Vent.RateAt(v, 0.25f), Vent.RateAt(v, 0.3f), Vent.RateAt(v, 0.5f), Vent.RateAt(v, 0.9f), Vent.RateAt(v, 1.5f)));
        var h = PlasmaGun.Heat!;
        Assert.Equal((VentStage.Overheat, VentStage.High, VentStage.Low, VentStage.NoHeat, VentStage.NoHeat),
            (Vent.StageOf(Vent45, h, 100f, true), Vent.StageOf(Vent45, h, 50f, false), Vent.StageOf(Vent45, h, 20f, false), Vent.StageOf(Vent45, h, 0.5f, false), Vent.StageOf(Vent45, h, 0f, false)));
        // 0.1 s at the 1.0 fraction's 40/s × 0.5 while overheated: 2 heat; still above the recovery amount, so still overheated.
        var (heat, over) = Vent.Step(v, h, 100f, true, 0.1f);
        Assert.Equal(98f, heat, 4);
        Assert.True(over);
        // Reaching the recovery amount ends the overheat.
        Assert.False(Vent.Step(v, h, 0.5f, true, 0.1f).Overheated);
    }

    [Fact]
    public void The_plasma_cannon_overheat_halves_the_carriers_speed_for_1_5_s()
    {
        float SpeedAfter(PlayerState s, int ticks)
        {
            for (int i = 0; i < ticks; i++) Step(ref s, InputButtons.None, moveY: 1f);
            return new Vector2(s.Velocity.X, s.Velocity.Y).Length();
        }
        var cool = Fresh(Cannoneer);
        var hot = cool with { Heat = 100f, Overheated = true, SinceHeat = 0f };
        float normal = SpeedAfter(cool, 5), slowed = SpeedAfter(hot, 5);
        Assert.Equal(normal * 0.5f, slowed, 2);
        // After 1.5 s the speed is back (the cannon is still overheated: it vents at 27/s).
        Assert.Equal(normal, SpeedAfter(hot, 50), 2);
        // A plasma gun's overheat does not slow.
        var gun = Fresh(Tactical);
        Assert.Equal(SpeedAfter(gun, 5), SpeedAfter(gun with { Heat = 100f, Overheated = true, SinceHeat = 0f }, 5), 3);
        Assert.Equal((1f, 0.5f, 1f), (Vent.OverheatSpeed(PlasmaCannon.Heat, false, 0f), Vent.OverheatSpeed(PlasmaCannon.Heat, true, 1.49f), Vent.OverheatSpeed(PlasmaCannon.Heat, true, 1.5f)));
    }

    [Fact]
    public void Bracing_from_a_sprint_stops_the_body_on_the_press_tick()
    {
        // Full sprint speed first (the test config reaches it at once), then R: the entry clip has no root motion, so the body stops (ours).
        var s = Fresh(Devastator);
        for (int i = 0; i < 10; i++) Step(ref s, InputButtons.Sprint, moveY: 1f);
        Assert.True(new Vector2(s.Velocity.X, s.Velocity.Y).Length() > 6f);
        Step(ref s, InputButtons.Reload | InputButtons.Sprint, moveY: 1f);
        Assert.Equal(BracePhase.Entering, s.Brace);
        var at = s.Position;
        Assert.Equal(Vector2.Zero, new Vector2(s.Velocity.X, s.Velocity.Y));
        // With the slow acceleration of the install (0.2 s to full speed) the body would slide about a metre; here it stays put.
        var slow = Config with { GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f };
        var t = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Devastator }, slow);
        for (int i = 0; i < 30; i++) t = Combat.StepSelf(t, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), slow, out _, out _);
        t = Combat.StepSelf(t, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Reload | InputButtons.Sprint), slow, out _, out _);
        var planted = t.Position;
        for (int i = 0; i < 20; i++) t = Combat.StepSelf(t, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), slow, out _, out _);
        Assert.Equal(BracePhase.Entering, t.Brace);
        Assert.True(Vector3.Distance(planted, t.Position) < 1e-3f, $"slid {Vector3.Distance(planted, t.Position):0.000} m");
        for (int i = 0; i < 5; i++) Step(ref s, InputButtons.Sprint, moveY: 1f);
        Assert.Equal(at, s.Position);
    }

    static readonly SimConfig VentMoreConfig = Config with
    {
        Classes = [Config.Classes[Devastator], Config.Classes[Tactical] with { MeleeRush = RushTests.Spec }, Config.Classes[Cannoneer],
            Config.Classes[Tactical] with { Name = "test - plasma jumper", JumpPack = 0 }],
        JumpPacks = [JumpPackTests.Pack],
    };

    [Fact]
    public void A_reaction_the_charge_and_a_jump_pack_jump_stop_a_vent_strafing_and_backpedalling_keep_it()
    {
        PlayerState Venting(int cls)
        {
            var v = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, VentMoreConfig) with { Heat = 90f, SinceHeat = 0f };
            v = Combat.StepSelf(v, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Reload), VentMoreConfig, out _, out _);
            Assert.True(v.Venting);
            return v;
        }
        PlayerState After(PlayerState v, float x, float y, InputButtons b) => Combat.StepSelf(v, new PlayerInput(0, x, y, 0f, b), VentMoreConfig, out _, out _);
        Assert.False(After(Venting(Tactical) with { Reaction = ReactionKind.Stun, ReactionTicks = 0 }, 0f, 0f, InputButtons.None).Venting, "reaction");
        Assert.False(After(Venting(Tactical) with { Rush = RushPhase.Rush, RushTicks = 1 }, 0f, 0f, InputButtons.None).Venting, "charge");
        var jumper = After(Venting(3), 0f, 0f, InputButtons.Jump);
        Assert.NotEqual(JumpPhase.None, jumper.Jump);
        Assert.False(jumper.Venting, "jump-pack jump");
        var s = Venting(Tactical);
        for (int i = 0; i < 10; i++) s = After(s, 1f, 0f, InputButtons.None);
        Assert.True(s.Venting, "strafing");
        s = Venting(Tactical);
        for (int i = 0; i < 10; i++) s = After(s, 0f, -1f, InputButtons.None);
        Assert.True(s.Venting, "backpedalling");
    }

    [Fact]
    public void Aim_and_sprint_hold_off_the_overheated_auto_vent()
    {
        // Follows the owner's rule (everything but walking stops venting): an overheated player who holds Aim or sprints does not vent and only cools
        // passively (about 26 s for a full overheat); spec "Brace and vent", to check in a playtest.
        var hot = Fresh(Tactical) with { Heat = 100f, Overheated = true, SinceHeat = 0f };
        var s = hot;
        for (int i = 0; i < 30; i++) { Step(ref s, InputButtons.Aim); Assert.False(s.Venting, "aim"); }
        Assert.True(s.Overheated);
        s = hot;
        for (int i = 0; i < 30; i++) { Step(ref s, InputButtons.Sprint, moveY: 1f); Assert.False(s.Venting, "sprint"); }
        s = hot;
        Step(ref s, InputButtons.None);
        Assert.True(s.Venting);   // let go: it vents by itself
    }

    static readonly DamageSpec Hurt = new(25f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly SplashSpec Stomp = new(7f, Hurt, [new(0f, 1f), new(7f, 1f)], true, true, "target_all")
    {
        Reactions = [new(0.5f, ReactionKind.Stun, Backward: 2f, Duration: 0.2f), new(0f, ReactionKind.Interrupt)],
    };
    static float T(float ticks) => ticks / 30f;
    static readonly MeleeStep StompStep = new(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), 0f, 0f, 3.5f) { Stomp = Stomp };
    static readonly SimConfig BlastConfig = Config with
    {
        Classes = [Config.Classes[Devastator] with { Side = Team.Chaos }, Config.Classes[Tactical] with { MeleeHeavy = [StompStep], Side = Team.SpaceMarines }],
        HealthMax = 1000f, ShieldMax = 0f, HealthRegenDelay = 1e6f,
    };

    [Fact]
    public void A_real_blast_damages_a_braced_player_but_does_not_stun_or_push_him()
    {
        foreach (bool brace in new[] { true, false })
        {
            var w = new World(BlastConfig, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 2, 0), 0f)]);
            w.Spawn(1, 1);   // the stomper (marines)
            w.Spawn(2, 0);   // the heavy bolter (chaos)
            var reactions = new List<ReactionEvent>();
            for (int k = 0; k < 70; k++)
            {
                int tick = k;
                w.Step(id => new PlayerInput(0, 0f, 0f, id == 1 ? 0f : MathF.PI,
                    id == 2 && brace && tick == 0 ? InputButtons.Reload : id == 1 && tick == 50 ? InputButtons.MeleeHeavy : InputButtons.None));
                reactions.AddRange(w.LastReactions.Where(r => r.Target == 2));
            }
            var victim = w.Players[2];
            Assert.True(victim.Health < 1000f, "the blast's damage applies");
            if (brace)
            {
                Assert.Equal(BracePhase.Braced, victim.Brace);
                Assert.Empty(reactions);
                Assert.Equal((ReactionKind.None, new Vector3(0, 2, 0)), (victim.Reaction, victim.Position));
            }
            else Assert.NotEmpty(reactions);   // control: unbraced, the same blast stuns
        }
    }

    // ---------------------------------------------------------------- the reader

    static (string, object)[] Default(params (string, object)[] fields) =>
        [.. fields, ("required_upgrade_name", new (string, object)[] { ("required_list", Array.Empty<(string, object)>()) })];
    static (string, object)[] Needs(string upgrade, params (string, object)[] fields) =>
        [.. fields, ("required_upgrade_name", new (string, object)[] { ("required_list", new (string, object)[] { ("upgrade_name", upgrade) }) })];
    static (string, object)[] HeatCommand(float amount) =>
        [("action", new (string, object)[] { ("commands", new (string, object)[] { ("apply_heat", new (string, object)[] {
            ("heat_generated", new (string, object)[] { ("entry", new (string, object)[] { ("current_heat_percentage", 0f), ("heat_to_generate", amount) }) }) }) }) })];
    static (string, object)[] Delivery(float cooldown, float heat, float perkHeat) =>
    [
        ("ranged_delivery_type", new (string, object)[] { ("entry", Default(("type", new (string, object)[] { ("$ref", "_base_templates\\ranged_delivery_types\\raycast") }))) }),
        ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { ("do_damage", new (string, object)[]
        {
            ("damage_general", 35f), ("damagetype", "ranged"),
            ("damage_material_multipliers", new (string, object)[] { ("default_head", 1f), ("default_body", 1f) }),
            ("armourshield_damage_material_multipliers", new (string, object)[] { ("default_head", 0.7f), ("default_body", 0.7f) }),
        }) }) }),
        ("basic_weapon_tuning", new (string, object)[] { ("entry", Default(("shot_cooldown", cooldown))) }),
        ("max_range", 250f),
        ("fire_sim", new (string, object)[] { ("actions_by_upgrade", new (string, object)[] { ("entry", Default(HeatCommand(heat))), ("entry", Needs("perk_coolant", HeatCommand(perkHeat))) }) }),
    ];
    static (string, object)[] VentAbility(float rate, float overheatMultiplier) =>
    [
        ("$ref", "wargear\\ability_types\\heat_vent"),
        ("heat_vented", new (string, object)[] { ("entry", new (string, object)[] { ("current_heat_percentage", 1f), ("heat_to_vent_per_second", rate) }),
            ("entry", new (string, object)[] { ("current_heat_percentage", 0.1f), ("heat_to_vent_per_second", rate) }) }),
        ("vent_stages", new (string, object)[]
        {
            ("0. no_heat", new (string, object)[] { ("heat_percent", 0f) }), ("1. low_heat", new (string, object)[] { ("heat_percent", 0.01f) }),
            ("2. medium_heat", new (string, object)[] { ("heat_percent", 0f) }), ("3. high_heat", new (string, object)[] { ("heat_percent", 0.5f) }),
        }),
        ("overheat_heat_venting_multiplier", overheatMultiplier),
    ];
    static BafDocument Weapon(bool braced, bool vent, bool overheatSlow) => BafDocument.Read(SyntheticBafWriter.Build(
        ("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[][]
        {
            [("ability_ranged_primary", new (string, object)[] { ("$ref", "wargear\\ability_types\\standard_ranged"),
                ("trigger_data", new (string, object)[] { ("trigger", new (string, object)[] { ("$ref", "wargear\\trigger_policy\\full_auto") }) }), ("delivery", Delivery(0.233f, 3f, 1f)) })],
            braced ? [(WeaponTuning.BracedAbility, new (string, object)[] { ("$ref", "wargear\\ability_types\\standard_ranged"), ("delivery", Delivery(0.1f, 1.8f, 0.7f)) })] : [],
            vent ? [("ability_utility_upgraded", VentAbility(70f, 1f)), ("ability_utility", VentAbility(45f, 0.6f))] : [],
        }.SelectMany(x => x).ToArray()) }),
        ("common_data", new (string, object)[]
        {
            ("slot", "ranged"),
            ("ammunition", new (string, object)[] { ("entry", new (string, object)[] { ("by_upgrade", new (string, object)[] { ("ammunition_info", Default(("capacity_clip", 400), ("capacity_reserve", 0))) }) }) }),
            ("heat", new (string, object)[] { ("wargear\\heat_info", new (string, object)[]
            {
                ("overheat_amount", 100f), ("high_heat_amount", 75f), ("recovery_amount", 20f),
                ("cool_down_upgrade_list", new (string, object)[] { ("upgradeable", new (string, object)[] { ("cool_down_delay_after_heat_gain", 0.3f), ("cool_down_delay_after_overheat", 0f), ("cool_down_rate", 25f) }) }),
                ("actions", new (string, object)[] { ("overheat", new (string, object)[] { ("commands", overheatSlow ? new (string, object)[] { ("apply_modifiers", new (string, object)[]
                {
                    ("modifiers", new (string, object)[] { ("modifier", new (string, object)[] { ("modifier_type", "speed_maximum"), ("operation_type", "multiplication"), ("amount", 0.5f) }) }),
                    ("lifetime", 1.5f),
                }) } : []) }) }),
            }) }),
            ("fire_mode_secondary", new (string, object)[] { ("entry", Default(("upgrade_granted", braced ? "mp_fire_mode_heavy_bolter_braced_fire" : ""))) }),
        })));

    [Fact]
    public void The_reader_takes_the_braced_ability_the_vent_and_the_overheat_slow_with_their_perk_variants()
    {
        var notes = new List<string>();
        var hb = WeaponTuning.Read("hb", Weapon(braced: true, vent: false, overheatSlow: false), notes)!;
        Assert.Equal((0.233f, 3f), (hb.ShotCooldown, hb.Heat!.PerShot));
        Assert.Equal(new BraceSpec(0.1f, 1.8f) { FireMode = "mp_fire_mode_heavy_bolter_braced_fire" }, hb.Brace);
        Assert.Null(hb.Vent);
        Assert.Equal((1f, 0f), (hb.Heat.OverheatSpeed, hb.Heat.OverheatSpeedTime));
        using (UpgradeScope.Grant(["perk_coolant", BraceSpec.ExpertiseUpgrade]))
        {
            var perked = WeaponTuning.Read("hb", Weapon(braced: true, vent: false, overheatSlow: false), [])!;
            Assert.Equal((0.7f, true), (perked.Brace!.HeatPerShot, perked.Brace.Expertise));
        }

        var plasma = WeaponTuning.Read("plasma", Weapon(braced: false, vent: true, overheatSlow: true), notes)!;
        Assert.Null(plasma.Brace);
        Assert.Equal(("ability_utility", 0.6f, 0.01f, 0.5f), (plasma.Vent!.Ability, plasma.Vent.OverheatMultiplier, plasma.Vent.LowFrom, plasma.Vent.HighFrom));
        Assert.Equal([new DistancePoint(0.1f, 45f), new DistancePoint(1f, 45f)], plasma.Vent.Rates);
        Assert.Equal((0.5f, 1.5f), (plasma.Heat!.OverheatSpeed, plasma.Heat.OverheatSpeedTime));
        // The Plasma Heat Sink chain: the perk's string (or the blueprint's mp_plasma_pistol_upgraded_vent) selects the upgraded vent.
        foreach (var grant in WeaponTuning.UpgradedVentStrings)
            using (UpgradeScope.Grant([grant]))
                Assert.Equal(("ability_utility_upgraded", 70f), (WeaponTuning.Read("plasma", Weapon(false, true, true), [])!.Vent!.Ability, WeaponTuning.Read("plasma", Weapon(false, true, true), [])!.Vent!.Rates[0].Value));
        output.WriteLine(string.Join("\n", notes));
    }

    [Fact]
    public void Brace_timing_is_set_on_the_weapon_and_its_variants_from_the_graph_timings()
    {
        var timings = new BraceTuning.Timings("enter", 1.5f, 0.9556f, "exit", 1.367f, 0.9512f, 1f,
            new Dictionary<string, float> { [BraceTuning.Upgrade(BraceSpec.ExpertiseUpgrade)] = 2f },
            new Dictionary<string, float> { [BraceTuning.Upgrade("mp_fire_mode_heavy_bolter_braced_fire")] = 3f });
        var raw = HeavyBolter with { Brace = new BraceSpec(0.1f, 1.8f) { FireMode = "mp_fire_mode_heavy_bolter_braced_fire" } };
        raw = raw with { Variants = [new([BraceSpec.ExpertiseUpgrade], raw with { Brace = raw.Brace! with { Expertise = true } })] };
        var set = BraceTuning.WithTiming([raw, Pistol], timings);
        Assert.Same(Pistol, set[1]);
        Assert.Equal((1.4334f, 1.3002f), (set[0].Brace!.EnterTime, set[0].Brace!.ExitTime), new PairComparer());
        Assert.Equal((0.7167f, 0.6501f), (set[0].Variants[0].Spec.Brace!.EnterTime, set[0].Variants[0].Spec.Brace!.ExitTime), new PairComparer());   // Expertise speeds both (owner's recollection)
        // The brace graph's fire-mode speed (3) is not used: with it the exit would take 0.433 s.
        Assert.Equal(0.4334f, timings.ExitSeconds("mp_fire_mode_heavy_bolter_braced_fire", false), 3);
    }

    sealed class PairComparer : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) a, (float, float) b) => MathF.Abs(a.Item1 - b.Item1) < 1e-3f && MathF.Abs(a.Item2 - b.Item2) < 1e-3f;
        public int GetHashCode((float, float) v) => 0;
    }

    [Fact]
    public void The_config_hash_covers_the_brace_and_the_vent_and_unusable_ones_are_refused()
    {
        var seen = new HashSet<ulong> { Config.Hash() };
        WeaponSpec[] variants =
        [
            HeavyBolter with { Brace = null }, HeavyBolter with { Brace = HeavyBolter.Brace! with { ShotCooldown = 0.11f } },
            HeavyBolter with { Brace = HeavyBolter.Brace! with { HeatPerShot = 1.9f } }, HeavyBolter with { Brace = HeavyBolter.Brace! with { EnterTime = 1f } },
            HeavyBolter with { Brace = HeavyBolter.Brace! with { ExitTime = 1f } }, HeavyBolter with { Brace = HeavyBolter.Brace! with { Expertise = true } },
            HeavyBolter with { Vent = Vent45 }, HeavyBolter with { Heat = HeavyBolter.Heat! with { OverheatSpeed = 0.5f, OverheatSpeedTime = 1f } },
        ];
        foreach (var v in variants)
            Assert.True(seen.Add((Config with { Weapons = [v, PlasmaGun, PlasmaCannon, Pistol] }).Hash()), $"{v.Brace} {v.Vent} left the hash unchanged");
        foreach (var v in new[] { Vent45 with { OverheatMultiplier = 0.7f }, Vent45 with { LowFrom = 0.02f }, Vent45 with { Rates = [new(1f, 46f)] }, Vent45 with { Ability = "x" } })
            Assert.True(seen.Add((Config with { Weapons = [HeavyBolter, PlasmaGun with { Vent = v }, PlasmaCannon, Pistol] }).Hash()), $"{v} left the hash unchanged");

        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), Config.Weapons, Config.Classes));
        WeaponSpec[] bad =
        [
            HeavyBolter with { Brace = HeavyBolter.Brace! with { EnterTime = 2.2f } },   // 66 ticks: more than the 6 bits carry
            HeavyBolter with { Brace = HeavyBolter.Brace! with { ShotCooldown = 0f } }, HeavyBolter with { Brace = HeavyBolter.Brace! with { HeatPerShot = float.NaN } },
            HeavyBolter with { Vent = Vent45 with { Rates = [] } }, HeavyBolter with { Vent = Vent45 with { Rates = [new(0.5f, 1f), new(0.1f, 1f)] } },
            HeavyBolter with { Vent = Vent45 with { OverheatMultiplier = float.PositiveInfinity } },
            HeavyBolter with { Heat = HeavyBolter.Heat! with { OverheatSpeed = -1f } },
        ];
        foreach (var b in bad)
            Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [b, PlasmaGun, PlasmaCannon, Pistol], Config.Classes));
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void Snapshots_carry_the_brace_phase_and_the_vent_in_every_record_and_the_transition_ticks_in_the_own_record()
    {
        var me = Fresh(Devastator) with { PlayerId = 3, Brace = BracePhase.Entering, BraceTicks = 37, ReloadHeld = true };
        var other = me with { PlayerId = 4, Brace = BracePhase.Exiting, BraceTicks = 5, Venting = true };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(5, 4, 1, [me, other], Self: 3))));
        Assert.Equal(me, back.Players[0]);
        Assert.Equal(Protocol.Slim(other), back.Players[1]);
        Assert.Equal((BracePhase.Exiting, 0, true), (back.Players[1].Brace, back.Players[1].BraceTicks, back.Players[1].Venting));
        foreach (var phase in Enum.GetValues<BracePhase>())
            foreach (int ticks in new[] { 1, 31, 32, 63 })
            {
                var p = me with { Brace = phase, BraceTicks = phase is BracePhase.Entering or BracePhase.Exiting ? ticks : 0, Venting = ticks == 32 };
                Assert.Equal(p, Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(5, 4, 1, [p], Self: 3)))).Players[0]);
            }
        // Ticks without a transition, or a transition without ticks, are refused; so is the slot byte's bit 7.
        var braced = Protocol.Write(new Snapshot(5, 4, 1, [me with { Brace = BracePhase.Braced, BraceTicks = 0 }], Self: 3));
        Assert.Equal(2, braced[^34] & 7);   // the Reload byte comes just before the melee flag byte (HeatTests: ^33); Reload held
        var orphan = (byte[])braced.Clone(); orphan[^34] |= 8;   // ticks 1 while braced
        Assert.Null(Protocol.Read(orphan));
        var entering = Protocol.Write(new Snapshot(5, 4, 1, [me], Self: 3));
        var noTicks = (byte[])entering.Clone(); noTicks[^34] &= 6;
        Assert.Null(Protocol.Read(noTicks));
        var slim = Protocol.Write(new Snapshot(5, 4, 1, [other]));
        var unknown = (byte[])slim.Clone(); unknown[^31] |= 128;
        Assert.Null(Protocol.Read(unknown));
    }

    [Fact]
    public void A_sixteen_player_snapshot_with_the_brace_and_the_vent_stays_at_the_1200_byte_budget()
    {
        Assert.Equal(51, Protocol.Version);
        Assert.Equal(1194, Protocol.MaxSnapshotBytes);
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new PlayerState(i, new Vector3(1e4f), new Vector3(100f), 3f, true)
        {
            Alive = true, Health = 1e3f, Shield = 1e3f, Class = Protocol.MaxClasses - 1, Slot = WeaponSlot.Pistol, Clip = 65535, Reserve = 65535, OtherClip = 65535, OtherReserve = 65535,
            Cooldown = 1, ReloadLeft = 1, SinceDamage = 1, RespawnLeft = 1, SwitchLeft = 1, Pitch = 1, MeleeStep = Protocol.MaxMeleeStep, MeleeTime = 1,
            Heat = 100f, Overheated = true, SinceHeat = 1f, OtherHeat = 99f, OtherOverheated = true, OtherSinceHeat = 2f,
            Brace = BracePhase.Exiting, BraceTicks = 63, Venting = true, ReloadHeld = true, DetonateHeld = true, ReadyTicks = 300, RaiseTicks = 9,
        }).ToArray();
        var projectiles = Enumerable.Range(0, Protocol.MaxSnapshotProjectiles).Select(k => new ProjectileView(k, 0, 0, new Vector3(1e3f), ProjectileFlags.None)).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles));
        output.WriteLine($"16 players, {projectiles.Length} projectiles, braced exits and vents (protocol {Protocol.Version}): {bytes.Length} bytes");
        Assert.True(bytes.Length <= Protocol.SnapshotBudget, $"{bytes.Length} bytes");
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(players.Select(p => p.PlayerId == 7 ? p : Protocol.Slim(p)), back.Players);
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(71)]
    [InlineData(72)]
    public void Prediction_matches_the_server_through_brace_and_vent_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // No damage (hits are not predicted).
        // The Devastator has the hop, so a Jump that starts an evade during the exit (phase 126) ends the stance under loss too.
        var config = EvadeConfig with { Weapons = Config.Weapons.Select(w => w with { Damage = 0f }).ToList() };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Devastator });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Tactical });
        h.AddClient(nonce: 102, options: new ClientOptions { RequestedClass = Cannoneer });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons;
            uint phase = (t + (uint)i * 11) % 240;
            if (i == 0)
            {
                if (phase is 0 or 1 or 20) buttons |= InputButtons.Reload;          // brace (the press at 20 lands in the entry: ignored)
                if (phase is >= 30 and < 110) buttons |= InputButtons.Fire;          // fire through the entry and braced
                if (phase is 120 or 121) buttons |= InputButtons.Reload;            // exit
                if (phase == 126) buttons |= InputButtons.Jump;                     // the exit's evade
                if (phase is 160 or 161) buttons |= InputButtons.Reload;            // brace again
                if (phase is 196 or 197) buttons |= InputButtons.Reload;            // and leave it (the 1.3 s exit ends before the cycle does)
                if (phase is >= 222 and < 236) buttons |= InputButtons.Fire;
            }
            else
            {
                bool fire = phase < 60 ? phase % 4 == 0 : phase is >= 150 and < 170 && phase % 6 == 0;
                if (fire) buttons |= InputButtons.Fire;                             // overheat, then a few shots
                if (phase is 110 or 111 or 175 or 176) buttons |= InputButtons.Reload;   // vent the rest
                if (phase is 180) buttons |= InputButtons.Fire;                     // fire ends that vent
            }
            return move with { Buttons = buttons, Pitch = 0.2f };
        }
        h.Run(TimeSpan.FromSeconds(24), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 3; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });

        for (int i = 0; i < 3; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            var pairs = mine.Zip(mine.Skip(1)).ToList();
            int braces = pairs.Count(p => p.First.Brace != BracePhase.Braced && p.Second.Brace == BracePhase.Braced);
            int exits = pairs.Count(p => p.First.Brace == BracePhase.Exiting && p.Second.Brace == BracePhase.None);
            int evadeExits = pairs.Count(p => p.First.Brace == BracePhase.Exiting && p.Second.Brace == BracePhase.None && p.Second.EvadeTicks > 0);
            int vents = pairs.Count(p => !p.First.Venting && p.Second.Venting);
            int overheats = pairs.Count(p => !p.First.Overheated && p.Second.Overheated);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {braces} braces, {exits} exits ({evadeExits} by an evade), {vents} vents, {overheats} overheats");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            if (i == 0) Assert.True(braces >= 3 && exits >= 3 && evadeExits >= 1, $"client {i}: too little brace activity");
            else Assert.True(vents >= 4 && overheats >= 2, $"client {i}: too little vent activity");
            Assert.Empty(mismatches);
        }
    }
}
