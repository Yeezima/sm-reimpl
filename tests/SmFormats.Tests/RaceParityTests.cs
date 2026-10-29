using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// Weapons task A (owner's knowledge, 2026-10-28): the Chaos weapons are identical to the loyalists' in stats and behaviour; only the models, effects and
/// sounds differ. Synthetic wargear shaped like the install's (a fake sm / csm pair that differs in damage, cooldown, fire delay, projectile and grenade values);
/// no original bytes. The install-wide check (every pair, 0 differences after) is <c>smtool race-parity auto</c>.
/// </summary>
public class RaceParityTests
{
    const string ProjectileDelivery = "_base_templates\\ranged_delivery_types\\projectile";

    static BafDocument Rifle(float damage, float cooldown, int clip, int reserve) =>
        BafDocument.Read(SyntheticBafWriter.RangedWeapon(damage, cooldown, clip, reserve, 1.3f, 250f, [(0f, 1f), (45f, 1f), (70f, 0.8f)],
            head: 2.55f, body: 1f, shieldHead: 0.5f, shieldBody: 0.505f));

    static BafDocument Plasma(string alt, float fireDelay, float cooldown) =>
        BafDocument.Read(SyntheticBafWriter.RangedWeapon(35f, 0.233f, 400, 0, 1f, 250f, [], 1f, 1f, 0.7f, 0.7f,
            charge: new ChargeFixture(MinTime: 1.1f, HeatPerSecond: 28f, HeatPerShot: 40f, AmmoCost: 15, Range: 250f, Cooldown: cooldown,
                Delivery: ProjectileDelivery, Blueprint: alt, Damage: 0f, FireDelay: fireDelay)));

    static BafDocument Knife(float damage) => BafDocument.Read(SyntheticBafWriter.Build(("common_data", new (string, object)[] { ("slot", "melee") }),
        ("ability_data", new (string, object)[] { ("ability_definitions",
            new[] { "ability_melee_ck1", "ability_melee_ck2", "ability_melee_ck3" }.Select(a => (a, (object)new (string, object)[]
            {
                ("primary_target_actions", new (string, object)[] { ("hit_sim", new (string, object)[] { ("actions", new (string, object)[] { ("do_damage", new (string, object)[] { ("damage_general", damage) }) }) }) }),
            })).ToArray()) })));

    static readonly BehaviorGraph KnifeGraph = new("MP_SM_MEL_CombatKnife", [],
    [
        .. new[] { "CK 1 Sequence", "CK 2 Sequence", "CK 3 Sequence" }.Select(n => new BehaviorSequencer(n, ["A_3.5m"],
        [
            new BehaviorTag("Event", 0.2f, 0.2f, "ACTN_Hit_Targets_Multiplayer", null),
            new BehaviorTag("Branch", 0.3f, 0.5f, "BRANCH_Melee_Combo", null),
        ], [1f])),
    ]);

