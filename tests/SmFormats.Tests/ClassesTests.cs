using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>Weapon specs from wargear and default class loadouts from the class table.</summary>
public class ClassesTests
{
    static BafDocument Gun(string slot = "ranged", string trigger = "wargear\\trigger_policy\\full_auto",
        string delivery = "_base_templates\\ranged_delivery_types\\raycast", float damage = 22f) =>
        BafDocument.Read(SyntheticBafWriter.RangedWeapon(damage, 0.133f, 30, 270, 1.3f, 250f, [(0f, 1f), (45f, 1f), (70f, 0.8f)],
            head: 2.55f, body: 1f, shieldHead: 0.5f, shieldBody: 0.505f, slot, trigger, delivery));

    [Fact]
    public void Hit_scan_weapons_read_slot_trigger_and_tuning()
    {
        var notes = new List<string>();
        var bolter = WeaponTuning.Read("bolter", Gun(), notes)!;
        Assert.Equal((WeaponSlot.Ranged, TriggerKind.FullAuto), (bolter.Slot, bolter.Trigger));
        Assert.Equal((22f, 0.133f, 30, 270, 1.3f, 250f), (bolter.Damage, bolter.ShotCooldown, bolter.ClipSize, bolter.ReserveSize, bolter.ReloadTime, bolter.MaxRange));
        Assert.Equal((45f, 70f, 0.8f), (bolter.FallOffStart, bolter.FallOffEnd, bolter.FallOffEndMultiplier));
        Assert.Equal((2.55f, 1f, 0.5f, 0.505f), (bolter.HeadMultiplier, bolter.BodyMultiplier, bolter.ShieldHeadMultiplier, bolter.ShieldBodyMultiplier));
        var pistol = WeaponTuning.Read("pistol", Gun("pistol", "wargear\\trigger_policy\\semi_auto"), notes)!;
        Assert.Equal((WeaponSlot.Pistol, TriggerKind.SemiAuto), (pistol.Slot, pistol.Trigger));
        Assert.Empty(notes);
    }

    [Fact]
    public void Heat_comes_from_the_pool_and_the_default_heat_per_shot()
    {
        var notes = new List<string>();
        var hot = WeaponTuning.Read("hot", BafDocument.Read(SyntheticBafWriter.RangedWeapon(35f, 0.233f, 400, 0, 1f, 250f, [], 1f, 1f, 0.7f, 0.7f,
            heat: (3f, 100f, 75f, 20f, 25f, 0.3f, 0f))), notes)!;
        Assert.Equal(new HeatSpec(3f, 100f, 75f, 20f, 25f, 0.3f, 0f), hot.Heat);   // 3 per shot, not the upgraded 1
        Assert.Null(WeaponTuning.Read("cold", Gun(), notes)!.Heat);                // control: no pool, no heat
        Assert.Empty(notes);
        var inert = WeaponTuning.Read("inert", BafDocument.Read(SyntheticBafWriter.RangedWeapon(35f, 0.233f, 400, 0, 1f, 250f, [], 1f, 1f, 0.7f, 0.7f,
            heat: (0f, 100f, 75f, 20f, 25f, 0.3f, 0f))), notes)!;
        Assert.Null(inert.Heat);                                                    // a pool whose shots add nothing is no heat
        Assert.Contains(notes, n => n.Contains("inert") && n.Contains("no heat per primary shot"));
    }

    [Fact]
    public void Unknown_triggers_are_noted_and_treated_as_full_auto()
    {
        var notes = new List<string>();
        var odd = WeaponTuning.Read("odd", Gun(trigger: "wargear\\trigger_policy\\charge"), notes)!;
        Assert.Equal(TriggerKind.FullAuto, odd.Trigger);
        Assert.Contains(notes, n => n.Contains("odd") && n.Contains("neither full nor semi auto"));
    }

    const string ProjectileDelivery = "_base_templates\\ranged_delivery_types\\projectile";

