using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// M43: perk variants read from synthetic wargear and player blueprints (no original bytes). The synthetic fixtures key their
/// upgraded entries by "some_upgrade" (weapons, thrown items) or by the real perk strings (jump packs); a perk file applying
/// the string makes the loader read the variant, whose values must equal the fixture's perk entries while the base stays as is.
/// </summary>
public class PerkVariantTests
{
    static (string, object)[] T(params (string, object)[] items) => items;

    /// <summary>A perk wargear file applying <paramref name="upgrade"/> (the original's layout: lifetime action, apply_upgrade_string).</summary>
    static BafDocument Perk(string upgrade) => BafDocument.Read(SyntheticBafWriter.Build(
        N("common_data", ("slot", "perk"), N("unlock_info", N("level_requirement", ("level_required", 1)))),
        N("action_data", N("lifetime_actions", N("commands", N("apply_upgrade_string", N("strings", ("entry", upgrade))))))));

    static BafDocument Gun(float damage = 22f, (float, float, float, float, float, float, float)? heat = null) =>
        BafDocument.Read(SyntheticBafWriter.RangedWeapon(damage, 0.133f, 30, 270, 1.3f, 250f, [(0f, 1f), (45f, 1f), (70f, 0.8f)],
            head: 2.55f, body: 1f, shieldHead: 0.5f, shieldBody: 0.505f, heat: heat));

    static MpClasses Build(IReadOnlyList<string> perks, Dictionary<string, BafDocument> wargear, IReadOnlyList<string> startup, IReadOnlyList<string>? baseWargear = null,
        IReadOnlyList<BehaviorGraph>? graphs = null, Func<string, BafDocument?>? blueprints = null, List<string>? notes = null)
    {
        var classes = new[] { new MpClass("sm - tactical", "tactical", "space_marine", baseWargear ?? [], startup, perks, perks.Count > 0 ? perks[0] : null) };
        return MpClasses.Build(classes, wargear, graphs ?? [], _ => 1.5f, notes ?? [], blueprints);
    }

    [Fact]
    public void A_weapon_perk_reads_the_entries_that_need_its_string_and_leaves_the_base_as_it_was()
    {
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_ranged_bolter"] = Gun(heat: (3f, 100f, 75f, 20f, 25f, 0.3f, 0f)),
            ["mp_perk_test"] = Perk("some_upgrade"),
            ["mp_perk_other"] = Perk("unused_string"),
        };
        var notes = new List<string>();
        var built = Build(["mp_perk_test", "mp_perk_other"], wargear, ["sm_mp_ranged_bolter"], notes: notes);
        var bolter = Assert.Single(built.Weapons);
        // The base: the fixture's default entries.
        Assert.Equal((22f, 0.133f, 30, 270, 1.3f, 3f), (bolter.Damage, bolter.ShotCooldown, bolter.ClipSize, bolter.ReserveSize, bolter.ReloadTime, bolter.Heat!.PerShot));
        // One variant (a string the file never needs makes none): every "some_upgrade" entry of the fixture.
        var variant = Assert.Single(bolter.Variants);
        Assert.Equal(["some_upgrade"], variant.Upgrades);
        var v = variant.Spec;
        Assert.Equal((44f, 0.133f / 2, 60, 540, 1.3f / 2, 1f), (v.Damage, v.ShotCooldown, v.ClipSize, v.ReserveSize, v.ReloadTime, v.Heat!.PerShot));
        Assert.Empty(v.Variants);
        Assert.Contains(notes, n => n.Contains("perk variants read"));
        // The simulation's pick: the variant with the string granted, the base without.
        Assert.Same(v, bolter.With(new PerkUpgrades("some_upgrade", null)));
        Assert.Same(bolter, bolter.With(new PerkUpgrades("unused_string", null)));
        Assert.Same(bolter, bolter.With(default));