    static BafDocument Blueprint(float speed, float splash) => BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(speed, 0f, 0.25f, 6f, splash, 0f,
        [(0f, 1f), (4f, 1f), (6f, 0.05f)], [(0f, 0f), (5f, 0f)], sticky: true, detonateOnCollision: false, bounces: 1));

    /// <summary>The fake pair: the Chaos files differ from the loyalists' in numbers the owner says they must not (names and blueprints differ, as in the install).</summary>
    static MpClasses Build(IEnumerable<string>? chaosStartup = null, bool withLoyalPlasma = true, List<string>? notes = null)
    {
        var classes = new[]
        {
            new MpClass("sm - tactical", "tactical", "space_marine", ["sm_mp_melee_combat_knife", "sm_mp_pistol_bolt_pistol"],
                ["sm_mp_eq_frag_grenade", "sm_mp_ranged_bolter", .. withLoyalPlasma ? new[] { "sm_mp_ranged_plasma_gun" } : []], [], null),
            new MpClass("csm - tactical", "tactical", "chaos", ["csm_mp_melee_combat_knife", "csm_mp_pistol_bolt_pistol"],
                (chaosStartup ?? ["csm_mp_eq_frag_grenade", "csm_mp_ranged_plasma_gun", "csm_mp_ranged_bolter"]).ToList(), [], null),
        };
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_melee_combat_knife"] = Knife(40f), ["csm_mp_melee_combat_knife"] = Knife(25f),
            ["sm_mp_pistol_bolt_pistol"] = BafDocument.Read(SyntheticBafWriter.RangedWeapon(40f, 0.3f, 12, 96, 1.5f, 120f, [(0f, 1f)], 2f, 1f, 0.7f, 0.7f, slot: "pistol")),
            ["csm_mp_pistol_bolt_pistol"] = BafDocument.Read(SyntheticBafWriter.RangedWeapon(33f, 0.4f, 10, 80, 1.9f, 100f, [(0f, 1f)], 2f, 1f, 0.7f, 0.7f, slot: "pistol")),
            ["sm_mp_ranged_bolter"] = Rifle(22f, 0.133f, 30, 270), ["csm_mp_ranged_bolter"] = Rifle(30f, 0.2f, 20, 200),
            ["sm_mp_ranged_plasma_gun"] = Plasma("ebps\\projectiles\\plasma_alt", 0.1f, 0.1f),
            ["csm_mp_ranged_plasma_gun"] = Plasma("ebps\\projectiles\\plasma_alt_chaos", 0.5f, 0.3f),
            ["sm_mp_eq_frag_grenade"] = BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\projectiles\\frag", 4, 2, 1f, 10f)),
            ["csm_mp_eq_frag_grenade"] = BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\projectiles\\frag_chaos", 2, 1, 2f, 12f)),
        };
        var blueprints = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["ebps\\projectiles\\plasma_alt"] = Blueprint(31f, 150f), ["ebps\\projectiles\\plasma_alt_chaos"] = Blueprint(25f, 120f),
            ["ebps\\projectiles\\frag"] = Blueprint(20f, 75f), ["ebps\\projectiles\\frag_chaos"] = Blueprint(15f, 60f),
        };
        return MpClasses.Build(classes, wargear, [KnifeGraph], _ => 1.5f, notes ?? [], p => blueprints.GetValueOrDefault(p));
    }

    static T Item<T>(MpClasses m, string name) where T : class => (T)RaceParity.Items(m)[name];

    [Fact]
    public void The_fake_pair_differs_before_parity()
    {
        var data = Build();
        // Control: the fixture really differs (otherwise the tests below prove nothing).
        Assert.NotEmpty(RaceParity.Differences(Item<WeaponSpec>(data, "csm_mp_ranged_bolter"), Item<WeaponSpec>(data, "sm_mp_ranged_bolter")));
        Assert.NotEmpty(RaceParity.Differences(Item<WeaponSpec>(data, "csm_mp_ranged_plasma_gun"), Item<WeaponSpec>(data, "sm_mp_ranged_plasma_gun")));
        Assert.NotEmpty(RaceParity.Differences(Item<ThrownSpec>(data, "csm_mp_eq_frag_grenade"), Item<ThrownSpec>(data, "sm_mp_eq_frag_grenade")));
        Assert.NotEmpty(RaceParity.Differences(Item<MeleeCombo>(data, "csm_mp_melee_combat_knife"), Item<MeleeCombo>(data, "sm_mp_melee_combat_knife")));
        var plasma = Item<WeaponSpec>(data, "csm_mp_ranged_plasma_gun");
        Assert.Equal(0.5f, plasma.Charge!.Stages.Last(s => s.Shot is not null).Shot!.FireDelay);   // the delay is read (and simulated) behaviour
    }

    [Fact]
    public void Chaos_numbers_follow_the_loyalist_twins_while_names_and_blueprints_stay()
    {
        var data = Build();
        var notes = new List<string>();
        var after = RaceParity.Apply(data, notes);
        foreach (var (chaos, loyal) in new[] { ("csm_mp_ranged_bolter", "sm_mp_ranged_bolter"), ("csm_mp_ranged_plasma_gun", "sm_mp_ranged_plasma_gun"), ("csm_mp_pistol_bolt_pistol", "sm_mp_pistol_bolt_pistol") })
            Assert.Empty(RaceParity.Differences(Item<WeaponSpec>(after, chaos), Item<WeaponSpec>(after, loyal)));
        Assert.Empty(RaceParity.Differences(Item<ThrownSpec>(after, "csm_mp_eq_frag_grenade"), Item<ThrownSpec>(after, "sm_mp_eq_frag_grenade")));
        Assert.Empty(RaceParity.Differences(Item<MeleeCombo>(after, "csm_mp_melee_combat_knife"), Item<MeleeCombo>(after, "sm_mp_melee_combat_knife")));

        // The numbers are the loyalists': damage, cooldown, clip, the fire delay of the charged shot, the projectile's speed and splash, the grenade count.
        var bolter = Item<WeaponSpec>(after, "csm_mp_ranged_bolter");
        Assert.Equal((22f, 0.133f, 30, 270), (bolter.Damage, bolter.ShotCooldown, bolter.ClipSize, bolter.ReserveSize));
        var plasma = Item<WeaponSpec>(after, "csm_mp_ranged_plasma_gun");
        var shot = plasma.Charge!.Stages.Last(s => s.Shot is not null).Shot!;
        Assert.Equal((0.1f, 0.1f, 31f), (shot.FireDelay, shot.ShotCooldown, shot.Projectile!.Speed));
        Assert.Equal(150f, shot.Projectile.Splash!.Damage.Damage);
        var frag = Item<ThrownSpec>(after, "csm_mp_eq_frag_grenade");
        Assert.Equal((4, 2, 1f, 20f), (frag.Count, frag.InitialCount, frag.Cooldown, frag.Projectile.Speed));   // as the loyalist grenade
        Assert.Equal(40f, Item<MeleeCombo>(after, "csm_mp_melee_combat_knife").Steps[0].Damage);

        // What makes them Chaos stays: the name, the combo's wargear and every projectile blueprint (the key of models, effects and sounds).
        Assert.Equal("csm_mp_ranged_bolter", bolter.Name);
        Assert.Equal("csm_mp_ranged_plasma_gun", plasma.Name);
        Assert.Equal("ebps\\projectiles\\plasma_alt_chaos", shot.Projectile.Blueprint);
        Assert.Equal("ebps\\projectiles\\frag_chaos", frag.Projectile.Blueprint);
        Assert.Equal("csm_mp_eq_frag_grenade", frag.Name);
        Assert.Equal("csm_mp_melee_combat_knife", Item<MeleeCombo>(after, "csm_mp_melee_combat_knife").Wargear);
        // The loyalist side is untouched, index for index.
        Assert.Equal(data.Weapons.Select(w => w.Name), after.Weapons.Select(w => w.Name));
        Assert.Equal(Item<WeaponSpec>(data, "sm_mp_ranged_bolter"), Item<WeaponSpec>(after, "sm_mp_ranged_bolter"));
        Assert.Equal("ebps\\projectiles\\plasma_alt", Item<WeaponSpec>(after, "sm_mp_ranged_plasma_gun").Charge!.Stages.Last(s => s.Shot is not null).Shot!.Projectile!.Blueprint);
        Assert.Empty(notes);
    }

    [Fact]
    public void A_Chaos_class_default_is_the_twin_of_the_loyalist_default()
    {
        var data = Build();   // the Chaos startup list names the plasma gun before the bolter
        var before = data.Loadouts[1];
        Assert.Equal("csm_mp_ranged_plasma_gun", data.Weapons[before.Primary].Name);
        var after = RaceParity.Apply(data, []);
        var chaos = after.Loadouts[1];
        Assert.Equal("sm_mp_ranged_bolter", after.Weapons[after.Loadouts[0].Primary].Name);   // the loyalist default
        Assert.Equal("csm_mp_ranged_bolter", after.Weapons[chaos.Primary].Name);               // its twin, not the plasma gun
        Assert.Equal(before.PrimaryChoices.Count, chaos.PrimaryChoices.Count);                 // both stay choices
        Assert.Contains(chaos.PrimaryChoices, i => after.Weapons[i].Name == "csm_mp_ranged_plasma_gun");
        Assert.Equal("csm_mp_pistol_bolt_pistol", after.Weapons[chaos.Pistol].Name);
        Assert.Equal(40f, chaos.Melee[0].Damage);                                              // the loadout's default combo steps follow the adopted combo
        Assert.Equal(after.Loadouts[0].Melee.Select(m => m.Damage), chaos.Melee.Select(m => m.Damage));
        Assert.Equal(data.Loadouts[0], after.Loadouts[0]);                                     // the loyalist class is untouched
    }

    [Fact]
    public void Parity_changes_the_config_hash()
    {
        var data = Build();
        var after = RaceParity.Apply(data, []);
        static ulong Hash(MpClasses m) => new SimConfig { Weapons = m.Weapons, Classes = m.Loadouts, Thrown = m.Thrown }.Hash();
        Assert.NotEqual(Hash(data), Hash(after));
        Assert.Equal(Hash(after), Hash(RaceParity.Apply(after, [])));   // idempotent: a second pass changes nothing
    }

    [Fact]
    public void An_item_without_a_twin_keeps_its_data_with_a_note()
    {
        var notes = new List<string>();
        // The Chaos plasma gun's twin is not offered by any loyalist class: it stays as read, and says so.
        var data = Build(withLoyalPlasma: false);
        var after = RaceParity.Apply(data, notes);
        Assert.Equal(Item<WeaponSpec>(data, "csm_mp_ranged_plasma_gun"), Item<WeaponSpec>(after, "csm_mp_ranged_plasma_gun"));
        Assert.Contains(notes, n => n.Contains("csm_mp_ranged_plasma_gun") && n.Contains("sm_mp_ranged_plasma_gun") && n.Contains("data stay"));
        // A Chaos file the table does not know at all.
        var custom = RaceParity.Apply(data with { Weapons = [.. data.Weapons, data.Weapons[^1] with { Name = "csm_mp_ranged_unheard_of" }] }, notes);
        Assert.Contains(notes, n => n.Contains("csm_mp_ranged_unheard_of") && n.Contains("no loyalist twin"));
        Assert.Equal("csm_mp_ranged_unheard_of", custom.Weapons[^1].Name);
    }

    static WeaponSpec Spec(string name, float damage, string blueprint = "bp") => new(name, WeaponSlot.Ranged, TriggerKind.FullAuto, damage, 0.2f, 30, 100, 1.3f, 100f, 40f, 70f, 0.8f, 2f, 1f, 1f, 1f)
    {
        Delivery = DeliveryKind.Projectile,
        Projectile = new ProjectileSpec(blueprint, 30f, 5f, "raycast", 0.2f, [], true, false, false, 0, 0f, 0f, 0, null, null),
    };

    [Fact]
    public void Perk_variants_are_copied_with_the_Chaos_names_and_blueprints()
    {
        var loyal = Spec("sm_mp_ranged_bolter", 20f, "sm_bp") with
        {
            Variants = [new UpgradeVariant<WeaponSpec>(["mp_perk_a"], Spec("sm_mp_ranged_bolter", 30f, "sm_bp_a")), new UpgradeVariant<WeaponSpec>(["mp_perk_b", "mp_perk_c"], Spec("sm_mp_ranged_bolter", 35f, "sm_bp_b"))],
        };
        var chaos = Spec("csm_mp_ranged_bolter", 25f, "csm_bp") with
        {
            Variants = [new UpgradeVariant<WeaponSpec>(["mp_perk_a"], Spec("csm_mp_ranged_bolter", 99f, "csm_bp_a"))],   // one variant only, with another blueprint
        };
        var m = new MpClasses([], [loyal, chaos], [], [], []);
        var after = RaceParity.Apply(m, []);
        var c = after.Weapons[1];
        Assert.Empty(RaceParity.Differences(c, after.Weapons[0]));
        Assert.Equal(2, c.Variants.Count);                                     // the loyalist's variants, including the one Chaos lacks
        Assert.Equal(30f, c.Variants[0].Spec.Damage);                          // number: loyalist
        Assert.Equal("csm_mp_ranged_bolter", c.Variants[0].Spec.Name);         // name: Chaos
        Assert.Equal("csm_bp_a", c.Variants[0].Spec.Projectile!.Blueprint);    // blueprint: the Chaos variant's
        Assert.Equal("csm_bp", c.Variants[1].Spec.Projectile!.Blueprint);      // no Chaos variant for the pair: the Chaos base blueprint
        Assert.Equal(["mp_perk_b", "mp_perk_c"], c.Variants[1].Upgrades);
    }

    [Fact]
    public void Jump_packs_and_melee_combos_take_the_loyalist_values_and_variants()
    {
        JumpPackSpec Pack(string name, float power, float pound) => new(name, [new(0f, 1f)], [new(0f, 10f)], power, 5f, 30f, 20f, 0.2f, 40f, 6f, 0.5f, 0.1f) { PoundSpeed = pound };
        var loyalPack = Pack("sm_mp_abil_jumppack", 110f, 30f) with { Variants = [new UpgradeVariant<JumpPackSpec>(["mp_perk_x"], Pack("sm_mp_abil_jumppack", 150f, 30f))] };
        var chaosPack = Pack("csm_mp_abil_jumppack", 100f, 25f);
        var step = new MeleeStep { Damage = 40f };
        var loyalCombo = new MeleeCombo("sm_mp_melee_chainsword", [step]) { Variants = [new UpgradeVariant<MeleeCombo>(["mp_perk_y"], new MeleeCombo("sm_mp_melee_chainsword", [step with { Damage = 60f }]))] };
        var chaosCombo = new MeleeCombo("csm_mp_melee_chainsword", [step with { Damage = 20f }]);
        var loadouts = new[]
        {
            new ClassLoadout("sm - assault marine", -1, -1, loyalCombo.Steps) { MeleeChoices = [loyalCombo], Side = Team.SpaceMarines, Role = "assault", JumpPack = 0 },
            new ClassLoadout("csm - raptor", -1, -1, chaosCombo.Steps) { MeleeChoices = [chaosCombo], Side = Team.Chaos, Role = "assault", JumpPack = 1 },
        };
        var m = new MpClasses([], [], loadouts, [["a"], ["b"]], []) { JumpPacks = [loyalPack, chaosPack] };
        var after = RaceParity.Apply(m, []);
        Assert.Empty(RaceParity.Differences(after.JumpPacks[1], after.JumpPacks[0]));
        Assert.Equal("csm_mp_abil_jumppack", after.JumpPacks[1].Name);
        Assert.Equal(110f, after.JumpPacks[1].MaxPower);
        Assert.Equal(150f, after.JumpPacks[1].Variants[0].Spec.MaxPower);
        Assert.Equal("csm_mp_abil_jumppack", after.JumpPacks[1].Variants[0].Spec.Name);
        var combo = after.Loadouts[1].MeleeChoices[0];
        Assert.Empty(RaceParity.Differences(combo, after.Loadouts[0].MeleeChoices[0]));
        Assert.Equal(("csm_mp_melee_chainsword", 40f), (combo.Wargear, after.Loadouts[1].Melee[0].Damage));
        Assert.Equal(60f, combo.Variants[0].Spec.Steps[0].Damage);
        Assert.Equal("csm_mp_melee_chainsword", combo.Variants[0].Spec.Wargear);
    }

    [Fact]
    public void The_owners_bolter_balance_applies_after_parity_to_both_races_alike()
    {
        var data = Build();
        var after = MpClasses.WithOwnerBalance(RaceParity.Apply(data, []), 1f / 30f, 100f, 100f);
        Assert.NotEmpty(after.OwnerRows);   // the rule ran (the fixture's bolter is below its bolt pistol's DPS against a shield)
        Assert.Empty(RaceParity.Differences(Item<WeaponSpec>(after, "csm_mp_ranged_bolter"), Item<WeaponSpec>(after, "sm_mp_ranged_bolter")));
        Assert.Equal(Item<WeaponSpec>(after, "sm_mp_ranged_bolter").Damage, Item<WeaponSpec>(after, "csm_mp_ranged_bolter").Damage);
        // Control: on the raw data the same rule leaves the two bolters different.
        var raw = MpClasses.WithOwnerBalance(data, 1f / 30f, 100f, 100f);
        Assert.NotEmpty(RaceParity.Differences(Item<WeaponSpec>(raw, "csm_mp_ranged_bolter"), Item<WeaponSpec>(raw, "sm_mp_ranged_bolter")));
    }

    static ThrownSpec Frag(string name) => new(name, 2, 1, 1f, 10f, Spec("x", 1f, name + "_bp").Projectile!);

    static MeleeCombo Combo(string wargear, float damage) => new(wargear, [new MeleeStep { Damage = damage }]);

    [Fact]
    public void A_Chaos_melee_and_equipment_default_that_is_not_the_twin_is_moved_first()
    {
        var sm = new ClassLoadout("sm - tactical", -1, -1, [new MeleeStep { Damage = 40f }])
        {
            MeleeChoices = [Combo("sm_mp_melee_combat_knife", 40f), Combo("sm_mp_melee_chainsword", 30f)], EquipmentChoices = [0, 1], Side = Team.SpaceMarines, Role = "tactical",
        };
        var chaos = new ClassLoadout("csm - tactical", -1, -1, [new MeleeStep { Damage = 30f }])
        {
            MeleeChoices = [Combo("csm_mp_melee_chainsword", 5f), Combo("csm_mp_melee_combat_knife", 5f)], EquipmentChoices = [3, 2], Side = Team.Chaos, Role = "tactical",
        };
        var m = new MpClasses([], [], [sm, chaos], [["a"], ["b"]], []) { Thrown = [Frag("sm_mp_eq_frag_grenade"), Frag("sm_mp_eq_krak_grenade"), Frag("csm_mp_eq_frag_grenade"), Frag("csm_mp_eq_krak_grenade")] };
        var after = RaceParity.Apply(m, []);
        var c = after.Loadouts[1];
        Assert.Equal(["csm_mp_melee_combat_knife", "csm_mp_melee_chainsword"], c.MeleeChoices.Select(x => x.Wargear));   // the twin of the loyalist default first
        Assert.Equal(40f, c.Melee[0].Damage);                                                                          // the default combo's steps are the knife's, adopted
        Assert.Equal(30f, c.MeleeChoices[1].Steps[0].Damage);
        Assert.Equal([2, 3], c.EquipmentChoices);                                                                      // frag first, as the loyalists'
        Assert.Equal(m.Loadouts[0], after.Loadouts[0]);                                                                // the loyalist class keeps its order
        // A twin that was not read leaves the order and says so.
        var notes = new List<string>();
        var missing = RaceParity.Apply(m with { Loadouts = [sm, chaos with { MeleeChoices = [Combo("csm_mp_melee_chainsword", 5f)] }] }, notes);
        Assert.Equal(["csm_mp_melee_chainsword"], missing.Loadouts[1].MeleeChoices.Select(x => x.Wargear));
        Assert.Contains(notes, n => n.Contains("csm_mp_melee_combat_knife") && n.Contains("not read"));
    }

    [Fact]
    public void The_default_weapon_twin_rules_never_grant_a_weapon_and_say_when_the_twin_is_unusable()
    {
        WeaponSpec[] weapons = [Spec("sm_mp_ranged_bolter", 22f), Spec("csm_mp_ranged_bolter", 30f), Spec("csm_mp_ranged_plasma_gun", 35f)];
        ClassLoadout Loyal() => new("sm - tactical", 0, -1, []) { PrimaryChoices = [0], Side = Team.SpaceMarines, Role = "tactical" };
        ClassLoadout Chaos(int primary, params int[] choices) => new("csm - tactical", primary, -1, []) { PrimaryChoices = choices, Side = Team.Chaos, Role = "tactical" };
        MpClasses Of(ClassLoadout chaos, WeaponSpec[]? w = null) => new([], w ?? weapons, [Loyal(), chaos], [[], []], []);

        // Granting: a Chaos class without a primary (the Raptor) stays without one although the loyalist class of its role has one.
        Assert.Equal(-1, RaceParity.Apply(Of(Chaos(-1)), []).Loadouts[1].Primary);
        // The twin is a choice: the default becomes the bolter twin.
        Assert.Equal(1, RaceParity.Apply(Of(Chaos(2, 1, 2)), []).Loadouts[1].Primary);
        // Not a choice of the Chaos class: the default stays, with a note.
        var notes = new List<string>();
        Assert.Equal(2, RaceParity.Apply(Of(Chaos(2, 2)), notes).Loadouts[1].Primary);
        Assert.Contains(notes, n => n.Contains("csm - tactical") && n.Contains("csm_mp_ranged_bolter") && n.Contains("not a choice"));
        // Not read at all (the Chaos bolter is not in the weapon list): the default stays, with a note.
        notes = [];
        WeaponSpec[] withoutTwin = [weapons[0], weapons[2]];
        Assert.Equal(1, RaceParity.Apply(Of(Chaos(1, 1), withoutTwin), notes).Loadouts[1].Primary);   // index 1 is the plasma gun here
        Assert.Contains(notes, n => n.Contains("csm_mp_ranged_bolter") && n.Contains("not read"));
    }

    [Fact]
    public void A_class_without_a_role_keeps_its_choices_as_built()
    {
        WeaponSpec[] weapons = [Spec("sm_mp_ranged_bolter", 22f), Spec("csm_mp_ranged_bolter", 30f), Spec("csm_mp_ranged_plasma_gun", 35f)];
        var loyal = new ClassLoadout("sm - x", 0, -1, []) { PrimaryChoices = [0], Side = Team.SpaceMarines, Role = "" };
        var chaos = new ClassLoadout("csm - x", 2, -1, []) { PrimaryChoices = [1, 2], Side = Team.Chaos, Role = "" };
        var after = RaceParity.Apply(new MpClasses([], weapons, [loyal, chaos], [[], []], []), []);
        Assert.Equal(2, after.Loadouts[1].Primary);   // not moved to the bolter twin: two roleless classes are not counterparts
    }

    [Fact]
    public void The_pair_table_is_consistent()
    {
        Assert.All(RaceParity.Pairs, p =>
        {
            Assert.StartsWith("csm_mp_", p.Chaos);
            Assert.StartsWith("sm_mp_", p.Loyalist);
            Assert.Equal(p.Loyalist, RaceParity.LoyalistTwin(p.Chaos));
            Assert.Equal(p.Chaos, RaceParity.ChaosTwin(p.Loyalist));
        });
        Assert.Equal(RaceParity.Pairs.Count, RaceParity.Pairs.Select(p => p.Chaos).Distinct().Count());
        Assert.Equal(RaceParity.Pairs.Count, RaceParity.Pairs.Select(p => p.Loyalist).Distinct().Count());
        Assert.DoesNotContain(RaceParity.NoTwins, n => RaceParity.LoyalistTwin(n.Chaos) is not null);
        // The two renamed melee weapons (checked against the install: same slot and ability layout).
        Assert.Equal("sm_mp_melee_power_axe", RaceParity.LoyalistTwin("csm_mp_melee_chain_axe"));
        Assert.Equal("sm_mp_melee_2hand_thunder_hammer", RaceParity.LoyalistTwin("csm_mp_melee_2hand_power_mace"));
        Assert.Null(RaceParity.LoyalistTwin("csm_mp_ranged_lasgun_ai"));
    }

    [Fact]
    public void Chaos_plasma_is_purple_and_the_loyalist_plasma_is_not_recoloured()
    {
        // The Chaos files' own plasma effects are purple already: never recoloured.
        foreach (string own in new[] { "chaos_plasma_gun_muzzle", "chaos_plasma_gun_tracer", "chaos_plasma_gun_trail", "csm_plasma_cannon_primary_trail", "csm_plasma_charging_ready", "chaos_plasma_gun_dmg_body" })
            Assert.Null(PlasmaColour.TintFor("csm_mp_ranged_plasma_gun", own));
        // The loyalist-named plasma effects their containers share (the charged blast, the impacts) are tinted purple when a Chaos plasma weapon plays them.
        foreach (string wargear in new[] { "csm_mp_ranged_plasma_gun", "csm_mp_pistol_plasma_pistol", "csm_mp_exotic_plasma_cannon" })
            foreach (string shared in new[] { "plasma_gun_charged_dmg", "plasma_gun_mp_charged_dmg", "plasma_dmg_body_mp", "plasma_dmg_armor", "plasma_charging_ready" })
                Assert.Equal(PlasmaColour.ChaosPurple, PlasmaColour.TintFor(wargear, shared));
        // The loyalists' plasma stays as in the data (cyan, blue and green keys); other Chaos weapons and non-plasma effects are never tinted.
        Assert.Null(PlasmaColour.TintFor("sm_mp_ranged_plasma_gun", "plasma_gun_charged_dmg"));
        Assert.Null(PlasmaColour.TintFor("csm_mp_ranged_bolter", "plasma_gun_charged_dmg"));
        Assert.Null(PlasmaColour.TintFor("csm_mp_ranged_plasma_gun", "bolter_dmg_body_tough_mp"));
        Assert.Null(PlasmaColour.TintFor("csm_mp_ranged_plasma_gun", "light_led_green"));
        Assert.Null(PlasmaColour.TintFor("csm_mp_pistol_plasma_pistol", "plasma_pistol_charged_pen_metal"));   // metal sparks stay neutral
        // The tint is purple (blue is the smallest of red and blue?): red and blue both above green, blue brightest.
        var (r, g, b) = PlasmaColour.ChaosPurple;
        Assert.True(b >= r && r > g);
        // The placeholder ball: blue-white for the loyalists, purple for Chaos.
        var (lr, lg, lb) = PlasmaColour.BallColour(chaos: false);
        var (cr, cg, cb) = PlasmaColour.BallColour(chaos: true);
        Assert.True(lb > lr && lg > lr);       // blue-white
        Assert.True(cr > cg && cb > cg);       // purple
    }

    [Fact]
    public void Chaos_plasma_effects_of_a_weapon_file_get_the_tint_only_where_the_data_are_loyalist()
    {
        // A Chaos plasma gun file shaped like the install's: its own muzzle and tracer, and a hit container.
        static (string, object)[] Play(string name) =>
        [
            ("$ref", "action_commands\\play_fx"), ("fx_package", "test_fx"), ("fx_name", name), ("scale", 1f), ("length", 1f), ("attach_to_marker", false), ("trigger_point", "self"),
            ("marker", new (string, object)[] { ("marker", "fx_muzzle") }), ("trigger_point_target", ""), ("marker_target", Array.Empty<(string, object)>()), ("rotation_source", "trigger_point"),
            ("trigger_direction", "negative_hit_direction"), ("scale_if_from_player", false), ("on_stop_policy", "stop_emitting"), ("lifetime", 0f), ("apply_only_if_on_player", false),
        ];
        var doc = BafDocument.Read(SyntheticBafWriter.Build(("ability_data", new (string, object)[] { ("ability_definitions", new (string, object)[]
        {
            ("ability_ranged_primary", new (string, object)[] { ("delivery", new (string, object)[]
            {
                ("$ref", "wargear\\ability_components\\delivery\\ranged_delivery"),
                ("fire_fx", new (string, object)[] { ("actions", new (string, object)[] { ("play_fx", Play("chaos_plasma_gun_muzzle")) }) }),
                ("hit_fx", new (string, object)[] { ("actions", new (string, object)[] { ("play_fx", Play("chaos_plasma_gun_tracer")), ("play_fx", Play("plasma_gun_charged_dmg")), ("play_fx", Play("bolter_dmg_husk")) }) }),
                ("max_range", 250f), ("muzzle_marker", "fx_muzzle"),
            }) }),
        }) })));
        var effects = WeaponEffects.Read("csm_mp_ranged_plasma_gun", doc, null, []);
        var played = effects.Bindings.Where(b => b.Play is not null).Select(b => b.Play!.Name).ToList();
        Assert.Equal(["chaos_plasma_gun_muzzle", "chaos_plasma_gun_tracer", "plasma_gun_charged_dmg", "bolter_dmg_husk"], played);
        var tinted = played.Where(n => PlasmaColour.TintFor(effects.Wargear, n) is not null).ToList();
        Assert.Equal(["plasma_gun_charged_dmg"], tinted);
        Assert.All(played, n => Assert.Null(PlasmaColour.TintFor("sm_mp_ranged_plasma_gun", n)));
    }
}
