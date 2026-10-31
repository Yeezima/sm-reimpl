using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M31, the owner's rules (2026-10-28): the charge needs the sprint (superseded in protocol 39: W held is enough; the jump-pack classes still charge from the ground only); the
/// first shot waits for the weapon to come up (raise delays by weapon family, always-ready weapons, aim mode, the ready hold and
/// what lowers the weapon); Fire taps and holds on every charge weapon; spawn-room blockers reach above the level (a change from
/// the original), shots still pass; protocol 20 carries the raise state within the 1200-byte budget. M32: the owner confirmed the
/// raise after a switch and the always-up weapons that never wait; the 10 s hold (protocol 21: ready ticks in a u16). Synthetic data only.
/// </summary>
public class OwnerRulesTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;

    // ---------------------------------------------------------------- the charge (M31's sprint gate, superseded in protocol 39)

    const int Assault = 0, Raptor = 1, Tactical = 2;

    /// <summary>Install-like speeds; an Assault (Space Marines) and a Raptor (Chaos) with the jump pack, a Tactical without, all with the charge.</summary>
    static readonly SimConfig RushConfig = new()
    {
        Weapons = [EvadeTests.Config.Weapons[0], EvadeTests.Config.Weapons[1]],
        Classes =
        [
            new ClassLoadout("assault", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, MeleeRush = RushTests.Spec, Side = Team.SpaceMarines },
            new ClassLoadout("raptor", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, MeleeRush = RushTests.Spec, Side = Team.Chaos },
            new ClassLoadout("tactical", 1, 0, SimConfig.PlaceholderMeleeSteps) { MeleeRush = RushTests.Spec, Side = Team.SpaceMarines },
        ],
        JumpPacks = [JumpPackTests.Pack],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    static PlayerState Fresh(SimConfig c, int cls) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, c);

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float my = 1f, CollisionWorld? world = null) =>
        Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out _, world);

    [Fact]
    public void Superseded_by_protocol_49_the_charge_needs_the_sprint_held_as_well_as_W()
    {
        // Protocol 39 dropped M31's speed gate ("just be holding w when you press f"); protocol 49 (sim/charge-needs-sprint, owner 2026-10-31): "you also need to be
        // holding shift". Only the held Sprint button and W matter (not the speed, aim mode or the trigger).
        var s = Fresh(RushConfig, Tactical);
        Assert.True(Rush.CanStart(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), RushTests.Spec, RushConfig));    // standing, Shift + W
        Assert.False(Rush.CanStart(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.None), RushTests.Spec, RushConfig));     // W alone
        for (int k = 0; k < 20; k++) s = Step(s, RushConfig, InputButtons.None);
        Assert.Equal(6.5f, s.Velocity.Y, 3);
        foreach (var b in new[] { InputButtons.Sprint, InputButtons.Sprint | InputButtons.Aim, InputButtons.Sprint | InputButtons.Fire })
            Assert.True(Rush.CanStart(s, new PlayerInput(0, 0f, 1f, 0f, b), RushTests.Spec, RushConfig), b.ToString());
        foreach (var b in new[] { InputButtons.None, InputButtons.Aim, InputButtons.Fire })
            Assert.False(Rush.CanStart(s, new PlayerInput(0, 0f, 1f, 0f, b), RushTests.Spec, RushConfig), b.ToString());
        Assert.False(Rush.CanStart(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Sprint), RushTests.Spec, RushConfig));   // Shift without W
    }

    [Theory]
    [InlineData(Assault)]
    [InlineData(Raptor)]
    public void The_jump_pack_classes_charge_on_the_ground_and_never_in_any_jump_phase(int cls)
    {
        // On the ground, sprinting: F charges.
        var s = Fresh(RushConfig, cls);
        for (int k = 0; k < 10; k++) s = Step(s, RushConfig, InputButtons.Sprint);
        Assert.Equal(RushPhase.Rush, Step(s, RushConfig, InputButtons.Sprint | InputButtons.MeleeHeavy).Rush);
        // Running without the sprint: protocol 49 (owner 2026-10-31): no charge, the stun strike.
        var run = Fresh(RushConfig, cls);
        for (int k = 0; k < 20; k++) run = Step(run, RushConfig, InputButtons.None);
        Assert.Equal(RushPhase.None, Step(run, RushConfig, InputButtons.MeleeHeavy).Rush);

        // Sprint, then jump with W and Sprint held and press F in every tick of the jump: take-off, ascent, hang, fall, landing.
        s = Fresh(RushConfig, cls);
        for (int k = 0; k < 10; k++) s = Step(s, RushConfig, InputButtons.Sprint);
        s = Step(s, RushConfig, InputButtons.Sprint | InputButtons.Jump);
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
        var phases = new HashSet<JumpPhase>();
        float fastest = 0f;
        for (int k = 0; k < 150 && s.Jump != JumpPhase.None; k++)
        {
            phases.Add(s.Jump);
            fastest = MathF.Max(fastest, s.Velocity.Y);
            // A release before each press so every tick is a press edge.
            var tried = Step(s, RushConfig, InputButtons.Sprint | InputButtons.MeleeHeavy);
            Assert.Equal(RushPhase.None, tried.Rush);
            s = Step(s, RushConfig, InputButtons.Sprint);
            Assert.Equal(RushPhase.None, s.Rush);
        }
        output.WriteLine($"{RushConfig.Classes[cls].Name}: phases {string.Join(", ", phases)}; fastest {fastest:F1} m/s forward");
        Assert.Superset(new HashSet<JumpPhase> { JumpPhase.TakeOff, JumpPhase.Ascent, JumpPhase.Hang, JumpPhase.Fall, JumpPhase.Landed }, phases);
        Assert.True(fastest > 0f, "W was held through the flight: only the phase stopped the charge");
        // Back on the ground after the landing: sprinting charges again.
        for (int k = 0; k < 10; k++) s = Step(s, RushConfig, InputButtons.Sprint);
        Assert.Equal(RushPhase.Rush, Step(s, RushConfig, InputButtons.Sprint | InputButtons.MeleeHeavy).Rush);
    }

    // ---------------------------------------------------------------- the first shot waits for the raise

    static WeaponSpec Gun(string name, TriggerKind trigger = TriggerKind.SemiAuto) =>
        new(name, WeaponSlot.Ranged, trigger, 10f, 0.1f, 30, 90, 1f, 250f, 245f, 250f, 1f, 1f, 1f, 1f, 1f);

    static SimConfig RaiseConfig(WeaponSpec w) => new()
    {
        Weapons = [w, Gun("pistol") with { Slot = WeaponSlot.Pistol, RaiseDelay = 1f / 6f }],
        Classes = [new ClassLoadout("t", 0, 1, SimConfig.PlaceholderMeleeSteps) { Evade = -1 }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    /// <summary>Ticks from a press on a lowered weapon (tick 1) until a shot leaves; 0: in the press tick.</summary>
    static int TicksToShot(SimConfig c, InputButtons extra = InputButtons.None, PlayerState? from = null, bool hold = false)
    {
        var s = from ?? Fresh(c, 0);
        for (int k = 0; k < 40; k++)
        {
            var buttons = extra | (k == 0 || hold ? InputButtons.Fire : InputButtons.None);
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, buttons), c, out var e);
            if (e.Fired) return k;
        }
        return -1;
    }

    [Theory]
    [InlineData(0.2f, 6)]          // rifle: 0.2 s
    [InlineData(1f / 6f, 5)]       // pistol: 0.167 s (5 / 30)
    [InlineData(1f / 3f, 10)]      // heavy bolter's family: 0.333 s
    public void A_press_on_a_lowered_weapon_fires_when_its_family_raise_ends(float delay, int ticks)
    {
        var c = RaiseConfig(Gun("gun") with { RaiseDelay = delay });
        Assert.Equal(ticks, Combat.RaiseTicksOf(c.Weapons[0], c));
        var s = Fresh(c, 0);
        Assert.Equal(0, s.ReadyTicks);   // a fresh life: the weapon is down
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var press);
        Assert.False(press.Fired);
        Assert.Equal((ticks, c.ReadyHoldTicks), (s.RaiseTicks, s.ReadyTicks));
        for (int k = 1; k < ticks; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var e);
            Assert.False(e.Fired, $"tick {k}");
            Assert.Equal(ticks - k, s.RaiseTicks);
        }
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var shot);
        Assert.True(shot.Fired);   // a tap is enough: the press is kept through the raise
        Assert.Equal((0, 29, c.ReadyHoldTicks), (s.RaiseTicks, s.Clip, s.ReadyTicks));
        // Up now: the next press (after the cooldown) fires at once.
        Assert.Equal(0, TicksToShot(c, from: s with { Cooldown = 0f }));
    }

    /// <summary>anim/relic-bolter-idle: the True Grit bolter (animation_set "pistol" in its wargear) raises as the bolt pistol: 5 ticks from the press to the shot, not the rifle's 6.</summary>
    [Fact]
    public void The_true_grit_bolter_raises_in_the_pistols_five_ticks()
    {
        static HipFireSet Set(HipFireWeaponClass k, float delay, string? condition) =>
            new(k, "nav", "Run Ready", [], "fire", null, delay, 0.1f, 0.1f, null, condition, 0.2f, 0.2f, null, null, []);
        var data = new HipFireAnimations(
        [
            Set(HipFireWeaponClass.Rifle, 0.2f, "any[BOOL_IsReady, any[BOOL_Melta]]"),
            Set(HipFireWeaponClass.Pistol, 1f / 6f, "all[any[BOOL_IsReady, BOOL_IsAiming], any[BOOL_Pistol, BOOL_PlasmaPistol]]"),
        ]);
        var doc = SmFormats.Baf.BafDocument.Read(SmFormats.Baf.SyntheticBafWriter.Build(
            ("common_data", new (string, object)[] { ("fire_mode_primary", new (string, object)[] { ("entry", new (string, object)[] { ("animation_set", "pistol"), ("upgrade_granted", "pistol_bolter") }) }) })));
        Assert.Equal("pistol", MpClasses.AnimationSetOf(doc));
        var gun = MpClasses.WithRaise(Gun("sm_mp_pistol_bolter_gold"), data, [], MpClasses.AnimationSetOf(doc));
        Assert.Equal(5, TicksToShot(RaiseConfig(gun)));
        Assert.Equal(6, TicksToShot(RaiseConfig(MpClasses.WithRaise(Gun("sm_mp_pistol_bolter_gold"), data, []))));   // by name alone (before): the rifle's 6
        Assert.Null(MpClasses.AnimationSetOf(SmFormats.Baf.BafDocument.Read(SmFormats.Baf.SyntheticBafWriter.Build(("common_data", new (string, object)[] { ("slot", "pistol") })))));
    }

    [Fact]
    public void Always_ready_weapons_aim_mode_and_weapons_without_data_never_wait()
    {
        Assert.Equal(0, TicksToShot(RaiseConfig(Gun("heavy") with { RaiseDelay = 1f / 3f, AlwaysReady = true })));
        Assert.Equal(0, TicksToShot(RaiseConfig(Gun("plain"))));                                 // no data: RaiseDelay 0
        Assert.Equal(0, TicksToShot(RaiseConfig(Gun("rifle") with { RaiseDelay = 0.2f }), InputButtons.Aim));
        Assert.Equal(6, TicksToShot(RaiseConfig(Gun("rifle") with { RaiseDelay = 0.2f })));      // control
        // An always-ready weapon is shown up every tick while not lowered.
        var c = RaiseConfig(Gun("heavy") with { RaiseDelay = 1f / 3f, AlwaysReady = true });
        var s = Combat.StepPlayer(Fresh(c, 0), new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _);
        Assert.Equal(c.ReadyHoldTicks, s.ReadyTicks);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), c, out _);   // a sprint lowers it
        Assert.Equal(0, s.ReadyTicks);
    }

    [Fact]
    public void The_weapon_stays_up_for_the_hold_after_a_shot_and_a_full_auto_hold_waits_once()
    {
        var c = RaiseConfig(Gun("rifle", TriggerKind.FullAuto) with { RaiseDelay = 0.2f });
        Assert.Equal(10f, new SimConfig().ReadyHold);   // M32: the owner's rough estimate, unverified (M31: 1.0 s, ours)
        Assert.Equal(300, c.ReadyHoldTicks);            // 10 s at 30 Hz: more than a byte holds (protocol 21: a u16)
        Assert.Equal(600, (c with { TickRate = 60 }).ReadyHoldTicks);
        Assert.Equal(SimConfig.MaxReadyTicks, (c with { ReadyHold = 1e9f }).ReadyHoldTicks);
        foreach (var (idle, waits) in new[] { (299, false), (300, true) })
        {
            var s = Fresh(c, 0) with { ReadyTicks = 1 };
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var e);
            Assert.True(e.Fired);
            for (int k = 0; k < idle; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.None), c, out _);   // running keeps it up
            Assert.Equal(waits ? 6 : 0, TicksToShot(c, from: s));
        }
        // Held from a lowered weapon: the first shot after the raise, then the weapon's own cadence.
        var held = Fresh(c, 0);
        var shots = new List<int>();
        for (int k = 0; k < 20; k++)
        {
            held = Combat.StepPlayer(held, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var e);
            if (e.Fired) shots.Add(k);
        }
        output.WriteLine($"full auto from lowered: shots at ticks {string.Join(", ", shots)}");
        Assert.Equal(6, shots[0]);
        Assert.Equal(3, shots[1] - shots[0]);   // 0.1 s cooldown: 3 ticks
    }

    [Fact]
    public void A_sprint_a_switch_melee_and_a_throw_lower_the_weapon_and_the_raise_keeps_the_sprint_off()
    {
        var c = RaiseConfig(Gun("rifle") with { RaiseDelay = 0.2f }) with { Thrown = [GrenadeTests.Frag], WeaponSwitchTime = 0.1f };
        c = c with { Classes = [c.Classes[0] with { EquipmentChoices = [0] }] };
        PlayerState Up() => Fresh(c, 0) with { ReadyTicks = c.ReadyHoldTicks };
        Assert.Equal(0, TicksToShot(c, from: Up()));

        var sprinted = Combat.StepPlayer(Up(), new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), c, out _);
        Assert.Equal(0, sprinted.ReadyTicks);
        Assert.Equal(6, TicksToShot(c, from: sprinted));
        // Sprint held with the stick forward but no movement forward (stick back): no sprint, still up.
        Assert.True(Combat.StepPlayer(Up(), new PlayerInput(0, 0f, -1f, 0f, InputButtons.Sprint), c, out _).ReadyTicks > 0);

        var switched = Combat.StepPlayer(Up(), new PlayerInput(0, 0f, 0f, 0f, InputButtons.Switch), c, out _);
        Assert.Equal(0, switched.ReadyTicks);
        for (int k = 0; k < 5; k++) switched = Combat.StepPlayer(switched, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _);
        Assert.Equal(5, TicksToShot(c, from: switched));   // the pistol now in hand comes up in its 0.167 s
        var melee = Combat.StepPlayer(Up(), new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee), c, out _);
        Assert.Equal(0, melee.ReadyTicks);
        var thrown = Combat.StepPlayer(Up(), new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade), c, out _);
        Assert.True(thrown.Throwing);
        Assert.Equal(0, thrown.ReadyTicks);

        // While the raise runs with Sprint held the player moves at run speed; the sprint returns after the shot.
        var s = Fresh(c, 0);
        for (int k = 0; k < 30; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), c, out _);
        Assert.Equal(10f, s.Velocity.Y, 3);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.Fire), c, out _);
        Assert.True(s.RaiseTicks > 0);
        int fired = -1;
        for (int k = 1; k <= 20; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), c, out var e);
            if (e.Fired) fired = k;
            if (fired < 0) Assert.True(s.Velocity.Y < 10f - 1e-3f, $"tick {k}: sprinting during the raise");
        }
        Assert.Equal(6, fired);   // 6 ticks (0.2 s) after the press
        Assert.Equal(10f, s.Velocity.Y, 3);
        Assert.Equal(0, s.ReadyTicks);   // sprinting again: lowered
    }

    /// <summary>
    /// M32, confirmed by the owner (2026-10-28, user's knowledge of the original): the first shot after a weapon switch waits for
    /// the raise; the always-up weapons (heavy bolter, lascannon, plasma cannon, melta, grenade launcher) never wait, also after a
    /// sprint or a roll.
    /// </summary>
    [Fact]
    public void A_normal_weapon_waits_after_a_switch_and_an_always_ready_one_fires_at_once_after_a_switch_a_sprint_and_a_roll()
    {
        foreach (var (always, wait) in new[] { (false, 6), (true, 0) })
        {
            var primary = Gun(always ? "heavy" : "rifle") with { RaiseDelay = always ? 1f / 3f : 0.2f, AlwaysReady = always };
            var c = RaiseConfig(primary) with { Evades = [EvadeTests.Roll], WeaponSwitchTime = 0.1f };
            c = c with { Classes = [c.Classes[0] with { Evade = 0 }] };
            string kind = always ? "always ready" : "normal";
            // A switch from the pistol (held up) to the primary: the primary waits for its raise unless always ready.
            var s = Fresh(c, 0) with { Slot = WeaponSlot.Pistol, ReadyTicks = c.ReadyHoldTicks };
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.SlotPrimary), c, out _);
            Assert.True(s.SwitchLeft > 0f, kind);
            for (int k = 0; k < 10 && s.SwitchLeft > 0f; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _);
            Assert.Equal((WeaponSlot.Ranged, 0f), (s.Slot, s.SwitchLeft));
            Assert.Equal(wait, TicksToShot(c, from: s));
            // After a sprint.
            var sprinted = Fresh(c, 0) with { ReadyTicks = c.ReadyHoldTicks };
            for (int k = 0; k < 20; k++) sprinted = Combat.StepPlayer(sprinted, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), c, out _);
            Assert.Equal(10f, sprinted.Velocity.Y, 3);
            Assert.Equal(wait, TicksToShot(c, from: sprinted));
            Assert.Equal(wait, TicksToShot(c, InputButtons.Sprint, from: sprinted));   // the trigger with Sprint still held
            // After a roll (played to its end).
            var rolled = Combat.StepPlayer(Fresh(c, 0) with { ReadyTicks = c.ReadyHoldTicks }, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Jump), c, out _);
            Assert.True(rolled.EvadeTicks > 0, kind);
            for (int k = 0; k < 60 && rolled.EvadeTicks > 0; k++) rolled = Combat.StepPlayer(rolled, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _);
            Assert.Equal(0, rolled.EvadeTicks);
            Assert.Equal(wait, TicksToShot(c, from: rolled));
            output.WriteLine($"{kind}: {wait} ticks after a switch, a sprint and a roll");
        }
    }

    [Fact]
    public void A_reaction_or_death_during_the_raise_drops_the_waiting_shot()
    {
        var c = RaiseConfig(Gun("rifle") with { RaiseDelay = 0.2f });
        var s = Combat.StepPlayer(Fresh(c, 0), new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out _);
        Assert.Equal(6, s.RaiseTicks);
        var dead = Combat.Killed(s, c);
        Assert.Equal((0, 0), (dead.RaiseTicks, dead.ReadyTicks));
        var stunned = s with { Reaction = ReactionKind.Stun, ReactionTicks = 0 };
        for (int k = 0; k < 10; k++)
        {
            stunned = Combat.StepPlayer(stunned, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var e);
            Assert.False(e.Fired);
            Assert.Equal(0, stunned.RaiseTicks);
        }
    }

    // ---------------------------------------------------------------- tap and hold on every charge weapon

    static readonly SimConfig ChargeConfig = new()
    {
        Weapons = [ChargeTests.Las, ChargeTests.PlasmaGun, ChargeTests.PlasmaPistol, ChargeTests.Cannon],
        Classes =
        [
            new ClassLoadout("las", 0, -1, SimConfig.PlaceholderMeleeSteps), new ClassLoadout("plasma gun", 1, -1, SimConfig.PlaceholderMeleeSteps),
            new ClassLoadout("plasma pistol", -1, 2, SimConfig.PlaceholderMeleeSteps), new ClassLoadout("cannon", 3, -1, SimConfig.PlaceholderMeleeSteps),
        ],
    };

    [Theory]
    [InlineData(0, 45)]   // lascannon held-charge variant (max_charge_time 0): charged at 1.5 s; the real lascannon fires on the press (below)
    [InlineData(1, 33)]   // plasma gun: 1.1 s
    [InlineData(2, 20)]   // plasma pistol (its charge on the primary ability): 0.65 s
    [InlineData(3, 90)]   // plasma cannon: 3 s
    public void A_tap_of_fire_is_the_basic_shot_and_a_hold_the_charged_shot_on_every_charge_weapon(int cls, int chargeTicks)
    {
        var c = ChargeConfig;
        var weapon = Loadouts.InHand(c, Fresh(c, cls))!;
        output.WriteLine($"{weapon.Name}: charge on {weapon.Charge!.Ability}, needs {weapon.Charge.AmmoToStart}");
        // A tap: press, release; the basic shot leaves on the release (a projectile weapon's basic shot is a projectile).
        var s = Fresh(c, cls);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var press);
        Assert.False(press.Fired);
        Assert.True(s.Charging);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var tap);
        Assert.Equal((true, 0), (tap.Fired, tap.ShotStage));
        Assert.Equal(weapon.ClipSize - Math.Max(1, weapon.AmmoPerShot), s.Clip);
        // A hold to the stage with the shot: the charged shot (after its fire delay, if any).
        s = Fresh(c, cls);
        for (int k = 0; k < chargeTicks; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out _);
        int stage = Combat.ChargeStage(weapon.Charge, s.ChargeTime, firing: true);
        Assert.True(stage > 0, $"stage {stage} after {s.ChargeTime} s");
        SelfEvents shot = default;
        for (int k = 0; k < 6 && !shot.Fired; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out shot);
        Assert.Equal((true, stage + 0), (shot.Fired, shot.ShotStage));
        // AltFire is gone: bit 64 drives nothing.
        var alt = Fresh(c, cls);
        for (int k = 0; k < 5; k++) alt = Combat.StepPlayer(alt, new PlayerInput(0, 0f, 0f, 0f, (InputButtons)64), c, out var e);
        Assert.Equal((false, weapon.ClipSize), (alt.Charging, alt.Clip));
    }

    [Fact]
    public void The_lascannon_fires_its_shot_on_the_press_tick_and_holding_charges_nothing()
    {
        var c = ChargeConfig with { Weapons = [ChargeTests.LasPress, ChargeTests.PlasmaGun, ChargeTests.PlasmaPistol, ChargeTests.Cannon] };
        var s = Fresh(c, 0);
        var weapon = Loadouts.InHand(c, s)!;
        Assert.Equal(1.5f, weapon.Charge!.MaxChargeTime);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var press);
        Assert.Equal((true, 0, false), (press.Fired, press.ShotStage, s.Charging));   // the press tick; the basic shot, no charge
        Assert.Equal(weapon.ClipSize - Math.Max(1, weapon.AmmoPerShot), s.Clip);
        // Holding on (past the old 1.5 s charge) fires nothing more, and the release fires nothing.
        for (int k = 0; k < 60; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var held);
            Assert.False(held.Fired);
            Assert.False(s.Charging);
        }
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var rel);
        Assert.False(rel.Fired);
    }

    [Fact]
    public void A_charged_shot_with_a_data_fire_delay_leaves_on_the_release_tick()
    {
        var c = ChargeConfig;
        foreach (int cls in new[] { 1, 2, 3 })
        {
            var s = Fresh(c, cls);
            var weapon = Loadouts.InHand(c, s)!;
            Assert.True(weapon.Charge!.Stages.Any(st => st.Shot is { FireDelay: > 0f }), weapon.Name);   // the data does carry a delay
            int need = (int)MathF.Ceiling(weapon.Charge.Stages.Last(st => st.Shot is not null).MinTime / (1f / 30f)) + 1;
            for (int k = 0; k < need; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var held);
            Assert.True(Combat.ChargeStage(weapon.Charge, s.ChargeTime, firing: true) > 0);
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var rel);
            Assert.True(rel.Fired, weapon.Name);
            Assert.True(rel.ShotStage > 0, weapon.Name);
            Assert.Equal(0, s.PendingShot);
        }
    }

    [Fact]
    public void A_tap_on_a_lowered_charge_weapon_waits_for_the_raise_and_a_hold_charges_through_it()
    {
        var gun = ChargeTests.PlasmaGun with { RaiseDelay = 0.2f };
        var c = ChargeConfig with { Weapons = [ChargeTests.Las, gun, ChargeTests.PlasmaPistol, ChargeTests.Cannon] };
        var s = Fresh(c, 1);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out _);
        Assert.Equal((true, 6), (s.Charging, s.RaiseTicks));
        int fired = -1;
        for (int k = 1; k <= 10; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var e);   // released at once: a tap
            if (e.Fired) { fired = k; Assert.Equal(0, e.ShotStage); }
        }
        Assert.Equal(6, fired);
        // Held through the raise: no shot at its end; the release after 1.1 s is the charged shot.
        s = Fresh(c, 1);
        int shots = 0;
        for (int k = 0; k < 34; k++) { s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var e); if (e.Fired) shots++; }
        Assert.Equal((0, true, 0), (shots, s.Charging, s.RaiseTicks));
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var rel);
        Assert.Equal((true, 2, 0), (rel.Fired, rel.ShotStage, s.PendingShot));   // sim/instant-fire: on the release tick
    }

    [Fact]
    public void A_charged_release_during_a_longer_raise_fires_only_the_charged_shot()
    {
        // Review M31: a raise longer than the charge's firing stage (not on the install: stages from 0.65 s, raises up to 0.33 s)
        // must not add a basic shot after the charged one.
        var gun = ChargeTests.PlasmaGun with { RaiseDelay = 2f };
        var c = ChargeConfig with { Weapons = [ChargeTests.Las, gun, ChargeTests.PlasmaPistol, ChargeTests.Cannon] };
        var s = Fresh(c, 1);
        var stages = new List<int>();
        for (int k = 0; k < 34; k++) { s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var e); if (e.Fired) stages.Add(e.ShotStage); }
        Assert.True(s.RaiseTicks > 0, "the raise is still under way at the release");
        for (int k = 0; k < 90; k++) { s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out var e); if (e.Fired) stages.Add(e.ShotStage); }
        Assert.Equal([2], stages);
        Assert.Equal(0, s.RaiseTicks);
    }

    // ---------------------------------------------------------------- spawn-room blockers reach above the level

    static StaticMesh Mesh(params (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] quads)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var (a, b, q, d) in quads) { int n = v.Count; v.AddRange([a, b, q, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        return new StaticMesh(v.ToArray(), idx.ToArray());
    }

    static (Vector3, Vector3, Vector3, Vector3) Floor(float z, float x0, float x1, float y0 = -30, float y1 = 30) => (new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));

    /// <summary>A 1.5 m (marker-sized) marine blocker across the X axis at x 0, a floor, and a 12 m platform at x −8..−4 to jump from.</summary>
    static readonly SpawnBlocker Low = new(Team.SpaceMarines, new Vector3(0, 0, 0.25f), new Vector3(1, 40, 1.5f), 0f);

    static CollisionWorld TallLevel() => new(Mesh(Floor(0f, -60, 60), Floor(12f, -8, -4, -4, 4)), Mesh(Floor(0f, -60, 60)), null, [Low]);

    [Fact]
    public void A_blocker_reaches_above_the_levels_highest_point_with_its_footprint_and_bottom_kept()
    {
        var level = TallLevel();
        var b = Assert.Single(level.Blockers);
        Assert.Equal(12f, level.Character.Max.Z);
        Assert.Equal(Low.Center.Z - Low.Size.Z / 2f, b.Center.Z - b.Size.Z / 2f, 4);             // bottom kept (−0.5)
        Assert.Equal(12f + SpawnBlocker.AboveLevel, b.Center.Z + b.Size.Z / 2f, 4);               // top: level top + 25 m
        Assert.Equal((Low.Center.X, Low.Center.Y, Low.Size.X, Low.Size.Y, Low.Yaw), (b.Center.X, b.Center.Y, b.Size.X, b.Size.Y, b.Yaw));
        Assert.True(b.Contains(new Vector3(0, 0, 30f)));
        Assert.Equal(new Vector3(0.5f + 0.45f + 0.01f, 0, 30f), b.PushOut(new Vector3(0.1f, 0, 30f), 0.45f, 1.9f));   // push-out at any height
        // A box already taller than that is left as it is.
        var tall = Low with { Center = new Vector3(0, 0, 50f), Size = new Vector3(1, 40, 101f) };
        Assert.Equal(tall, new CollisionWorld(Mesh(Floor(0f, -60, 60)), Mesh(Floor(0f, -60, 60)), null, [tall]).Blockers[0]);
        // Shots pass at any height; only the other side's character mesh has the box, up to its new top.
        Assert.Null(level.Weapons.Raycast(new Vector3(-5, 0, 30f), Vector3.UnitX, 10f));
        Assert.Null(level.CharacterFor(Team.SpaceMarines).Raycast(new Vector3(-5, 0, 30f), Vector3.UnitX, 10f));
        Assert.NotNull(level.CharacterFor(Team.Chaos).Raycast(new Vector3(-5, 0, 30f), Vector3.UnitX, 10f));
        Assert.Null(level.CharacterFor(Team.Chaos).Raycast(new Vector3(-5, 0, 37.5f), Vector3.UnitX, 10f));
    }

    [Theory]
    [InlineData(-3f, 0f)]    // from the floor, 3 m short of the box
    [InlineData(-6f, 12f)]   // from the top of the 12 m platform
    public void No_jump_pack_flight_or_pound_passes_over_a_low_blocker_for_the_enemy_and_the_own_side_flies_over(float fromX, float fromZ)
    {
        var level = TallLevel();
        var c = RushConfig;
        float east = MathF.Atan2(-1f, 0f);
        foreach (var (team, cls) in new[] { (Team.Chaos, Raptor), (Team.SpaceMarines, Assault) })
        {
            var s = Combat.Respawned(new PlayerState(1, new Vector3(fromX, 0, fromZ), Vector3.Zero, east, true) { Class = cls, Team = team }, c);
            float highest = 0f, farthest = float.MinValue;
            for (int t = 0; t < 360; t++)
            {
                int phase = t % 120;
                // Full-height jumps (held 0.4 s) straight east; the later ones end in a ground pound near the apex.
                var b = phase < 14 ? InputButtons.Jump : phase == 40 && t >= 120 ? InputButtons.Melee : InputButtons.None;
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, east, b, Pitch: -0.4f), c, out _, level);
                highest = MathF.Max(highest, s.Position.Z);
                farthest = MathF.Max(farthest, s.Position.X);
            }
            output.WriteLine($"{team} from {fromX},{fromZ}: highest {highest:F1} m, farthest east {farthest:F2} m, end {s.Position}");
            Assert.True(highest > Low.Center.Z + Low.Size.Z / 2f + 8f, "the jumps rose well above the marker's own top");
            if (team == Team.Chaos) Assert.True(farthest <= -0.5f - c.CapsuleRadius + 2e-3f, $"crossed to {farthest}");
            else Assert.True(farthest > 1f, "the own side did not fly over its blocker");
        }
    }

    // ---------------------------------------------------------------- protocol 20 (21 since M32)

    [Fact]
    public void Protocol_21_carries_the_raise_and_a_ten_second_hold_in_the_own_record_and_weapon_up_in_slim_ones_within_the_budget()
    {
        Assert.Equal(50, Protocol.Version);
        Assert.Equal(1194, Protocol.MaxSnapshotBytes);   // protocol 23: + the perk word (21: ready ticks in 2 bytes, 1198; 20: 1197)
        Assert.True(Protocol.MaxSnapshotBytes <= Protocol.SnapshotBudget);
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, ReadyTicks = 300, RaiseTicks = 6 };
        var own = Protocol.Write(new Snapshot(1, 1, 1, [me], Self: 3));
        Assert.Equal(me, Assert.IsType<Snapshot>(Protocol.Read(own)).Players[0]);
        // The own record ends: … charge lock [^28], ready [^27..^26] (u16 little-endian since protocol 21), raise [^25], then 24 bytes of blocks.
        Assert.Equal((300, 6), (own[^27] | own[^26] << 8, (int)own[^25]));
        var largest = me with { ReadyTicks = SimConfig.MaxReadyTicks };
        Assert.Equal(largest, Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [largest], Self: 3)))).Players[0]);
        var disagree = (byte[])own.Clone(); disagree[^27] = 0; disagree[^26] = 0; disagree[^25] = 0;   // the slim bit says up
        Assert.Null(Protocol.Read(disagree));
        // Slim: the weapon-up bit only.
        var other = me with { PlayerId = 4 };
        var slim = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [other])))).Players[0];
        Assert.Equal((1, 0), (slim.ReadyTicks, slim.RaiseTicks));
        Assert.Equal(slim, Protocol.Slim(other));
        Assert.Equal(0, Protocol.Slim(other with { ReadyTicks = 0, RaiseTicks = 0 }).ReadyTicks);
        Assert.Equal(1, Protocol.Slim(other with { ReadyTicks = 0, RaiseTicks = 2 }).ReadyTicks);
        // The AltFire bit (64) is dropped when an input batch is read.
        var batch = Protocol.Write(new InputBatch([new PlayerInput(1, 0, 0, 0, InputButtons.Fire | (InputButtons)64)], 1, 0));
        Assert.Equal(InputButtons.Fire, Assert.IsType<InputBatch>(Protocol.Read(batch)).Inputs[0].Buttons);
    }

    [Theory]
    [InlineData(81)]
    [InlineData(82)]
    public void Prediction_matches_the_server_through_raises_taps_holds_and_sprint_charges_under_150ms_rtt_and_5pct_loss(int seed)
    {
        static ProjectileSpec Harmless(ProjectileSpec p) => p with { Splash = p.Splash! with { Damage = p.Splash.Damage with { Damage = 0f, ShieldDamage = 0f } } };
        var gun = ChargeTests.PlasmaGun with
        {
            Damage = 0f, ClipSize = 3000, RaiseDelay = 0.2f, Heat = ChargeTests.PlasmaGun.Heat! with { CoolRate = 40f },
            Charge = ChargeTests.PlasmaGun.Charge! with { Stages = [ChargeTests.PlasmaGun.Charge.Stages[0], ChargeTests.PlasmaGun.Charge.Stages[1] with { Shot = ChargeTests.PlasmaGun.Charge.Stages[1].Shot! with { Projectile = Harmless(ChargeTests.Bolt) } }] },
        };
        var rifle = Gun("rifle", TriggerKind.FullAuto) with { Damage = 0f, ReserveSize = 5000, RaiseDelay = 0.2f };
        var pistol = Gun("pistol") with { Slot = WeaponSlot.Pistol, Damage = 0f, ReserveSize = 5000, RaiseDelay = 1f / 6f };
        var rush = RushTests.Spec with
        {
            Primary = RushTests.Spec.Primary with { Damage = 0f, ShieldDamage = 0f }, Secondary = RushTests.Spec.Secondary with { Damage = 0f },
            PrimaryReaction = new ReactionEntry(0f, ReactionKind.None), SecondaryReaction = new ReactionEntry(0f, ReactionKind.None),
        };
        var config = RushConfig with
        {
            Weapons = [gun, rifle, pistol],
            Classes =
            [
                new ClassLoadout("plasma", 0, 2, [new MeleeStep(1f, 0.3f, 0.5f, 0.8f, 0.45f, 0.8f, 0f, 0f, 3.5f)]) { MeleeRush = rush, Side = Team.None },
                new ClassLoadout("rifle", 1, 2, [new MeleeStep(1f, 0.3f, 0.5f, 0.8f, 0.45f, 0.8f, 0f, 0f, 3.5f)]) { MeleeRush = rush, Side = Team.None },
            ],
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = 0 });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = 1 });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        var external = new HashSet<(uint Tick, int Player)>();   // a charge's contact is the server's (as in RushTests): skipped for a while
        PlayerInput Input(int i, uint t)
        {
            uint phase = (t + (uint)i * 17) % 240;
            var b = InputButtons.None;
            float y = 0f, x = 0f;
            if (phase < 30) { y = 1f; b |= InputButtons.Sprint; }                                    // sprint (lowers the weapon) …
            if (phase == 20) b |= InputButtons.MeleeHeavy;                                              // … and charge
            if (phase is >= 60 and < 62) b |= InputButtons.Fire;                                        // a tap on a lowered weapon: the raise
            if (phase is >= 70 and < 110) b |= InputButtons.Fire;                                       // a hold (the plasma gun charges)
            if (phase is >= 120 and < 150) { y = 1f; b |= InputButtons.Sprint; if (phase % 5 == 0) b |= InputButtons.Fire; }   // taps while sprinting
            if (phase is >= 160 and < 175) { x = 1f; b |= InputButtons.Aim | (phase % 4 == 0 ? InputButtons.Fire : InputButtons.None); }   // aim mode: no wait
            if (phase is 180 or 181) b |= InputButtons.Switch;
            if (phase is >= 185 and < 200 && phase % 3 == 0) b |= InputButtons.Fire;
            if (phase is 205 or 206) b |= InputButtons.Switch;
            return new PlayerInput(t, x, y, i * 1.3f, b, 0.1f);
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            foreach (var r in h.Server.World.LastRushes) { external.Add((r.Tick, r.Player)); external.Add((r.Tick, r.Target)); }
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < 2; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            var contacts = external.Where(x => x.Player == id).Select(x => x.Tick).ToList();
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                if (contacts.Any(c => c <= tick && tick < c + 16)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int raises = mine.Zip(mine.Skip(1)).Count(p => p.First.RaiseTicks == 0 && p.Second.RaiseTicks > 0);
            int rushes = mine.Zip(mine.Skip(1)).Count(p => p.First.Rush == RushPhase.None && p.Second.Rush == RushPhase.Rush);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {raises} raises, {rushes} charges");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} compared");
            Assert.True(raises >= 4, $"client {i}: only {raises} raises");
            Assert.True(rushes >= 2, $"client {i}: only {rushes} charges");
            Assert.Empty(mismatches);
        }
    }

    // ---------------------------------------------------------------- the data path

    [Fact]
    public void Weapons_get_their_familys_raise_delay_and_the_ready_while_carrying_list_from_the_graphs()
    {
        static HipFireSet Set(HipFireWeaponClass k, float delay, string? condition) =>
            new(k, "nav", "Run Ready", [], "fire", null, delay, 0.1f, 0.1f, null, condition, 0.2f, 0.2f, null, null, []);
        var data = new HipFireAnimations(
        [
            Set(HipFireWeaponClass.Rifle, 0.2f, "all[not (BOOL_IsAiming & !BOOL_AimAnimDisabled), any[BOOL_IsReady, any[BOOL_Grenade_Launcher, BOOL_Melta, BOOL_Lascannon, BOOL_PlasmaCannon]]]"),
            Set(HipFireWeaponClass.Pistol, 1f / 6f, "all[any[BOOL_IsReady, BOOL_IsAiming], any[BOOL_Pistol, BOOL_PlasmaPistol]]"),
            Set(HipFireWeaponClass.Heavy, 1f / 3f, null),
        ]);
        Assert.Equal(new[] { "GrenadeLauncher", "Melta", "Lascannon", "PlasmaCannon" }, HipFireAnimations.ReadyCarried(data.Sets[0].ReadyCondition!));
        Assert.Empty(HipFireAnimations.ReadyCarried(data.Sets[1].ReadyCondition!));
        var notes = new List<string>();
        (float, bool) Of(string name) { var w = MpClasses.WithRaise(Gun(name), data, notes); return (w.RaiseDelay, w.AlwaysReady); }
        Assert.Equal((0.2f, false), Of("sm_mp_ranged_bolter"));
        Assert.Equal((0.2f, false), Of("sm_mp_ranged_plasma_gun"));
        Assert.Equal((0.2f, false), Of("sm_mp_pistol_bolter"));               // by name alone a bolter in the pistol slot is a rifle ...
        // ... but its wargear names animation_set "pistol" (anim/relic-bolter-idle): the pistol's raise.
        Assert.Equal(1f / 6f, MpClasses.WithRaise(Gun("sm_mp_pistol_bolter"), data, notes, "pistol").RaiseDelay);
        Assert.Equal(1f / 6f, MpClasses.WithRaise(Gun("csm_mp_pistol_bolter_gold"), data, notes, "pistol").RaiseDelay);
        Assert.Equal(0.2f, MpClasses.WithRaise(Gun("sm_mp_ranged_bolter"), data, notes, "bolter").RaiseDelay);
        Assert.Equal((1f / 6f, false), Of("sm_mp_pistol_bolt_pistol"));
        Assert.Equal((1f / 6f, false), Of("csm_mp_pistol_plasma_pistol"));      // not "ready while carrying" (that list is the pistol family's gate)
        Assert.Equal((1f / 3f, true), Of("sm_mp_exotic_heavy_bolter"));
        foreach (var always in new[] { "sm_mp_ranged_grenade_launcher", "sm_mp_ranged_melta", "sm_mp_exotic_las_cannon", "csm_mp_exotic_plasma_cannon" })
            Assert.True(Of(always).Item2, always);
        Assert.Equal((0f, false), (MpClasses.WithRaise(Gun("x"), null, notes).RaiseDelay, MpClasses.WithRaise(Gun("x"), null, notes).AlwaysReady));
        // The config hash covers the raise and the hold.
        var a = RaiseConfig(Gun("rifle") with { RaiseDelay = 0.2f });
        Assert.NotEqual(a.Hash(), RaiseConfig(Gun("rifle") with { RaiseDelay = 0.21f }).Hash());
        Assert.NotEqual(a.Hash(), RaiseConfig(Gun("rifle") with { RaiseDelay = 0.2f, AlwaysReady = true }).Hash());
        Assert.NotEqual(a.Hash(), (a with { ReadyHold = 1.5f }).Hash());
    }
}