        // Without a class perk applying the string there is no variant (the readers outside a perk read are unchanged).
        var plain = Build([], wargear, ["sm_mp_ranged_bolter"]);
        Assert.Empty(Assert.Single(plain.Weapons).Variants);
        Assert.Equal(Variants.Bytes(bolter with { Variants = [] }), Variants.Bytes(plain.Weapons[0]));
    }

    [Fact]
    public void Thrown_items_and_projectile_weapons_get_the_perk_projectile_and_counts()
    {
        var grenade = BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(47.5f, 0f, 0.25f, 6f, 75f, 0f, [(0f, 1f), (6f, 0.05f)], [], sticky: false, detonateOnCollision: false, bounces: 1));
        var upgraded = BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(47.5f, 0f, 0.25f, 9f, 110f, 0f, [(0f, 1f), (9f, 0.05f)], [], sticky: false, detonateOnCollision: false, bounces: 1));
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["ebps\\projectiles\\frag"] = grenade, ["ebps\\projectiles\\frag_upgraded"] = upgraded,
            ["ebps\\projectiles\\gl"] = grenade, ["ebps\\projectiles\\gl_upgraded"] = upgraded,
        };
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_eq_frag_grenade"] = BafDocument.Read(SyntheticBafWriter.ThrownItem("ebps\\projectiles\\frag", 2, 1, 1f, 10f)),
            ["sm_mp_ranged_grenade_launcher"] = BafDocument.Read(SyntheticBafWriter.RangedWeapon(10f, 0f, 1, 17, 0.6f, 150f, [], 1f, 1f, 1f, 1f,
                delivery: "_base_templates\\ranged_delivery_types\\projectile", blueprint: "ebps\\projectiles\\gl", maxActive: 3)),
            ["mp_perk_test"] = Perk("some_upgrade"),
        };
        var built = Build(["mp_perk_test"], wargear, ["sm_mp_eq_frag_grenade", "sm_mp_ranged_grenade_launcher"], blueprints: docs.GetValueOrDefault);
        var frag = Assert.Single(built.Thrown);
        Assert.Equal((2, 1, 6f), (frag.Count, frag.SpawnCount, frag.Projectile.Splash!.Radius));
        var fragVariant = Assert.Single(frag.Variants).Spec;
        Assert.Equal((4, 2, 9f, 110f), (fragVariant.Count, fragVariant.SpawnCount, fragVariant.Projectile.Splash!.Radius, fragVariant.Projectile.Splash.Damage.Damage));
        Assert.EndsWith("_upgraded", fragVariant.Projectile.Blueprint);
        var launcher = Assert.Single(built.Weapons);
        Assert.Equal(3, launcher.Projectile!.MaxActive);
        Assert.Equal(5, Assert.Single(launcher.Variants).Spec.Projectile!.MaxActive);   // the fixture's perk entry: max active + 2
    }

    [Fact]
    public void Jump_pack_perks_read_the_recharge_the_pound_and_the_take_off_blast()
    {
        var pack = BafDocument.Read(SyntheticBafWriter.JumpPack([(0.1f, 4f), (0.4f, 18f)], [(0f, 10f), (0.4f, 40f)], 100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.1f, 0.05f,
            poundDamage: (25f, 50f), takeOffDamage: (25f, 50f)));
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_abil_jumppack"] = pack,
            ["mp_perk_jump_pack_reduced_cooldown"] = Perk("mp_perk_jump_pack_reduced_cooldown"),
            ["mp_perk_jump_pack_ground_pound_damage"] = Perk("mp_perk_jump_pack_ground_pound_damage"),
            ["mp_perk_jump_pack_take_off_damage"] = Perk("mp_perk_jump_pack_take_off_damage"),
        };
        var built = Build(["mp_perk_jump_pack_reduced_cooldown", "mp_perk_jump_pack_ground_pound_damage", "mp_perk_jump_pack_take_off_damage"], wargear, [], ["sm_mp_abil_jumppack"]);
        var spec = Assert.Single(built.JumpPacks);
        Assert.Equal((10f, 25f, 0), (spec.RechargeRate, spec.Pound!.Damage.Damage, spec.TakeOffBlasts.Count));   // the base: no take-off blast
        JumpPackSpec With(params string[] perks) => spec.With(new PerkUpgrades(perks.ElementAtOrDefault(0), perks.ElementAtOrDefault(1)));
        Assert.Equal((15f, 2f), (With("mp_perk_jump_pack_reduced_cooldown").RechargeRate, With("mp_perk_jump_pack_reduced_cooldown").PoundDelay));
        Assert.Equal(75f, With("mp_perk_jump_pack_ground_pound_damage").Pound!.Damage.Damage);
        var blast = Assert.Single(With("mp_perk_jump_pack_take_off_damage").TakeOffBlasts);
        Assert.Equal((4f, 25f, 50f), (blast.Radius, blast.Damage.Damage, blast.Damage.ShieldDamage));
        // A pair of perks the file has no joint entry for gets both effects (each block picks its own entry).
        var both = With("mp_perk_jump_pack_reduced_cooldown", "mp_perk_jump_pack_ground_pound_damage");
        Assert.Equal((15f, 75f), (both.RechargeRate, both.Pound!.Damage.Damage));
    }

    static readonly BehaviorGraph KnifeGraph = new("MP_SM_MEL_CombatKnife", [],
    [
        .. new[] { "CK 1 Sequence", "CK 2 Sequence", "CK 3 Sequence" }.Select(n => new BehaviorSequencer(n, ["A_3.5m"],
        [
            new BehaviorTag("Event", 0.2f, 0.2f, "ACTN_Hit_Targets_Multiplayer", null),
            new BehaviorTag("Branch", 0.3f, 0.5f, "BRANCH_Melee_Combo", null),
        ], [1f])),
    ]);

    /// <summary>The knife: damage 40 by default and 65 with "u_knife" (one upgrade block), and a heal block needing "u_heal" (no default entry).</summary>
    static BafDocument Knife()
    {
        var damage65 = N("do_damage", ("damage_general", 65f), ("damagetype", "melee"));
        var damage40 = N("do_damage", ("damage_general", 40f), ("damagetype", "melee"));
        var heal = N("target_self_action", N("action", N("commands", N("check_prerequisites",
            N("satisfied_actions", N("commands", N("do_heal", ("health", 30f), ("armourshield", 0f))))))));
        var actions = N("actions",
            N("actions_by_upgrade", N("actions", UpgradeEntry("u_knife", damage65), UpgradeEntry(null, damage40))),
            N("actions_by_upgrade", N("actions", UpgradeEntry("u_heal", heal))));
        var ability = T(N("primary_target_actions", N("hit_sim", actions)));
        var definitions = new[] { "ability_melee_ck1", "ability_melee_ck2", "ability_melee_ck3" }.Select(a => (a, (object)ability)).ToArray();
        return BafDocument.Read(SyntheticBafWriter.Build(N("common_data", ("slot", "melee")), N("ability_data", ("ability_definitions", definitions))));
    }

    /// <summary>A named table.</summary>
    static (string, object) N(string name, params (string, object)[] items) => (name, items);

    /// <summary>An <c>actions_by_upgrade</c> entry requiring <paramref name="upgrade"/> (null: the default entry) with these commands.</summary>
    static (string, object) UpgradeEntry(string? upgrade, params (string, object)[] commands) => N("entry",
        N("required_upgrade_name", upgrade is null ? N("required_list") : N("required_list", ("upgrade_name", upgrade))),
        N("action", N("commands", commands)));

    [Fact]
    public void Melee_perks_read_the_upgraded_damage_and_the_heal_block()
    {
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_melee_combat_knife"] = Knife(), ["mp_perk_knife"] = Perk("u_knife"), ["mp_perk_heal"] = Perk("u_heal"),
        };
        var built = Build(["mp_perk_knife", "mp_perk_heal"], wargear, [], ["sm_mp_melee_combat_knife"], [KnifeGraph]);
        var combo = Assert.Single(built.Loadouts[0].MeleeChoices);
        Assert.All(combo.Steps, s => Assert.Equal((40f, 0f, DamageType.Melee), (s.Damage, s.Heal, s.DamageType)));
        Assert.All(combo.With(new PerkUpgrades("u_knife", null)).Steps, s => Assert.Equal((65f, 0f), (s.Damage, s.Heal)));
        Assert.All(combo.With(new PerkUpgrades("u_heal", null)).Steps, s => Assert.Equal((40f, 30f), (s.Damage, s.Heal)));
        Assert.All(combo.With(new PerkUpgrades("u_heal", "u_knife")).Steps, s => Assert.Equal((65f, 30f), (s.Damage, s.Heal)));
        Assert.Equal(3, combo.Variants.Count);   // knife, heal, and the pair (differs from either single)
    }

    sealed record Box(float Value);

    /// <summary>One upgrade block: a default entry (10), entries needing "a" (20) and "b" (30), and with <paramref name="joint"/> one needing both (40).</summary>
    static BafTable Block(bool joint)
    {
        var entries = new List<(string, object)>
        {
            UpgradeEntry(null, N("do_damage", ("damage_general", 10f))),
            UpgradeEntry("a", N("do_damage", ("damage_general", 20f))),
            UpgradeEntry("b", N("do_damage", ("damage_general", 30f))),
        };
        if (joint) entries.Add(N("entry", N("required_upgrade_name", N("required_list", ("upgrade_name", "a"), ("upgrade_name", "b"))),
            N("action", N("commands", N("do_damage", ("damage_general", 40f))))));
        return BafDocument.Read(SyntheticBafWriter.Build(N("actions", [.. entries]))).Root.Table("actions")!.Value;
    }

    static float Damage(BafTable block, params string[] granted)
    {
        using (UpgradeScope.Grant(granted)) return UpgradeScope.Pick(block, "entry")!.Value.Table("action/commands/do_damage")!.Value.Float("damage_general")!.Value;
    }

    [Fact]
    public void Entry_choice_prefers_the_entry_needing_the_most_granted_strings_and_ties_go_to_the_first_listed()
    {
        var joint = Block(joint: true);
        Assert.Equal((10f, 20f, 30f, 40f), (Damage(joint), Damage(joint, "a"), Damage(joint, "b"), Damage(joint, "b", "a")));
        Assert.Equal(10f, Damage(joint, "c"));                       // a string no entry needs: the default
        var separate = Block(joint: false);
        Assert.Equal(20f, Damage(separate, "b", "a"));               // two perks in one block, no joint entry: the entry listed first
    }

    [Fact]
    public void Variants_keep_a_pair_only_when_it_differs_from_what_the_singles_give()
    {
        foreach (bool joint in new[] { true, false })
        {
            var block = Block(joint);
            var basic = new Box(Damage(block));
            var variants = UpgradeScope.VariantsOf(basic, ["a", "b", "never"], () => new Box(UpgradeScope.Pick(block, "entry")!.Value.Table("action/commands/do_damage")!.Value.Float("damage_general")!.Value),
                b => BitConverter.GetBytes(b.Value));
            // "never" changes nothing (no variant); the singles always; the pair only with its own joint entry (else the pick of the singles, 20, is right).
            Assert.Equal(joint ? ["a", "b", "a+b"] : ["a", "b"], variants.Select(v => string.Join("+", v.Upgrades)));
            Assert.Equal(joint ? 40f : 20f, Variants.Pick(basic, variants, new PerkUpgrades("b", "a")).Value);
            Assert.Equal(30f, Variants.Pick(basic, variants, new PerkUpgrades("b", "never")).Value);
            Assert.Same(basic, Variants.Pick(basic, variants, new PerkUpgrades("never", null)));
        }
    }

    [Fact]
    public void A_perk_entry_without_the_command_keeps_the_default_entrys()
    {
        // The perk's entry changes only the reaction: the damage stays the default entry's 40 (not 0).
        static (string, object) Reaction(string type) => N("trigger_reaction", N("reaction_by_intensity", N("entry", ("intensity", 0f), ("reaction_type", type))));
        var perkEntry = UpgradeEntry("u_react", Reaction("reaction_mp_stagger"));
        var defaultEntry = UpgradeEntry(null, N("do_damage", ("damage_general", 40f)), Reaction("reaction_mp_stun"));
        var bytes = SyntheticBafWriter.Build(N("actions", N("actions_by_upgrade", N("actions", perkEntry, defaultEntry))));
        var actions = BafDocument.Read(bytes).Root.Table("actions");
        using (UpgradeScope.Grant(["u_react"]))
        {
            Assert.Equal(40f, UpgradeScope.Command(actions, "do_damage")!.Value.Float("damage_general"));
            Assert.Equal(ReactionKind.Stagger, ReactionTuning.HitReaction(actions, null).Kind);
        }
        Assert.Equal(ReactionKind.Stun, ReactionTuning.HitReaction(actions, null).Kind);
    }

    [Fact]
    public void The_loader_refuses_more_perks_than_the_wire_carries_and_bursts_longer_than_their_cooldown()
    {
        var gun = new WeaponSpec("gun", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.475f, 10, 40, 1.5f, 250f, 245f, 250f, 1f, 2.5f, 1f, 0.5f, 0.5f);
        ClassLoadout[] classes = [new ClassLoadout("c", 0, -1, [])];
        PerkSpec[] Perks(int n) => Enumerable.Range(0, n).Select(i => new PerkSpec($"p{i}", $"u{i}", PerkKind.Class, 1)).ToArray();
        Assert.Equal(127, SimConfig.MaxPerks);
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [gun], classes, perks: Perks(127)));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [gun], classes, perks: Perks(128)));   // index 127 would be "none" on the wire
        // Burst Fire's numbers (2 rounds 0.1 s apart, 0.475 s cooldown) pass; a burst whose last round comes after its cooldown does not.
        var burst = gun with { BurstLength = 2, BurstInterval = 0.1f };
        Assert.NotNull(GameplayTuning.WithClasses(new SimConfig(), [gun with { Variants = [new(["u0"], burst)] }], classes));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [gun with { Variants = [new(["u0"], burst with { BurstInterval = 0.5f })] }], classes));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [burst with { BurstLength = 3, BurstInterval = 0.25f }], classes));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), [burst with { BurstInterval = 0f }], classes));
    }

    [Fact]
    public void Player_blueprint_perks_read_modifiers_shield_and_blasts()
    {
        static (string, object) Block(params (string, object)[] entries) => N("actions_by_upgrade", N("actions", entries));
        static (string, object) Modifier(string type, string op, float amount) =>
            N("modifier", ("exclusive", false), ("modifier_type", type), ("operation_type", op), ("amount", amount));
        static (string, object) Modifiers(params (string, object)[] modifiers) => N("apply_modifiers", N("modifiers", modifiers));
        static (string, object) Aoe(float radius, float damage, float shield) => N("area_of_effect",
            N("hit_actions", N("commands", N("do_damage", ("damage_general", damage), ("damagetype", "explosive"), ("damage_armourshield", shield)))),
            ("target_filter", "target_all"), N("shape", ("$ref", "_base_templates\\shape\\sphere"), ("radius", radius)), ("line_of_sight", true), ("ignore_self", true),
            N("fall_off", N("fall_off_entry", ("distance", 0f), ("intensity_value", 1f)), N("fall_off_entry", ("distance", radius), ("intensity_value", 0.25f))));
        static (string, object) Shield(string? upgrade, float max, params (string, object)[] extra) => N("armourshield_info",
        [
            N("required_upgrade_name", upgrade is null ? N("required_list") : N("required_list", ("upgrade_name", upgrade))),
            ("armourshield_max", max), ("armourshield_regen_delay", 7f), ("armourshield_regen_rate", 33f), .. extra,
        ]);
        var lifetime = N("entity_actions_ext", N("lifetime_action", N("commands",
            Block(UpgradeEntry("u_fnp", Modifiers(Modifier("received_explosive_damage", "multiplication", 0.7f), Modifier("received_melee_damage", "multiplication", 0.7f)))),
            Block(UpgradeEntry("u_larr", Modifiers(Modifier("health_regeneration", "addition", 45f), Modifier("health_regeneration_delay", "addition", -1.5f)))),
            Block(UpgradeEntry("u_coop", Modifiers(Modifier("received_ranged_damage", "multiplication", 0.1f)))))));
        var death = N("death_action", N("commands", Block(UpgradeEntry("u_veng", N("delayed_action", N("action", N("commands", Aoe(8f, 100f, 100f))), ("delay", 2.5f))))));
        var shields = N("armourshield_info",
            Shield("u_halo", 150f),
            Shield(null, 100f, N("armourshield_broken", N("commands", Block(UpgradeEntry("u_react", Aoe(4.5f, 25f, 35f)))))));
        var blueprint = BafDocument.Read(SyntheticBafWriter.Build(N("extensions", lifetime, N("health_ext", death, shields))));
        var notes = new List<string>();
        var read = PlayerUpgrades.Read(blueprint, ["u_fnp", "u_larr", "u_veng", "u_halo", "u_react"], notes);
        var by = read.ToDictionary(u => u.Upgrade);
        Assert.Equal(["u_fnp", "u_larr", "u_veng", "u_halo", "u_react"], read.Select(u => u.Upgrade));   // u_coop is no perk's: not read
        Assert.Equal((1f, 0.7f, 0.7f), (by["u_fnp"].ReceivedRanged, by["u_fnp"].ReceivedMelee, by["u_fnp"].ReceivedExplosive));
        Assert.Equal((45f, -1.5f), (by["u_larr"].HealthRegenRate, by["u_larr"].HealthRegenDelay));
        Assert.Equal(new ShieldInfo(150f, 7f, 33f), by["u_halo"].Shield);
        var deathBlast = Assert.Single(by["u_veng"].DeathBlasts);
        Assert.Equal((2.5f, 8f, 100f, 100f, DamageType.Explosive), (by["u_veng"].DeathBlastDelay, deathBlast.Radius, deathBlast.Damage.Damage, deathBlast.Damage.ShieldDamage, deathBlast.Damage.Type));
        var broken = Assert.Single(by["u_react"].ShieldBreakBlasts);
        Assert.Equal((4.5f, 25f, 35f), (broken.Radius, broken.Damage.Damage, broken.Damage.ShieldDamage));

        // Into a config: the body stats a player with the perks gets.
        var config = GameplayTuning.WithPlayerUpgrades(new SimConfig
        {
            Perks = [new("p_fnp", "u_fnp", PerkKind.Class, 1), new("p_larr", "u_larr", PerkKind.Class, 1), new("p_halo", "u_halo", PerkKind.Class, 1)],
            Classes = [new ClassLoadout("c", 0, -1, []) { PerkChoices = [0, 1, 2] }],
        }, blueprint, null, notes);
        Assert.Equal(3, config.PlayerUpgrades.Count);   // only the config's perks' strings
        var body = Loadouts.Body(config, new PlayerState(1, default, default, 0f, true) { Perk1 = 1, Perk2 = 2 });
        Assert.Equal((50f, 3.5f, 150f, 7f), (body.HealthRegenRate, body.HealthRegenDelay, body.ShieldMax, body.ShieldRegenDelay));
        var plain = Loadouts.Body(config, new PlayerState(1, default, default, 0f, true) { Perk1 = 0 });
        Assert.Equal((5f, 5f, 100f, 0.7f, 1f), (plain.HealthRegenRate, plain.HealthRegenDelay, plain.ShieldMax, plain.ReceivedMelee, plain.ReceivedRanged));
    }
}
