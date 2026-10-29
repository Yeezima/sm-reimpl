using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Weapon heat (per shot, overheat lock, recovery, cool delay and rate, delay after an overheat, holstered cooling,
/// switching, respawn) and loadout choice (chosen primary, pistol and melee; invalid choices; handshake; snapshots; config
/// hash; validation), plus exact prediction while overheating. Synthetic weapons and classes only (no game data).
/// </summary>
public class HeatTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.FullAuto,
        Damage: 10f, ShotCooldown: 0.1f, ClipSize: 20, ReserveSize: 60, ReloadTime: 1.0f, MaxRange: 100f,
        FallOffStart: 100f, FallOffEnd: 100f, FallOffEndMultiplier: 1f,
        HeadMultiplier: 2f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f);
    static readonly WeaponSpec Pistol = new("test_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto,
        Damage: 30f, ShotCooldown: 0.02f, ClipSize: 8, ReserveSize: 24, ReloadTime: 0.5f, MaxRange: 20f,
        FallOffStart: 20f, FallOffEnd: 20f, FallOffEndMultiplier: 1f,
        HeadMultiplier: 3f, BodyMultiplier: 1.5f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 0.5f);
    /// <summary>
    /// Heavy-bolter-like: fires every tick at 30 Hz; 10 heat per shot, overheat 100, recovery 20.5, cools 30/s (1 per tick)
    /// after 0.3 s (9 ticks), at once after an overheat. A reserve, so the reload rule while overheated can be seen.
    /// </summary>
    static readonly WeaponSpec HeatGun = new("test_heat_gun", WeaponSlot.Ranged, TriggerKind.FullAuto,
        Damage: 15f, ShotCooldown: 1f / 30f, ClipSize: 200, ReserveSize: 50, ReloadTime: 2.0f, MaxRange: 100f,
        FallOffStart: 100f, FallOffEnd: 100f, FallOffEndMultiplier: 1f,
        HeadMultiplier: 2f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f)
    { Heat = new HeatSpec(PerShot: 10f, Overheat: 100f, HighHeat: 75f, Recovery: 20.5f, CoolRate: 30f, CoolDelay: 0.3f, CoolDelayAfterOverheat: 0f) };
    /// <summary>Plasma-like: semi auto, 25 per shot, recovery 0.1, cools 6/s (0.2 per tick) after 0.35 s (10.5 ticks), 1 s after an overheat.</summary>
    static readonly WeaponSpec Plasma = new("test_plasma", WeaponSlot.Ranged, TriggerKind.SemiAuto,
        Damage: 40f, ShotCooldown: 0.05f, ClipSize: 100, ReserveSize: 0, ReloadTime: 1.0f, MaxRange: 100f,
        FallOffStart: 100f, FallOffEnd: 100f, FallOffEndMultiplier: 1f,
        HeadMultiplier: 2f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f)
    { Heat = new HeatSpec(PerShot: 25f, Overheat: 100f, HighHeat: 75f, Recovery: 0.1f, CoolRate: 6f, CoolDelay: 0.35f, CoolDelayAfterOverheat: 1f) };
    static readonly WeaponSpec GoldPistol = Pistol with { Name = "test_gold_pistol", Damage = 35f };

    const int RifleIndex = 0, PistolIndex = 1, HeatGunIndex = 2, PlasmaIndex = 3, GoldIndex = 4;

    static readonly MeleeStep Knife = new(1.0f, 0.25f, 0.4f, 0.6f, 0.5f, 0.7f, 40f, 20f, 3.5f);
    static readonly MeleeStep Sword = new(0.5f, 0.09f, 0.2f, 0.4f, 0.3f, 0.4f, 70f, 30f, 3.5f);
    static readonly MeleeCombo KnifeCombo = new("test_knife", [Knife, Knife]);
    static readonly MeleeCombo SwordCombo = new("test_sword", [Sword]);

    const int Tactical = 0, Devastator = 1, Assault = 2;

    /// <summary>
    /// Tactical: rifle (or plasma, heat gun) + pistol (or gold), knife (or sword). Devastator: heat gun + pistol, no melee.
    /// Assault: no primary, pistol (or gold), knife (or sword).
    /// </summary>
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol, HeatGun, Plasma, GoldPistol],
        Classes =
        [
            new ClassLoadout("test - tactical", RifleIndex, PistolIndex, KnifeCombo.Steps)
            { PrimaryChoices = [RifleIndex, PlasmaIndex, HeatGunIndex], PistolChoices = [PistolIndex, GoldIndex], MeleeChoices = [KnifeCombo, SwordCombo] },
            new ClassLoadout("test - devastator", HeatGunIndex, PistolIndex, []) { PrimaryChoices = [HeatGunIndex], PistolChoices = [PistolIndex] },
            new ClassLoadout("test - assault", -1, PistolIndex, KnifeCombo.Steps) { PistolChoices = [PistolIndex, GoldIndex], MeleeChoices = [KnifeCombo, SwordCombo] },
        ],
    };

    static float Dt => Config.TickSeconds;

    static PlayerState Fresh(int playerClass, int primary = -1, int pistol = -1, int melee = -1, SimConfig? c = null)
    {
        c ??= Config;
        return Combat.Respawned(Loadouts.Choose(c, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = playerClass }, primary, pistol, melee), c);
    }

    static bool Step(ref PlayerState s, InputButtons buttons, SimConfig? c = null)
    {
        s = Combat.StepSelf(s, new PlayerInput(0, 0, 0, 0, buttons), c ?? Config, out bool fired, out _);
        return fired;
    }

    static bool Step(ref PlayerState s, InputButtons buttons, out int meleeHit)
    {
        s = Combat.StepSelf(s, new PlayerInput(0, 0, 0, 0, buttons), Config, out bool fired, out meleeHit);
        return fired;
    }

    // ---------------------------------------------------------------- heat per shot, overheat, recovery

    [Fact]
    public void Each_shot_adds_its_heat_and_the_shot_reaching_overheat_still_leaves_then_firing_locks()
    {
        var s = Fresh(Devastator);
        Assert.Same(HeatGun, Loadouts.InHand(Config, s));
        Assert.Equal((0f, false, 0f), (s.Heat, s.Overheated, s.SinceHeat));
        for (int k = 1; k <= 10; k++)
        {
            Assert.True(Step(ref s, InputButtons.Fire), $"shot {k} did not leave");
            Assert.Equal(10f * k, s.Heat, 4);   // no cooling between shots: the delay restarts with every shot
            Assert.Equal(0f, s.SinceHeat);
            Assert.Equal(k == 10, s.Overheated);
        }
        Assert.Equal(190, s.Clip);
        // Overheated: neither the held trigger, fresh presses nor a reload press do anything (reserve 50, clip not full).
        for (int k = 1; k <= 20; k++)
        {
            Assert.False(Step(ref s, k % 3 == 0 ? InputButtons.Reload : k % 2 == 0 ? InputButtons.Fire : InputButtons.None));
            Assert.Equal(0f, s.ReloadLeft);
        }
        Assert.True(s.Overheated);
        Assert.Equal((190, 50), (s.Clip, s.Reserve));
    }

    [Fact]
    public void An_overheated_weapon_is_released_once_heat_falls_to_the_recovery_amount()
    {
        var s = Fresh(Devastator);
        for (int k = 1; k <= 10; k++) Step(ref s, InputButtons.Fire);
        Assert.True(s.Overheated);
        // No delay after an overheat: 1 heat per tick from the next tick. 100 - k after k ticks; released at 20 <= 20.5,
        // in the 80th tick, which (trigger held) fires again in that same tick: 20 + 10.
        int released = 0;
        for (int k = 1; k <= 80; k++)
        {
            bool fired = Step(ref s, InputButtons.Fire);
            if (k < 80)
            {
                Assert.False(fired, $"fired {k} ticks after the overheat");
                Assert.True(s.Overheated);
                Assert.Equal(100f - k, s.Heat, 3);
            }
            else { released = k; Assert.True(fired); }
        }
        Assert.Equal(80, released);
        Assert.False(s.Overheated);
        Assert.Equal(30f, s.Heat, 3);
        Assert.Equal(0f, s.SinceHeat);
    }

    // ---------------------------------------------------------------- cooling

    [Fact]
    public void Heat_cools_at_the_rate_after_the_delay_in_exact_ticks()
    {
        // Whole-tick delay: 0.3 s = 9 ticks. The shot's tick is k = 0; ticks 1..9 keep the heat; tick 10 cools a full tick.
        var s = Fresh(Devastator);
        Step(ref s, InputButtons.Fire);
        Assert.Equal(10f, s.Heat);
        for (int k = 1; k <= 9; k++) { Step(ref s, InputButtons.None); Assert.Equal(10f, s.Heat); }
        Assert.Equal(9 * Dt, s.SinceHeat, 5);
        Step(ref s, InputButtons.None);
        Assert.Equal(9f, s.Heat, 4);
        Step(ref s, InputButtons.None);
        Assert.Equal(8f, s.Heat, 4);
        for (int k = 12; k <= 30; k++) Step(ref s, InputButtons.None);
        Assert.Equal(0f, s.Heat);   // clamped at 0 (reached at k = 19)
        Assert.False(s.Overheated);

        // Fractional delay: 0.35 s = 10.5 ticks. Ticks 1..10 keep 25; tick 11 cools half a tick (0.1); then 0.2 per tick.
        var p = Fresh(Tactical, primary: 1);
        Assert.Same(Plasma, Loadouts.InHand(Config, p));
        Assert.True(Step(ref p, InputButtons.Fire));
        Assert.Equal(25f, p.Heat);
        for (int k = 1; k <= 10; k++) { Step(ref p, InputButtons.None); Assert.Equal(25f, p.Heat); }
        Step(ref p, InputButtons.None);
        Assert.Equal(24.9f, p.Heat, 4);
        Step(ref p, InputButtons.None);
        Assert.Equal(24.7f, p.Heat, 4);
        // 24.7 / 0.2 = 123.5 more ticks: above 0 after 123 (0.1 left), 0 after 124.
        for (int k = 13; k <= 135; k++) Step(ref p, InputButtons.None);
        Assert.Equal(0.1f, p.Heat, 2);
        Step(ref p, InputButtons.None);
        Assert.Equal(0f, p.Heat);
    }

    [Fact]
    public void After_an_overheat_the_longer_delay_applies_and_the_lock_lasts_until_recovery()
    {
        var p = Fresh(Tactical, primary: 1);   // plasma, semi auto
        for (int k = 0; k < 6; k++) Step(ref p, k % 2 == 0 ? InputButtons.Fire : InputButtons.None);   // three presses: 75
        Assert.Equal((75f, false), (p.Heat, p.Overheated));
        Assert.True(Step(ref p, InputButtons.Fire));   // the 4th shot overheats: tick 0
        Assert.Equal((100f, true, 96), (p.Heat, p.Overheated, p.Clip));
        // Ticks 1..30 (1 s) keep 100 although the 0.35 s delay has long passed; tick 31 cools a full tick (0.2).
        for (int k = 1; k <= 30; k++)
        {
            Assert.False(Step(ref p, k % 2 == 0 ? InputButtons.Fire : InputButtons.None));
            Assert.Equal(100f, p.Heat);
        }
        Step(ref p, InputButtons.None);
        Assert.Equal(99.8f, p.Heat, 3);
        // Recovery 0.1: 100 / 0.2 = 500 cooling ticks to 0; still locked at about 0.2, released at about 0.
        for (int k = 32; k <= 529; k++) { Step(ref p, k % 2 == 0 ? InputButtons.Fire : InputButtons.None); Assert.True(p.Overheated, $"released at tick {k}"); }
        Assert.Equal(0.2f, p.Heat, 1);
        Step(ref p, InputButtons.None);   // tick 530
        Assert.False(p.Overheated);
        Assert.True(p.Heat <= 0.1f);
        Assert.True(Step(ref p, InputButtons.Fire));
        Assert.Equal(25f, p.Heat, 2);
    }

    // ---------------------------------------------------------------- switching, holstered cooling, respawn

    [Fact]
    public void The_holstered_weapon_cools_and_a_switch_swaps_the_heat_states()
    {
        var s = Fresh(Devastator);
        for (int k = 0; k < 3; k++) Step(ref s, InputButtons.Fire);   // 30 heat; the last shot is tick 0
        Assert.Equal(30f, s.Heat, 4);
        Step(ref s, InputButtons.Switch);   // tick 1: no cooling yet; the pistol (no heat) comes to hand
        Assert.Equal((WeaponSlot.Pistol, 0f, false, 0f), (s.Slot, s.Heat, s.Overheated, s.SinceHeat));
        Assert.Equal((30f, false), (s.OtherHeat, s.OtherOverheated));
        Assert.Equal(Dt, s.OtherSinceHeat, 5);
        // Holstered, the heat gun keeps its timer: 30 through tick 9, 29 at tick 10, 20 at tick 19.
        for (int k = 2; k <= 9; k++) { Step(ref s, InputButtons.None); Assert.Equal(30f, s.OtherHeat); }
        Step(ref s, InputButtons.None);
        Assert.Equal(29f, s.OtherHeat, 4);
        for (int k = 11; k <= 19; k++) Step(ref s, InputButtons.None);
        Assert.Equal(20f, s.OtherHeat, 3);
        Assert.Equal(0f, s.Heat);
        // Tick 20: both cool first, then the switch brings the heat gun back with its heat (19).
        Step(ref s, InputButtons.Switch);
        Assert.Equal((WeaponSlot.Ranged, 0f), (s.Slot, s.OtherHeat));
        Assert.Equal(19f, s.Heat, 3);
        Step(ref s, InputButtons.None);
        Assert.Equal(18f, s.Heat, 3);

        // An overheated weapon stays overheated in the holster; the pistol fires meanwhile; back in hand it is still locked.
        var o = Fresh(Devastator) with { };
        for (int k = 0; k < 10; k++) Step(ref o, InputButtons.Fire);
        Assert.True(o.Overheated);
        Step(ref o, InputButtons.Switch);
        Assert.Equal((WeaponSlot.Pistol, false, true), (o.Slot, o.Overheated, o.OtherOverheated));
        for (int k = 0; k < 18; k++) Step(ref o, InputButtons.None);   // switch time 0.6 s = 18 ticks
        Assert.True(Step(ref o, InputButtons.Fire));
        Assert.Equal(0f, o.Heat);   // the pistol has no heat
        Step(ref o, InputButtons.None);
        Step(ref o, InputButtons.Switch);
        Assert.Equal((WeaponSlot.Ranged, true), (o.Slot, o.Overheated));
        for (int k = 0; k < 18; k++) Step(ref o, InputButtons.None);
        Assert.False(Step(ref o, InputButtons.Fire));   // 100 - 40 = 60 > 20.5: still locked
        Assert.True(o.Overheated);
    }

    [Fact]
    public void Respawn_clears_heat_and_a_weapon_without_heat_never_heats()
    {
        var s = Fresh(Devastator);
        for (int k = 0; k < 10; k++) Step(ref s, InputButtons.Fire);
        Step(ref s, InputButtons.Switch);
        Assert.True(s.OtherOverheated && s.OtherHeat > 90f);
        var back = Combat.Respawned(Combat.Killed(s, Config), Config);
        Assert.Equal((0f, false, 0f, 0f, false, 0f), (back.Heat, back.Overheated, back.SinceHeat, back.OtherHeat, back.OtherOverheated, back.OtherSinceHeat));
        Assert.Equal(WeaponSlot.Ranged, back.Slot);
        Assert.True(Step(ref back, InputButtons.Fire));

        // The rifle (no heat spec): 300 ticks of held fire, every reload included, never any heat.
        var r = Fresh(Tactical);
        int shots = 0;
        for (int k = 0; k < 300; k++)
        {
            if (Step(ref r, InputButtons.Fire)) shots++;
            Assert.Equal((0f, false, 0f, 0f, false, 0f), (r.Heat, r.Overheated, r.SinceHeat, r.OtherHeat, r.OtherOverheated, r.OtherSinceHeat));
        }
        Assert.True(shots > 60, $"{shots} shots");   // several clips and reloads
        Assert.True(r.Reserve < 20);
    }

    [Fact]
    public void Heat_spec_rules_in_isolation()
    {
        var spec = HeatGun.Heat!;
        // Clamped to [0, Overheat]; no spec: nothing tracked.
        Assert.Equal((100f, true, 0f), Combat.AddShotHeat(spec, 95f, false));
        Assert.Equal((0f, false, 0f), Combat.AddShotHeat(null, 50f, true));
        Assert.Equal((0f, false, 0f), Combat.Cool(null, 50f, true, 3f, Dt));
        var (heat, overheated, since) = Combat.Cool(spec, 0.5f, false, 5f, Dt);
        Assert.Equal((0f, false), (heat, overheated));
        Assert.Equal(5f + Dt, since, 5);
        // Exactly at the recovery amount releases.
        Assert.False(Combat.Cool(spec with { CoolRate = 30f }, 21.5f, true, 1f, Dt).Overheated);
        Assert.True(Combat.Cool(spec with { CoolRate = 15f }, 21.5f, true, 1f, Dt).Overheated);
    }

    // ---------------------------------------------------------------- loadout choice

    [Fact]
    public void The_chosen_primary_pistol_and_melee_are_used_and_kept_across_respawns()
    {
        var s = Fresh(Tactical, primary: 1, pistol: 1, melee: 1);
        Assert.Equal((PlasmaIndex, GoldIndex, 1), (s.Primary, s.Pistol, s.MeleeChoice));
        Assert.Same(Plasma, Loadouts.InHand(Config, s));
        Assert.Same(GoldPistol, Loadouts.Holstered(Config, s));
        Assert.Equal(SwordCombo.Steps, Loadouts.Melee(Config, s));
        Assert.Equal((100, 0, 8, 24), (s.Clip, s.Reserve, s.OtherClip, s.OtherReserve));
        Assert.Equal("test - tactical: primary test_plasma, pistol test_gold_pistol, melee test_sword", Loadouts.Describe(Config, s));

        // The sword's hit lands at 0.09 s (tick 3 of the attack), the knife's at 0.25 s (tick 8).
        int HitTick(PlayerState p)
        {
            Step(ref p, InputButtons.Melee, out int hit);
            for (int k = 1; k < 20; k++) { Step(ref p, InputButtons.None, out hit); if (hit > 0) return k; }
            return -1;
        }
        Assert.Equal(3, HitTick(s));
        Assert.Equal(8, HitTick(Fresh(Tactical)));

        // A respawn keeps the choices.
        var back = Combat.Respawned(Combat.Killed(s, Config), Config);
        Assert.Equal((Tactical, PlasmaIndex, GoldIndex, 1, 100), (back.Class, back.Primary, back.Pistol, back.MeleeChoice, back.Clip));

        // In the world, too, and the sword's own damage lands (70 on the shield instead of the knife's 40).
        var world = new World(Config with { RespawnTime = 0f }, spawns: [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 2, 0), 0f)]);
        world.Spawn(1, Tactical, primaryChoice: 2, pistolChoice: 1, meleeChoice: 1);
        world.Spawn(2, Devastator);
        Assert.Same(HeatGun, Loadouts.InHand(Config, world.Players[1]));
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0, InputButtons.Melee, 0, world.Tick) : default);
        for (int k = 0; k < 5 && world.LastHits.Count == 0; k++) world.Step(_ => default);
        var hit = Assert.Single(world.LastHits);
        Assert.Equal((true, 70f), (hit.Melee, hit.ShieldDamage));
        world.Players.TryGetValue(1, out var me);
        Assert.Equal((HeatGunIndex, GoldIndex, 1), (me.Primary, me.Pistol, me.MeleeChoice));
    }

    [Fact]
    public void Invalid_choices_fall_back_to_the_class_default()
    {
        // Out-of-range requests.
        var s = Fresh(Tactical, primary: 3, pistol: 2, melee: 2);
        Assert.Equal((RifleIndex, PistolIndex, 0), (s.Primary, s.Pistol, s.MeleeChoice));
        Assert.Equal(KnifeCombo.Steps, Loadouts.Melee(Config, s));
        // A class without primaries or melee choices.
        var a = Fresh(Assault, primary: 0, pistol: 1, melee: 1);
        Assert.Equal((-1, GoldIndex, 1, WeaponSlot.Pistol), (a.Primary, a.Pistol, a.MeleeChoice, a.Slot));
        var d = Fresh(Devastator, primary: 0, pistol: 0, melee: 0);
        Assert.Equal((HeatGunIndex, PistolIndex, -1), (d.Primary, d.Pistol, d.MeleeChoice));
        Assert.Empty(Loadouts.Melee(Config, d));
        // Empty choice lists: only the class default (synthetic configs predating choices).
        var plain = Config with { Classes = [new ClassLoadout("plain", RifleIndex, PistolIndex, [Knife])] };
        var q = Fresh(0, primary: 0, pistol: 0, melee: 0, c: plain);
        Assert.Equal((RifleIndex, PistolIndex, -1), (q.Primary, q.Pistol, q.MeleeChoice));
        Assert.Equal(new[] { Knife }, Loadouts.Melee(plain, q));
        Assert.Equal(new[] { RifleIndex }, Loadouts.Choices(plain, 0, WeaponSlot.Ranged));

        // A state naming a weapon the class may not choose (here the pistol as a primary, a primary as a pistol) gets the
        // class default from every lookup and from a respawn.
        var odd = Fresh(Tactical) with { Primary = GoldIndex, Pistol = PlasmaIndex, MeleeChoice = 7 };
        Assert.Same(Rifle, Loadouts.InHand(Config, odd));
        Assert.Same(Pistol, Loadouts.Holstered(Config, odd));
        Assert.Equal(KnifeCombo.Steps, Loadouts.Melee(Config, odd));
        var fixedUp = Combat.Respawned(odd, Config);
        Assert.Equal((RifleIndex, PistolIndex, 0), (fixedUp.Primary, fixedUp.Pistol, fixedUp.MeleeChoice));
        // A bare state (no choice made: -1) is the class default.
        var bare = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Devastator }, Config);
        Assert.Same(HeatGun, Loadouts.InHand(Config, bare));
    }

    [Fact]
    public void The_handshake_carries_the_requested_loadout_and_the_server_validates_it()
    {
        var hello = new Hello(Protocol.Version, 1, 2, 3, 4, 5, 6, 2, 1, 0, 7);
        Assert.Equal(hello, Protocol.Read(Protocol.Write(hello)));
        Assert.Equal((Protocol.DefaultChoice, Protocol.DefaultChoice, Protocol.DefaultChoice), (new Hello(Protocol.Version, 1, 2, 3).RequestedPrimary,
            new Hello(Protocol.Version, 1, 2, 3).RequestedPistol, new Hello(Protocol.Version, 1, 2, 3).RequestedMelee));
        Assert.Equal(42, Protocol.Version);

        var joined = new List<PlayerState>();
        var link = new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);
        var h = new Harness(link, 3, 0, Config);
        h.Server.PlayerJoined += joined.Add;
        var chosen = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Tactical, RequestedPrimary = 2, RequestedPistol = 1, RequestedMelee = 1 });
        var invalid = h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Tactical, RequestedPrimary = 3, RequestedPistol = 200, RequestedMelee = 254 });
        var unset = h.AddClient(nonce: 102, options: new ClientOptions { RequestedClass = Assault });
        h.Run(TimeSpan.FromSeconds(1));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        (int, int, int) Choice(GameClient c) { var p = h.Server.World.Players[c.PlayerId]; return (p.Primary, p.Pistol, p.MeleeChoice); }
        Assert.Equal((HeatGunIndex, GoldIndex, 1), Choice(chosen));
        Assert.Equal((RifleIndex, PistolIndex, 0), Choice(invalid));
        Assert.Equal((-1, PistolIndex, 0), Choice(unset));
        // Each client predicts with its own choices (learnt from its full record).
        Assert.Equal((HeatGunIndex, GoldIndex, 1), (chosen.Predicted.Primary, chosen.Predicted.Pistol, chosen.Predicted.MeleeChoice));
        Assert.Same(HeatGun, Loadouts.InHand(Config, chosen.Predicted));
        // The join event names the chosen loadout (the console server prints it).
        Assert.Equal(3, joined.Count);
        Assert.Equal("test - tactical: primary test_heat_gun, pistol test_gold_pistol, melee test_sword",
            Loadouts.Describe(Config, Assert.Single(joined, p => p.PlayerId == chosen.PlayerId)));
        Assert.Equal((-1, 0, 200, 254), (GameServer.ChoiceFor(Protocol.DefaultChoice), GameServer.ChoiceFor(0), GameServer.ChoiceFor(200), GameServer.ChoiceFor(254)));
    }

    // ---------------------------------------------------------------- snapshots

    [Fact]
    public void Snapshots_carry_heat_and_choices_in_the_own_record_and_the_weapon_in_hand_and_melee_choice_in_slim_ones()
    {
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 1f, true)
        {
            Alive = true, Class = Tactical, Slot = WeaponSlot.Pistol, Primary = PlasmaIndex, Pistol = GoldIndex, MeleeChoice = 1,
            Heat = 0f, Overheated = false, SinceHeat = 0f, OtherHeat = 87.5f, OtherOverheated = true, OtherSinceHeat = 0.25f,
        };
        var other = me with { PlayerId = 4, Slot = WeaponSlot.Ranged, Heat = 50f, Overheated = true, SinceHeat = 2f };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(5, 4, 1, [me, other], Self: 3))));
        Assert.Equal(me, back.Players[0]);
        var slim = back.Players[1];
        Assert.Equal(Protocol.Slim(other), slim);
        Assert.Equal((PlasmaIndex, -1, 1, 0f, false), (slim.Primary, slim.Pistol, slim.MeleeChoice, slim.Heat, slim.Overheated));
        Assert.Same(Plasma, Loadouts.InHand(Config, slim));
        // Swapped slots, and "none".
        var bare = me with { Slot = WeaponSlot.Ranged, Primary = -1, Pistol = 0, MeleeChoice = -1, Overheated = true, OtherOverheated = false };
        Assert.Equal(bare, Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(5, 4, 1, [bare], Self: 3)))).Players[0]);
        var slimBare = Protocol.Slim(bare with { PlayerId = 9 });
        Assert.Equal((-1, -1, -1), (slimBare.Primary, slimBare.Pistol, slimBare.MeleeChoice));

        // The overheat flags travel in the own record's melee flag byte; 128 (a heavy buffered press, protocol 17) without a
        // buffered press is refused.
        var bytes = Protocol.Write(new Snapshot(5, 4, 1, [me], Self: 3));
        // That byte comes before the 4 reaction bytes (protocol 17), the charge lock (18), the ready and raise ticks (20; 3 bytes since 21), the match block (9 bytes), the kill count (protocol 10), the
        // point count (11), the 12-byte hit block (12) and the shot count (16).
        Assert.Equal(32, bytes[^33] & 48);
        bytes[^33] |= 128;
        Assert.Null(Protocol.Read(bytes));
    }

    [Fact]
    public void A_sixteen_player_snapshot_with_heat_stays_within_1200_bytes()
    {
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new PlayerState(i, new Vector3(1e4f), new Vector3(100f), 3f, true)
        {
            Alive = true, Health = 1e3f, Shield = 1e3f, Class = Protocol.MaxClasses - 1, Slot = WeaponSlot.Pistol, Clip = 65535, Reserve = 65535, OtherClip = 65535, OtherReserve = 65535,
            Cooldown = 1, ReloadLeft = 1, SinceDamage = 1, RespawnLeft = 1, SwitchLeft = 1, Pitch = 1, MeleeStep = Protocol.MaxMeleeStep, MeleeTime = 1,
            MeleeQueued = true, MeleeHeld = true, FireHeld = true, SwitchHeld = true,
            Primary = 254, Pistol = 253, MeleeChoice = 254, Heat = 100f, Overheated = true, SinceHeat = 1f, OtherHeat = 99f, OtherOverheated = true, OtherSinceHeat = 2f,
        }).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7));
        output.WriteLine($"16-player snapshot (protocol {Protocol.Version}): {bytes.Length} bytes");
        Assert.True(bytes.Length <= 1200, $"{bytes.Length} bytes");
        Assert.True(bytes.Length <= UdpServerTransport.MaxPacket);
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(players.Select(p => p.PlayerId == 7 ? p : Protocol.Slim(p)), back.Players);
        // Every truncation is refused.
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));
    }

    // ---------------------------------------------------------------- config hash and validation

    [Fact]
    public void The_config_hash_covers_every_heat_field_and_every_choice_list()
    {
        ulong h = Config.Hash();
        Assert.Equal(h, (Config with
        {
            Weapons = Config.Weapons.Select(w => w with { Heat = w.Heat is null ? null : w.Heat with { } }).ToList(),
            Classes = Config.Classes.Select(c => c with { PrimaryChoices = c.PrimaryChoices.ToList(), MeleeChoices = c.MeleeChoices.Select(m => m with { Steps = m.Steps.ToList() }).ToList() }).ToList(),
        }).Hash());

        var heat = HeatGun.Heat!;
        HeatSpec?[] heatVariants =
        [
            null, heat with { PerShot = 11 }, heat with { Overheat = 101 }, heat with { HighHeat = 76 }, heat with { Recovery = 21 },
            heat with { CoolRate = 31 }, heat with { CoolDelay = 0.4f }, heat with { CoolDelayAfterOverheat = 0.1f },
        ];
        var seen = new HashSet<ulong> { h };
        foreach (var v in heatVariants)
            Assert.True(seen.Add((Config with { Weapons = [Rifle, Pistol, HeatGun with { Heat = v }, Plasma, GoldPistol] }).Hash()), $"{v} left the hash unchanged");
        Assert.True(seen.Add((Config with { Weapons = [Rifle with { Heat = heat }, Pistol, HeatGun, Plasma, GoldPistol] }).Hash()), "heat on a weapon without");

        var t = Config.Classes[Tactical];
        ClassLoadout[] choiceVariants =
        [
            t with { PrimaryChoices = [RifleIndex, PlasmaIndex] }, t with { PrimaryChoices = [RifleIndex, HeatGunIndex, PlasmaIndex] },
            t with { PistolChoices = [PistolIndex] }, t with { PistolChoices = [GoldIndex, PistolIndex] },
            t with { MeleeChoices = [KnifeCombo] }, t with { MeleeChoices = [SwordCombo, KnifeCombo] },
            t with { MeleeChoices = [KnifeCombo, SwordCombo with { Wargear = "other" }] },
            t with { MeleeChoices = [KnifeCombo, SwordCombo with { Steps = [Sword with { Damage = 71 }] }] },
            t with { MeleeChoices = [KnifeCombo, SwordCombo with { Steps = [Sword, Sword] }] },
        ];
        foreach (var v in choiceVariants)
            Assert.True(seen.Add((Config with { Classes = [v, Config.Classes[1], Config.Classes[2]] }).Hash()),
                $"[{string.Join(",", v.PrimaryChoices)}] [{string.Join(",", v.PistolChoices)}] [{string.Join(",", v.MeleeChoices.Select(m => m.Wargear))}]: hash unchanged");
    }

    [Fact]
    public void Loadouts_with_unusable_heat_or_choices_are_refused_and_accepted_ones_are_copied()
    {
        var weapons = Config.Weapons;
        var classes = Config.Classes;
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), weapons, classes));
        var heat = HeatGun.Heat!;
        HeatSpec[] bad =
        [
            heat with { Overheat = 0 }, heat with { Recovery = 100 }, heat with { Recovery = -1 }, heat with { CoolRate = 0 },
            heat with { PerShot = float.NaN }, heat with { CoolDelay = -0.1f }, heat with { CoolDelayAfterOverheat = float.PositiveInfinity }, heat with { HighHeat = -1 },
        ];
        foreach (var b in bad)
            Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [Rifle, Pistol, HeatGun with { Heat = b }, Plasma, GoldPistol], classes));
        var t = classes[Tactical];
        ClassLoadout[] badClasses =
        [
            t with { PrimaryChoices = [5] }, t with { PistolChoices = [-1] }, t with { MeleeChoices = [new MeleeCombo("x", [Knife with { Duration = 0 }])] },
            t with { PrimaryChoices = Enumerable.Repeat(0, 256).ToArray() },
        ];
        foreach (var b in badClasses)
            Assert.Null(GameplayTuning.WithClasses(new SimConfig(), weapons, [b, classes[1], classes[2]]));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), Enumerable.Repeat(Rifle, 256).ToArray(), [new ClassLoadout("c", 0, -1, [])]));

        // Copies: changing the reader's lists afterwards changes neither the config nor its hash.
        var primaries = new List<int> { RifleIndex, PlasmaIndex };
        var steps = new List<MeleeStep> { Sword };
        var melees = new List<MeleeCombo> { KnifeCombo, new("test_sword", steps) };
        var config = GameplayTuning.WithClasses(new SimConfig(), weapons, [t with { PrimaryChoices = primaries, MeleeChoices = melees }])!;
        ulong before = config.Hash();
        primaries.Add(HeatGunIndex); steps.Add(Sword); melees.Add(SwordCombo);
        Assert.Equal(before, config.Hash());
        Assert.Equal(2, config.Classes[0].PrimaryChoices.Count);
        Assert.Single(config.Classes[0].MeleeChoices[1].Steps);
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(61)]
    [InlineData(62)]
    public void Prediction_matches_the_server_while_overheating_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // No damage (not predicted); the plasma cools faster so it overheats several times in the run.
        var config = Config with
        {
            Weapons = [Rifle with { Damage = 0f }, Pistol with { Damage = 0f, ReserveSize = 400 }, HeatGun with { Damage = 0f, ClipSize = 2000, ReserveSize = 0 },
                Plasma with { Damage = 0f, ClipSize = 2000, Heat = Plasma.Heat! with { CoolRate = 40f } }, GoldPistol with { Damage = 0f, ReserveSize = 400 }],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Devastator });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Tactical, RequestedPrimary = 1, RequestedPistol = 1, RequestedMelee = 1 });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~InputButtons.Jump;
            uint phase = (t + (uint)i * 7) % 240;
            bool primaryFire = phase < 100 || phase is >= 140 and < 200;
            if (phase is 100 or 101) buttons |= InputButtons.Switch;          // to the pistol while hot
            if (phase is 130 or 131) buttons |= InputButtons.Switch;          // and back
            if (i == 0 && primaryFire) buttons |= InputButtons.Fire;          // heat gun: held
            if (i == 1 && primaryFire && phase % 2 == 0) buttons |= InputButtons.Fire;   // plasma: tapped
            if (phase is >= 105 and < 125 && phase % 4 == 0) buttons |= InputButtons.Fire; // pistol taps
            if (phase is 90 or 210) buttons |= InputButtons.Reload;           // ignored while overheated
            if (i == 1 && phase is 220 or 221) buttons |= InputButtons.Melee;  // the chosen sword
            return move with { Buttons = buttons, Pitch = 1.3f };
        }
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });

        for (int i = 0; i < 2; i++)
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
            int overheats = pairs.Count(p => p.Second.Slot == p.First.Slot && !p.First.Overheated && p.Second.Overheated);
            int releases = pairs.Count(p => p.Second.Slot == p.First.Slot
                && ((p.First.Overheated && !p.Second.Overheated) || (p.First.OtherOverheated && !p.Second.OtherOverheated)));
            int holsteredCooling = pairs.Count(p => p.Second.Slot == p.First.Slot && p.Second.OtherHeat < p.First.OtherHeat);
            int switches = pairs.Count(p => p.First.Slot != p.Second.Slot);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {overheats} overheats, {releases} releases, {holsteredCooling} holstered cooling ticks, {switches} switches");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(overheats >= 2 && releases >= 2 && holsteredCooling > 10 && switches >= 4, $"client {i}: too little heat activity");
            Assert.Empty(mismatches);
        }
    }
}
