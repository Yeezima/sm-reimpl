using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Charged fire (M12; M31: Fire charges every charge weapon, a tap is the basic shot): charge time per tick, the stage a release
/// fires, ammunition to start and per shot, heat per second while charging, overheat, tap versus hold on each charge weapon (and a
/// tap waiting for the weapon's raise), fire delay, cancels (switch, melee, reload, death), the move speed multiplier, a charged
/// ray in the world, and exact prediction while charging. Synthetic weapons only (shaped like the install's; no game data).
/// </summary>
public class ChargeTests(ITestOutputHelper output)
{
    internal static readonly ChargeShot LasShot = new(DeliveryKind.Raycast, new DamageSpec(200f, 0f, 2f, 1f, 2f, 1f, false), 500f, 3f, 4, 0f, 0f, null);
    /// <summary>Lascannon-like: basic ray 50 (a tap), clip 4; held 1.5 s a 200 ray using the whole clip; needs 2 to start; reload does not cancel.</summary>
    internal static readonly WeaponSpec Las = new("test_las", WeaponSlot.Ranged, TriggerKind.SemiAuto,
        Damage: 50f, ShotCooldown: 1f, ClipSize: 4, ReserveSize: 8, ReloadTime: 2f, MaxRange: 300f,
        FallOffStart: 300f, FallOffEnd: 300f, FallOffEndMultiplier: 1f, HeadMultiplier: 2f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f)
    {
        Charge = new ChargeSpec(Shots.SecondaryAbility, [new("1.charge_light", 0f, 0f, 1f, null), new("3.charge_medium", 1.5f, 0f, 1f, LasShot)], 0f, 1f, false, 2),
    };
    /// <summary>
    /// The real lascannon's data (<c>max_charge_time</c> 1.5): sim/instant-fire, the press fires the basic shot at once. <see cref="Las"/> is the
    /// same weapon without a <c>max_charge_time</c>, a held-charge ray weapon that keeps the charge machinery (pierce, ammunition, reload rules) tested.
    /// </summary>
    internal static readonly WeaponSpec LasPress = Las with { Name = "test_las_press", Charge = Las.Charge! with { MaxChargeTime = 1.5f } };

    internal static readonly ProjectileSpec Bolt = new("ebps\\test\\bolt", 90f, 3f, "raycast", 0.25f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, null,
        new SplashSpec(7f, new DamageSpec(75f, 75f, 1f, 1f, 1f, 1f, true), [new(2f, 1f), new(7f, 0.01f)], true, false, "target_all"));
    /// <summary>Plasma-gun-like: basic ray 20 (+10 heat; a tap); held: 10 heat/s from 0 s, the charged bolt from 1.1 s with 28 heat/s, 15 ammo, +40 heat, 0.1 s delay.</summary>
    internal static readonly WeaponSpec PlasmaGun = new("test_plasma_gun", WeaponSlot.Ranged, TriggerKind.SemiAuto,
        Damage: 20f, ShotCooldown: 0.1f, ClipSize: 100, ReserveSize: 100, ReloadTime: 1f, MaxRange: 250f,
        FallOffStart: 250f, FallOffEnd: 250f, FallOffEndMultiplier: 1f, HeadMultiplier: 2f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f)
    {
        Heat = new HeatSpec(10f, 100f, 80f, 0.1f, 6f, 0.35f, 1f),
        Charge = new ChargeSpec(Shots.SecondaryAbility,
            [new("1.charge_light", 0f, 10f, 1f, null), new("4.charge_heavy", 1.1f, 28f, 1f, new ChargeShot(DeliveryKind.Projectile, null, 250f, 0.1f, 15, 40f, 0.1f, Bolt))],
            0f, 1f, true, 15),
    };