    [Fact]
    public void Projectile_weapons_without_a_resolvable_blueprint_are_refused_with_a_note()
    {
        var notes = new List<string>();
        Assert.Null(WeaponTuning.Read("launcher", Gun(delivery: ProjectileDelivery), notes));                 // names no blueprint
        Assert.Contains(notes, n => n.Contains("launcher") && n.Contains("without a blueprint"));
        var named = BafDocument.Read(SyntheticBafWriter.RangedWeapon(10f, 0f, 1, 17, 0.6f, 150f, [], 1f, 1f, 1f, 1f,
            delivery: ProjectileDelivery, blueprint: "ebps\\projectiles\\grenade"));
        Assert.Null(WeaponTuning.Read("named", named, notes));                                                 // no resolver
        Assert.Null(WeaponTuning.Read("named", named, notes, _ => null));                                      // resolver without it
        Assert.Equal(2, notes.Count(n => n.Contains("named") && n.Contains("'ebps\\projectiles\\grenade' not found")));
        Assert.Null(WeaponTuning.Read("cone", Gun(delivery: "_base_templates\\ranged_delivery_types\\shapecast"), notes));
        Assert.Contains(notes, n => n.Contains("cone") && n.Contains("neither a raycast nor a projectile"));
    }

    static BafDocument Grenade(float speed) => BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(speed, 0f, 0.25f, 6f, 75f, 0f,
        [(0f, 1f), (4f, 1f), (5f, 0.5f), (6f, 0.05f)], [(0f, 0f), (5f, 0f), (5.1f, 0.25f)], sticky: true, detonateOnCollision: false, bounces: 1));

    /// <summary>Blueprints by path; each "_upgraded" variant is a different projectile (readers must use the default entry's).</summary>
    static Func<string, BafDocument?> Blueprints(params (string Path, BafDocument Doc)[] docs)
    {
        var all = docs.ToDictionary(d => d.Path, d => d.Doc);
        foreach (var (path, _) in docs) all[path + "_upgraded"] = Grenade(999f);
        return all.GetValueOrDefault;
    }

    [Fact]
    public void Projectile_weapons_read_their_blueprint_and_splash()
    {
        var notes = new List<string>();
        var launcher = WeaponTuning.Read("launcher", BafDocument.Read(SyntheticBafWriter.RangedWeapon(10f, 0f, 1, 17, 0.6f, 150f, [], 1f, 1f, 1f, 1f,
            trigger: "wargear\\trigger_policy\\semi_auto", delivery: ProjectileDelivery, blueprint: "ebps\\projectiles\\grenade", maxActive: 3)),
            notes, Blueprints(("ebps\\projectiles\\grenade", Grenade(130f))))!;
        Assert.Empty(notes);
        Assert.Equal((DeliveryKind.Projectile, 10f, 0f, 1, 17), (launcher.Delivery, launcher.Damage, launcher.ShotCooldown, launcher.ClipSize, launcher.ReserveSize));
        var p = launcher.Projectile!;
        Assert.Equal(("ebps\\projectiles\\grenade", 130f, 0f, "raycast", 0.25f), (p.Blueprint, p.Speed, p.Lifetime, p.CollisionType, p.CollisionRadius));
        Assert.Equal((false, true, true, 1, 3), (p.DetonateOnCollision, p.DetonateWhenLifetimeOver, p.Sticky, p.MaxBounces, p.MaxActive));
        Assert.Equal([new(0f, 0f), new(5f, 0f), new DistancePoint(5.1f, 0.25f)], p.Gravity);
        Assert.Null(p.DetonateDamage);
        Assert.Empty(launcher.DetonationDelays);   // control: no detonate ability, all at once
        var remote = WeaponTuning.Read("remote", BafDocument.Read(SyntheticBafWriter.RangedWeapon(10f, 0f, 1, 17, 0.6f, 150f, [], 1f, 1f, 1f, 1f,
            delivery: ProjectileDelivery, blueprint: "ebps\\projectiles\\grenade", maxActive: 3, detonationDelays: [0f, 0.25f, 0.32f])),
            notes, Blueprints(("ebps\\projectiles\\grenade", Grenade(130f))))!;
        Assert.Equal([0f, 0.25f, 0.32f], remote.DetonationDelays);
        var s = p.Splash!;   // the damaging blast, not the reaction-only one listed before it
        Assert.Equal((6f, true, false, "target_all"), (s.Radius, s.LineOfSight, s.IgnoreSelf, s.TargetFilter));
        Assert.Equal(new DamageSpec(75f, 0f, 1f, 1f, 2f, 2f, true), s.Damage);
        Assert.Equal([new(0f, 1f), new(4f, 1f), new(5f, 0.5f), new DistancePoint(6f, 0.05f)], s.FallOff);
        // Controls: a hit-scan weapon has no projectile; a projectile weapon without direct-hit damage reads 0 without notes.
        Assert.Equal((DeliveryKind.Raycast, (ProjectileSpec?)null), (WeaponTuning.Read("bolter", Gun(), notes)!.Delivery, WeaponTuning.Read("bolter", Gun(), notes)!.Projectile));
        var cannon = WeaponTuning.Read("cannon", BafDocument.Read(SyntheticBafWriter.RangedWeapon(0f, 0.35f, 100, 0, 1f, 150f, [], 1f, 1f, 1f, 1f,
            delivery: ProjectileDelivery, blueprint: "ebps\\projectiles\\bolt")), notes,
            Blueprints(("ebps\\projectiles\\bolt", BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(31f, 5f, 0.225f, 15f, 150f, 450f, [(0f, 1f), (15f, 0.1f)], [], detonateDamage: 200f)))))!;
        Assert.Equal(0f, cannon.Damage);
        Assert.Equal(new DamageSpec(200f, 0f, 0f, 0f, 1f, 1f, true), cannon.Projectile!.DetonateDamage);
        Assert.Equal((150f, 450f), (cannon.Projectile.Splash!.Damage.Damage, cannon.Projectile.Splash.Damage.ShieldDamage));
        Assert.Empty(notes);
    }

    static readonly ChargeFixture PlasmaCharge = new(MinTime: 1.1f, HeatPerSecond: 28f, HeatPerShot: 40f, AmmoCost: 15, Range: 250f, Cooldown: 0.1f,
        Delivery: ProjectileDelivery, Blueprint: "ebps\\projectiles\\alt", Damage: 0f);

    [Fact]
    public void Charged_fire_reads_its_stages_and_the_firing_stages_shot()
    {
        var notes = new List<string>();
        var gun = WeaponTuning.Read("plasma", BafDocument.Read(SyntheticBafWriter.RangedWeapon(30f, 0.267f, 125, 0, 1f, 250f, [], 1f, 1f, 1f, 1f,
            charge: PlasmaCharge)), notes, Blueprints(("ebps\\projectiles\\alt", Grenade(90f))))!;
        Assert.Equal((DeliveryKind.Raycast, 30f), (gun.Delivery, gun.Damage));   // the basic shot is still the primary's
        var c = gun.Charge!;
        Assert.Equal(("ability_ranged_secondary", 0f, 1f, true, 1), (c.Ability, c.MaxChargeTime, c.MinTimeMultiplier, c.ReloadCancels, c.AmmoToStart));
        Assert.Equal(["1.charge_light", "4.charge_heavy"], c.Stages.Select(s => s.Name));   // the empty dead-zone slot is skipped
        Assert.Equal((0f, (ChargeShot?)null), (c.Stages[0].MinTime, c.Stages[0].Shot));     // releasing early fires nothing
        var stage = c.Stages[1];
        Assert.Equal((1.1f, 28f, 1f), (stage.MinTime, stage.HeatPerSecond, stage.MoveSpeedMultiplier));
        var shot = stage.Shot!;
        Assert.Equal((DeliveryKind.Projectile, 250f, 0.1f, 15, 40f, 0.1f), (shot.Delivery, shot.MaxRange, shot.ShotCooldown, shot.AmmoCost, shot.HeatPerShot, shot.FireDelay));
        Assert.Null(shot.Damage);
        Assert.Equal(90f, shot.Projectile!.Speed);
        Assert.DoesNotContain(notes, n => n.Contains("dead_zone"));
        Assert.Null(WeaponTuning.Read("bolter", Gun(), notes)!.Charge);   // control
    }

    [Fact]
    public void A_fire_modes_granted_upgrade_selects_the_charged_shots_entries()
    {
        var notes = new List<string>();
        var las = WeaponTuning.Read("las", BafDocument.Read(SyntheticBafWriter.RangedWeapon(100f, 1f, 4, 30, 2.25f, 500f, [], 2.25f, 1f, 2.25f, 1f,
            trigger: "wargear\\trigger_policy\\semi_auto",
            charge: new ChargeFixture(1.5f, 0f, 0f, 4, 500f, 3f, "_base_templates\\ranged_delivery_types\\raycast", null, 200f,
                MaxChargeTime: 1.5f, MoveSpeedMultiplier: 0.5f, FireDelay: 0f, AmmoToStart: 2, FireModeUpgrade: "las_secondary_fire_mode"))), notes)!;
        var c = las.Charge!;
        Assert.Equal((1.5f, 2), (c.MaxChargeTime, c.AmmoToStart));
        var stage = c.Stages[^1];
        Assert.Equal(0.5f, stage.MoveSpeedMultiplier);   // the speed modifier only, not the other modifier
        var shot = stage.Shot!;
        Assert.Equal(3f, shot.ShotCooldown);             // the fire mode's entry, not the default (15) or the other upgrade's (30)
        Assert.Equal((DeliveryKind.Raycast, 4, 0f), (shot.Delivery, shot.AmmoCost, shot.HeatPerShot));
        Assert.Equal(new DamageSpec(200f, 0f, 2f, 1f, 2f, 1f, false), shot.Damage);
        Assert.Equal((100f, 1f), (las.Damage, las.ShotCooldown));   // the primary is unaffected
    }

    [Fact]
    public void A_charge_only_primary_takes_the_basic_shot_from_the_secondary()
    {
        var notes = new List<string>();
        var pistol = WeaponTuning.Read("ppistol", BafDocument.Read(SyntheticBafWriter.RangedWeapon(15f, 0.1f, 100, 0, 1f, 250f, [], 1f, 1f, 1f, 1f,
            slot: "pistol", trigger: "wargear\\trigger_policy\\semi_auto", charge: PlasmaCharge with { MinTime = 0.65f }, chargeIsPrimary: true)),
            notes, Blueprints(("ebps\\projectiles\\alt", Grenade(90f))))!;
        Assert.Equal((WeaponSlot.Pistol, TriggerKind.SemiAuto, 15f, 0.1f), (pistol.Slot, pistol.Trigger, pistol.Damage, pistol.ShotCooldown));
        Assert.Equal(("ability_ranged_primary", 0.65f), (pistol.Charge!.Ability, pistol.Charge.Stages[^1].MinTime));
        Assert.Contains(notes, n => n.Contains("ppistol") && n.Contains("charge-only"));
    }

    [Fact]
    public void Thrown_grenades_read_count_and_projectile()
    {
        var notes = new List<string>();
        var frag = WeaponTuning.ReadThrown("frag", BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\projectiles\\frag", 2, 1, 1f, 10f)), notes,
            Blueprints(("ebps\\projectiles\\frag", BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(47.5f, 30f, 0f, 10f, 200f, 0f,
                [(0f, 1f), (2.5f, 1f), (2.6f, 0.5f), (10f, 0.05f)], [(5f, 0f), (10f, 1f)], collisionType: "rigidbody", bounces: 1, groundLifetime: 1f, extraAimAngle: 1.5f)))))!;
        Assert.Empty(notes);
        Assert.Equal((2, 1, 1f, 10f), (frag.Count, frag.InitialCount, frag.Cooldown, frag.MaxRange));   // the default entry, not the perk's 4 / 2
        Assert.Equal((47.5f, "rigidbody", 1f, 1.5f), (frag.Projectile.Speed, frag.Projectile.CollisionType, frag.Projectile.GroundLifetime, frag.Projectile.ExtraAimAngle));
        Assert.Equal((10f, 200f), (frag.Projectile.Splash!.Radius, frag.Projectile.Splash.Damage.Damage));
        Assert.Null(WeaponTuning.ReadThrown("gun", Gun(), notes, null));   // control: no utility projectile
        Assert.Contains(notes, n => n.Contains("gun") && n.Contains("no thrown projectile"));
    }

    [Fact]
    public void Config_accepts_projectile_and_charge_data_and_refuses_bad_values()
    {
        var notes = new List<string>();
        var launcher = WeaponTuning.Read("launcher", BafDocument.Read(SyntheticBafWriter.RangedWeapon(0f, 0f, 1, 17, 0.6f, 150f, [], 1f, 1f, 1f, 1f,
            delivery: ProjectileDelivery, blueprint: "ebps\\projectiles\\grenade")), notes, Blueprints(("ebps\\projectiles\\grenade", Grenade(130f))))!;
        var plasma = WeaponTuning.Read("plasma", BafDocument.Read(SyntheticBafWriter.RangedWeapon(30f, 0.267f, 125, 0, 1f, 250f, [], 1f, 1f, 1f, 1f,
            charge: PlasmaCharge)), notes, Blueprints(("ebps\\projectiles\\alt", Grenade(90f))))!;
        ClassLoadout[] classes = [new("c", 0, -1, [])];
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [launcher], classes));   // no direct damage, no cooldown: fine for a projectile
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [plasma], classes));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [launcher with { Projectile = null }], classes));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [launcher with { Projectile = launcher.Projectile! with { Speed = float.NaN } }], classes));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [launcher with { Delivery = DeliveryKind.Raycast }], classes));   // a ray needs damage
        var stages = plasma.Charge!.Stages.ToArray();
        stages[1] = stages[1] with { MinTime = -1f };
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [plasma with { Charge = plasma.Charge with { Stages = stages } }], classes));
    }

    static byte[] HashExtras(WeaponSpec w)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) w.WriteHashExtras(writer);
        return stream.ToArray();
    }

    [Fact]
    public void Hash_extras_cover_delivery_projectile_and_charge()
    {
        var notes = new List<string>();
        var plasma = WeaponTuning.Read("plasma", BafDocument.Read(SyntheticBafWriter.RangedWeapon(30f, 0.267f, 125, 0, 1f, 250f, [], 1f, 1f, 1f, 1f,
            charge: PlasmaCharge)), notes, Blueprints(("ebps\\projectiles\\alt", Grenade(90f))))!;
        var again = WeaponTuning.Read("plasma", BafDocument.Read(SyntheticBafWriter.RangedWeapon(30f, 0.267f, 125, 0, 1f, 250f, [], 1f, 1f, 1f, 1f,
            charge: PlasmaCharge)), notes, Blueprints(("ebps\\projectiles\\alt", Grenade(90f))))!;
        Assert.Equal(HashExtras(plasma), HashExtras(again));   // control: same data, same bytes
        var shot = plasma.Charge!.Stages[1].Shot!;
        var stages = plasma.Charge.Stages.ToArray();
        stages[1] = stages[1] with { Shot = shot with { Projectile = shot.Projectile! with { Splash = shot.Projectile.Splash! with { Radius = 8f } } } };
        Assert.NotEqual(HashExtras(plasma), HashExtras(plasma with { Charge = plasma.Charge with { Stages = stages } }));
        Assert.NotEqual(HashExtras(plasma), HashExtras(plasma with { Delivery = DeliveryKind.Projectile }));
        Assert.NotEqual(HashExtras(plasma), HashExtras(plasma with { Charge = null }));
    }

    static readonly BehaviorGraph KnifeGraph = new("MP_SM_MEL_CombatKnife", [],
    [
        .. new[] { "CK 1 Sequence", "CK 2 Sequence", "CK 3 Sequence" }.Select(n => new BehaviorSequencer(n, ["A_3.5m"],
        [
            new BehaviorTag("Event", 0.2f, 0.2f, "ACTN_Hit_Targets_Multiplayer", null),
            new BehaviorTag("Branch", 0.3f, 0.5f, "BRANCH_Melee_Combo", null),
        ], [1f])),
    ]);

    static BafDocument Knife() => BafDocument.Read(SyntheticBafWriter.Build(("common_data", new (string, object)[] { ("slot", "melee") }),
        ("ability_data", new (string, object)[] { ("ability_definitions",
            new[] { "ability_melee_ck1", "ability_melee_ck2", "ability_melee_ck3" }.Select(a => (a, (object)new (string, object)[]
            {
                ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { ("do_damage", new (string, object)[] { ("damage_general", 40f) }) }) }) }),
            })).ToArray()) })));

    [Fact]
    public void Default_loadouts_take_base_wargear_then_the_first_supported_choice_per_slot()
    {
        var classes = new[]
        {
            new MpClass("sm - tactical", "tactical", "space_marine", ["sm_mp_abil_sprint", "sm_mp_melee_combat_knife", "sm_mp_pistol_bolt_pistol"],
                ["sm_mp_eq_frag_grenade", "sm_mp_ranged_grenade_launcher", "sm_mp_ranged_bolter", "sm_mp_ranged_plasma_gun"], [], null),
            new MpClass("sm - assault marine", "assault", "space_marine", ["sm_mp_abil_jumppack"],
                ["sm_mp_pistol_bolt_pistol", "sm_mp_melee_lightning_claw", "sm_mp_melee_combat_knife"], [], null),
        };
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_melee_combat_knife"] = Knife(),
            ["sm_mp_pistol_bolt_pistol"] = Gun("pistol", "wargear\\trigger_policy\\semi_auto", damage: 40f),
            ["sm_mp_ranged_grenade_launcher"] = Gun(delivery: "_base_templates\\ranged_delivery_types\\projectile"),
            ["sm_mp_ranged_bolter"] = Gun(),
            ["sm_mp_ranged_plasma_gun"] = Gun(damage: 30f),
            ["sm_mp_melee_lightning_claw"] = BafDocument.Read(SyntheticBafWriter.Build(("common_data", new (string, object)[] { ("slot", "melee") }))),
        };
        var notes = new List<string>();
        var result = MpClasses.Build(classes, wargear, [KnifeGraph], _ => 1.5f, notes);

        var tactical = result.Loadouts[0];
        Assert.Equal("sm_mp_ranged_bolter", result.Weapons[tactical.Primary].Name);   // the launcher (projectile) is skipped
        Assert.Equal("sm_mp_pistol_bolt_pistol", result.Weapons[tactical.Pistol].Name);
        Assert.Equal(3, tactical.Melee.Count);
        Assert.Equal(40f, tactical.Melee[0].Damage);
        Assert.Equal(["A_3.5m", "A_3.5m", "A_3.5m"], result.MeleeAnimations[0]);

        var assault = result.Loadouts[1];
        Assert.Equal(-1, assault.Primary);
        Assert.Equal(tactical.Pistol, assault.Pistol);   // the same weapon is read once
        Assert.Equal(3, assault.Melee.Count);            // the (fictional) lightning claw is not supported; the knife after it is
        Assert.Contains(notes, n => n.Contains("lightning_claw") && n.Contains("not supported"));
        Assert.Contains(notes, n => n.Contains("assault") && n.Contains("no supported primary"));
        // Every supported choice is offered (the default first); nothing is read twice.
        Assert.Equal(["sm_mp_ranged_bolter", "sm_mp_ranged_plasma_gun"], tactical.PrimaryChoices.Select(i => result.Weapons[i].Name));
        Assert.Equal([tactical.Pistol], tactical.PistolChoices);
        Assert.Equal(["sm_mp_melee_combat_knife"], assault.MeleeChoices.Select(c => c.Wargear));
        Assert.Empty(assault.PrimaryChoices);
        Assert.Equal(3, result.Weapons.Count);           // bolter, pistol, plasma gun
    }

    [Fact]
    public void Projectile_weapons_are_offered_only_when_asked_and_grenades_are_read()
    {
        var classes = new[]
        {
            new MpClass("sm - tactical", "tactical", "space_marine", [],
                ["sm_mp_eq_frag_grenade", "sm_mp_ranged_grenade_launcher", "sm_mp_ranged_bolter"], [], null),
        };
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_eq_frag_grenade"] = BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\projectiles\\frag", 2, 1, 1f, 10f)),
            ["sm_mp_ranged_grenade_launcher"] = BafDocument.Read(SyntheticBafWriter.RangedWeapon(10f, 0f, 1, 17, 0.6f, 150f, [], 1f, 1f, 1f, 1f,
                delivery: ProjectileDelivery, blueprint: "ebps\\projectiles\\grenade", maxActive: 3)),
            ["sm_mp_ranged_bolter"] = Gun(),
        };
        var blueprints = Blueprints(("ebps\\projectiles\\grenade", Grenade(130f)), ("ebps\\projectiles\\frag", Grenade(47.5f)));
        var notes = new List<string>();
        var gated = MpClasses.Build(classes, wargear, [], _ => 1f, notes, blueprints, offerProjectileWeapons: false);
        Assert.Equal(["sm_mp_ranged_bolter"], gated.Loadouts[0].PrimaryChoices.Select(i => gated.Weapons[i].Name));
        Assert.Equal(["sm_mp_ranged_bolter"], gated.Weapons.Select(w => w.Name));   // not in the config either
        Assert.Contains(notes, n => n.Contains("grenade_launcher") && n.Contains("not offered"));
        Assert.Equal(["sm_mp_eq_frag_grenade"], gated.Thrown.Select(t => t.Name));

        var offered = MpClasses.Build(classes, wargear, [], _ => 1f, [], blueprints, offerProjectileWeapons: true);
        Assert.Equal(["sm_mp_ranged_grenade_launcher", "sm_mp_ranged_bolter"], offered.Loadouts[0].PrimaryChoices.Select(i => offered.Weapons[i].Name));
        Assert.Equal(DeliveryKind.Projectile, offered.Weapons[offered.Loadouts[0].Primary].Delivery);
    }

    [Fact]
    public void Class_table_is_read_from_the_global_file()
    {
        var global = BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[] { ("multiplayer_settings", new (string, object)[] {
            ("ingame_progression", new (string, object)[] { ("classes", new (string, object)[]
            {
                ("sm - tactical", new (string, object)[]
                {
                    ("mp_class", "tactical"), ("race", "space_marine"),
                    ("base_wargear", new (string, object)[] { ("wargear", "wargear\\wargear\\multiplayer\\race_marine\\weapons\\sm_mp_melee_combat_knife") }),
                    ("startup_wargear", new (string, object)[] { ("wargear", "wargear\\wargear\\multiplayer\\race_marine\\weapons\\sm_mp_ranged_bolter") }),
                    ("class_perks", new (string, object)[] { ("wargear", "wargear\\wargear\\multiplayer\\common\\perks\\mp_perk_a") }),
                    ("default_perk", "wargear\\wargear\\multiplayer\\common\\perks\\mp_perk_a"),
                }),
            }) }) }) })));
        var c = Assert.Single(MpClasses.ReadClasses(global));
        Assert.Equal(("sm - tactical", "tactical", "space_marine"), (c.Key, c.Role, c.Race));
        Assert.Equal(["sm_mp_melee_combat_knife"], c.BaseWargear);
        Assert.Equal(["sm_mp_ranged_bolter"], c.StartupWargear);
        Assert.Equal("mp_perk_a", c.DefaultPerk);
    }
}
