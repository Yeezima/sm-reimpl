using System.IO.Compression;
using System.Text;
using SmContent;
using SmFormats.Baf;
using SmFormats.Locale;
using SmFormats.Obpk;
using SmSim;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// <see cref="CustomizationCatalog"/> and <see cref="PerkCatalog"/> on a synthetic install shaped like the real one (names, tables and counts are made up;
/// no original data). Passing proves the readers agree with our documented layout, not compatibility with the install.
/// </summary>
public class CustomizationCatalogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-cust-" + Guid.NewGuid().ToString("N"));
    string Packages => Path.Combine(_dir, "data", "packages");

    public CustomizationCatalogTests() => Directory.CreateDirectory(Packages);
    public void Dispose() => Directory.Delete(_dir, true);

    // ---- builders -------------------------------------------------------------------------------------------------------------------------

    static (string, object)[] T(params (string, object)[] items) => items;
    static byte[] Baf(params (string, object)[] root) => SyntheticBafWriter.Build(root);
    static SyntheticObpkWriter.File Entry(string name, byte[] data) => new("attr_pc", name, data);
    void WritePackage(string package, params SyntheticObpkWriter.File[] files) =>
        File.WriteAllBytes(Path.Combine(Packages, package + ".oppc"), SyntheticObpkWriter.Build(files));

    const string W = @"wargear\wargear\multiplayer\";
    static (string, object) Wg(string name) => ("wargear", W + "x\\" + name);
    static (string, object)[] WgList(params string[] names) => names.Select(Wg).ToArray();

    /// <summary>A wargear file: slot, level, text ids, visibility, attachment packages, an optional upgrade string, a thrown delivery.</summary>
    static byte[] Gear(string slot, int level = 1, int name = 0, int description = 0, bool visible = true, string[]? packages = null,
        string? upgrade = null, bool thrown = false, string helmet = "no_override")
    {
        var attachments = (packages ?? []).Select((p, i) => (i == 0 ? "torso" : @"wargear\attachment_entry", (object)T(("visual_package", p)))).ToArray();
        var root = new List<(string, object)>
        {
            ("visual_data", T(("attachments", attachments))),
            ("common_data", T(("slot", slot), ("helmet_override", helmet), ("unlock_info", T(("level_requirement", T(("level_required", level))))))),
            ("ui_data", T(("name", name), ("description", description), ("visible_in_customizer", visible))),
        };
        if (upgrade is not null)
            root.Add(Nest(["action_data", "lifetime_actions", "commands", "apply_upgrade_string", "strings"], T(("entry", upgrade))));
        if (thrown) root.Add(Nest(["ability_data", "ability_definitions", "ability_utility", "delivery"], T()));
        return SyntheticBafWriter.Build(root.ToArray());
    }

    static byte[] ArmourSet(string race, string usage, string setId, int name, string? scheme, params string[] items) => Baf(
        ("item_list", WgList(items)), ("set_name", name), ("set_description", name + 1), ("usable_by_race", race), ("usage", usage),
        ("custom_paint_scheme", scheme is null ? "" : @"custom_paint\custom_paint_scheme\" + scheme), ("set_id", setId));

    static (string, object)[] ClassTable(string race, string role, int name, string[] baseGear, string[] startup, string[] perks, string? defaultPerk,
        string defaultSet, params (string Wargear, string Required)[] overrides) => T(
        ("race", race), ("mp_class", role), ("display_name", name), ("display_description", name + 1),
        ("base_wargear", WgList(baseGear)), ("startup_wargear", WgList(startup)), ("class_perks", WgList(perks)),
        ("default_perk", defaultPerk is null ? "" : W + "p\\" + defaultPerk), ("default_armor_set", defaultSet),
        ("override_wargear", overrides.Select(o => ("gear_and_req", (object)T(("wargear", W + "a\\" + o.Wargear), ("required_wargear", o.Required.Length == 0 ? "" : W + "a\\" + o.Required)))).ToArray()));

    static (string, object)[] Challenge(int description, int target, params string[] unlocks) =>
        T(("display_name", 1), ("display_description", description), ("target", target), ("wargear", WgList(unlocks)));

    static (string, object) Nest(string[] names, (string, object)[] leaf)
    {
        (string, object) current = (names[^1], leaf);
        for (int i = names.Length - 2; i >= 0; i--) current = (names[i], T(current));
        return current;
    }

    static (string, object)[] ChallengeGroup(int nameId, string setName, params (string, object)[][] challenges) =>
        T(("display_name", nameId), ("challenge_sets", T((setName, T(("challenges", challenges.Select(c => (@"_base_templates\challenge", (object)c)).ToArray()))))));

    static (string, object)[] Progression(bool present)
    {
        if (!present) return T();
        // prestige and the fourth to sixth loadouts are missing on purpose
        return T(("unlocked_func_info", T(
            ("customizer_unlocked", T(("level_required", 4))), ("custom_appearance_unlocked", T(("level_required", 5))),
            ("assault_marine_unlocked", T(("level_required", 3))), ("devastator_marine_unlocked", T(("level_required", 2))),
            ("perk_slot_2_unlocked", T(("level_required", 15))),
            ("second_equipment_loadout_unlocked", T(("level_required", 11))), ("first_equipment_loadout_unlocked", T(("level_required", 4))),
            ("third_equipment_loadout_unlocked", T(("level_required", 17))))));
    }

    static byte[] GlobalBaf(bool withProgression = true)
    {
        var classes = T(
            ("sm - tactical", ClassTable("space_marine", "tactical", 1000,
                ["sm_mp_abil_sprint", "sm_mp_melee_combat_knife", "sm_mp_pistol_bolt_pistol"],
                ["sm_mp_eq_frag_grenade", "sm_mp_eq_stimpack", "sm_mp_eq_melta_bomb", "sm_mp_ranged_bolter", "sm_mp_ranged_plasma_gun", "sm_mp_ranged_missing"],
                ["mp_perk_a", "mp_perk_weap_x", "mp_perk_secondary_weapon"], "mp_perk_a", "sm_default")),
            ("sm - assault marine", ClassTable("space_marine", "assault", 1010,
                ["sm_mp_abil_sprint"],
                ["sm_mp_pistol_bolt_pistol", "sm_mp_pistol_bolter", "sm_mp_pistol_bolter_gold", "sm_mp_melee_lightning_claw", "sm_mp_melee_chainsword", "sm_mp_eq_frag_grenade"],
                ["mp_perk_a", "mp_perk_bolter_in_place_of_pistol"], "mp_perk_a", "sm_default")),
            ("sm - devastator", ClassTable("space_marine", "heavy", 1020,
                ["sm_mp_abil_sprint", "sm_mp_pistol_bolt_pistol"], ["sm_mp_exotic_heavy_bolter"], ["mp_perk_a"], null, "sm_default",
                ("sm_backpack_heavy_bolter", "sm_mp_exotic_heavy_bolter"))),
            ("csm - raptor", ClassTable("chaos", "assault", 1030,
                ["sm_mp_abil_sprint"], ["csm_mp_melee_chainsword"], ["mp_perk_a"], "mp_perk_a", "chaos_default",
                ("csm_raptor_head", ""), ("mp_perk_iron_halo_for_havoc", "mp_perk_iron_halo"))));
        var challengeGroups = T(
            ("bolter", ChallengeGroup(1800, "bolter 1", Challenge(1801, 5), Challenge(1801, 50, "mp_perk_weap_x"))),
            ("armor group", ChallengeGroup(1802, "wins", Challenge(1803, 15, "sm_b_torso"))));
        var cameras = T(
            ("initial", T(("horizontal_angle", 0f), ("vertical_angle", -0.16f), ("distance", 4.1f), ("vertical_offset", 0.61f), ("horizontal_offset", -0.7f))),
            ("perks", T(("horizontal_angle", 0), ("vertical_angle", -0.16f), ("distance", 4.1f), ("vertical_offset", 0.61f), ("horizontal_offset", -1.5f))));
        return Baf(("extensions", T(
            ("multiplayer_settings", T(("ingame_progression", T(("classes", classes))), ("challenge_groups", challengeGroups))),
            ("character_customization", T(("camera_settings", cameras))),
            ("profile_progression_info", Progression(withProgression)))));
    }

    static byte[] Colour(int name, string group, bool visible, float r = 0.1f) => Baf(
        ("name", name), ("diffuse", T(("red", r), ("green", 0.2f), ("blue", 0.3f), ("alpha", 0f))),
        ("specular", T(("red", 0.5f), ("green", 0.5f), ("blue", 0.5f), ("alpha", 0f))), ("colour_type", group), ("visible_in_customizer", visible));

    static byte[] Scheme(string race, int name, bool visible, bool badge = false) => Baf(
        ("name", name), ("race", race), ("paints", T()),
        ("default_paint", T(("primary_colour", @"custom_paint\citadel_paints\blue_1"), ("secondary_colour", @"custom_paint\citadel_paints\red_1"),
            ("tertiary_colour", @"custom_paint\citadel_paints\red_1"), ("pattern_enabled", false))),
        ("visible_in_customizer", visible), ("class_badge_enabled", badge));

    static byte[] Decal(string race, int name, bool visible, string texture = "tex") => Baf(
        ("name", name), ("decal", texture), ("decal_package", texture), ("visible_in_customizer", visible), ("race", race));

    static readonly string[] SmAquila = ["sm_a_head", "sm_a_torso", "sm_a_shoulder_left", "sm_a_shoulder_right", "sm_a_arm_left", "sm_a_arm_right", "sm_a_leg_left", "sm_a_leg_right", "sm_a_backpack"];

    /// <summary>Writes a complete synthetic install (the locale unless <paramref name="locale"/> is false).</summary>
    void WriteInstall(bool locale = true, bool progression = true)
    {
        WritePackage("global", Entry("global", GlobalBaf(progression)));
        WritePackage("wargear",
            // weapons and equipment
            Entry("sm_mp_ranged_bolter", Gear("ranged", 1, 1100, 1101)),
            Entry("sm_mp_ranged_plasma_gun", Gear("ranged", 8, 0)),
            Entry("sm_mp_pistol_bolt_pistol", Gear("pistol", 1, 1106)),
            Entry("sm_mp_pistol_bolter", Gear("pistol", 1, 1109)),
            Entry("sm_mp_pistol_bolter_gold", Gear("pistol", 1, 1109)),
            Entry("sm_mp_melee_combat_knife", Gear("melee", 1, 1107)),
            Entry("sm_mp_melee_chainsword", Gear("melee", 1, 1105)),
            Entry("csm_mp_melee_chainsword", Gear("melee", 1, 1105)),
            Entry("sm_mp_melee_lightning_claw", Gear("melee", 6, 1102)),
            Entry("sm_mp_eq_frag_grenade", Gear("equipment", 1, 1103, thrown: true)),
            Entry("sm_mp_eq_stimpack", Gear("equipment", 9, 1104)),
            Entry("sm_mp_eq_melta_bomb", Gear("equipment", 1, 1108, visible: false, thrown: true)),
            Entry("sm_mp_abil_sprint", Gear("coreability")),
            Entry("sm_mp_exotic_heavy_bolter", Gear("ranged", 1, 1110)),
            // perks
            Entry("mp_perk_a", Gear("perk", 7, 1200, 1201, upgrade: "mp_perk_a")),
            Entry("mp_perk_weap_x", Gear("perk", 1, 1202, 1203, upgrade: "mp_perk_weap_x_reload")),
            Entry("mp_perk_secondary_weapon", Gear("perk", 13, 1204)),
            Entry("mp_perk_bolter_in_place_of_pistol", Gear("perk", 25, 1205)),
            Entry("mp_perk_iron_halo_for_havoc", Gear("perk")),
            // armour: set A (default), set B (shares arms and the backpack with A), a predefined set, a Chaos set, and an item that is not armour
            Entry("sm_a_head", Gear("head", 1, 1300, 1301, packages: ["pkg_a_helmet"], helmet: "helmet")),
            Entry("sm_a_torso", Gear("torso", 1, 1302, packages: ["pkg_a_torso", "pkg_a_belt"])),
            Entry("sm_a_shoulder_left", Gear("leftshoulder", packages: ["pkg_a_shldrl"])),
            Entry("sm_a_shoulder_right", Gear("rightshoulder", packages: ["pkg_a_shldrr"])),
            Entry("sm_a_arm_left", Gear("leftarm", packages: ["pkg_a_arml"])),
            Entry("sm_a_arm_right", Gear("rightarm", packages: ["pkg_a_armr"])),
            Entry("sm_a_leg_left", Gear("leftleg", packages: ["pkg_a_legl"])),
            Entry("sm_a_leg_right", Gear("rightleg", packages: ["pkg_a_legr"])),
            Entry("sm_a_backpack", Gear("backpack", packages: ["pkg_a_bp"])),
            Entry("sm_b_head", Gear("head", 1, 1310, packages: ["pkg_b_helmet"])),
            Entry("sm_b_torso", Gear("torso", 1, 1311, packages: ["pkg_b_torso"])),
            Entry("sm_b_leg_left", Gear("leftleg", packages: ["pkg_b_legl"])),
            Entry("sm_b_leg_right", Gear("rightleg", packages: ["pkg_b_legr"])),
            Entry("sm_b_belt", Gear("belt", packages: ["pkg_b_belt"])),
            Entry("sm_bt_head", Gear("head", packages: ["pkg_bt_helmet"])),
            Entry("sm_bt_torso", Gear("torso", packages: ["pkg_bt_torso"])),
            Entry("csm_basic_head", Gear("head", packages: ["pkg_c_helmet"])),
            Entry("csm_basic_torso", Gear("torso", packages: ["pkg_c_torso"])),
            Entry("csm_raptor_head", Gear("head", 1, 1320, packages: ["pkg_r_helmet"], helmet: "helmet")),
            Entry("sm_backpack_heavy_bolter", Gear("backpack", packages: ["pkg_hb_bp"])));
        WritePackage("mp_armor_set",
            Entry("mk_8_errant", ArmourSet("space_marine", "customizable", "", 1401, null,
                "sm_b_leg_right", "sm_b_head", "sm_b_torso", "sm_b_leg_left", "sm_a_shoulder_left", "sm_a_shoulder_right", "sm_a_arm_left", "sm_a_arm_right", "sm_a_backpack", "sm_b_belt")),
            Entry("mk_7_aquila", ArmourSet("space_marine", "customizable", "sm_default", 1400, null, SmAquila)),
            Entry("black_templars", ArmourSet("space_marine", "predefined", "black_templars", 1402, "sm_black_templars", "sm_bt_head", "sm_bt_torso")),
            Entry("chaos_default", ArmourSet("chaos", "customizable", "chaos_default", 1410, null, "csm_basic_head", "csm_basic_torso")));
        WritePackage("custom_paint_colour",
            Entry("red_1", Colour(1501, "red", true)), Entry("blue_1", Colour(1500, "blue", true)), Entry("gold", Colour(1502, "metallics", true)),
            Entry("blue_0", Colour(1503, "blue", true)), Entry("dev_pink", Colour(0, "blue", false)));
        WritePackage("custom_paint_scheme",
            Entry("sm_x", Scheme("space_marine", 1700, true, badge: true)), Entry("sm_a", Scheme("space_marine", 1701, true)),
            Entry("sm_hidden", Scheme("space_marine", 1702, false)), Entry("sm_black_templars", Scheme("space_marine", 1703, false)),
            Entry("csm_y", Scheme("chaos", 1704, true)), Entry("chaos_multiplayer", Scheme("chaos", 0, false)));
        WritePackage("custom_paint_decal",
            Entry("sm_foo_orig", Decal("space_marine", 1600, true)), Entry("sm_foo_black", Decal("space_marine", 1601, true)),
            Entry("sm_custom_01_white", Decal("space_marine", 1602, true)), Entry("sm_bar", Decal("space_marine", 1603, true)),
            Entry("sm_bar_bw_inverted", Decal("space_marine", 1604, true)), Entry("sm_rank_hidden", Decal("space_marine", 1605, false)),
            Entry("sm_blank_badge", Decal("space_marine", 1606, true, "blank")),
            Entry("csm_star", Decal("chaos", 1607, true)), Entry("csm_blank", Decal("chaos", 0, false, "blank")));
        if (locale)
            File.WriteAllBytes(LocalePath(), LocaleBytes(
                "1000\tTactical Marine\r\n1001\tVersatile.\r\n1010\tAssault Marine\r\n1011\tFast.\r\n1020\tDevastator\r\n1030\tChaos Raptor\r\n" +
                "1100\tBolter\r\n1101\tA rifle.\r\n1102\tPower Axe\r\n1103\tFrag Grenades\r\n1104\tCombat Stimulants\r\n1105\tChainsword\r\n1106\tBolt Pistol\r\n1107\tCombat Knife\r\n" +
                "1108\tPlasma Grenade\r\n1109\tBolter\r\n1110\tHeavy Bolter\r\n" +
                "1200\tPerk A\r\n1201\tDoes A.\r\n1202\tWeapon Perk X\r\n1203\tDoes X.\r\n1204\tWeapon Versatility\r\n1205\tTrue Grit\r\n" +
                "1300\tAquila Helm\r\n1301\tA helmet.\r\n1302\tAquila Torso\r\n1310\tErrant Helm\r\n1311\tErrant Torso\r\n1320\tRaptor\r\n" +
                "1400\tMk7 Aquila\r\n1401\tMk8 Errant\r\n1402\tBlack Templars\r\n1410\tChaos Armor\r\n" +
                "1500\tBlue One\r\n1501\tRed One\r\n1502\tGold\r\n1503\tBlue Zero\r\n" +
                "1600\tFoo\r\n1601\tFoo - Black\r\n1602\tCustom One - White\r\n1603\tBar\r\n1604\tBar - Monochrome 2\r\n1606\tNo Badge\r\n1607\tStar\r\n" +
                "1700\tChapter X\r\n1701\tChapter A\r\n1704\tWarband Y\r\n" +
                "1800\tBolter\r\n1801\tKill %1TARGET% enemies\r\n1802\tMk8 Armor\r\n1803\tWin %1TARGET% games\r\n"));
    }

    string LocalePath()
    {
        string dir = Path.Combine(_dir, "data", "locale", "english");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "ucs.pc");
    }

    static byte[] LocaleBytes(string text)
    {
        var raw = Encoding.Unicode.GetBytes("﻿" + text);
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        var file = new byte[4 + packed.Length];
        BitConverter.GetBytes(raw.Length).CopyTo(file, 0);
        packed.ToArray().CopyTo(file, 4);
        return file;
    }

    CustomizationCatalog Load(List<string>? notes = null)
    {
        WriteInstall();
        return CustomizationCatalog.Load(_dir, notes ?? []);
    }

    // ---- locale move ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void LocaleTable_lives_in_SmFormats_and_parses_in_memory()
    {
        var table = LocaleTable.Parse(Encoding.Unicode.GetBytes("5\tFive\n6\tSix\tmore\n"));
        Assert.Equal(("Five", "Six\tmore", (string?)null), (table.Get(5), table.Get(6), table.Get(7)));
    }

    // ---- armour pieces: lists, order, sets --------------------------------------------------------------------------------------------------

    [Fact]
    public void Pieces_per_slot_are_the_union_of_the_customizable_sets_ordinal_by_name()
    {
        var notes = new List<string>();
        var sm = Load(notes).SpaceMarines;
        Assert.Equal(CustomizationCatalog.ArmourSlots.Order(), sm.Pieces.Keys.Order());
        Assert.Equal(["sm_a_head", "sm_b_head"], sm.PiecesFor("head").Select(p => p.Name));       // sm_bt_head is a predefined set's: not offered
        Assert.Equal(["sm_a_torso", "sm_b_torso"], sm.PiecesFor("torso").Select(p => p.Name));
        Assert.Equal(["sm_a_leg_left", "sm_b_leg_left"], sm.PiecesFor("leftleg").Select(p => p.Name));
        Assert.Equal(["sm_a_leg_right", "sm_b_leg_right"], sm.PiecesFor("rightleg").Select(p => p.Name));
        Assert.Equal(["sm_a_arm_left"], sm.PiecesFor("leftarm").Select(p => p.Name));              // shared by both sets: listed once
        Assert.Equal(["sm_a_backpack"], sm.PiecesFor("backpack").Select(p => p.Name));
        Assert.Empty(sm.PiecesFor("belt"));
        Assert.Empty(sm.PiecesFor("nonsense"));
        Assert.Contains(notes, n => n.Contains("sm_b_belt") && n.Contains("not an armour slot"));
        // Chaos has its own, smaller lists; slots without any piece are empty lists.
        var chaos = Load().ChaosMarines;
        Assert.Equal(["csm_basic_head"], chaos.PiecesFor("head").Select(p => p.Name));
        Assert.Empty(chaos.PiecesFor("leftleg"));
    }

    [Fact]
    public void A_piece_carries_its_packages_slot_helmet_flag_and_the_sets_that_share_it()
    {
        var sm = Load().SpaceMarines;
        var torso = sm.PiecesFor("torso").Single(p => p.Name == "sm_a_torso");
        Assert.Equal(["pkg_a_torso", "pkg_a_belt"], torso.Packages);                // the belt rides on the torso
        Assert.Equal(("torso", "space_marine"), (torso.Slot, torso.Race));
        Assert.Equal(["mk_7_aquila"], torso.SetNames);
        Assert.Equal(["mk_7_aquila", "mk_8_errant"], sm.PiecesFor("leftarm").Single().SetNames);   // shared piece names both sets, ordinal
        Assert.Equal("helmet", sm.PiecesFor("head").Single(p => p.Name == "sm_a_head").HelmetOverride);
    }

    [Fact]
    public void Sets_keep_customizable_and_predefined_apart_and_the_default_resolves_by_set_id()
    {
        var c = Load();
        var sm = c.SpaceMarines;
        Assert.Equal(["mk_7_aquila", "mk_8_errant"], sm.CustomizableSets.Select(s => s.Name));
        var predefined = Assert.Single(sm.PredefinedSets);
        Assert.Equal(("black_templars", "sm_black_templars", "Black Templars"), (predefined.Name, predefined.Scheme, predefined.DisplayName));
        Assert.Equal("mk_7_aquila", sm.DefaultSet?.Name);                           // the classes' "sm_default" is the set_id of mk_7_aquila
        Assert.Equal("sm_a_torso", sm.DefaultSet!.Items["torso"]);
        Assert.Equal("chaos_default", c.ChaosMarines.DefaultSet?.Name);
        Assert.Equal(("sm_ultramarines", "chaos_multiplayer"), (sm.DefaultScheme, c.ChaosMarines.DefaultScheme));
        Assert.Equal("mk_7_aquila", c.Class("sm - devastator")!.DefaultArmourSet);
        Assert.Null(sm.Set("nothing"));
    }

    // ---- paint ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_palette_has_only_visible_colours_grouped_ordinal_with_rgb()
    {
        var c = Load();
        Assert.Equal(["blue_0", "blue_1", "gold", "red_1"], c.Palette.Select(p => p.Name));       // dev_pink is hidden
        Assert.Equal(["blue", "metallics", "red"], c.PaletteGroups.Select(g => g.Name));
        Assert.Equal(["blue_0", "blue_1"], c.PaletteGroups[0].Colours.Select(p => p.Name));
        var blue = c.Palette.Single(p => p.Name == "blue_1");
        Assert.Equal("Blue One", blue.DisplayName);
        Assert.Equal([0.1f, 0.2f, 0.3f], blue.Diffuse);
        Assert.Equal([0.5f, 0.5f, 0.5f], blue.Specular);
    }

    [Fact]
    public void Presets_and_badges_are_filtered_by_visibility_and_race()
    {
        var c = Load();
        Assert.Equal(["sm_a", "sm_x"], c.SpaceMarines.Presets.Select(p => p.Name));              // hidden and DLC-set schemes dropped
        Assert.Equal(["csm_y"], c.ChaosMarines.Presets.Select(p => p.Name));
        var x = c.SpaceMarines.Presets.Single(p => p.Name == "sm_x");
        Assert.True(x.ClassBadgeEnabled);
        Assert.Equal(("Chapter X", "blue_1"), (x.DisplayName, x.Scheme.Default!.Primary.Name));
        Assert.Equal(["sm_bar", "sm_bar_bw_inverted", "sm_blank_badge", "sm_custom_01_white", "sm_foo_black", "sm_foo_orig"], c.SpaceMarines.Decals.Select(d => d.Name));
        Assert.Equal(["csm_star"], c.ChaosMarines.Decals.Select(d => d.Name));                  // the hidden blank is dropped
    }

    [Fact]
    public void Badge_variants_group_into_families_by_suffix_and_the_blank_is_flagged()
    {
        var d = Load().SpaceMarines.Decals.ToDictionary(x => x.Name);
        Assert.Equal(("sm_foo", "black"), (d["sm_foo_black"].Family, d["sm_foo_black"].Variant));
        Assert.Equal(("sm_foo", "orig"), (d["sm_foo_orig"].Family, d["sm_foo_orig"].Variant));
        Assert.Equal(("sm_bar", "bw_inverted"), (d["sm_bar_bw_inverted"].Family, d["sm_bar_bw_inverted"].Variant));
        Assert.Equal(("sm_bar", ""), (d["sm_bar"].Family, d["sm_bar"].Variant));                  // no variants
        Assert.Equal(("sm_custom_01", "white"), (d["sm_custom_01_white"].Family, d["sm_custom_01_white"].Variant));
        Assert.True(d["sm_blank_badge"].IsBlank);
        Assert.False(d["sm_foo_orig"].IsBlank);
        Assert.Equal("Foo - Black", d["sm_foo_black"].DisplayName);
        Assert.Equal(("x", ""), CustomizationCatalog.DecalFamily("x"));
        Assert.Equal(("orig", ""), CustomizationCatalog.DecalFamily("orig"));                      // a bare suffix is a name, not a variant
    }

    // ---- levels and unlocks ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Unlock_levels_come_from_the_progression_table_and_missing_gates_read_as_level_one()
    {
        var notes = new List<string>();
        var u = Load(notes).Unlocks;
        Assert.Equal((4, 5, 15, 1, 2, 3), (u.Customizer, u.CustomAppearance, u.SecondPerkSlot, u.Prestige, u.DevastatorClass, u.AssaultClass));
        Assert.Equal([4, 11, 17], u.LoadoutSlots);                                 // the fourth to sixth are absent: not invented
        Assert.Equal((1, 2, 3, 1), (u.ClassLevel("tactical"), u.ClassLevel("heavy"), u.ClassLevel("assault"), u.ClassLevel("other")));
        Assert.Equal((1, 15), (u.PerkSlotLevel(1), u.PerkSlotLevel(2)));   // 1-based, as Loadouts.PerkIndex
        Assert.Contains(notes, n => n.Contains("unlocked_func_info") && n.Contains("not found"));
    }

    [Fact]
    public void Without_a_progression_table_every_gate_is_one_and_a_note_says_so()
    {
        WriteInstall(progression: false);
        var notes = new List<string>();
        var u = CustomizationCatalog.Load(_dir, notes).Unlocks;
        Assert.Equal((1, 1, 1, 1, 1, 1), (u.Customizer, u.CustomAppearance, u.SecondPerkSlot, u.Prestige, u.DevastatorClass, u.AssaultClass));
        Assert.Empty(u.LoadoutSlots);
        Assert.Contains(notes, n => n.Contains("no profile_progression_info"));
    }

    [Fact]
    public void Items_and_perks_carry_the_files_unlock_level_and_classes_the_progression_level()
    {
        var c = Load();
        var tactical = c.Perks.Class("sm - tactical")!;
        Assert.Equal([1, 8], tactical.Primary.Items.Select(i => i.Level));
        Assert.Equal(9, tactical.Equipment.Items.Single(i => i.Name == "sm_mp_eq_stimpack").Level);
        Assert.Equal(7, c.Perks.Perk("mp_perk_a")!.Level);
        Assert.Equal(25, c.Perks.Perk("mp_perk_bolter_in_place_of_pistol")!.Level);
        Assert.Equal((1, 3, 2), (c.Class("sm - tactical")!.Level, c.Class("sm - assault marine")!.Level, c.Class("sm - devastator")!.Level));
    }

    // ---- class overrides and cameras --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Class_overrides_name_the_forced_piece_its_packages_and_the_wargear_it_requires()
    {
        var c = Load();
        var dev = Assert.Single(c.Class("sm - devastator")!.Overrides);
        Assert.Equal(("sm_backpack_heavy_bolter", "backpack", "sm_mp_exotic_heavy_bolter"), (dev.Piece.Name, dev.Piece.Slot, dev.RequiredWargear));
        Assert.Equal(["pkg_hb_bp"], dev.Piece.Packages);
        var raptor = c.Class("csm - raptor")!.Overrides;
        Assert.Equal("csm_raptor_head", raptor[0].Piece.Name);
        Assert.Null(raptor[0].RequiredWargear);                                    // an empty required_wargear means always
        Assert.Equal("chaos", raptor[0].Piece.Race);
        Assert.Equal(("mp_perk_iron_halo_for_havoc", "perk"), (raptor[1].Piece.Name, raptor[1].Piece.Slot));
        Assert.Equal("mp_perk_iron_halo", raptor[1].RequiredWargear);
        Assert.Empty(c.Class("sm - tactical")!.Overrides);
    }

    [Fact]
    public void Customizer_cameras_keep_the_tab_order_and_read_integer_or_float_values()
    {
        var c = Load();
        Assert.Equal(["initial", "perks"], c.Cameras.Select(k => k.Tab));
        var perks = c.Camera("PERKS")!;
        Assert.Equal((0f, -0.16f, 4.1f, 0.61f, -1.5f), (perks.HorizontalAngle, perks.VerticalAngle, perks.Distance, perks.VerticalOffset, perks.HorizontalOffset));
        Assert.Null(c.Camera("honors"));
    }

    // ---- locale resolution and fallbacks ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Display_text_is_resolved_through_the_locale_with_the_file_name_as_fallback()
    {
        var c = Load();
        var head = c.SpaceMarines.PiecesFor("head").Single(p => p.Name == "sm_a_head");
        Assert.Equal(("Aquila Helm", "A helmet."), (head.DisplayName, head.Description));
        var noName = c.SpaceMarines.PiecesFor("leftarm").Single();
        Assert.Equal(("sm_a_arm_left", ""), (noName.DisplayName, noName.Description));          // text id 0: the file name, no description
        Assert.Equal("Mk8 Errant", c.SpaceMarines.Set("mk_8_errant")!.DisplayName);
        Assert.Equal("Tactical Marine", c.Perks.Class("sm - tactical")!.DisplayName);
        Assert.Equal("Versatile.", c.Perks.Class("sm - tactical")!.Description);
        Assert.Equal("sm_mp_ranged_plasma_gun", c.Perks.Class("sm - tactical")!.Primary.Items[1].DisplayName);       // text id 0: the file name
        Assert.Equal("Chapter A", c.SpaceMarines.Presets.Single(p => p.Name == "sm_a").DisplayName);
    }

    [Fact]
    public void A_missing_locale_falls_back_to_file_names_everywhere_and_adds_a_note()
    {
        WriteInstall(locale: false);
        var notes = new List<string>();
        var c = CustomizationCatalog.Load(_dir, notes);
        Assert.Contains(notes, n => n.Contains("locale english not found"));
        Assert.Equal("sm_a_head", c.SpaceMarines.PiecesFor("head")[0].DisplayName);
        Assert.Equal("blue_1", c.Palette.Single(p => p.Name == "blue_1").DisplayName);
        Assert.Equal("mp_perk_a", c.Perks.Perk("mp_perk_a")!.DisplayName);
        Assert.Equal("sm - tactical", c.Perks.Class("sm - tactical")!.DisplayName);
        Assert.Null(c.Perks.Perk("mp_perk_weap_x")!.ChallengeText);                  // challenge text needs the locale
        Assert.Equal("mk_8_errant", c.SpaceMarines.Set("mk_8_errant")!.DisplayName);
    }

    [Fact]
    public void A_corrupt_locale_is_noted_and_does_not_stop_the_catalogue()
    {
        WriteInstall(locale: false);
        File.WriteAllBytes(LocalePath(), [9, 9, 9, 9, 1, 2, 3, 4, 5, 6]);
        var notes = new List<string>();
        var c = CustomizationCatalog.Load(_dir, notes);
        Assert.Contains(notes, n => n.Contains("locale english unreadable"));
        Assert.Equal(4, c.Palette.Count);
    }

    // ---- perks and loadout choices -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Perks_follow_the_class_list_order_with_kind_level_upgrade_string_and_challenge_text()
    {
        var tactical = Load().Perks.Class("sm - tactical")!;
        Assert.Equal(["mp_perk_a", "mp_perk_weap_x", "mp_perk_secondary_weapon"], tactical.Perks.Select(p => p.Name));
        Assert.Equal("mp_perk_a", tactical.DefaultPerk!.Name);
        var a = tactical.Perks[0];
        Assert.Equal(("mp_perk_a", PerkKind.Class, 7, "Perk A", "Does A.", (string?)null), (a.UpgradeString, a.Kind, a.Level, a.DisplayName, a.Description, a.ChallengeText));
        var x = tactical.Perks[1];
        Assert.Equal(("mp_perk_weap_x_reload", PerkKind.Weapon, 1, "Bolter: Kill 50 enemies"), (x.UpgradeString, x.Kind, x.Level, x.ChallengeText));   // string differs from the name; %1TARGET% filled in
        Assert.Null(tactical.Perks[2].UpgradeString);                              // Weapon Versatility applies none
        Assert.Equal("Win 15 games", Load().SpaceMarines.PiecesFor("torso").Single(p => p.Name == "sm_b_torso").ChallengeText!.Split(": ")[1]);
        Assert.Null(Load().Perks.Class("sm - devastator")!.DefaultPerk);
    }

    [Fact]
    public void All_lists_each_perk_once_ordinal_and_lookup_is_case_insensitive()
    {
        var perks = Load().Perks;
        Assert.Equal(["mp_perk_a", "mp_perk_bolter_in_place_of_pistol", "mp_perk_secondary_weapon", "mp_perk_weap_x"], perks.All.Select(p => p.Name));
        Assert.Same(perks.Perk("mp_perk_a"), perks.Perk("MP_PERK_A"));
        Assert.Null(perks.Perk("mp_perk_nothing"));
        Assert.Equal("sm - tactical", perks.ForRole("tactical", "space_marine")!.Key);
        Assert.Equal("csm - raptor", perks.ForRole("assault", "chaos")!.Key);
        Assert.Null(perks.ForRole("tactical", "chaos"));
    }

    [Fact]
    public void Tactical_choices_are_the_startup_lists_with_fixed_pistol_and_knife_and_no_hidden_or_missing_items()
    {
        var notes = new List<string>();
        var tactical = Load(notes).Perks.Class("sm - tactical")!;
        Assert.True(tactical.Primary.Selectable);
        Assert.Equal(["sm_mp_ranged_bolter", "sm_mp_ranged_plasma_gun"], tactical.Primary.Items.Select(i => i.Name));   // sm_mp_ranged_missing has no file
        Assert.Contains(notes, n => n.Contains("sm_mp_ranged_missing") && n.Contains("not found"));
        Assert.False(tactical.Pistol.Selectable);
        Assert.Equal(["sm_mp_pistol_bolt_pistol"], tactical.Pistol.Items.Select(i => i.Name));
        Assert.False(tactical.Melee.Selectable);
        Assert.Equal(["sm_mp_melee_combat_knife"], tactical.Melee.Items.Select(i => i.Name));
        Assert.Equal(["sm_mp_eq_frag_grenade", "sm_mp_eq_stimpack"], tactical.Equipment.Items.Select(i => i.Name));    // the hidden melta bomb is not offered
        Assert.Equal("sm_mp_ranged_bolter", tactical.Primary.Default!.Name);
        Assert.Same(tactical.Primary, tactical.For("ranged"));
        Assert.Same(tactical.Equipment, tactical.For("equipment"));
        Assert.Throws<ArgumentException>(() => tactical.For("head"));
    }

    [Fact]
    public void Unsimulated_stims_and_melee_weapons_are_listed_but_flagged()
    {
        var c = Load().Perks;
        var stims = c.Class("sm - tactical")!.Equipment.Items.Single(i => i.Name == "sm_mp_eq_stimpack");
        Assert.False(stims.Simulated);
        Assert.NotNull(stims.Note);
        Assert.True(c.Class("sm - tactical")!.Equipment.Items.Single(i => i.Name == "sm_mp_eq_frag_grenade").Simulated);
        var assault = c.Class("sm - assault marine")!;
        Assert.Equal(["sm_mp_melee_lightning_claw", "sm_mp_melee_chainsword"], assault.Melee.Items.Select(i => i.Name));          // data order kept
        Assert.Equal([false, true], assault.Melee.Items.Select(i => i.Simulated));
        Assert.Equal("sm_mp_melee_chainsword", assault.Melee.Default!.Name);                                                   // the first simulated one
        Assert.True(c.Class("sm - tactical")!.Melee.Items[0].Simulated);                                                       // knife
        Assert.True(c.Class("csm - raptor")!.Melee.Items.Single().Simulated);                                                  // Chaos chainsword matches by suffix
    }

    [Fact]
    public void Assault_has_no_primary_and_chooses_pistol_and_melee_with_the_bolter_pistols_needing_true_grit()
    {
        var assault = Load().Perks.Class("sm - assault marine")!;
        Assert.False(assault.Primary.Selectable);
        Assert.Empty(assault.Primary.Items);
        Assert.Null(assault.Primary.Default);
        Assert.True(assault.Pistol.Selectable);
        Assert.Equal([null, PerkCatalog.BolterPistolPerk, PerkCatalog.BolterPistolPerk], assault.Pistol.Items.Select(i => i.RequiredPerk));
        Assert.Equal("sm_mp_pistol_bolt_pistol", assault.Pistol.Default!.Name);
    }

    [Fact]
    public void Bolter_pistols_are_marked_only_for_classes_that_list_True_Grit()
    {
        // The rule is data-implied: only a class whose perk list has True Grit marks the pistol-slot bolters.
        var global = BafDocument.Read(GlobalBaf());
        var perks = PerkCatalog.Build(global, name => name.StartsWith("mp_perk") ? BafDocument.Read(Gear("perk")) : BafDocument.Read(Gear(name.Contains("pistol") ? "pistol" : "melee")), null, []);
        Assert.Equal(PerkCatalog.BolterPistolPerk, perks.Class("sm - assault marine")!.Pistol.Items.Single(i => i.Name == "sm_mp_pistol_bolter").RequiredPerk);
        Assert.Null(perks.Class("sm - tactical")!.Pistol.Items.Single().RequiredPerk);
    }

    [Fact]
    public void A_perk_without_a_file_is_listed_by_name_with_a_note()
    {
        var global = BafDocument.Read(GlobalBaf());
        var notes = new List<string>();
        var perks = PerkCatalog.Build(global, _ => null, null, notes);
        var a = perks.Perk("mp_perk_a")!;
        Assert.Equal(("mp_perk_a", null, PerkKind.Class, 1), (a.DisplayName, a.UpgradeString, a.Kind, a.Level));
        Assert.Equal(PerkKind.Weapon, perks.Perk("mp_perk_weap_x")!.Kind);
        Assert.Contains(notes, n => n.Contains("mp_perk_a") && n.Contains("not found"));
        Assert.All(perks.Classes, c => Assert.Empty(c.Primary.Items));
    }

    // ---- loading -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PerkCatalog_loads_on_its_own_from_the_same_install()
    {
        WriteInstall();
        var perks = PerkCatalog.Load(_dir);
        Assert.Equal(["sm - tactical", "sm - assault marine", "sm - devastator", "csm - raptor"], perks.Classes.Select(c => c.Key));   // the data's order
        Assert.Equal("Bolter: Kill 50 enemies", perks.Perk("mp_perk_weap_x")!.ChallengeText);
        Assert.Equal("Chaos Raptor", perks.Class("csm - raptor")!.DisplayName);
    }

    [Fact]
    public void Missing_optional_packages_leave_empty_lists_and_notes_but_a_missing_global_throws()
    {
        WritePackage("global", Entry("global", GlobalBaf()));
        var notes = new List<string>();
        var c = CustomizationCatalog.Load(_dir, notes);
        Assert.Empty(c.SpaceMarines.PiecesFor("head"));
        Assert.Empty(c.Palette);
        Assert.Empty(c.SpaceMarines.Decals);
        Assert.Null(c.SpaceMarines.DefaultSet);
        Assert.Contains(notes, n => n.Contains("mp_armor_set"));
        Assert.Contains(notes, n => n.Contains("wargear"));
        Assert.Contains(notes, n => n.Contains("custom_paint_colour"));
        Assert.Equal(4, c.Perks.Classes.Count);
        Assert.ThrowsAny<IOException>(() => CustomizationCatalog.Load(Path.Combine(_dir, "nowhere")));
    }

    [Fact]
    public void Loading_twice_gives_the_same_lists()
    {
        WriteInstall();
        var a = CustomizationCatalog.Load(_dir);
        var b = CustomizationCatalog.Load(_dir);
        Assert.Equal(a.Palette.Select(p => p.Name), b.Palette.Select(p => p.Name));
        Assert.Equal(CustomizationCatalog.ArmourSlots.SelectMany(s => a.SpaceMarines.PiecesFor(s).Select(p => p.Name)),
            CustomizationCatalog.ArmourSlots.SelectMany(s => b.SpaceMarines.PiecesFor(s).Select(p => p.Name)));
    }
}
