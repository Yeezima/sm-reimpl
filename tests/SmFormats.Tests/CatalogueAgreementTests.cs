using SmContent;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// The customizer's catalogue and the simulation agree (review findings S2/S3 of T1+T2): every catalogue item marked simulated is one of the
/// simulation's choices; wire indices come from the simulation's lists by name (<see cref="LoadoutIndices"/>), not catalogue positions; one
/// perk reader for both; the armour slot order of <see cref="AppearanceCode"/> is the catalogue's. A synthetic install.
/// </summary>
public class CatalogueAgreementTests
{
    static (string, object)[] T(params (string, object)[] entries) => entries;

    static BafDocument Gun(string slot = "ranged") => BafDocument.Read(SyntheticBafWriter.RangedWeapon(20f, 0.133f, 30, 270, 1.3f, 250f, [(0f, 1f)], 2f, 1f, 0.5f, 0.5f, slot));

    static BafDocument Knife()
    {
        var damage = T(("hit_sim", T(("actions", T(("do_damage", T(("damage_general", 40f))))))));
        var abilities = new[] { "ability_melee_ck1", "ability_melee_ck2", "ability_melee_ck3" }.Select(a => (a, (object)T(("primary_target_actions", damage)))).ToArray();
        return BafDocument.Read(SyntheticBafWriter.Build(("common_data", T(("slot", "melee"))), ("ability_data", T(("ability_definitions", abilities)))));
    }

    static readonly BehaviorGraph KnifeGraph = new("MP_SM_MEL_CombatKnife", [],
    [
        .. new[] { "CK 1 Sequence", "CK 2 Sequence", "CK 3 Sequence" }.Select(n => new BehaviorSequencer(n, ["A_3.5m"],
        [
            new BehaviorTag("Event", 0.2f, 0.2f, "ACTN_Hit_Targets_Multiplayer", null),
            new BehaviorTag("Branch", 0.3f, 0.5f, "BRANCH_Melee_Combo", null),
        ], [1f])),
    ]);

    static BafDocument Perk(string upgrade, int? level) => BafDocument.Read(SyntheticBafWriter.Build(
        ("action_data", T(("lifetime_actions", T(("commands", T(("apply_upgrade_string", T(("strings", T(("entry", upgrade))))))))))),
        ("common_data", level is { } l ? T(("slot", "perk"), ("unlock_info", T(("level_requirement", T(("level_required", l)))))) : T(("slot", "perk")))));

    static (string, object) Class(string key, string role, string race, string[] baseWargear, string[] startup, string[] perks, string defaultPerk) =>
        (key, T(("mp_class", role), ("race", race),
            ("base_wargear", baseWargear.Select(w => ("wargear", (object)("wargear\\" + w))).ToArray()),
            ("startup_wargear", startup.Select(w => ("wargear", (object)("wargear\\" + w))).ToArray()),
            ("class_perks", perks.Select(w => ("wargear", (object)("perks\\" + w))).ToArray()),
            ("default_perk", "perks\\" + defaultPerk)));

    [Fact]
    public void Every_simulated_catalogue_item_is_a_simulation_choice_found_by_name_not_by_position()
    {
        var classTable = T(
            Class("sm - tactical", "tactical", "space_marine", ["sm_mp_melee_combat_knife", "sm_mp_pistol_bolt_pistol"],
                ["sm_mp_eq_stimpack", "sm_mp_eq_frag_grenade", "sm_mp_ranged_bolter", "sm_mp_ranged_plasma_gun"], ["mp_perk_a", "mp_perk_weap_b"], "mp_perk_a"),
            Class("sm - assault marine", "assault", "space_marine", [],
                ["sm_mp_pistol_bolt_pistol", "sm_mp_pistol_bolter", "sm_mp_melee_lightning_claw", "sm_mp_melee_combat_knife"], ["mp_perk_c", "mp_perk_bolter_in_place_of_pistol"], "mp_perk_c"));
        var global = BafDocument.Read(SyntheticBafWriter.Build(("extensions", T(("multiplayer_settings", T(("ingame_progression", T(("classes", classTable)))))))));
        var frag = SyntheticBafWriter.ThrownItem("ebps\\projectiles\\frag", 2, 1, 1f, 10f);
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["sm_mp_melee_combat_knife"] = Knife(),
            ["sm_mp_pistol_bolt_pistol"] = Gun("pistol"),
            ["sm_mp_pistol_bolter"] = Gun("pistol"),
            ["sm_mp_ranged_bolter"] = Gun(),
            ["sm_mp_ranged_plasma_gun"] = Gun(),
            ["sm_mp_melee_lightning_claw"] = BafDocument.Read(SyntheticBafWriter.Build(("common_data", T(("slot", "melee"))))),
            ["sm_mp_eq_stimpack"] = BafDocument.Read(SyntheticBafWriter.Build(("common_data", T(("slot", "equipment"))))),
            ["sm_mp_eq_frag_grenade"] = BafDocument.Read(frag),
            ["mp_perk_a"] = Perk("mp_perk_a", 1),
            ["mp_perk_weap_b"] = Perk("mp_perk_weap_b", null),   // no level: 1 in both readers
            ["mp_perk_c"] = Perk("mp_perk_c", 7),
            ["mp_perk_bolter_in_place_of_pistol"] = Perk("mp_perk_bolter_in_place_of_pistol", 25),
        };
        var blueprint = BafDocument.Read(SyntheticBafWriter.ProjectileBlueprint(20f, 0f, 0.25f, 6f, 75f, 0f, [(0f, 1f), (6f, 0.05f)], [(0f, 0f)]));
        var classes = MpClasses.ReadClasses(global);
        var built = MpClasses.Build(classes, wargear, [KnifeGraph], _ => 1.5f, [], p => p.StartsWith("ebps\\projectiles\\frag", StringComparison.Ordinal) ? blueprint : null);
        var config = GameplayTuning.WithClasses(new SimConfig(), built.Weapons, built.Loadouts, built.Thrown, built.JumpPacks, built.Evades, perks: built.Perks)!;
        Assert.NotNull(config);
        var catalogue = PerkCatalog.Build(global, wargear.GetValueOrDefault, null, []);

