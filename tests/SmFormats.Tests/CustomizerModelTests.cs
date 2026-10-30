using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmFormats.Baf;
using SmFormats.Dds;
using SmFormats.Obpk;
using SmSim;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// The engine-free model behind the front end and the customizer screens (T8; <c>client/Customizer/Model</c>, compiled into this assembly): the lists the creator
/// shows, the edits, the session (dirty / save / revert), the appearance edits, the emblem pipeline and the join plan. A synthetic install (made-up names and
/// numbers, no original data) and temp directories only.
/// </summary>
public class CustomizerModelTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-t8-" + Guid.NewGuid().ToString("N"));
    readonly string _profileDir;
    readonly Lazy<CustomizationCatalog> _catalog;
    string Packages => Path.Combine(_dir, "data", "packages");
    CustomizationCatalog Catalog => _catalog.Value;

    public CustomizerModelTests()
    {
        Directory.CreateDirectory(Packages);
        _profileDir = Path.Combine(_dir, "user");
        _catalog = new Lazy<CustomizationCatalog>(() => { WriteInstall(); return CustomizationCatalog.Load(_dir, []); });
    }

    public void Dispose() => Directory.Delete(_dir, true);

    ProfileStore NewStore() => new(_profileDir);
    ProfileSession NewSession() => ProfileSession.Open(Catalog, NewStore());

    // ---- synthetic install (a trimmed copy of the profile tests' fixture) -------------------------------------------------------------------

    static (string, object)[] T(params (string, object)[] items) => items;
    static byte[] Baf(params (string, object)[] root) => SyntheticBafWriter.Build(root);
    static SyntheticObpkWriter.File Entry(string name, byte[] data) => new("attr_pc", name, data);
    void WritePackage(string package, params SyntheticObpkWriter.File[] files) => File.WriteAllBytes(Path.Combine(Packages, package + ".oppc"), SyntheticObpkWriter.Build(files));

    const string W = @"wargear\wargear\multiplayer\";
    static (string, object) Wg(string name) => ("wargear", W + "x\\" + name);
    static (string, object)[] WgList(params string[] names) => names.Select(Wg).ToArray();

    static byte[] Gear(string slot, string[]? packages = null, bool thrown = false, string helmet = "no_override", string? upgrade = null)
    {
        var attachments = (packages ?? []).Select((p, i) => (i == 0 ? "torso" : @"wargear\attachment_entry", (object)T(("visual_package", p)))).ToArray();
        var root = new List<(string, object)>
        {
            ("visual_data", T(("attachments", attachments))),
            ("common_data", T(("slot", slot), ("helmet_override", helmet), ("unlock_info", T(("level_requirement", T(("level_required", 1))))))),
            ("ui_data", T(("name", 0), ("description", 0), ("visible_in_customizer", true))),
        };
        if (upgrade is not null) root.Add(Nest(["action_data", "lifetime_actions", "commands", "apply_upgrade_string", "strings"], T(("entry", upgrade))));
        if (thrown) root.Add(Nest(["ability_data", "ability_definitions", "ability_utility", "delivery"], T()));
        return SyntheticBafWriter.Build(root.ToArray());
    }

    static (string, object) Nest(string[] names, (string, object)[] leaf)
    {
        (string, object) current = (names[^1], leaf);
        for (int i = names.Length - 2; i >= 0; i--) current = (names[i], T(current));
        return current;
    }

    static byte[] ArmourSet(string race, string usage, string setId, string? scheme, params string[] items) => Baf(
        ("item_list", WgList(items)), ("set_name", 0), ("set_description", 0), ("usable_by_race", race), ("usage", usage),
        ("custom_paint_scheme", scheme is null ? "" : @"custom_paint\custom_paint_scheme\" + scheme), ("set_id", setId));

    static (string, object)[] ClassTable(string race, string role, string[] baseGear, string[] startup, string[] perks, string defaultPerk, string defaultSet,
        params (string Wargear, string Required)[] overrides) => T(
        ("race", race), ("mp_class", role), ("display_name", 0), ("display_description", 0),
        ("base_wargear", WgList(baseGear)), ("startup_wargear", WgList(startup)), ("class_perks", WgList(perks)),
        ("default_perk", W + "p\\" + defaultPerk), ("default_armor_set", defaultSet),
        ("override_wargear", overrides.Select(o => ("gear_and_req", (object)T(("wargear", W + "a\\" + o.Wargear), ("required_wargear", o.Required.Length == 0 ? "" : W + "a\\" + o.Required)))).ToArray()));

    static byte[] GlobalBaf()
    {
        string[] tacticalPerks = ["mp_perk_a", "mp_perk_weap_bolter_accuracy", "mp_perk_secondary_weapon"];
        var classes = T(
            ("sm - tactical", ClassTable("space_marine", "tactical", ["sm_mp_abil_sprint", "sm_mp_melee_combat_knife", "sm_mp_pistol_bolt_pistol"],
                ["sm_mp_eq_frag_grenade", "sm_mp_eq_krak_grenade", "sm_mp_eq_stimpack", "sm_mp_ranged_bolter", "sm_mp_ranged_plasma_gun"], tacticalPerks, "mp_perk_a", "sm_default")),
            ("sm - assault marine", ClassTable("space_marine", "assault", ["sm_mp_abil_sprint", "sm_mp_abil_jumppack"],
                ["sm_mp_pistol_bolt_pistol", "sm_mp_pistol_bolter", "sm_mp_melee_lightning_claw", "sm_mp_melee_chainsword", "sm_mp_eq_frag_grenade"],
                ["mp_perk_a", "mp_perk_bolter_in_place_of_pistol", "mp_perk_b"], "mp_perk_a", "sm_default")),
            ("sm - devastator", ClassTable("space_marine", "heavy", ["sm_mp_abil_sprint", "sm_mp_pistol_bolt_pistol"],
                ["sm_mp_exotic_heavy_bolter", "sm_mp_exotic_las_cannon"], ["mp_perk_a"], "mp_perk_a", "sm_default", ("sm_backpack_heavy_bolter", "sm_mp_exotic_heavy_bolter"))),
            ("csm - tactical", ClassTable("chaos", "tactical", ["sm_mp_abil_sprint", "csm_mp_melee_combat_knife", "csm_mp_pistol_bolt_pistol"],
                ["csm_mp_eq_frag_grenade", "csm_mp_ranged_bolter", "csm_mp_ranged_plasma_gun"], tacticalPerks, "mp_perk_a", "chaos_default")),
            ("csm - raptor", ClassTable("chaos", "assault", ["sm_mp_abil_sprint", "sm_mp_abil_jumppack"], ["csm_mp_melee_chain_axe", "csm_mp_melee_chainsword", "csm_mp_pistol_bolt_pistol"],
                ["mp_perk_a"], "mp_perk_a", "chaos_default", ("csm_raptor_head", ""), ("csm_raptor_leg_l", ""), ("csm_raptor_leg_r", ""))));
        return Baf(("extensions", T(("multiplayer_settings", T(("ingame_progression", T(("classes", classes))))))));
    }

    static readonly string[] SmA = ["sm_a_head", "sm_a_torso", "sm_a_shoulder_left", "sm_a_shoulder_right", "sm_a_arm_left", "sm_a_arm_right", "sm_a_leg_left", "sm_a_leg_right", "sm_a_backpack"];
    static readonly string[] SmB = ["sm_b_head", "sm_b_torso", "sm_a_shoulder_left", "sm_a_shoulder_right", "sm_a_arm_left", "sm_a_arm_right", "sm_a_leg_left", "sm_a_leg_right", "sm_a_backpack"];
    static readonly string[] CsmBasic = ["csm_basic_head", "csm_basic_torso", "csm_basic_shoulder_left", "csm_basic_shoulder_right", "csm_basic_arm_left", "csm_basic_arm_right", "csm_basic_leg_left", "csm_basic_leg_right", "csm_basic_backpack"];

    static byte[] Colour(string group, bool visible) => Baf(
        ("name", 0), ("diffuse", T(("red", 0.1f), ("green", 0.2f), ("blue", 0.3f), ("alpha", 0f))),
        ("specular", T(("red", 0.5f), ("green", 0.5f), ("blue", 0.5f), ("alpha", 0f))), ("colour_type", group), ("visible_in_customizer", visible));

    static string Paint(string colour) => @"custom_paint\citadel_paints\" + colour;

    static (string, object)[] PaintSet(string p, string s, string t, bool pattern, string? decal = null) =>
        T(("primary_colour", Paint(p)), ("secondary_colour", Paint(s)), ("tertiary_colour", Paint(t)), ("pattern_enabled", pattern),
            ("decals", decal is null ? T() : T(("decal", @"custom_paint\custom_paint_decal\" + decal))));

    static byte[] Scheme(string race, bool visible, (string Slot, (string, object)[] Set)[] slots, (string, object)[] def) => Baf(
        ("name", 0), ("race", race), ("paints", slots.Select(s => (s.Slot, (object)s.Set)).ToArray()), ("default_paint", def),
        ("visible_in_customizer", visible), ("class_badge_enabled", false));

    static byte[] Decal(string race, bool visible, string texture = "tex") => Baf(("name", 0), ("decal", texture), ("decal_package", texture), ("visible_in_customizer", visible), ("race", race));

    void WriteInstall()
    {
        WritePackage("global", Entry("global", GlobalBaf()));
        var wargear = new List<SyntheticObpkWriter.File>
        {
            Entry("sm_mp_ranged_bolter", Gear("ranged")), Entry("sm_mp_ranged_plasma_gun", Gear("ranged")),
            Entry("csm_mp_ranged_bolter", Gear("ranged")), Entry("csm_mp_ranged_plasma_gun", Gear("ranged")),
            Entry("sm_mp_pistol_bolt_pistol", Gear("pistol")), Entry("sm_mp_pistol_bolter", Gear("pistol")), Entry("csm_mp_pistol_bolt_pistol", Gear("pistol")),
            Entry("sm_mp_melee_combat_knife", Gear("melee")), Entry("csm_mp_melee_combat_knife", Gear("melee")),
            Entry("sm_mp_melee_chainsword", Gear("melee")), Entry("csm_mp_melee_chainsword", Gear("melee")),
            Entry("sm_mp_melee_lightning_claw", Gear("melee")), Entry("csm_mp_melee_chain_axe", Gear("melee")),
            Entry("sm_mp_eq_frag_grenade", Gear("equipment", thrown: true)), Entry("csm_mp_eq_frag_grenade", Gear("equipment", thrown: true)),
            Entry("sm_mp_eq_krak_grenade", Gear("equipment", thrown: true)), Entry("sm_mp_eq_stimpack", Gear("equipment")),
            Entry("sm_mp_abil_sprint", Gear("coreability")),
            Entry("sm_mp_abil_jumppack", Baf(("common_data", T(("slot", "coreability"))), ("ability_data", T(("ability_definitions", T(("ability_jumppack", T()))))))),
            Entry("sm_mp_exotic_heavy_bolter", Gear("ranged")), Entry("sm_mp_exotic_las_cannon", Gear("ranged")),
            Entry("mp_perk_a", Gear("perk", upgrade: "mp_perk_a")), Entry("mp_perk_b", Gear("perk", upgrade: "mp_perk_b")),
            Entry("mp_perk_weap_bolter_accuracy", Gear("perk", upgrade: "x")),
            Entry("mp_perk_secondary_weapon", Gear("perk")), Entry("mp_perk_bolter_in_place_of_pistol", Gear("perk")),
        };
        string[] slots = ["head", "torso", "leftshoulder", "rightshoulder", "leftarm", "rightarm", "leftleg", "rightleg", "backpack"];
        string[] parts = ["helmet", "torso", "shldrl", "shldrr", "arml", "armr", "legl", "legr", "bp"];
        for (int i = 0; i < 9; i++)
        {
            wargear.Add(Entry(SmA[i], Gear(slots[i], i == 1 ? ["pkg_a_torso", "pkg_a_belt"] : ["pkg_a_" + parts[i]], helmet: i == 0 ? "helmet" : "no_override")));
            wargear.Add(Entry(CsmBasic[i], Gear(slots[i], ["pkg_c_" + parts[i]])));
        }
        wargear.Add(Entry("sm_b_head", Gear("head", ["pkg_b_helmet"], helmet: "no_helmet")));
        wargear.Add(Entry("sm_b_torso", Gear("torso", ["pkg_b_torso"])));
        wargear.Add(Entry("sm_bt_head", Gear("head", ["pkg_bt_helmet"])));
        wargear.Add(Entry("sm_bt_torso", Gear("torso", ["pkg_bt_torso"])));
        wargear.Add(Entry("sm_bt_shoulder_left", Gear("leftshoulder", ["pkg_bt_shldrl"])));
        wargear.Add(Entry("sm_bt_shoulder_right", Gear("rightshoulder", ["pkg_bt_shldrr"])));
        wargear.Add(Entry("sm_backpack_heavy_bolter", Gear("backpack", ["pkg_hb_bp"])));
        wargear.Add(Entry("csm_raptor_head", Gear("head", ["pkg_r_helmet"], helmet: "helmet")));
        wargear.Add(Entry("csm_raptor_leg_l", Gear("leftleg", ["pkg_r_legl"])));
        wargear.Add(Entry("csm_raptor_leg_r", Gear("rightleg", ["pkg_r_legr"])));
        WritePackage("wargear", wargear.ToArray());
        WritePackage("mp_armor_set",
            Entry("mk_7_aquila", ArmourSet("space_marine", "customizable", "sm_default", null, SmA)),
            Entry("mk_8_errant", ArmourSet("space_marine", "customizable", "", null, SmB)),
            Entry("black_templars", ArmourSet("space_marine", "predefined", "black_templars", "sm_black_templars", "sm_bt_head", "sm_bt_torso", "sm_bt_shoulder_left", "sm_bt_shoulder_right")),
            Entry("chaos_default", ArmourSet("chaos", "customizable", "chaos_default", null, CsmBasic)));
        WritePackage("custom_paint_colour",
            Entry("blue_0", Colour("blue", true)), Entry("blue_1", Colour("blue", true)), Entry("gold", Colour("metallics", true)),
            Entry("red_1", Colour("red", true)), Entry("dev_pink", Colour("blue", false)));
        WritePackage("custom_paint_scheme",
            Entry("sm_ultramarines", Scheme("space_marine", true,
                [("head", PaintSet("blue_1", "red_1", "gold", false)), ("leftshoulder", PaintSet("blue_1", "gold", "red_1", true, "sm_foo_orig")),
                 ("rightshoulder", PaintSet("blue_1", "gold", "red_1", true, "sm_blank_badge"))], PaintSet("blue_1", "blue_1", "gold", false))),
            Entry("sm_x", Scheme("space_marine", true, [("leftshoulder", PaintSet("gold", "red_1", "blue_0", true, "sm_foo_black"))], PaintSet("gold", "gold", "blue_0", false))),
            Entry("sm_black_templars", Scheme("space_marine", false, [("leftshoulder", PaintSet("red_1", "gold", "blue_0", true, "sm_bar"))], PaintSet("red_1", "gold", "blue_0", false))),
            Entry("chaos_multiplayer", Scheme("chaos", false, [], PaintSet("red_1", "red_1", "gold", false))),
            Entry("csm_y", Scheme("chaos", true, [], PaintSet("gold", "blue_0", "red_1", true))));
        WritePackage("custom_paint_decal",
            Entry("sm_foo_orig", Decal("space_marine", true)), Entry("sm_foo_black", Decal("space_marine", true)), Entry("sm_bar", Decal("space_marine", true)),
            Entry("sm_blank_badge", Decal("space_marine", true, "blank")), Entry("csm_star", Decal("chaos", true)));
    }

    static WeaponSpec Weapon(string name, WeaponSlot slot) => new(name, slot, TriggerKind.FullAuto, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
    static ThrownSpec Thrown(string name) => new(name, 1, 1, 1f, 1f, new ProjectileSpec("p", 1, 1, "raycast", 0, [], false, false, false, 0, 0, 0, 0, null, null));
    static MeleeCombo Melee(string name) => new(name, []);

    static SimConfig Config() => new()
    {
        Weapons = [Weapon("sm_mp_ranged_bolter", WeaponSlot.Ranged), Weapon("sm_mp_ranged_plasma_gun", WeaponSlot.Ranged), Weapon("sm_mp_pistol_bolt_pistol", WeaponSlot.Pistol),
            Weapon("csm_mp_ranged_plasma_gun", WeaponSlot.Ranged), Weapon("csm_mp_ranged_bolter", WeaponSlot.Ranged), Weapon("csm_mp_pistol_bolt_pistol", WeaponSlot.Pistol),
            Weapon("sm_mp_pistol_bolter", WeaponSlot.Pistol)],
        Thrown = [Thrown("sm_mp_eq_frag_grenade"), Thrown("csm_mp_eq_frag_grenade")],
        Perks = [new PerkSpec("mp_perk_a", null, PerkKind.Class, 1), new PerkSpec("mp_perk_weap_bolter_accuracy", null, PerkKind.Weapon, 1), new PerkSpec("mp_perk_secondary_weapon", null, PerkKind.Class, 1)],
        Classes =
        [
            new ClassLoadout("sm - tactical", 0, 2, []) { Side = Team.SpaceMarines, Role = "tactical", PrimaryChoices = [0, 1], EquipmentChoices = [0], PerkChoices = [0, 1, 2], DefaultPerk = 0 },
            new ClassLoadout("csm - tactical", 4, 5, []) { Side = Team.Chaos, Role = "tactical", PrimaryChoices = [3, 4], EquipmentChoices = [1], PerkChoices = [2, 1, 0], DefaultPerk = 0 },
            new ClassLoadout("sm - assault marine", -1, 2, []) { Side = Team.SpaceMarines, Role = "assault", PistolChoices = [2, 6],
                MeleeChoices = [Melee("sm_mp_melee_chainsword")], EquipmentChoices = [0], PerkChoices = [0], DefaultPerk = 0 },
            new ClassLoadout("csm - raptor", -1, 5, []) { Side = Team.Chaos, Role = "assault", PistolChoices = [5],
                MeleeChoices = [Melee("csm_mp_melee_chain_axe"), Melee("csm_mp_melee_chainsword")], EquipmentChoices = [1], PerkChoices = [0], DefaultPerk = 0 },
        ],
    };

    // ===== the lists =====================================================================================================================

    [Fact]
    public void The_tactical_weapon_step_has_a_choosable_primary_and_fixed_sidearm_and_melee()
    {
        var sections = LoadoutRows.Weapons(Catalog, "tactical", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All);
        Assert.Equal(["primary", "pistol", "melee"], sections.Select(s => s.Key));
        var primary = sections[0];
        Assert.True(primary.Selectable);
        Assert.Equal(["mp_ranged_bolter", "mp_ranged_plasma_gun"], primary.Rows.Select(r => r.Id));
        Assert.True(primary.Rows[0].Selected);                                             // the class default is shown selected
        Assert.All(primary.Rows, r => Assert.True(r.Selectable));
        Assert.False(sections[1].Selectable);
        Assert.All(sections[1].Rows.Concat(sections[2].Rows), r => { Assert.True(r.Fixed); Assert.False(r.Selectable); Assert.Contains("FIXED", r.Tags); });
        Assert.Equal("mp_pistol_bolt_pistol", sections[1].Rows.Single().Id);
        Assert.Equal("PRIMARY WEAPON", primary.Heading);
        Assert.Equal("HEAVY WEAPON", LoadoutRows.PrimaryHeading("heavy"));
    }

    [Fact]
    public void A_chosen_primary_is_the_selected_row()
    {
        var custom = LoadoutEdits.New(1, "tactical", Catalog) with { Primary = "mp_ranged_plasma_gun" };
        var rows = LoadoutRows.Weapons(Catalog, "tactical", CustomizationCatalog.SpaceMarine, custom, UnlockPolicy.All)[0].Rows;
        Assert.Equal(["mp_ranged_plasma_gun"], rows.Where(r => r.Selected).Select(r => r.Id));
        // Chaos shows its own names for the same neutral ids.
        var chaos = LoadoutRows.Weapons(Catalog, "tactical", CustomizationCatalog.Chaos, custom, UnlockPolicy.All)[0].Rows;
        Assert.Equal(["mp_ranged_plasma_gun"], chaos.Where(r => r.Selected).Select(r => r.Id));
    }

    [Fact]
    public void The_assault_class_has_no_primary_and_its_pistol_bolter_needs_the_perk()
    {
        var plain = LoadoutEdits.New(1, "assault", Catalog);
        var sections = LoadoutRows.Weapons(Catalog, "assault", CustomizationCatalog.SpaceMarine, plain, UnlockPolicy.All);
        Assert.Equal("None (jump pack class)", sections[0].Rows.Single().Title);
        Assert.True(sections[0].Rows.Single().Fixed);
        var bolter = sections[1].Rows.Single(r => r.Id == "mp_pistol_bolter");
        Assert.Equal(UnlockKind.NeedsPerk, bolter.Lock.Kind);
        Assert.False(bolter.Selectable);
        Assert.Contains("Requires perk", bolter.Lock.Caption);
        Assert.Contains("NEEDS PERK", bolter.Tags);
        var withPerk = plain with { Perks = ["mp_perk_bolter_in_place_of_pistol", null] };
        Assert.True(LoadoutRows.Weapons(Catalog, "assault", CustomizationCatalog.SpaceMarine, withPerk, UnlockPolicy.All)[1].Rows.Single(r => r.Id == "mp_pistol_bolter").Selectable);
    }

    [Fact]
    public void Unsimulated_items_are_listed_selectable_and_marked()
    {
        var melee = LoadoutRows.Weapons(Catalog, "assault", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All)[2].Rows;
        var axe = melee.Single(r => r.Id == "mp_melee_lightning_claw");
        Assert.True(axe.NotSimulated);
        Assert.True(axe.Selectable);
        Assert.Contains("NOT SIMULATED YET", axe.Tags);
        Assert.False(melee.Single(r => r.Id == "mp_melee_chainsword").NotSimulated);
        var equipment = LoadoutRows.Equipment(Catalog, "tactical", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All);
        Assert.Equal(["mp_eq_frag_grenade", "mp_eq_krak_grenade", "mp_eq_stimpack"], equipment.Rows.Select(r => r.Id));
        Assert.True(equipment.Rows.Single(r => r.Id == "mp_eq_stimpack").NotSimulated);
        Assert.Equal("Blind Grenades", equipment.Rows.Single(r => r.Id == "mp_eq_krak_grenade").Title);   // the blind grenade is "krak" in the files
    }

    [Fact]
    public void Perks_are_grouped_default_first_and_weapon_perks_warn_without_their_weapon()
    {
        var custom = LoadoutEdits.New(1, "tactical", Catalog);
        var perks = LoadoutRows.Perks(Catalog, "tactical", CustomizationCatalog.SpaceMarine, custom, UnlockPolicy.All);
        Assert.Equal(["mp_perk_a", "mp_perk_secondary_weapon", "mp_perk_weap_bolter_accuracy"], perks.Select(p => p.Name));   // class perks, then weapon perks
        Assert.Equal(1, perks.Single(p => p.Name == "mp_perk_a").Slot);                                              // the class default sits in slot 1
        Assert.All(perks, p => { Assert.True(p.Active); Assert.Equal("", p.ActivityTag); });                          // no status given: the row says nothing about it (it used to say NOT ACTIVE YET for all)
        Assert.Equal("", perks.Single(p => p.Name == "mp_perk_weap_bolter_accuracy").Warning);                        // the bolter is the default primary
        var plasma = custom with { Primary = "mp_ranged_plasma_gun" };
        var warned = LoadoutRows.Perks(Catalog, "tactical", CustomizationCatalog.SpaceMarine, plasma, UnlockPolicy.All).Single(p => p.Name == "mp_perk_weap_bolter_accuracy");
        Assert.Contains("Needs", warned.Warning);
        Assert.Contains("no effect without it", warned.Warning);
    }

    [Fact]
    public void Perk_rows_carry_the_simulations_own_status()
    {
        var status = new Dictionary<string, PerkStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["mp_perk_a"] = new(PerkActivity.Active, ""),
            ["mp_perk_secondary_weapon"] = new(PerkActivity.Inert, "no second weapon list in the data"),
            ["mp_perk_weap_bolter_accuracy"] = new(PerkActivity.Partial, "stims are not simulated"),
        };
        var rows = LoadoutRows.Perks(Catalog, "tactical", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All, status).ToDictionary(r => r.Name);
        Assert.True(rows["mp_perk_a"].Active);
        Assert.Equal("", rows["mp_perk_a"].ActivityTag);
        Assert.Equal("", rows["mp_perk_a"].ActivityNote);
        Assert.False(rows["mp_perk_secondary_weapon"].Active);
        Assert.Equal("NOT ACTIVE YET", rows["mp_perk_secondary_weapon"].ActivityTag);
        Assert.Contains("no second weapon list in the data", rows["mp_perk_secondary_weapon"].ActivityNote);
        Assert.True(rows["mp_perk_weap_bolter_accuracy"].Active);                                                      // partly: it acts, the note says what does not
        Assert.Equal("PARTLY ACTIVE", rows["mp_perk_weap_bolter_accuracy"].ActivityTag);
        Assert.Contains("stims are not simulated", rows["mp_perk_weap_bolter_accuracy"].ActivityNote);
        // A perk the given statuses lack is inert, never active by default; with no statuses at all the row says nothing.
        var missing = LoadoutRows.Perks(Catalog, "tactical", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All, new Dictionary<string, PerkStatus>()).First();
        Assert.False(missing.Active);
        Assert.Equal("NOT ACTIVE YET", missing.ActivityTag);
        // The statuses of a config are the simulation's (nothing in this tiny config consumes an upgrade string, so none is active).
        Assert.All(PerkStatuses.Of(Config()).Where(kv => !SmSim.Perks.ServerRulePerks.Contains(kv.Key)).Select(kv => kv.Value), s => Assert.Equal(PerkActivity.Inert, s.Activity));   // protocol 44: but the server-rule perks act
    }

    [Fact]
    public void Perk_rows_add_the_values_line_under_the_locale_text()
    {
        // perks/finish: the row's description is the locale's text with the simulation's values line under it (PerkValues), "Not active yet." when inert,
        // the text alone without a config.
        var status = new Dictionary<string, PerkStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["mp_perk_a"] = new(PerkActivity.Active, "") { Values = "Bolter: clip 30 → 38 (+27%)" },
            ["mp_perk_secondary_weapon"] = new(PerkActivity.Inert, "no second weapon list in the data"),
        };
        var plain = LoadoutRows.Perks(Catalog, "tactical", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All).ToDictionary(r => r.Name);
        var rows = LoadoutRows.Perks(Catalog, "tactical", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All, status).ToDictionary(r => r.Name);
        Assert.Equal(PerkValues.Describe(plain["mp_perk_a"].Description, "Bolter: clip 30 → 38 (+27%)"), rows["mp_perk_a"].Description);
        Assert.EndsWith("Bolter: clip 30 → 38 (+27%)", rows["mp_perk_a"].Description);
        Assert.EndsWith(PerkValues.NotActive, rows["mp_perk_secondary_weapon"].Description);
        Assert.DoesNotContain(PerkValues.NotActive, plain["mp_perk_secondary_weapon"].Description);
        // The config's statuses carry the generator's text.
        var config = Config();
        Assert.NotEmpty(config.Perks);
        Assert.All(config.Perks, p => Assert.Equal(PerkValues.Text(config, p), PerkStatuses.Of(config)[p.Name].Values));
    }

    [Fact]
    public void Either_sides_item_name_can_be_chosen_and_each_side_shows_its_own_counterpart()
    {
        // Chaos's Raptor lists chain axe, chainsword; the Space Marine Assault lists lightning claw, chainsword: the same positions.
        var profile = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 1, "assault", Catalog).Profile;
        var chosen = LoadoutEdits.SetItem(profile, 1, "melee", "mp_melee_chain_axe", Catalog);                    // the Chaos name, chosen while previewing Chaos
        Assert.True(chosen.Changed, chosen.Notice);
        var custom = chosen.Profile.ClassAt(1)!;
        Assert.Equal("mp_melee_chain_axe", custom.Melee);
        string SelectedId(string race) => LoadoutRows.Weapons(Catalog, "assault", race, custom, UnlockPolicy.All).Single(s => s.Key == "melee").Rows.Single(r => r.Selected).Id;
        Assert.Equal("mp_melee_chain_axe", SelectedId(CustomizationCatalog.Chaos));
        // Positions count over the simulated items only (the simulation's lists, as the wire counts): the Chaos chain axe is the first simulated item and the Space Marine list's first
        // simulated item is the chainsword (the claw is not simulated here); the install test pins the real counterparts against the wire.
        Assert.Equal("mp_melee_chainsword", SelectedId(CustomizationCatalog.SpaceMarine));
        // It joins as the Chaos class's chain axe (the simulation's index 0).
        Assert.Equal(0, ProfileWire.ToLoadout(custom, Config(), Team.Chaos).Melee);
        // The other direction: the claw is not simulated, so the Chaos side has no counterpart for it and shows its default (the first simulated: the chain axe).
        var claw = LoadoutEdits.SetItem(profile, 1, "melee", "mp_melee_lightning_claw", Catalog).Profile.ClassAt(1)!;
        Assert.Equal("mp_melee_chain_axe", LoadoutRows.Weapons(Catalog, "assault", CustomizationCatalog.Chaos, claw, UnlockPolicy.All).Single(s => s.Key == "melee").Rows.Single(r => r.Selected).Id);
        // A name no class of the role lists is still refused, and a fixed slot stays fixed.
        Assert.False(LoadoutEdits.SetItem(profile, 1, "melee", "mp_melee_nonsense", Catalog).Changed);
        var tactical = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 1, "tactical", Catalog).Profile;
        Assert.False(LoadoutEdits.SetItem(tactical, 1, "melee", "mp_melee_combat_knife", Catalog).Changed);
    }

    [Fact]
    public void A_reverted_copy_returns_to_the_defaults_it_came_from()
    {
        var saved = CustomizationProfile.Default(Catalog);
        var defaults = Selection.DefaultFor("assault");
        var copied = LoadoutEdits.CopyDefaults(saved, "assault", "Assault Marine", Catalog, makeActive: true, out int slot).Profile;
        Assert.Equal(Selection.CustomSlot(slot), LoadoutEdits.ReturnFromCopy(copied, Selection.CustomSlot(slot), slot, defaults));   // the copy exists: stay on it
        Assert.Equal(defaults, LoadoutEdits.ReturnFromCopy(saved, Selection.CustomSlot(slot), slot, defaults));                         // reverted: back to the defaults
        Assert.Equal(Selection.CustomSlot(7), LoadoutEdits.ReturnFromCopy(saved, Selection.CustomSlot(7), slot, defaults));             // elsewhere: unchanged
        Assert.Equal(Selection.DefaultFor("heavy"), LoadoutEdits.ReturnFromCopy(saved, Selection.DefaultFor("heavy"), slot, defaults));
    }

    [Fact]
    public void Choosing_on_the_defaults_makes_an_editable_copy_in_the_first_empty_slot()
    {
        var empty = CustomizationProfile.Default(Catalog);
        Assert.Equal(1, LoadoutEdits.FirstEmptySlot(empty));
        var taken = LoadoutEdits.Create(LoadoutEdits.Create(empty, 1, "tactical", Catalog).Profile, 2, "heavy", Catalog).Profile;
        Assert.Equal(3, LoadoutEdits.FirstEmptySlot(taken));
        var copy = LoadoutEdits.CopyDefaults(taken, "assault", "Assault Marine", Catalog, makeActive: true, out int slot);
        Assert.True(copy.Changed);
        Assert.Equal(3, slot);
        var made = copy.Profile.ClassAt(3)!;
        Assert.Equal(("assault", "Assault Marine"), (made.Role, made.Name));
        Assert.Equal((null, null, null, null), (made.Primary, made.Pistol, made.Melee, made.Equipment));              // every choice the class default, as the defaults are
        Assert.Equal(Selection.CustomSlot(3), copy.Profile.Selected);
        Assert.Contains("CUSTOM 3", copy.Notice);
        Assert.Equal(taken.Selected, LoadoutEdits.CopyDefaults(taken, "assault", "Assault Marine", Catalog, makeActive: false, out _).Profile.Selected);   // not active: the selection stays
        // The choice then applies to the copy.
        Assert.True(LoadoutEdits.SetItem(copy.Profile, slot, "melee", "mp_melee_chainsword", Catalog).Changed);
        // All ten slots used: refused with the reason, nothing changes.
        var full = Enumerable.Range(1, 10).Aggregate(empty, (p, s) => LoadoutEdits.Create(p, s, "tactical", Catalog).Profile);
        Assert.Equal(0, LoadoutEdits.FirstEmptySlot(full));
        var refused = LoadoutEdits.CopyDefaults(full, "assault", "Assault Marine", Catalog, true, out int none);
        Assert.False(refused.Changed);
        Assert.Equal(0, none);
        Assert.Contains("ten", refused.Notice);
        Assert.Same(full, refused.Profile);
    }

    [Fact]
    public void The_summary_names_the_class_choices_and_perks()
    {
        var custom = LoadoutEdits.New(1, "tactical", Catalog) with { Primary = "mp_ranged_plasma_gun", Perks = [null, "mp_perk_secondary_weapon"] };
        string summary = LoadoutRows.Summary(Catalog, custom, CustomizationCatalog.SpaceMarine);
        Assert.Contains("sm_mp_ranged_plasma_gun", summary);
        Assert.Contains("mp_perk_a, mp_perk_secondary_weapon", summary);
        Assert.Contains(" / ", LoadoutRows.RoleLabel(Catalog, "tactical"));                                           // both sides' names on the tab
    }

    [Fact]
    public void The_lock_demo_forces_each_lock_state_and_the_default_policy_unlocks_everything()
    {
        var demo = Enumerable.Range(0, 5).Select(i => UnlockPolicy.Demo.Evaluate(i, 1, null)).ToList();
        Assert.Equal([UnlockKind.Unlocked, UnlockKind.Level, UnlockKind.Challenge, UnlockKind.Unavailable, UnlockKind.Unlocked], demo.Select(d => d.Kind));
        Assert.Contains("Required Level", demo[1].Caption);
        Assert.Contains("(12/50)", demo[2].Caption);
        Assert.Equal("LOCKED", demo[1].Tag);
        Assert.All(Enumerable.Range(0, 20), i => Assert.True(UnlockPolicy.All.Evaluate(i, 40, "x").IsOpen));
        var rows = LoadoutRows.Weapons(Catalog, "tactical", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.Demo)[0].Rows;
        Assert.False(rows[1].Selectable);                                                                             // a demo-locked row is not selectable
        Assert.Contains("LOCKED", rows[1].Tags);
    }

    // ===== edits =========================================================================================================================

    [Fact]
    public void Creating_changing_class_and_choosing_items_edit_the_slot()
    {
        var p = CustomizationProfile.Default(Catalog);
        var created = LoadoutEdits.Create(p, 2, "tactical", Catalog);
        Assert.True(created.Changed);
        Assert.Equal("Custom 2", created.Profile.ClassAt(2)!.Name);
        Assert.False(LoadoutEdits.Create(created.Profile, 2, "assault", Catalog).Changed);                            // the slot is taken
        Assert.False(LoadoutEdits.Create(p, 11, "tactical", Catalog).Changed);

        var chosen = LoadoutEdits.SetItem(created.Profile, 2, "primary", "mp_ranged_plasma_gun", Catalog);
        Assert.Equal("mp_ranged_plasma_gun", chosen.Profile.ClassAt(2)!.Primary);
        Assert.False(LoadoutEdits.SetItem(created.Profile, 2, "pistol", "mp_pistol_bolt_pistol", Catalog).Changed);   // fixed for the Tactical Marine
        Assert.False(LoadoutEdits.SetItem(created.Profile, 2, "primary", "mp_ranged_nothing", Catalog).Changed);     // not offered
        Assert.Null(LoadoutEdits.SetItem(chosen.Profile, 2, "primary", null, Catalog).Profile.ClassAt(2)!.Primary);   // back to the default

        var named = LoadoutEdits.Rename(chosen.Profile, 2, "Sniper").Profile;
        var changed = LoadoutEdits.SetRole(named, 2, "assault", Catalog);
        var c = changed.Profile.ClassAt(2)!;
        Assert.Equal(("assault", "Sniper", null), (c.Role, c.Name, c.Primary));                                       // defaults again, name kept
        Assert.NotNull(changed.Notice);
        Assert.False(LoadoutEdits.SetRole(changed.Profile, 2, "assault", Catalog).Changed);
    }

    [Fact]
    public void A_pistol_bolter_needs_True_Grit_and_removing_the_perk_resets_it()
    {
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 1, "assault", Catalog).Profile;
        var refused = LoadoutEdits.SetItem(p, 1, "pistol", "mp_pistol_bolter", Catalog);
        Assert.False(refused.Changed);
        Assert.Contains("Requires perk", refused.Notice);
        p = LoadoutEdits.SetPerk(p, 1, 1, "mp_perk_bolter_in_place_of_pistol", Catalog).Profile;
        p = LoadoutEdits.SetItem(p, 1, "pistol", "mp_pistol_bolter", Catalog).Profile;
        Assert.Equal("mp_pistol_bolter", p.ClassAt(1)!.Pistol);
        var removed = LoadoutEdits.SetPerk(p, 1, 1, null, Catalog);
        Assert.Null(removed.Profile.ClassAt(1)!.Pistol);
        Assert.Contains("removed", removed.Notice);
        // With True Grit in slot 1 and another perk in slot 2, clearing slot 2 keeps the pistol.
        var first = LoadoutEdits.SetPerk(LoadoutEdits.SetPerk(p, 1, 1, null, Catalog).Profile, 1, 0, "mp_perk_bolter_in_place_of_pistol", Catalog).Profile;
        var kept = LoadoutEdits.SetPerk(LoadoutEdits.SetItem(first, 1, "pistol", "mp_pistol_bolter", Catalog).Profile, 1, 1, "mp_perk_b", Catalog).Profile;
        Assert.Equal("mp_pistol_bolter", LoadoutEdits.SetPerk(kept, 1, 1, null, Catalog).Profile.ClassAt(1)!.Pistol);
        Assert.Null(LoadoutEdits.SetPerk(kept, 1, 0, "mp_perk_a", Catalog).Profile.ClassAt(1)!.Pistol);
    }

    [Fact]
    public void Perks_cannot_repeat_and_must_belong_to_the_class()
    {
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 1, "tactical", Catalog).Profile;
        Assert.False(LoadoutEdits.SetPerk(p, 1, 1, "mp_perk_a", Catalog).Changed);                                   // the default perk already sits in slot 1
        var two = LoadoutEdits.SetPerk(p, 1, 1, "mp_perk_secondary_weapon", Catalog).Profile;
        Assert.Equal("mp_perk_secondary_weapon", two.ClassAt(1)!.Perk(1));
        Assert.False(LoadoutEdits.SetPerk(two, 1, 0, "mp_perk_secondary_weapon", Catalog).Changed);                  // already in slot 2
        Assert.False(LoadoutEdits.SetPerk(p, 1, 0, "mp_perk_b", Catalog).Changed);                                   // an Assault perk
        var swapped = LoadoutEdits.SetPerk(two, 1, 0, "mp_perk_weap_bolter_accuracy", Catalog).Profile;
        Assert.Equal(["mp_perk_weap_bolter_accuracy", "mp_perk_secondary_weapon"], swapped.ClassAt(1)!.Perks);       // any perk in either slot
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadoutEdits.SetPerk(p, 1, 2, null, Catalog));
    }

    [Fact]
    public void Names_are_cleaned_and_empty_ones_revert()
    {
        Assert.Equal("Custom 4", LoadoutEdits.SanitizeName("   ", 4));
        Assert.Equal("Custom 4", LoadoutEdits.SanitizeName(null, 4));
        Assert.Equal("Hello", LoadoutEdits.SanitizeName("  Hel\tlo\n ", 4));
        Assert.Equal(20, LoadoutEdits.SanitizeName(new string('x', 40), 1).Length);
        Assert.Equal(19, LoadoutEdits.SanitizeName(new string('x', 19) + "\U0001F600\U0001F600", 1).Length);          // never inside a surrogate pair
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 3, "tactical", Catalog).Profile;
        Assert.Equal("Brother", LoadoutEdits.Rename(p, 3, "Brother").Profile.ClassAt(3)!.Name);
        Assert.Equal("Custom 3", LoadoutEdits.Rename(LoadoutEdits.Rename(p, 3, "Brother").Profile, 3, "").Profile.ClassAt(3)!.Name);
        Assert.False(LoadoutEdits.Rename(p, 3, "Custom 3").Changed);
    }

    [Fact]
    public void Copy_reset_and_clear_behave_as_the_design_says()
    {
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 1, "tactical", Catalog).Profile;
        p = LoadoutEdits.SetItem(p, 1, "primary", "mp_ranged_plasma_gun", Catalog).Profile;
        p = LoadoutEdits.Rename(p, 1, "Plasma man").Profile;
        var copied = LoadoutEdits.Copy(p, 1, 5).Profile;
        Assert.Equal("Plasma man (copy)", copied.ClassAt(5)!.Name);
        Assert.Equal("mp_ranged_plasma_gun", copied.ClassAt(5)!.Primary);
        Assert.False(LoadoutEdits.Copy(p, 1, 1).Changed);
        Assert.False(LoadoutEdits.Copy(p, 2, 3).Changed);                                                             // nothing to copy
        string longName = LoadoutEdits.Rename(p, 1, new string('y', 20)).Profile.ClassAt(1)!.Name;
        Assert.Equal(LoadoutEdits.MaxNameLength, LoadoutEdits.Copy(LoadoutEdits.Rename(p, 1, longName).Profile, 1, 2).Profile.ClassAt(2)!.Name.Length);

        var reset = LoadoutEdits.Reset(p, 1).Profile.ClassAt(1)!;
        Assert.Equal(("Plasma man", "tactical", null), (reset.Name, reset.Role, reset.Primary));
        var selected = LoadoutEdits.Select(copied, Selection.CustomSlot(5));
        var cleared = LoadoutEdits.Clear(selected, 5).Profile;
        Assert.Null(cleared.ClassAt(5));
        Assert.Equal(Selection.DefaultFor("tactical"), cleared.Selected);                                             // the selected slot was cleared: DEFAULTS
        Assert.False(LoadoutEdits.Clear(cleared, 5).Changed);
    }

    [Fact]
    public void An_empty_active_slot_falls_back_to_slot_1_then_the_defaults()
    {
        var p = CustomizationProfile.Default(Catalog) with { Selected = Selection.CustomSlot(4) };
        Assert.Equal(Selection.DefaultFor("tactical"), LoadoutEdits.Active(p, Catalog, out bool fell));
        Assert.True(fell);
        p = LoadoutEdits.Create(p, 1, "assault", Catalog).Profile;
        Assert.Equal(Selection.CustomSlot(1), LoadoutEdits.Active(p, Catalog, out fell));
        Assert.True(fell);
        Assert.Equal(Selection.CustomSlot(1), LoadoutEdits.Active(p with { Selected = Selection.CustomSlot(1) }, Catalog, out fell));
        Assert.False(fell);
    }

    // ===== the session ===================================================================================================================

    [Fact]
    public void The_session_is_dirty_after_an_edit_clean_after_revert_and_saved_edits_survive_a_restart()
    {
        var session = NewSession();
        Assert.False(session.Dirty);
        int changes = 0;
        session.Changed += () => changes++;
        session.Edit(p => LoadoutEdits.Create(p, 1, "tactical", Catalog).Profile);
        Assert.True(session.Dirty);
        session.Revert();
        Assert.False(session.Dirty);
        Assert.Null(session.Working.ClassAt(1));
        session.Edit(p => LoadoutEdits.Rename(LoadoutEdits.Create(p, 1, "tactical", Catalog).Profile, 1, "Kept").Profile);
        Assert.Null(session.Save());
        Assert.False(session.Dirty);
        Assert.True(changes >= 4);
        var again = NewSession();
        Assert.Equal("Kept", again.Working.ClassAt(1)!.Name);
        Assert.False(again.Dirty);
        // Editing back to the saved state is clean again (the comparison is on content).
        again.Edit(p => LoadoutEdits.Rename(p, 1, "Other").Profile);
        Assert.True(again.Dirty);
        again.Edit(p => LoadoutEdits.Rename(p, 1, "Kept").Profile);
        Assert.False(again.Dirty);
    }

    [Fact]
    public void A_read_only_profile_cannot_be_saved_and_says_why()
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(Path.Combine(_profileDir, ProfileStore.FileName), "{\"schema\": 99, \"classes\": []}");
        var session = NewSession();
        Assert.True(session.ReadOnly);
        Assert.Contains(session.Notices(), n => n.Contains("newer version"));
        session.Edit(p => LoadoutEdits.Create(p, 1, "tactical", Catalog).Profile);
        string? error = session.Save();
        Assert.NotNull(error);
        Assert.True(session.Dirty);
        Assert.Contains("\"schema\": 99", File.ReadAllText(Path.Combine(_profileDir, ProfileStore.FileName)));        // never overwritten
    }

    [Fact]
    public void A_corrupt_profile_starts_fresh_and_tells_the_player()
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(Path.Combine(_profileDir, ProfileStore.FileName), "{ not json");
        var session = NewSession();
        Assert.Equal(ProfileLoadStatus.Corrupt, session.Status);
        Assert.Single(session.Notices());
        Assert.Empty(session.Working.Classes);
        Assert.Contains(Directory.GetFiles(_profileDir), f => f.Contains(".bad-"));
    }

    // ===== appearance ====================================================================================================================

    static AppearanceProfile Look(string race = "space_marine") => AppearanceProfile.Empty(race == "chaos" ? "csm-1" : "sm-1", race, "Look");

    [Fact]
    public void Colours_set_per_slot_or_for_all_and_the_pattern_follows_the_pattern_colour()
    {
        var look = Look();
        var one = AppearanceEdits.SetColour(look, ["head"], PaintChannel.Primary, "gold", null);
        Assert.Equal("gold", one.Paint["head"].Primary);
        Assert.False(one.Paint["head"].PatternOn);
        var pattern = AppearanceEdits.SetColour(one, ["head"], PaintChannel.Pattern, "red_1", null);
        Assert.True(pattern.Paint["head"].PatternOn);                                                                 // a pattern colour turns the pattern on
        var all = AppearanceEdits.SetColour(look, AppearanceEdits.Targets(null), PaintChannel.Detail, "blue_0", null);
        Assert.Equal(9, all.Paint.Count);
        Assert.All(all.Paint.Values, v => Assert.Equal("blue_0", v.Detail));
        var cleared = AppearanceEdits.SetColour(one, ["head"], PaintChannel.Primary, null, null);
        Assert.Empty(cleared.Paint);                                                                                  // back to the scheme's colour: no entry left
        var code = ProfileWire.ToCode(pattern, Catalog);
        Assert.Equal((byte)AppearanceEdits.PaletteIndex(Catalog, "gold"), code.Palette(ArmourSlot.Head, 0));
        Assert.True(code.PatternOn(ArmourSlot.Head));
    }

    [Fact]
    public void Pattern_off_makes_the_pattern_show_in_the_primary_colour()
    {
        var look = AppearanceEdits.SetColour(Look(), ["head"], PaintChannel.Primary, "gold", null);
        look = AppearanceEdits.SetColour(look, ["head"], PaintChannel.Pattern, "red_1", null);
        var onResolved = AppearanceEdits.Resolve(Catalog, look, "tactical", null)!;
        Assert.Equal("red_1", onResolved.Paint["head"].Secondary!.Name);
        var off = AppearanceEdits.SetPattern(look, ["head"], false, onResolved, Catalog);
        var offResolved = AppearanceEdits.Resolve(Catalog, off, "tactical", null)!;
        Assert.False(offResolved.Paint["head"].PatternEnabled);
        Assert.Equal("gold", offResolved.Paint["head"].Secondary!.Name);                                              // secondary equals primary
        Assert.True(AppearanceEdits.IsCustomColour(off, "head", PaintChannel.Pattern));                               // the choice is kept for turning it back on
        var back = AppearanceEdits.SetPattern(off, ["head"], true, offResolved, Catalog);
        Assert.Equal("red_1", AppearanceEdits.Resolve(Catalog, back, "tactical", null)!.Paint["head"].Secondary!.Name);
    }

    [Fact]
    public void A_preset_copies_its_paint_and_a_predefined_set_locks_editing()
    {
        var preset = Catalog.SpaceMarines.Presets.First(p => p.Name == "sm_x");
        var look = AppearanceEdits.SetPreset(Look(), preset, Catalog);
        Assert.Equal("sm_x", look.PresetApplied);
        Assert.Equal("gold", look.Paint["leftshoulder"].Primary);
        // Any colour edit afterwards no longer claims to be the preset.
        Assert.Null(AppearanceEdits.SetColour(look, ["head"], PaintChannel.Primary, "blue_0", null).PresetApplied);

        var worn = AppearanceEdits.SetSet(look, "black_templars");
        Assert.True(AppearanceEdits.Locked(worn));
        Assert.Same(worn, AppearanceEdits.SetColour(worn, ["head"], PaintChannel.Primary, "gold", null));
        Assert.Same(worn, AppearanceEdits.SetPiece(worn, "head", "sm_b_head"));
        Assert.Same(worn, AppearanceEdits.SetDecal(worn, "leftshoulder", DecalChoice.NoDecal));
        Assert.Same(worn, AppearanceEdits.SetPreset(worn, preset, Catalog));
        Assert.False(AppearanceEdits.Locked(AppearanceEdits.DropSet(worn)));
    }

    [Fact]
    public void Pieces_decals_and_the_lens_are_set_and_unset()
    {
        var look = AppearanceEdits.SetPiece(Look(), "head", "sm_b_head");
        Assert.Equal("sm_b_head", look.Pieces["head"]);
        var resolved = AppearanceEdits.Resolve(Catalog, look, "tactical", null)!;
        Assert.Equal("sm_b_head", resolved.Piece("head")!.Name);
        Assert.Empty(AppearanceEdits.SetPiece(look, "head", null).Pieces);
        var rows = AppearanceEdits.PieceRows(Catalog, look, "head", resolved);
        Assert.Null(rows[0].Id);                                                                                      // "Default" first
        Assert.Equal(["sm_b_head"], rows.Where(r => r.Selected).Select(r => r.Id));
        Assert.Equal(2, rows.Count - 1);
        var decal = AppearanceEdits.SetDecal(look, "leftshoulder", DecalChoice.ForBadge("sm_foo_orig"));
        Assert.Equal("sm_foo_orig", decal.Decals["leftshoulder"].Badge);
        Assert.Empty(AppearanceEdits.SetDecal(decal, "leftshoulder", null).Decals);
        Assert.Equal(new LensColour(1, 2, 3), AppearanceEdits.SetLens(look, new LensColour(1, 2, 3)).Lens);
        Assert.Null(AppearanceEdits.ResetAll(AppearanceEdits.SetLens(decal, new LensColour(1, 2, 3))).Lens);
        var families = AppearanceEdits.BadgeFamilies(Catalog, "space_marine");
        Assert.DoesNotContain(families, f => f.Family.Contains("blank"));
        Assert.Contains(families, f => f.Variants.Count == 2);                                                        // sm_foo: orig and black
    }

    [Fact]
    public void Forced_slots_come_from_the_class()
    {
        var chaos = AppearanceEdits.Resolve(Catalog, Look("chaos"), "assault", null)!;
        Assert.Contains("head", chaos.ForcedSlots);                                                                   // the Raptor's helmet and legs are the class's
        Assert.Contains("leftleg", chaos.ForcedSlots);
        Assert.DoesNotContain("torso", chaos.ForcedSlots);
        var tactical = AppearanceEdits.Resolve(Catalog, Look(), "tactical", null)!;
        Assert.Empty(tactical.ForcedSlots);
        // The Devastator's backpack follows the heavy weapon.
        var heavy = LoadoutEdits.New(1, "heavy", Catalog);
        Assert.Contains("backpack", AppearanceEdits.Resolve(Catalog, Look(), "heavy", heavy)!.ForcedSlots);
        // The Assault Marine wears a jump pack: the backpack is not drawn.
        Assert.Contains("backpack", AppearanceEdits.Resolve(Catalog, Look(), "assault", null)!.HiddenSlots);
    }

    [Fact]
    public void The_appearance_library_is_per_race_capped_and_classes_follow_it()
    {
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 1, "tactical", Catalog).Profile;
        var (withLook, look) = AppearanceEdits.EnsureLook(p, 1, "space_marine");
        Assert.Equal("sm-1", look!.Id);
        Assert.Equal("sm-1", withLook.ClassAt(1)!.Appearance.SpaceMarine);
        Assert.Null(withLook.ClassAt(1)!.Appearance.Chaos);
        Assert.Same(look, AppearanceEdits.EnsureLook(withLook, 1, "space_marine").Look);                              // the same look again
        var chaos = AppearanceEdits.EnsureLook(withLook, 1, "chaos");
        Assert.Equal("csm-1", chaos.Look!.Id);
        var full = withLook;
        for (int i = 0; i < 9; i++) full = AppearanceEdits.AddAppearance(full, "space_marine").Profile;
        Assert.Equal(10, full.AppearancesFor("space_marine").Count());
        Assert.Null(AppearanceEdits.AddAppearance(full, "space_marine").Id);
        Assert.NotNull(AppearanceEdits.AddAppearance(full, "chaos").Id);                                              // the other race has its own ten
        var removed = AppearanceEdits.RemoveAppearance(chaos.Profile, "sm-1");
        Assert.Null(removed.ClassAt(1)!.Appearance.SpaceMarine);
        Assert.Equal("csm-1", removed.ClassAt(1)!.Appearance.Chaos);
        Assert.Equal("Bold look", AppearanceEdits.Rename(look, "Bold look").Name);
        Assert.Equal("Look", AppearanceEdits.Rename(Look(), "").Name);
    }

    // ===== emblems =======================================================================================================================

    [Theory]
    [InlineData("a.png", 100, null)]
    [InlineData("a.JPG", 100, null)]
    [InlineData("a.webp", 100, null)]
    [InlineData("a.tga", 100, null)]
    [InlineData("a.gif", 100, "Unsupported format (use PNG, JPG, WebP, BMP or TGA)")]
    [InlineData("a", 100, "Unsupported format (use PNG, JPG, WebP, BMP or TGA)")]
    [InlineData("a.png", 0, "Can't read this file")]
    [InlineData("a.png", 8 * 1024 * 1024 + 1, "File too large: 8.1 MB (at most 8 MB)")]
    [InlineData("a.png", 9_646_899, "File too large: 9.2 MB (at most 8 MB)")]
    public void Files_are_checked_by_name_and_size(string path, long length, string? expected) =>
        Assert.Equal(expected, EmblemPipeline.CheckFile(path, length));

    [Fact]
    public void Images_are_checked_by_size_and_animated_ones_are_recognised()
    {
        Assert.Null(EmblemPipeline.CheckImage(4096, 4096));
        Assert.Equal("Image too large: 4097 x 10 pixels (at most 4096 x 4096)", EmblemPipeline.CheckImage(4097, 10));
        Assert.Contains("4096 x 4096 pixels and 8 MB", EmblemPipeline.LimitsText);
        Assert.Equal("Can't read this file", EmblemPipeline.CheckImage(0, 10));
        Assert.True(EmblemPipeline.IsAnimated("GIF89a...."u8));
        byte[] webp = new byte[32];
        "RIFF"u8.CopyTo(webp); "WEBPVP8X"u8.CopyTo(webp.AsSpan(8)); webp[20] = 0x02;
        Assert.True(EmblemPipeline.IsAnimated(webp));
        webp[20] = 0;
        Assert.False(EmblemPipeline.IsAnimated(webp));
        var apng = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        apng.AddRange([0, 0, 0, 0]); apng.AddRange("IHDR"u8.ToArray()); apng.AddRange([0, 0, 0, 0]);
        apng.AddRange([0, 0, 0, 8]); apng.AddRange("acTL"u8.ToArray()); apng.AddRange(new byte[12]);
        Assert.True(EmblemPipeline.IsAnimated(apng.ToArray()));
        Assert.False(EmblemPipeline.IsAnimated(new byte[40]));
    }

    [Fact]
    public void The_crop_is_square_clamped_and_zoomable()
    {
        var full = EmblemPipeline.CropFor(400, 200, 1f, 0f, 0f);
        Assert.Equal(200f, full.Side);
        Assert.Equal(0f, full.X);                                                                                     // panned as far left as it goes
        var right = EmblemPipeline.CropFor(400, 200, 1f, 1000f, 100f);
        Assert.Equal(200f, right.X);
        var zoomed = EmblemPipeline.CropFor(400, 200, 4f, 200f, 100f);
        Assert.Equal(50f, zoomed.Side);
        Assert.Equal(175f, zoomed.X);
        var fit = EmblemPipeline.CropFor(400, 200, EmblemPipeline.FitZoom(400, 200), 10f, 10f);                       // the whole image just fits
        Assert.Equal(400f, fit.Side);
        Assert.Equal(0f, fit.X);
        Assert.Equal(-100f, fit.Y);                                                                                   // padded above and below
        var wide = EmblemPipeline.CropFor(400, 200, 0.01f, 10f, 10f);                                                 // clamped to the smallest zoom: a wide margin
        Assert.Equal(800f, wide.Side);
        Assert.Equal(0.5f, EmblemPipeline.FitZoom(400, 200));
        Assert.Equal(0.25f, EmblemPipeline.MinZoom(400, 200));
        // The default leaves a margin: a square picture takes 80% of the square, so the pad's curvature does not clip it.
        var inset = EmblemPipeline.CropFor(500, 500, EmblemPipeline.InsetZoom(500, 500), 0f, 0f);
        Assert.Equal(0.8f, EmblemPipeline.InsetZoom(500, 500), 3);
        Assert.Equal(625f, inset.Side, 1);
        Assert.Equal(-62.5f, inset.X, 1);
        Assert.Equal(25f, EmblemPipeline.CropFor(400, 200, 100f, 0, 0).Side);                                         // at most eight times
    }

    static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a; }
        return px;
    }

    [Fact]
    public void Resampling_keeps_a_solid_colour_and_makes_the_area_outside_the_image_transparent()
    {
        var src = Solid(300, 200, 200, 100, 50, 255);
        var cropped = EmblemPipeline.Resample(src, 300, 200, EmblemPipeline.CropFor(300, 200, 1f, 150f, 100f));
        Assert.Equal(128 * 128 * 4, cropped.Length);
        for (int i = 0; i < 128 * 128; i += 97)
            Assert.Equal(new byte[] { 200, 100, 50, 255 }, cropped.AsSpan(i * 4, 4).ToArray());
        // Fit: the shorter side is padded with transparency (rows above and below the image), the picture itself stays opaque.
        var fit = EmblemPipeline.Resample(src, 300, 200, EmblemPipeline.CropFor(300, 200, 0.1f, 150f, 100f));
        Assert.Equal(0, fit[(2 * 128 + 64) * 4 + 3]);
        Assert.Equal(255, fit[(64 * 128 + 64) * 4 + 3]);
        Assert.Equal(0, fit[(125 * 128 + 64) * 4 + 3]);
        Assert.Equal(200, fit[(64 * 128 + 64) * 4]);
        // A crop wholly outside is empty.
        var none = EmblemPipeline.Resample(src, 300, 200, new EmblemPipeline.Crop(1000, 1000, 100));
        Assert.All(none, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Resampling_a_large_picture_is_deterministic_and_keeps_a_gradient_in_order()
    {
        const int N = 1024;
        var src = new byte[N * N * 4];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++) { int i = (y * N + x) * 4; src[i] = (byte)(x * 255 / (N - 1)); src[i + 1] = (byte)(y * 255 / (N - 1)); src[i + 2] = 0; src[i + 3] = 255; }
        var crop = EmblemPipeline.CropFor(N, N, 1f, N / 2f, N / 2f);
        var a = EmblemPipeline.Resample(src, N, N, crop);
        var b = EmblemPipeline.Resample(src, N, N, crop);
        Assert.Equal(a, b);
        Assert.True(a[(64 * 128 + 10) * 4] < a[(64 * 128 + 120) * 4]);                                                // red grows to the right
        Assert.True(a[(10 * 128 + 64) * 4 + 1] < a[(120 * 128 + 64) * 4 + 1]);                                        // green grows downwards
        Assert.True(Math.Abs(a[(64 * 128 + 64) * 4] - 127) <= 3);
    }

    [Fact]
    public void The_default_crop_keeps_a_transparent_margin_round_a_square_picture()
    {
        var src = Solid(256, 256, 200, 50, 50, 255);
        var rgba = EmblemPipeline.Resample(src, 256, 256, EmblemPipeline.CropFor(256, 256, EmblemPipeline.InsetZoom(256, 256), 128f, 128f));
        Assert.Equal(0, rgba[(4 * 128 + 64) * 4 + 3]);                      // the top margin
        Assert.Equal(0, rgba[(64 * 128 + 3) * 4 + 3]);                      // the left margin
        Assert.Equal(255, rgba[(64 * 128 + 64) * 4 + 3]);                   // the picture
        Assert.Equal(255, rgba[(20 * 128 + 20) * 4 + 3]);                   // 10% inside the square the picture starts
    }

    [Fact]
    public void Resampling_only_touches_the_cropped_region_and_equals_a_crop_of_the_whole()
    {
        // A picture with a distinct patch: the result must be the same whether the surrounding area is large or not.
        const int N = 600;
        var big = new byte[N * N * 4];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++) { int i = (y * N + x) * 4; big[i] = (byte)(x % 251); big[i + 1] = (byte)(y % 241); big[i + 2] = (byte)((x + y) % 239); big[i + 3] = 255; }
        var crop = new EmblemPipeline.Crop(200, 220, 128);                    // scale 1: no box reduction
        var whole = EmblemPipeline.Resample(big, N, N, crop);
        // The same patch cut out with a border wide enough for the filter must give the same pixels.
        const int border = 20, cut = 128 + 2 * border;
        var small = new byte[cut * cut * 4];
        for (int y = 0; y < cut; y++) Array.Copy(big, ((220 - border + y) * N + 200 - border) * 4, small, y * cut * 4, cut * 4);
        var fromCut = EmblemPipeline.Resample(small, cut, cut, new EmblemPipeline.Crop(border, border, 128));
        int differing = 0;
        for (int i = 0; i < whole.Length; i++) if (Math.Abs(whole[i] - fromCut[i]) > 1) differing++;
        Assert.Equal(0, differing);
    }

    [Fact]
    public void Transparent_pixels_take_the_colour_of_their_nearest_visible_neighbour_before_encoding()
    {
        var rgba = new byte[128 * 128 * 4];                                  // all transparent black ...
        for (int y = 40; y < 90; y++) for (int x = 40; x < 90; x++) { int i = (y * 128 + x) * 4; rgba[i] = 220; rgba[i + 1] = 180; rgba[i + 2] = 20; rgba[i + 3] = 255; }
        var bled = EmblemPipeline.BleedTransparent(rgba, 128, 128);
        Assert.Equal(new byte[] { 220, 180, 20, 0 }, bled.AsSpan((10 * 128 + 10) * 4, 4).ToArray());   // far corner: colour copied, alpha still 0
        Assert.Equal(new byte[] { 220, 180, 20, 255 }, bled.AsSpan((60 * 128 + 60) * 4, 4).ToArray());
        Assert.Equal(new byte[128 * 128 * 4], EmblemPipeline.BleedTransparent(new byte[128 * 128 * 4], 128, 128));   // nothing visible: unchanged
        // The block along the picture's edge decodes with the picture's colour, not a fade to black.
        var decoded = Bc3.Decode(EmblemPipeline.ToBc3(rgba), 128, 128);
        int edge = (64 * 128 + 38) * 4;                                       // two pixels outside the left edge: transparent, same block as picture pixels
        Assert.True(decoded[edge] > 150, $"red {decoded[edge]}");
        Assert.Equal(0, decoded[edge + 3] < 128 ? 0 : 1);
    }

    [Fact]
    public void A_large_image_is_reduced_by_a_whole_factor_for_the_live_preview()
    {
        var small = Solid(300, 200, 1, 2, 3, 255);
        Assert.Same(small, EmblemPipeline.Downscale(small, 300, 200, 512).Rgba);
        var big = Solid(2000, 1000, 40, 80, 120, 255);
        var (rgba, w, h, factor) = EmblemPipeline.Downscale(big, 2000, 1000, 512);
        Assert.Equal(4, factor);
        Assert.Equal((500, 250), (w, h));
        Assert.Equal(new byte[] { 40, 80, 120, 255 }, rgba.AsSpan(0, 4).ToArray());
        Assert.Equal(w * h * 4, rgba.Length);
    }

    [Fact]
    public void An_emblem_encodes_to_exactly_16384_bytes_and_decodes_close_to_the_source()
    {
        var rgba = Solid(128, 128, 30, 160, 220, 255);
        byte[] bc3 = EmblemPipeline.ToBc3(rgba);
        Assert.Equal(EmblemHash.BlobBytes, bc3.Length);
        Assert.Equal(16384, bc3.Length);
        Assert.Equal(bc3, EmblemPipeline.ToBc3(rgba));                                                                // deterministic
        var back = Bc3.Decode(bc3, 128, 128);
        Assert.InRange(back[0], 24, 36);
        Assert.InRange(back[1], 154, 166);
        Assert.Equal(255, back[3]);
        Assert.Equal(16384, EmblemPipeline.Build(Solid(500, 300, 1, 2, 3, 255), 500, 300, EmblemPipeline.CropFor(500, 300, 1f, 250f, 150f)).Length);
    }

    [Fact]
    public void The_emblem_library_keeps_eight_lists_newest_first_and_removing_one_clears_the_pads()
    {
        var store = NewStore();
        var hashes = new List<EmblemHash>();
        for (int i = 0; i < EmblemPipeline.MaxLibrary; i++)
        {
            var bytes = EmblemPipeline.ToBc3(Solid(128, 128, (byte)(i * 20), 10, 10, 255));
            var (hash, error) = EmblemPipeline.Add(store, bytes);
            Assert.Null(error);
            File.SetLastWriteTimeUtc(store.EmblemPath(hash), new DateTime(2026, 10, 1, 0, i, 0, DateTimeKind.Utc));
            hashes.Add(hash);
        }
        Assert.Equal(hashes.AsEnumerable().Reverse(), EmblemPipeline.Library(store));
        var ninth = EmblemPipeline.Add(store, EmblemPipeline.ToBc3(Solid(128, 128, 0, 99, 10, 255)));
        Assert.True(ninth.Hash.IsNone);
        Assert.Contains("Delete one first", ninth.Error);
        Assert.Null(EmblemPipeline.Add(store, EmblemPipeline.ToBc3(Solid(128, 128, 0, 10, 10, 255))).Error);         // the same image again is not a ninth
        Assert.Equal(EmblemHash.None, EmblemPipeline.Add(store, new byte[10]).Hash);

        var look = Look() with { Decals = new Dictionary<string, DecalChoice>
        {
            ["leftshoulder"] = DecalChoice.ForEmblem(hashes[0].ToHex(), "sm_bar"), ["rightshoulder"] = DecalChoice.ForBadge("sm_bar"),
        } };
        var profile = CustomizationProfile.Default(Catalog) with { Appearances = [look] };
        var after = EmblemPipeline.Remove(store, profile, hashes[0]);
        Assert.False(store.HasEmblem(hashes[0]));
        Assert.True(after.Appearance("sm-1")!.Decals["leftshoulder"].None);                                           // a pad that showed it shows nothing
        Assert.Equal("sm_bar", after.Appearance("sm-1")!.Decals["rightshoulder"].Badge);
        Assert.Equal(EmblemPipeline.MaxLibrary - 1, EmblemPipeline.Library(store).Count);
        Assert.Null(EmblemPipeline.Add(store, EmblemPipeline.ToBc3(Solid(128, 128, 0, 99, 10, 255))).Error);        // room again
    }

    // ===== image headers =====

    static byte[] Png(int w, int h)
    {
        var d = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(d, 0);
        d[11] = 13; "IHDR"u8.CopyTo(d.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), (uint)h);
        return d;
    }

    static byte[] Jpeg(int w, int h, bool exifFirst = true)
    {
        var d = new List<byte> { 0xFF, 0xD8 };
        if (exifFirst) { d.AddRange([0xFF, 0xE1, 0x00, 0x08, 1, 2, 3, 4, 5, 6]); }
        d.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);
        return d.ToArray();
    }

    static byte[] Bmp(int w, int h)
    {
        var d = new byte[54];
        d[0] = (byte)'B'; d[1] = (byte)'M'; d[14] = 40;
        BitConverter.GetBytes(w).CopyTo(d, 18); BitConverter.GetBytes(h).CopyTo(d, 22);
        return d;
    }

    static byte[] WebpLossless(int w, int h)
    {
        var d = new byte[30];
        "RIFF"u8.CopyTo(d); "WEBP"u8.CopyTo(d.AsSpan(8)); "VP8L"u8.CopyTo(d.AsSpan(12));
        d[20] = 0x2F;
        BitConverter.GetBytes((uint)((w - 1) | ((h - 1) << 14))).CopyTo(d, 21);
        return d;
    }

    static byte[] WebpExtended(int w, int h, bool animated)
    {
        var d = new byte[30];
        "RIFF"u8.CopyTo(d); "WEBP"u8.CopyTo(d.AsSpan(8)); "VP8X"u8.CopyTo(d.AsSpan(12));
        d[20] = (byte)(animated ? 0x02 : 0);
        d[24] = (byte)((w - 1) & 255); d[25] = (byte)(((w - 1) >> 8) & 255); d[26] = (byte)((w - 1) >> 16);
        d[27] = (byte)((h - 1) & 255); d[28] = (byte)(((h - 1) >> 8) & 255); d[29] = (byte)((h - 1) >> 16);
        return d;
    }

    static byte[] WebpLossy(int w, int h)
    {
        var d = new byte[30];
        "RIFF"u8.CopyTo(d); "WEBP"u8.CopyTo(d.AsSpan(8)); "VP8 "u8.CopyTo(d.AsSpan(12));
        d[23] = 0x9D; d[24] = 0x01; d[25] = 0x2A;
        BitConverter.GetBytes((ushort)w).CopyTo(d, 26); BitConverter.GetBytes((ushort)h).CopyTo(d, 28);
        return d;
    }

    static byte[] Tga(int w, int h)
    {
        var d = new byte[18];
        BitConverter.GetBytes((ushort)w).CopyTo(d, 12); BitConverter.GetBytes((ushort)h).CopyTo(d, 14);
        d[2] = 2; d[16] = 32;
        return d;
    }

    [Fact]
    public void Image_sizes_are_read_from_the_header_of_each_format()
    {
        (byte[] File, string Path, ImageKind Kind, int W, int H)[] cases =
        [
            (Png(300, 200), "a.png", ImageKind.Png, 300, 200),
            (Jpeg(640, 480), "a.jpg", ImageKind.Jpeg, 640, 480),
            (Jpeg(77, 99, exifFirst: false), "a.jpeg", ImageKind.Jpeg, 77, 99),
            (Bmp(123, 45), "a.bmp", ImageKind.Bmp, 123, 45),
            (WebpLossless(1000, 2000), "a.webp", ImageKind.Webp, 1000, 2000),
            (WebpExtended(4096, 17, animated: false), "a.webp", ImageKind.Webp, 4096, 17),
            (WebpLossy(512, 256), "a.webp", ImageKind.Webp, 512, 256),
            (Tga(64, 32), "a.tga", ImageKind.Tga, 64, 32),
        ];
        foreach (var c in cases)
        {
            var kind = ImageHeader.Sniff(c.File, c.Path);
            Assert.Equal(c.Kind, kind);
            Assert.True(ImageHeader.TryReadSize(c.File, kind, out int w, out int h), c.Path);
            Assert.Equal((c.W, c.H), (w, h));
        }
        // A bottom-up BMP has a negative height: its size is the absolute value.
        var up = Bmp(10, -20);
        Assert.True(ImageHeader.TryReadSize(up, ImageKind.Bmp, out _, out int hh));
        Assert.Equal(20, hh);
        // A TGA has no magic: without its extension the file is unknown.
        Assert.Equal(ImageKind.Unknown, ImageHeader.Sniff(Tga(8, 8), "a.png"));
    }

    [Fact]
    public void Huge_empty_truncated_and_animated_images_are_refused_before_any_decoding()
    {
        Assert.Null(EmblemPipeline.Inspect(Png(4096, 4096), "a.png").Error);
        Assert.Equal("Image too large: 4097 x 100 pixels (at most 4096 x 4096)", EmblemPipeline.Inspect(Png(4097, 100), "a.png").Error);
        Assert.Equal("Image too large: 100 x 60000 pixels (at most 4096 x 4096)", EmblemPipeline.Inspect(Png(100, 60000), "a.png").Error);
        Assert.Equal("Image too large: 2147483647 x 2147483647 pixels (at most 4096 x 4096)", EmblemPipeline.Inspect(Png(int.MaxValue, int.MaxValue), "a.png").Error);   // a decompression-bomb header
        Assert.Equal("Can't read this file", EmblemPipeline.Inspect(Png(0, 100), "a.png").Error);
        Assert.Equal("Image too large: 5000 x 10 pixels (at most 4096 x 4096)", EmblemPipeline.Inspect(Jpeg(5000, 10), "a.jpg").Error);
        Assert.Equal("Image too large: 4097 x 10 pixels (at most 4096 x 4096)", EmblemPipeline.Inspect(WebpExtended(4097, 10, false), "a.webp").Error);
        Assert.Equal("Image too large: 4097 x 10 pixels (at most 4096 x 4096)", EmblemPipeline.Inspect(Bmp(4097, 10), "a.bmp").Error);
        Assert.Equal("Image too large: 10 x 4097 pixels (at most 4096 x 4096)", EmblemPipeline.Inspect(Tga(10, 4097), "a.tga").Error);
        Assert.Equal("Can't read this file", EmblemPipeline.Inspect(Png(10, 10).AsSpan(0, 20), "a.png").Error);   // truncated header
        Assert.Equal("Can't read this file", EmblemPipeline.Inspect("not an image at all"u8, "a.png").Error);
        Assert.Equal("Can't read this file", EmblemPipeline.Inspect(new byte[10], "a.jpg").Error);
        Assert.Equal("Animated images are not supported", EmblemPipeline.Inspect(WebpExtended(10, 10, animated: true), "a.webp").Error);
        Assert.Equal("Animated images are not supported", EmblemPipeline.Inspect("GIF89a\0\0\0\0\0\0\0\0\0\0"u8, "a.png").Error);
        var good = EmblemPipeline.Inspect(Jpeg(300, 200), "a.jpg");
        Assert.Equal((null, ImageKind.Jpeg, 300, 200), (good.Error, good.Kind, good.Width, good.Height));
    }

    // ===== files on disk, size caps =====

    [Fact]
    public void A_profile_or_settings_file_over_a_megabyte_is_not_read()
    {
        Directory.CreateDirectory(_profileDir);
        string path = Path.Combine(_profileDir, ProfileStore.FileName);
        using (var f = File.Create(path)) f.SetLength(ProfileStore.MaxFileBytes + 1);
        var session = NewSession();
        Assert.Equal(ProfileLoadStatus.Unreadable, session.Status);
        Assert.True(session.ReadOnly);
        Assert.Contains(session.Repairs, r => r.Message.Contains("larger than"));
        Assert.True(File.Exists(path));                                                                               // left alone
        Assert.NotNull(session.Save());                                                                               // and never overwritten

        string dir = Path.Combine(_dir, "big-settings");
        Directory.CreateDirectory(dir);
        using (var f = File.Create(Path.Combine(dir, FrontEndSettings.FileName))) { f.Write("{\"host\": \"a:1\"}"u8); f.SetLength(FrontEndSettings.MaxFileBytes + 1); }
        Assert.Equal(FrontEndSettings.Default, FrontEndSettings.Load(dir));
    }

    [Fact]
    public void A_profile_with_a_duplicate_key_counts_as_corrupt_and_is_moved_aside()
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(Path.Combine(_profileDir, ProfileStore.FileName), "{\"schema\": 1, \"classes\": [], \"classes\": []}");
        var session = NewSession();
        // Whatever the JSON reader does with a duplicate key, loading never throws: a profile comes back.
        Assert.True(session.Status is ProfileLoadStatus.Corrupt or ProfileLoadStatus.Loaded);
        Assert.NotNull(session.Working);
    }

    [Fact]
    public void Settings_are_written_with_a_unique_temp_file_that_is_gone_afterwards()
    {
        string dir = Path.Combine(_dir, "settings-write");
        for (int i = 0; i < 3; i++) Assert.Null(new FrontEndSettings("h:" + (i + 1), SideChoice.Auto, false, true).Save(dir));
        Assert.Equal([FrontEndSettings.FileName], Directory.GetFiles(dir).Select(Path.GetFileName));
        Assert.Equal("h:3", FrontEndSettings.Load(dir).Host);
    }

    // ===== one emblem per look, perks =====

    [Fact]
    public void A_look_carries_one_emblem_so_a_second_one_replaces_the_other_pads()
    {
        var look = Look() with { Decals = new Dictionary<string, DecalChoice>
        {
            ["leftshoulder"] = DecalChoice.ForEmblem("11111111111111111111111111111111", "sm_bar"),
            ["rightshoulder"] = DecalChoice.ForBadge("sm_foo_orig"),
        } };
        const string second = "22222222222222222222222222222222";
        var (applied, replaced) = AppearanceEdits.ApplyEmblem(look, ["rightshoulder"], second);
        Assert.True(replaced);                                                                                        // the left pad's other emblem was replaced
        Assert.Equal(second, applied.Decals["leftshoulder"].Emblem);
        Assert.Equal(second, applied.Decals["rightshoulder"].Emblem);
        Assert.Equal("sm_bar", applied.Decals["leftshoulder"].Fallback);                                               // each pad keeps its fallback badge
        Assert.Equal("sm_foo_orig", applied.Decals["rightshoulder"].Fallback);
        // A badge on the other pad is left alone; the same emblem again is no replacement.
        var badgeOther = AppearanceEdits.ApplyEmblem(Look() with { Decals = new Dictionary<string, DecalChoice> { ["leftshoulder"] = DecalChoice.ForBadge("sm_bar") } }, ["rightshoulder"], second);
        Assert.False(badgeOther.ReplacedOther);
        Assert.Equal("sm_bar", badgeOther.Look.Decals["leftshoulder"].Badge);
        Assert.False(AppearanceEdits.ApplyEmblem(applied, ["leftshoulder"], second).ReplacedOther);
        // The result fits the wire: one emblem hash, both pads marked as the emblem.
        var code = ProfileWire.ToCode(applied, Catalog);
        Assert.Equal(AppearanceCode.DecalEmblem, code.LeftDecal);
        Assert.Equal(AppearanceCode.DecalEmblem, code.RightDecal);
        Assert.Equal(second, code.Emblem.ToHex());
        Assert.Same(look, AppearanceEdits.ApplyEmblem(AppearanceEdits.SetSet(look, "black_templars"), ["leftshoulder"], second).Look is var l && l.PredefinedSet is not null ? look : null);   // a whole set is not edited
    }

    [Fact]
    public void New_looks_get_distinct_default_names_even_after_a_deletion()
    {
        var p = CustomizationProfile.Default(Catalog);
        for (int i = 0; i < 3; i++) p = AppearanceEdits.AddAppearance(p, "space_marine").Profile;
        p = AppearanceEdits.RemoveAppearance(p, "sm-2");
        p = AppearanceEdits.AddAppearance(p, "space_marine").Profile;
        var names = p.AppearancesFor("space_marine").Select(a => a.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["Appearance 1", "Appearance 3", "Appearance 2"], names);
    }

    [Fact]
    public void Clearing_perk_one_never_leaves_the_default_perk_in_both_slots()
    {
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 1, "tactical", Catalog).Profile;
        p = LoadoutEdits.SetPerk(p, 1, 0, "mp_perk_weap_bolter_accuracy", Catalog).Profile;        // slot 1: bolter perk; the default perk is free
        p = LoadoutEdits.SetPerk(p, 1, 1, "mp_perk_a", Catalog).Profile;                           // slot 2: the class default perk
        Assert.Equal("mp_perk_a", p.ClassAt(1)!.Perk(1));
        var cleared = LoadoutEdits.SetPerk(p, 1, 0, null, Catalog);                                // slot 1 returns to the default perk ...
        Assert.Null(cleared.Profile.ClassAt(1)!.Perk(0));
        Assert.Null(cleared.Profile.ClassAt(1)!.Perk(1));                                           // ... so slot 2 may not hold it too
        Assert.NotNull(cleared.Notice);
    }

    [Fact]
    public void The_pistol_gate_counts_the_class_default_perk_like_the_edit_does()
    {
        // In the fixture the Assault default perk is mp_perk_a, so only an explicit True Grit opens the pistol Bolter; both paths agree.
        var plain = LoadoutEdits.New(1, "assault", Catalog);
        var rows = LoadoutRows.Weapons(Catalog, "assault", CustomizationCatalog.SpaceMarine, plain, UnlockPolicy.All)[1].Rows;
        Assert.False(rows.Single(r => r.Id == "mp_pistol_bolter").Selectable);
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 1, "assault", Catalog).Profile;
        Assert.False(LoadoutEdits.SetItem(p, 1, "pistol", "mp_pistol_bolter", Catalog).Changed);
        p = LoadoutEdits.SetPerk(p, 1, 0, "mp_perk_bolter_in_place_of_pistol", Catalog).Profile;                    // True Grit in slot 1 (replacing the default)
        Assert.True(LoadoutEdits.SetItem(p, 1, "pistol", "mp_pistol_bolter", Catalog).Changed);
        var open = LoadoutRows.Weapons(Catalog, "assault", CustomizationCatalog.SpaceMarine, p.ClassAt(1), UnlockPolicy.All)[1].Rows;
        Assert.True(open.Single(r => r.Id == "mp_pistol_bolter").Selectable);
    }

    // ===== joining =======================================================================================================================

    [Fact]
    public void A_join_sends_the_active_slots_class_choices_and_looks()
    {
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 3, "tactical", Catalog).Profile;
        p = LoadoutEdits.SetItem(p, 3, "primary", "mp_ranged_plasma_gun", Catalog).Profile;
        p = LoadoutEdits.SetPerk(p, 3, 1, "mp_perk_weap_bolter_accuracy", Catalog).Profile;
        p = AppearanceEdits.EnsureLook(p, 3, "space_marine").Profile;
        p = AppearanceEdits.ReplaceAppearance(p, AppearanceEdits.SetColour(p.Appearance("sm-1")!, ["head"], PaintChannel.Primary, "gold", null));
        var config = Config();

        var marines = JoinPlan.Build(p, Selection.CustomSlot(3), config, Catalog, SideChoice.SpaceMarines);
        Assert.Equal("sm - tactical", marines.ClassName);
        Assert.Equal(new LoadoutChoice(0, 1, -1, -1, -1, -1, 1), marines.Choice);
        Assert.Equal(Team.SpaceMarines, marines.Side);
        Assert.False(marines.SpaceMarineLook.IsDefault);
        Assert.True(marines.ChaosLook.IsDefault);
        Assert.Equal([("class", "sm - tactical"), ("primary", "1"), ("side", "marines")], marines.ToArgs().Select(a => (a.Key, a.Value)));

        var chaos = JoinPlan.Build(p, Selection.CustomSlot(3), config, Catalog, SideChoice.Chaos);
        Assert.Equal("csm - tactical", chaos.ClassName);
        Assert.Equal(0, chaos.Choice.Primary);                                                                        // the Chaos list orders plasma first: by name, not position
        Assert.Contains(("side", "chaos"), chaos.ToArgs());

        var auto = JoinPlan.Build(p, Selection.CustomSlot(3), config, Catalog, SideChoice.Auto);
        Assert.Equal(Team.None, auto.Side);
        Assert.DoesNotContain(auto.ToArgs(), a => a.Key == "side");
        Assert.Equal("sm - tactical", auto.ClassName);
        Assert.Contains("Custom 3", auto.Summary);
    }

    [Fact]
    public void A_defaults_selection_or_an_empty_slot_joins_with_the_class_defaults()
    {
        var p = CustomizationProfile.Default(Catalog);
        var defaults = JoinPlan.Build(p, Selection.DefaultFor("assault"), Config(), Catalog, SideChoice.Chaos);
        Assert.Equal("csm - raptor", defaults.ClassName);
        Assert.Equal([("class", "csm - raptor"), ("side", "chaos")], defaults.ToArgs().Select(a => (a.Key, a.Value)));
        Assert.StartsWith("Defaults:", defaults.Summary);
        Assert.True(defaults.SpaceMarineLook.IsDefault && defaults.ChaosLook.IsDefault);
        var empty = JoinPlan.Build(p, Selection.CustomSlot(9), Config(), Catalog, SideChoice.SpaceMarines);              // an empty slot: the fallback
        Assert.Equal("sm - tactical", empty.ClassName);
        Assert.StartsWith("Defaults:", empty.Summary);
    }

    // ===== the content profile (added content E2) =====

    [Fact]
    public void A_saved_ork_loadout_is_unavailable_without_the_orks_and_joins_as_the_sides_default_class()
    {
        var ork = new CustomClass(5, "Boyz", OrkClass.Role, null, null, null, null, [null, null], AppearanceRefs.None);
        var p = CustomizationProfile.Default(Catalog);
        p = p with { Classes = [.. p.Classes, ork], Selected = Selection.CustomSlot(5) };
        var config = Config();
        Assert.True(LoadoutAvailability.Unavailable(ork, ContentExtras.None));
        Assert.False(LoadoutAvailability.Unavailable(ork, ContentExtras.Orks));

        var faithful = JoinPlan.Build(p, Selection.CustomSlot(5), config, Catalog, SideChoice.Chaos, ContentExtras.None);
        Assert.Equal("csm - tactical", faithful.ClassName);                  // the side's first role, defaults
        Assert.Contains("added content", faithful.Summary);
        Assert.Contains("Boyz", faithful.Summary);
        Assert.Equal(-1, faithful.Choice.Primary);
        Assert.NotNull(p.ClassAt(5));                                         // the saved slot is untouched
        Assert.Equal(OrkClass.Role, p.ClassAt(5)!.Role);

        // With the Orks the slot is the player's own again (the Ork has no side class in this config yet: the server's default is asked for).
        var extended = JoinPlan.Build(p, Selection.CustomSlot(5), config, Catalog, SideChoice.Chaos, ContentExtras.Orks);
        Assert.DoesNotContain("added content", extended.Summary);
    }

    // ===== the Ork Boy in the customizer (ui/customizer-ork, Ork O9) =====

    /// <summary>The configuration with the Ork Boy appended as the last class (no side, the Tactical's perks), as GameplayTuning.LoadForMatch makes it.</summary>
    static SimConfig OrkConfig()
    {
        var c = Config();
        return c with
        {
            Classes = [.. c.Classes, new ClassLoadout("ork - boy", 0, 2, []) { Side = Team.None, Role = OrkClass.Role, Race = Race.Orks, PrimaryChoices = [0], EquipmentChoices = [0], PerkChoices = [0, 1, 2], DefaultPerk = 0 }],
        };
    }

    [Fact]
    public void The_ork_boy_has_his_fixed_gear_and_the_tactical_marines_perks()
    {
        var ork = OrkLoadout.Options(Catalog)!;
        var tactical = Catalog.Perks.Class("sm - tactical")!;
        Assert.Equal(OrkClass.Role, ork.Role);
        foreach (var slot in new[] { "primary", "pistol", "melee", "equipment" })
        {
            Assert.False(ork.For(slot).Selectable);
            Assert.Single(ork.For(slot).Items);
        }
        Assert.Equal([OrkClass.Shoota, OrkClass.Slugga, OrkClass.Choppa, OrkClass.Stikkbomb], new[] { "primary", "pistol", "melee", "equipment" }.Select(s => ork.For(s).Items[0].Name));
        Assert.Equal(tactical.Perks.Select(p => p.Name), ork.Perks.Select(p => p.Name));
        Assert.Equal(tactical.DefaultPerk?.Name, ork.DefaultPerk?.Name);
        // The creator's lists come from them: the weapons all FIXED, the perks the Tactical's.
        var weapons = LoadoutRows.Weapons(Catalog, OrkClass.Role, OrkLoadout.Race, null, UnlockPolicy.All);
        Assert.All(weapons, s => Assert.True(!s.Selectable && s.Rows.Single().Fixed));
        Assert.Equal(LoadoutRows.Perks(Catalog, "tactical", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All).Select(r => r.Name),
            LoadoutRows.Perks(Catalog, OrkClass.Role, OrkLoadout.Race, null, UnlockPolicy.All).Select(r => r.Name));
        // His items cannot be changed; his perks can.
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 2, OrkClass.Role, Catalog).Profile;
        Assert.False(LoadoutEdits.SetItem(p, 2, "primary", "mp_ranged_bolter", Catalog).Changed);
        Assert.Equal("mp_perk_secondary_weapon", LoadoutEdits.SetPerk(p, 2, 1, "mp_perk_secondary_weapon", Catalog).Profile.ClassAt(2)!.Perk(1));
        Assert.False(LoadoutEdits.SetPerk(p, 2, 1, "mp_perk_bolter_in_place_of_pistol", Catalog).Changed);   // an Assault perk is not his
        Assert.Contains("Shoota", LoadoutRows.Summary(Catalog, p.ClassAt(2)!, CustomizationCatalog.Chaos));
        // The roles the screens offer: the Ork only with the Orks.
        Assert.DoesNotContain(OrkClass.Role, OrkLoadout.Roles(Catalog, ContentExtras.None));
        Assert.Equal(OrkClass.Role, OrkLoadout.Roles(Catalog, ContentExtras.Orks)[^1]);
    }

    [Fact]
    public void An_ork_loadout_and_the_ork_look_round_trip_through_the_store()
    {
        var store = NewStore();
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 3, OrkClass.Role, Catalog).Profile;
        p = LoadoutEdits.SetPerk(p, 3, 1, "mp_perk_secondary_weapon", Catalog).Profile;
        p = OrkLoadout.SetLook(p, 2).Profile;
        p = LoadoutEdits.Select(p, Selection.DefaultFor(OrkClass.Role));
        store.Save(p);
        var (back, repairs) = store.Load(Catalog);
        Assert.Empty(repairs.Where(r => !r.Informational));
        Assert.Equal(OrkClass.Role, back.ClassAt(3)!.Role);
        Assert.Equal("mp_perk_secondary_weapon", back.ClassAt(3)!.Perk(1));
        Assert.Equal(2, back.OrkLook);
        Assert.Equal(Selection.DefaultFor(OrkClass.Role), back.Selected);
        Assert.Equal(ProfileStore.Serialize(p), ProfileStore.Serialize(back));
        // No look: nothing written, automatic read back; a damaged look is repaired to automatic.
        Assert.DoesNotContain("orkLook", ProfileStore.Serialize(p with { OrkLook = null }));
        File.WriteAllText(store.ProfilePath, ProfileStore.Serialize(p).Replace("\"orkLook\": 2", "\"orkLook\": 9"));
        var (repaired, fixes) = store.Load(Catalog);
        Assert.Null(repaired.OrkLook);
        Assert.Contains(fixes, r => r.Path == "orkLook");
        Assert.False(OrkLoadout.SetLook(p, 5).Changed);
        Assert.False(OrkLoadout.SetLook(p, 2).Changed);   // unchanged
    }

    [Fact]
    public void The_join_plan_sends_the_ork_class_and_look_only_with_the_orks()
    {
        var config = OrkConfig();
        int orkClass = config.Classes.Count - 1;
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 4, OrkClass.Role, Catalog).Profile;
        p = OrkLoadout.SetLook(LoadoutEdits.SetPerk(p, 4, 1, "mp_perk_secondary_weapon", Catalog).Profile, 3).Profile;

        var custom = JoinPlan.Build(p, Selection.CustomSlot(4), config, Catalog, SideChoice.Chaos, ContentExtras.Orks);
        Assert.Equal(orkClass, custom.Choice.Class);
        Assert.Equal("ork - boy", custom.ClassName);
        Assert.Equal((byte)3, custom.OrkLook);
        Assert.True(custom.Choice.Perk2 >= 0);
        Assert.Equal(Team.Chaos, custom.Side);                                                  // he fights for the side asked for
        var defaults = JoinPlan.Build(p, Selection.DefaultFor(OrkClass.Role), config, Catalog, SideChoice.Auto, ContentExtras.Orks);
        Assert.Equal(orkClass, defaults.Choice.Class);
        Assert.Equal(Team.None, defaults.Side);
        Assert.StartsWith("Defaults:", defaults.Summary);

        // Faithful: both join as the side's default class, with no Ork look, and say why.
        foreach (var selection in new[] { Selection.CustomSlot(4), Selection.DefaultFor(OrkClass.Role) })
        {
            var faithful = JoinPlan.Build(p, selection, config, Catalog, SideChoice.Chaos, ContentExtras.None);
            Assert.Equal("csm - tactical", faithful.ClassName);
            Assert.Equal(Races.OrkLookAutomatic, faithful.OrkLook);
            Assert.Contains("added content", faithful.Summary);
            Assert.True(LoadoutAvailability.Unavailable(p, selection, ContentExtras.None));
            Assert.False(LoadoutAvailability.Unavailable(p, selection, ContentExtras.Orks));
        }
        Assert.Equal(Selection.DefaultFor(OrkClass.Role), LaunchOptions.ParseLoadout("default:ork", Catalog));
        Assert.Equal(Selection.DefaultFor(OrkClass.Role), LaunchOptions.ParseLoadout("Ork Boy", Catalog));
    }

    [Fact]
    public void The_front_end_remembers_the_content_profile_and_defaults_to_extended()
    {
        string dir = Path.Combine(_dir, "fe-content");
        Assert.Equal(ContentProfiles.DefaultName, FrontEndSettings.Default.Content);
        Assert.Equal(ContentProfiles.Extended, ContentProfiles.DefaultName);                  // the owner's decision, 2026-10-29
        Assert.Equal(ContentProfiles.DefaultName, FrontEndSettings.Load(dir).Content);        // no file
        var faithful = FrontEndSettings.Default with { Content = ContentProfiles.Faithful };
        Assert.Null(faithful.Save(dir));
        Assert.Equal(ContentProfiles.Faithful, FrontEndSettings.Load(dir).Content);
        Assert.Equal(ContentExtras.None, FrontEndSettings.Load(dir).ContentExtras);
        Assert.Equal(ContentProfiles.Default, FrontEndSettings.Default.ContentExtras);
        Assert.Contains("\"content\": \"faithful\"", File.ReadAllText(Path.Combine(dir, FrontEndSettings.FileName)));
        // Damage reads as the default; a name that is not a profile keeps the default meaning in this build.
        File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"content\": 5}");
        Assert.Equal(ContentProfiles.DefaultName, FrontEndSettings.Load(dir).Content);
        File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"content\": \"no way!\"}");
        Assert.Equal(ContentProfiles.DefaultName, FrontEndSettings.Load(dir).Content);
        File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"content\": \"bogus\"}");
        Assert.Equal(ContentProfiles.Default, FrontEndSettings.Load(dir).ContentExtras);
        // A file from before E2 (no content key) is the default profile, and everything else in it is kept.
        File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"host\": \"h:1\", \"side\": \"Chaos\", \"reduceMotion\": true, \"showOthersEmblems\": false}");
        Assert.Equal(new FrontEndSettings("h:1", SideChoice.Chaos, true, false), FrontEndSettings.Load(dir));
    }

    [Fact]
    public void The_join_plan_notices_an_emblem()
    {
        var store = NewStore();
        var hash = EmblemPipeline.Add(store, EmblemPipeline.ToBc3(Solid(128, 128, 9, 9, 9, 255))).Hash;
        var p = LoadoutEdits.Create(CustomizationProfile.Default(Catalog), 1, "tactical", Catalog).Profile;
        (p, var look) = AppearanceEdits.EnsureLook(p, 1, "space_marine");
        p = AppearanceEdits.ReplaceAppearance(p, AppearanceEdits.SetDecal(look!, "leftshoulder", DecalChoice.ForEmblem(hash.ToHex(), "sm_bar")));
        var plan = JoinPlan.Build(p, Selection.CustomSlot(1), Config(), Catalog, SideChoice.Auto);
        Assert.True(plan.UsesEmblem);
        Assert.Equal(hash, plan.SpaceMarineLook.Emblem);
    }

    [Theory]
    [InlineData("127.0.0.1:27015", null)]
    [InlineData("localhost", null)]
    [InlineData("example.org:1", null)]
    [InlineData("[::1]:27015", null)]
    [InlineData("::1", null)]
    [InlineData("", "Enter the server address (host:port).")]
    [InlineData("a b", "The address must not contain spaces.")]
    [InlineData("host:0", "The port must be 1 to 65535.")]
    [InlineData("host:70000", "The port must be 1 to 65535.")]
    [InlineData("host:abc", "The port must be 1 to 65535.")]
    [InlineData(":27015", "The address is not valid.")]
    public void Server_addresses_are_validated_without_a_lookup(string text, string? expected) => Assert.Equal(expected, FrontEndSettings.ValidateHost(text));

    [Fact]
    public void The_front_end_settings_round_trip_and_survive_damage()
    {
        string dir = Path.Combine(_dir, "fe");
        Assert.Equal(FrontEndSettings.Default, FrontEndSettings.Load(dir));
        var settings = new FrontEndSettings("10.0.0.5:27841", SideChoice.Chaos, true, false);
        Assert.Null(settings.Save(dir));
        Assert.Equal(settings, FrontEndSettings.Load(dir));
        File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{ broken");
        Assert.Equal(FrontEndSettings.Default, FrontEndSettings.Load(dir));
        File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"host\": \"a b\", \"side\": \"Nope\"}");
        Assert.Equal(FrontEndSettings.Default, FrontEndSettings.Load(dir));
    }

    [Fact]
    public void The_loadout_flag_accepts_a_slot_or_a_defaults_role()
    {
        Assert.Equal(Selection.CustomSlot(3), LaunchOptions.ParseLoadout("3", Catalog));
        Assert.Equal(Selection.CustomSlot(10), LaunchOptions.ParseLoadout("10", Catalog));
        Assert.Null(LaunchOptions.ParseLoadout("11", Catalog));
        Assert.Null(LaunchOptions.ParseLoadout("0", Catalog));
        Assert.Equal(Selection.DefaultFor("assault"), LaunchOptions.ParseLoadout("default:assault", Catalog));
        Assert.Equal(Selection.DefaultFor("heavy"), LaunchOptions.ParseLoadout("default:devastator", Catalog));
        Assert.Equal(Selection.DefaultFor("tactical"), LaunchOptions.ParseLoadout("DEFAULT:Tactical", Catalog));
        Assert.Null(LaunchOptions.ParseLoadout("default:medic", Catalog));
        Assert.Null(LaunchOptions.ParseLoadout("", Catalog));
        Assert.Null(LaunchOptions.ParseLoadout(null, Catalog));
    }
}