    internal static readonly ProjectileSpec StickyBolt = new("ebps\\test\\sticky", 90f, 1.5f, "raycast", 0.25f, [new(0f, 0f)], false, true, true, 0, 0f, 0f, 0, null,
        new SplashSpec(6f, new DamageSpec(50f, 75f, 1f, 1f, 1f, 1f, true), [new(2f, 1f), new(6f, 0.1f)], true, false, "target_all"));
    /// <summary>Plasma-pistol-like: the charge is on the primary ability (28 heat/s; the sticky shot from 0.65 s: 10 ammo, +100 heat, 0.1 s delay); a tap is the basic ray 15 (+6.5 heat).</summary>
    internal static readonly WeaponSpec PlasmaPistol = new("test_plasma_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto,
        Damage: 15f, ShotCooldown: 0.1f, ClipSize: 100, ReserveSize: 0, ReloadTime: 1f, MaxRange: 100f,
        FallOffStart: 100f, FallOffEnd: 100f, FallOffEndMultiplier: 1f, HeadMultiplier: 2f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f)
    {
        Heat = new HeatSpec(6.5f, 100f, 80f, 0.1f, 6f, 0.35f, 1f),
        Charge = new ChargeSpec(Shots.PrimaryAbility,
            [new("1.charge_light", 0f, 28f, 1f, null), new("4.charge_heavy", 0.65f, 0f, 1f, new ChargeShot(DeliveryKind.Projectile, null, 100f, 0.12f, 10, 100f, 0.1f, StickyBolt))],
            0f, 1f, true, 10),
    };

    internal static readonly ProjectileSpec CannonBall = new("ebps\\test\\cannon", 100f, 3f, "raycast", 0.1f, [new(0f, 0f)], true, true, false, 0, 0f, 0f, 0, null,
        new SplashSpec(7f, new DamageSpec(60f, 0f, 1f, 1f, 2f, 2f, true), [new(3.5f, 1f), new(7f, 0.15f)], true, false, "target_all"));
    /// <summary>Plasma-cannon-like: basic projectile (a tap); held: move ×0.5 (both stages), the charged shot from 3 s.</summary>
    internal static readonly WeaponSpec Cannon = new("test_cannon", WeaponSlot.Ranged, TriggerKind.FullAuto,
        Damage: 0f, ShotCooldown: 0.35f, ClipSize: 100, ReserveSize: 0, ReloadTime: 1f, MaxRange: 250f,
        FallOffStart: 250f, FallOffEnd: 250f, FallOffEndMultiplier: 1f, HeadMultiplier: 1f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f)
    {
        Delivery = DeliveryKind.Projectile, Projectile = CannonBall,
        Heat = new HeatSpec(21f, 100f, 80f, 0.1f, 3f, 0.55f, 1f),
        Charge = new ChargeSpec(Shots.SecondaryAbility,
            [new("1.charge_light", 0f, 0f, 0.5f, null), new("4.charge_heavy", 3f, 0f, 0.5f, new ChargeShot(DeliveryKind.Projectile, null, 250f, 0f, 25, 100f, 0.1f, CannonBall))],
            0f, 1f, true, 25),
    };

    const int PlasmaChoice = 0, LasChoice = 1, CannonChoice = 2;
    const int Tactical = 0, PistolOnly = 1;
    static readonly MeleeStep Knife = new(1.0f, 0.25f, 0.4f, 0.6f, 0.5f, 0.7f, 40f, 20f, 3.5f);

    static readonly SimConfig Config = new()
    {
        Weapons = [Las, PlasmaGun, PlasmaPistol, Cannon],
        Classes =
        [
            new ClassLoadout("test - tactical", 1, 2, [Knife]) { PrimaryChoices = [1, 0, 3], PistolChoices = [2] },
            new ClassLoadout("test - pistol", -1, 2, [Knife]) { PistolChoices = [2] },
        ],
    };

    static float Dt => Config.TickSeconds;