        foreach (var options in catalogue.Classes)
        {
            int k = LoadoutIndices.Class(config, options.Key);
            Assert.True(k >= 0, options.Key);
            foreach (var slot in new[] { "ranged", "pistol", "melee", "equipment" })
                foreach (var item in options.For(slot).Items)
                {
                    int index = LoadoutIndices.Choice(config, k, slot, item.Name);
                    if (item.Simulated) Assert.True(index >= 0, $"{options.Key} {slot} {item.Name}: simulated in the catalogue, not a simulation choice");
                    else Assert.Equal(-1, index);
                }
            foreach (var perk in options.Perks) Assert.True(LoadoutIndices.Choice(config, k, "perk", perk.Name) >= 0, perk.Name);
        }
        // Positions differ: the assault catalogue lists the power axe first, the simulation has only the knife.
        var assault = catalogue.Class("sm - assault marine")!;
        Assert.Equal(1, assault.Melee.Items.ToList().FindIndex(i => i.Name == "sm_mp_melee_combat_knife"));
        Assert.Equal(0, LoadoutIndices.Choice(config, 1, "melee", "sm_mp_melee_combat_knife"));
        // The stims are listed (not simulated) before the frag grenade: catalogue position 1, simulation index 0.
        var tactical = catalogue.Class("sm - tactical")!;
        Assert.Equal(1, tactical.Equipment.Items.ToList().FindIndex(i => i.Name == "sm_mp_eq_frag_grenade"));
        Assert.Equal(0, LoadoutIndices.Choice(config, 0, "equipment", "sm_mp_eq_frag_grenade"));
        Assert.Equal(new LoadoutChoice(1, -1, 1, 0, -1, 1, -1),
            LoadoutIndices.ToChoice(config, "sm - assault marine", null, "sm_mp_pistol_bolter", "sm_mp_melee_combat_knife", null, Loadouts.TrueGritPerk, "unknown"));
        Assert.Equal(-1, LoadoutIndices.Class(config, "nobody"));
        // One perk reader: the catalogue's perks are the simulation's (name, string, kind, level; a missing level reads 1 in both).
        foreach (var p in catalogue.All)
            Assert.Equal(config.Perks.Single(s => s.Name == p.Name), new PerkSpec(p.Name, p.UpgradeString, p.Kind, p.Level));
        Assert.Equal(1, config.Perks.Single(p => p.Name == "mp_perk_weap_b").Level);
        Assert.Equal(PerkKind.Weapon, config.Perks.Single(p => p.Name == "mp_perk_weap_b").Kind);
        // The pistol-bolter rule is one: the catalogue marks what the simulation's True Grit rule gates.
        Assert.Equal(PerkCatalog.BolterPistolPerk, assault.Pistol.Items.Single(i => i.Name == "sm_mp_pistol_bolter").RequiredPerk);
        Assert.True(Loadouts.IsPistolBolter(config.Weapons[config.Classes[1].PistolChoices[LoadoutIndices.Choice(config, 1, "pistol", "sm_mp_pistol_bolter")]]));
        // One melee list.
        Assert.Same(MpClasses.SimulatedMeleeSuffixes, PerkCatalog.SimulatedMeleeSuffixes);
    }

    [Fact]
    public void The_appearance_code_slot_order_is_the_catalogues()
    {
        Assert.Equal(CustomizationCatalog.ArmourSlots, Enum.GetValues<ArmourSlot>().Select(s => s.ToString().ToLowerInvariant()));
        Assert.Equal(Enumerable.Range(0, AppearanceCode.Slots), Enum.GetValues<ArmourSlot>().Select(s => (int)s));
    }
}