    static PlayerState Fresh(int playerClass, int primary = -1) =>
        Combat.Respawned(Loadouts.Choose(Config, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = playerClass }, primary, -1, -1), Config);

    static SelfEvents Step(ref PlayerState s, InputButtons buttons, float moveY = 0f)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, moveY, 0, buttons), Config, out var events);
        return events;
    }

    // ---------------------------------------------------------------- timing and stages

    [Theory]
    [InlineData(1, 0)]    // a tap: the basic shot
    [InlineData(44, 0)]   // one tick short of 1.5 s: still the basic shot
    [InlineData(45, 2)]   // exactly 1.5 s: the charged shot
    [InlineData(60, 2)]   // held longer: still the charged shot (no hold limit)
    public void A_release_fires_the_stage_reached_counting_every_held_tick(int held, int stage)
    {
        var s = Fresh(Tactical, LasChoice);
        Assert.Same(Las, Loadouts.InHand(Config, s));
        for (int k = 1; k <= held; k++)
        {
            var e = Step(ref s, InputButtons.Fire);
            Assert.False(e.Fired, $"tick {k}");
            Assert.True(s.Charging);
            Assert.Equal(k * Dt, s.ChargeTime, 4);
        }
        Assert.Equal(4, s.Clip);
        var release = Step(ref s, InputButtons.None);
        Assert.False(s.Charging);
        Assert.Equal(0f, s.ChargeTime);
        Assert.True(release.Fired);
        Assert.Equal(stage, release.ShotStage);
        if (stage == 2)
        {
            Assert.Equal(0, s.Clip);              // AmmoCost 4: the whole clip
            Assert.Equal(3f, s.Cooldown, 4);      // the charged shot's cooldown
        }
        else Assert.Equal((3, 1f), (s.Clip, s.Cooldown));   // the basic shot: one round, its own cooldown
    }

    [Fact]
    public void A_charge_needs_its_ammunition_to_start_and_the_shot_costs_at_most_the_clip()
    {
        var s = Fresh(Tactical, LasChoice) with { Clip = 1 };
        var basic = Step(ref s, InputButtons.Fire);
        Assert.False(s.Charging);   // 1 < AmmoToStart 2: the press is the basic shot at once
        Assert.Equal((true, 0, 0), (basic.Fired, basic.ShotStage, s.Clip));
        Step(ref s, InputButtons.None);

        s = s with { Clip = 3, Cooldown = 0f };   // control: 3 >= 2 starts
        for (int k = 0; k < 45; k++) Step(ref s, InputButtons.Fire);
        Assert.True(s.Charging);
        var e = Step(ref s, InputButtons.None);
        Assert.True(e.Fired);
        Assert.Equal(0, s.Clip);    // 4 asked, 3 in the clip
    }

    [Fact]
    public void Charging_adds_the_reached_stages_heat_per_second_and_the_shot_leaves_after_its_delay()
    {
        var s = Fresh(Tactical, PlasmaChoice);
        Assert.Same(PlasmaGun, Loadouts.InHand(Config, s));
        float expected = 0f;
        for (int k = 1; k <= 40; k++)
        {
            Step(ref s, InputButtons.Fire);
            expected += (k < 33 ? 10f : 28f) * Dt;   // stage 2 from 33 ticks (1.1 s)
            Assert.Equal(expected, s.Heat, 3);
            Assert.Equal(0f, s.SinceHeat);           // every charging tick restarts the cool-down delay
        }
        Assert.Equal(100, s.Clip);
        // Release: sim/instant-fire (owner, 2026-10-31): the shot leaves on the release tick although the data has a 0.1 s delay.
        var e = Step(ref s, InputButtons.None);
        Assert.True(e.Fired);
        Assert.Equal(2, e.ShotStage);
        Assert.Equal((0, 85), (s.PendingShot, s.Clip));
        Assert.Equal(expected + 40f, s.Heat, 3);
        Assert.Equal(0.1f, s.Cooldown, 4);
    }

    [Fact]
    public void Reaching_overheat_while_charging_cancels_the_charge()
    {
        var s = Fresh(Tactical, PlasmaChoice) with { Heat = 95f };
        int k = 0;
        while (k < 40)
        {
            k++;
            Step(ref s, InputButtons.Fire);
            if (!s.Charging) break;
        }
        Assert.InRange(k, 15, 16);   // 95 + 10/30 per tick
        Assert.True(s.Overheated);
        Assert.Equal(100f, s.Heat);
        Assert.Equal(0f, s.ChargeTime);
        Assert.False(Step(ref s, InputButtons.None).Fired);   // the release fires nothing
        Assert.Equal(100, s.Clip);
    }

    [Fact]
    public void The_plasma_pistol_shoots_on_a_tap_and_fires_its_charged_shot_when_held()
    {
        var s = Fresh(PistolOnly);
        Assert.Same(PlasmaPistol, Loadouts.InHand(Config, s));
        // Tap: the press starts a charge, the release before 0.65 s fires the basic shot.
        Assert.False(Step(ref s, InputButtons.Fire).Fired);
        Assert.True(s.Charging);
        var e = Step(ref s, InputButtons.None);
        Assert.True(e.Fired);
        Assert.Equal(0, e.ShotStage);
        Assert.Equal(99, s.Clip);
        Assert.Equal(28f * Dt + 6.5f, s.Heat, 3);

        // 19 ticks (0.633 s): still the basic shot.
        s = Fresh(PistolOnly);
        for (int k = 0; k < 19; k++) Step(ref s, InputButtons.Fire);
        e = Step(ref s, InputButtons.None);
        Assert.Equal((true, 0, 99), (e.Fired, e.ShotStage, s.Clip));

        // 20 ticks (0.667 s): the charged shot on the release tick (sim/instant-fire; the data's 0.1 s delay is not applied); +100 heat overheats.
        s = Fresh(PistolOnly);
        for (int k = 0; k < 20; k++) Step(ref s, InputButtons.Fire);
        e = Step(ref s, InputButtons.None);
        Assert.Equal((true, 2, 90), (e.Fired, e.ShotStage, s.Clip));
        Assert.True(s.Overheated);

        // Too little ammunition to charge: the Fire press fires the basic shot at once.
        s = Fresh(PistolOnly) with { Clip = 5 };
        e = Step(ref s, InputButtons.Fire);
        Assert.Equal((true, 0, false, 4), (e.Fired, e.ShotStage, s.Charging, s.Clip));
    }

    // ---------------------------------------------------------------- cancels

    [Fact]
    public void Switching_melee_reload_and_death_cancel_a_charge_or_a_pending_shot()
    {
        PlayerState Charged(int ticks)
        {
            var s = Fresh(Tactical, PlasmaChoice);
            for (int k = 0; k < ticks; k++) Step(ref s, InputButtons.Fire);
            Assert.True(s.Charging);
            return s;
        }

        var a = Charged(10);
        Step(ref a, InputButtons.Fire | InputButtons.Switch);
        Assert.Equal((false, 0f, WeaponSlot.Pistol), (a.Charging, a.ChargeTime, a.Slot));

        var b = Charged(10);
        Step(ref b, InputButtons.Fire | InputButtons.Melee);
        Assert.Equal((false, 1), (b.Charging, b.MeleeStep));

        var c = Charged(10) with { Clip = 90 };
        Step(ref c, InputButtons.Fire | InputButtons.Reload);   // ReloadCancels: cancelled, and the reload starts
        Assert.False(c.Charging);
        Assert.True(c.ReloadLeft > 0f);

        var d = Combat.Killed(Charged(10), Config);
        Assert.Equal((false, 0f), (d.Charging, d.ChargeTime));

        // A pending shot is lost to a switch.
        var p = Charged(40) with { Charging = false, ChargeTime = 0f, PendingShot = 2, PendingLeft = 0.1f };   // legacy state: releases no longer leave a pending shot
        Step(ref p, InputButtons.Switch);
        Assert.Equal(0, p.PendingShot);
        for (int k = 0; k < 30; k++) Assert.False(Step(ref p, InputButtons.None).Fired);

        // The lascannon ignores reload while charging (ReloadCancels false); control above: the plasma gun cancels.
        var l = Fresh(Tactical, LasChoice) with { Clip = 3 };
        for (int k = 0; k < 10; k++) Step(ref l, InputButtons.Fire);
        Step(ref l, InputButtons.Fire | InputButtons.Reload);
        Assert.True(l.Charging);
        Assert.Equal(0f, l.ReloadLeft);
    }

    [Fact]
    public void Charging_scales_movement_by_the_stage_multiplier()
    {
        var walk = Fresh(Tactical, CannonChoice);
        var charge = walk;
        for (int k = 0; k < 30; k++) { Step(ref walk, InputButtons.None, moveY: 1f); Step(ref charge, InputButtons.Fire, moveY: 1f); }
        float Speed(PlayerState s) => new Vector2(s.Velocity.X, s.Velocity.Y).Length();
        Assert.Equal(Config.RunSpeed, Speed(walk), 3);
        Assert.True(charge.Charging);
        Assert.Equal(Config.RunSpeed * 0.5f, Speed(charge), 3);
    }

    // ---------------------------------------------------------------- in the world

    [Theory]
    [InlineData(44, false)]   // released early: the basic ray (50)
    [InlineData(45, true)]    // held 1.5 s: the charged ray (200)
    public void A_charged_ray_hits_with_its_own_damage(int held, bool charged)
    {
        var config = Config with { ShieldMax = 50f, HealthMax = 500f };
        var world = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 10, 0), MathF.PI)]);
        world.Spawn(1, Tactical, LasChoice);
        world.Spawn(2, Tactical, LasChoice);
        Assert.Equal(new Vector3(0, 10, 0), world.Players[2].Position);
        var hitsSeen = new List<HitEvent>();
        for (int k = 0; k <= held; k++)
        {
            world.Step(id => new PlayerInput(0, 0, 0, id == 1 ? 0f : MathF.PI, id == 1 && k < held ? InputButtons.Fire : InputButtons.None));
            hitsSeen.AddRange(world.LastHits);
        }
        var hit = Assert.Single(hitsSeen);
        if (!charged)
        {
            Assert.Equal((1, 2, 50f, 0f), (hit.Shooter, hit.Target, hit.ShieldDamage, hit.HealthDamage));   // 50 on the body: the shield takes it
            Assert.Contains(world.LastShots, x => x.Shooter == 1 && x.Stage == 0);
            return;
        }
        Assert.Equal((1, 2, HitZone.Body), (hit.Shooter, hit.Target, hit.Zone));
        // 200 on the body: the 50 shield (×1) first, the remaining 150 to health (×1).
        Assert.Equal((50f, 150f), (hit.ShieldDamage, hit.HealthDamage));
        Assert.Equal(350f, world.Players[2].Health, 3);
        Assert.Contains(world.LastShots, x => x.Shooter == 1 && x.Stage == 2 && x.Projectile == 0);
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(71)]
    [InlineData(72)]
    public void Prediction_matches_the_server_while_charging_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // No damage (not predicted), so the players cannot hurt each other or themselves.
        static ProjectileSpec Harmless(ProjectileSpec p) => p with { Splash = p.Splash! with { Damage = p.Splash.Damage with { Damage = 0f, ShieldDamage = 0f } } };
        var config = Config with
        {
            Weapons =
            [
                Las with { Damage = 0f, Charge = Las.Charge! with { Stages = [Las.Charge.Stages[0], Las.Charge.Stages[1] with { Shot = LasShot with { Damage = LasShot.Damage! with { Damage = 0f }, ShotCooldown = 0.5f } }] }, ReserveSize = 2000 },
                PlasmaGun with { Damage = 0f, ClipSize = 3000, Heat = PlasmaGun.Heat! with { CoolRate = 40f }, RaiseDelay = 0.2f,
                    Charge = PlasmaGun.Charge! with { Stages = [PlasmaGun.Charge.Stages[0], PlasmaGun.Charge.Stages[1] with { Shot = PlasmaGun.Charge.Stages[1].Shot! with { Projectile = Harmless(Bolt) } }] } },
                PlasmaPistol with { Damage = 0f, ClipSize = 3000, Heat = PlasmaPistol.Heat! with { CoolRate = 60f }, RaiseDelay = 0.167f,
                    Charge = PlasmaPistol.Charge! with { Stages = [PlasmaPistol.Charge.Stages[0], PlasmaPistol.Charge.Stages[1] with { Shot = PlasmaPistol.Charge.Stages[1].Shot! with { Projectile = Harmless(StickyBolt) } }] } },
                Cannon with { Projectile = Harmless(CannonBall), ClipSize = 3000, Heat = Cannon.Heat! with { CoolRate = 40f },
                    Charge = Cannon.Charge! with { Stages = [Cannon.Charge.Stages[0], Cannon.Charge.Stages[1] with { Shot = Cannon.Charge.Stages[1].Shot! with { Projectile = Harmless(CannonBall) } }] } },
            ],
            Classes = Config.Classes.Select(c => c with { Melee = [Knife with { Damage = 0f, SecondaryDamage = 0f }] }).ToArray(),
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Tactical, RequestedPrimary = PlasmaChoice });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Tactical, RequestedPrimary = LasChoice });
        h.AddClient(nonce: 102, options: new ClientOptions { RequestedClass = PistolOnly });
        h.AddClient(nonce: 103, options: new ClientOptions { RequestedClass = Tactical, RequestedPrimary = CannonChoice });
        int n = h.Clients.Count;
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~InputButtons.Jump;
            uint phase = (t + (uint)i * 11) % 300;
            // M31: Fire is both: held it charges, a tap (one tick) is the basic shot; the sprint of the scripted moves lowers the weapon,
            // so taps also wait for the raise (the plasma gun and pistol have raise delays here).
            if (phase < 50 || phase is >= 70 and < 160) buttons |= InputButtons.Fire;      // holds of 1.7 s and 3 s (the second after the reload below)
            if (phase is >= 165 and < 167 || phase is >= 170 and < 185) buttons |= InputButtons.Fire;   // a tap, a short hold
            if (phase is >= 190 and < 225 && phase % 4 == 0) buttons |= InputButtons.Fire;             // taps: basic shots
            if (phase is 230 or 231) buttons |= InputButtons.Switch;
            if (phase is 260 or 261) buttons |= InputButtons.Switch;
            if (phase is >= 235 and < 255 && phase % 3 == 0) buttons |= InputButtons.Fire;   // the pistol's basic shot (the pistol-only class: the same)
            if (phase == 30) buttons |= InputButtons.Reload;                  // cancels the plasma weapons' and the cannon's first hold
            if (phase is 280 or 281) buttons |= InputButtons.Melee;
            return move with { Buttons = buttons, Pitch = 0.2f };
        }
        h.Run(TimeSpan.FromSeconds(40), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < n; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });

        for (int i = 0; i < n; i++)
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
            int charges = pairs.Count(p => !p.First.Charging && p.Second.Charging);
            int pendings = pairs.Count(p => p.First.PendingShot == 0 && p.Second.PendingShot > 0);
            int charged = pairs.Count(p => p.Second.Slot == p.First.Slot && p.Second.Clip <= p.First.Clip - 4);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {charges} charges, {pendings} pending shots, {charged} charged shots");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(charges >= 6 && charged >= 2, $"client {i}: too little charging");
            Assert.Equal(0, pendings);   // sim/instant-fire: no pending shots
            Assert.Empty(mismatches);
        }
    }
}
