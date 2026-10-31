using System.Text;
using SmContent;
using SmContent.Customization;
using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// The customization profile (T4): the store with its repair rules, the wire mapping to simulation indices and appearance codes, and the appearance
/// resolver, on a synthetic install and hand-built simulation configs (names, tables and counts are made up; no original data).
/// </summary>
public class CustomizationProfileTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-prof-" + Guid.NewGuid().ToString("N"));
    readonly string _profileDir;
    readonly Lazy<CustomizationCatalog> _catalog;
    string Packages => Path.Combine(_dir, "data", "packages");
    CustomizationCatalog Catalog => _catalog.Value;

    static readonly DateTime Noon = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    public CustomizationProfileTests()
    {
        Directory.CreateDirectory(Packages);
        _profileDir = Path.Combine(_dir, "user");
        _catalog = new Lazy<CustomizationCatalog>(() => { WriteInstall(); return CustomizationCatalog.Load(_dir, []); });
    }

    public void Dispose() => Directory.Delete(_dir, true);

    ProfileStore NewStore() => new(_profileDir, () => Noon);

    // ---- synthetic install -----------------------------------------------------------------------------------------------------------------

    static (string, object)[] T(params (string, object)[] items) => items;
    static byte[] Baf(params (string, object)[] root) => SyntheticBafWriter.Build(root);
    static SyntheticObpkWriter.File Entry(string name, byte[] data) => new("attr_pc", name, data);
    void WritePackage(string package, params SyntheticObpkWriter.File[] files) => File.WriteAllBytes(Path.Combine(Packages, package + ".oppc"), SyntheticObpkWriter.Build(files));

    const string W = @"wargear\wargear\multiplayer\";
    static (string, object) Wg(string name) => ("wargear", W + "x\\" + name);
    static (string, object)[] WgList(params string[] names) => names.Select(Wg).ToArray();

    static byte[] Gear(string slot, string[]? packages = null, bool visible = true, bool thrown = false, string helmet = "no_override", string? upgrade = null)
    {
        var attachments = (packages ?? []).Select((p, i) => (i == 0 ? "torso" : @"wargear\attachment_entry", (object)T(("visual_package", p)))).ToArray();
        var root = new List<(string, object)>
        {
            ("visual_data", T(("attachments", attachments))),
            ("common_data", T(("slot", slot), ("helmet_override", helmet), ("unlock_info", T(("level_requirement", T(("level_required", 1))))))),
            ("ui_data", T(("name", 0), ("description", 0), ("visible_in_customizer", visible))),
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
        string[] perks = ["mp_perk_a", "mp_perk_weap_x", "mp_perk_secondary_weapon"];
        var classes = T(
            ("sm - tactical", ClassTable("space_marine", "tactical", ["sm_mp_abil_sprint", "sm_mp_melee_combat_knife", "sm_mp_pistol_bolt_pistol"],
                ["sm_mp_eq_frag_grenade", "sm_mp_eq_stimpack", "sm_mp_ranged_bolter", "sm_mp_ranged_plasma_gun"], perks, "mp_perk_a", "sm_default")),
            ("sm - assault marine", ClassTable("space_marine", "assault", ["sm_mp_abil_sprint", "sm_mp_abil_jumppack"],
                ["sm_mp_pistol_bolt_pistol", "sm_mp_pistol_bolter", "sm_mp_melee_lightning_claw", "sm_mp_melee_chainsword", "sm_mp_eq_frag_grenade"],
                ["mp_perk_a", "mp_perk_bolter_in_place_of_pistol"], "mp_perk_a", "sm_default")),
            ("sm - devastator", ClassTable("space_marine", "heavy", ["sm_mp_abil_sprint", "sm_mp_pistol_bolt_pistol"],
                ["sm_mp_exotic_heavy_bolter", "sm_mp_exotic_las_cannon"], ["mp_perk_a"], "mp_perk_a", "sm_default", ("sm_backpack_heavy_bolter", "sm_mp_exotic_heavy_bolter"))),
            ("csm - tactical", ClassTable("chaos", "tactical", ["sm_mp_abil_sprint", "csm_mp_melee_combat_knife", "csm_mp_pistol_bolt_pistol"],
                ["csm_mp_eq_frag_grenade", "csm_mp_ranged_bolter", "csm_mp_ranged_plasma_gun"], perks, "mp_perk_a", "chaos_default")),
            ("csm - raptor", ClassTable("chaos", "assault", ["sm_mp_abil_sprint", "sm_mp_abil_jumppack"], ["csm_mp_melee_chain_axe", "csm_mp_melee_chainsword", "csm_mp_pistol_bolt_pistol"],
                ["mp_perk_a"], "mp_perk_a", "chaos_default", ("csm_raptor_head", ""), ("csm_raptor_leg_l", ""), ("csm_raptor_leg_r", ""))),
            ("csm - havoc", ClassTable("chaos", "heavy", ["sm_mp_abil_sprint", "csm_mp_pistol_bolt_pistol"], ["csm_mp_pistol_bolt_pistol"],
                ["mp_perk_a"], "mp_perk_a", "chaos_default", ("csm_backpack_havoc", ""))));
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
            Entry("sm_mp_eq_stimpack", Gear("equipment")),
            Entry("sm_mp_abil_sprint", Gear("coreability")),
            // The jump pack: the catalogue marks the classes that carry one (ClassCustomization.JumpPack), and the resolver hides their backpack.
            Entry("sm_mp_abil_jumppack", Baf(("common_data", T(("slot", "coreability"))), ("ability_data", T(("ability_definitions", T(("ability_jumppack", T()))))))),
            Entry("sm_mp_exotic_heavy_bolter", Gear("ranged")), Entry("sm_mp_exotic_las_cannon", Gear("ranged")),
            Entry("mp_perk_a", Gear("perk", ["sm_acc_perk_a"], upgrade: "mp_perk_a")), Entry("mp_perk_weap_x", Gear("perk", upgrade: "x")),
            Entry("mp_perk_secondary_weapon", Gear("perk")), Entry("mp_perk_bolter_in_place_of_pistol", Gear("perk")),
        };
        string[] slots = ["head", "torso", "leftshoulder", "rightshoulder", "leftarm", "rightarm", "leftleg", "rightleg", "backpack"];
        string[] smParts = ["helmet", "torso", "shldrl", "shldrr", "arml", "armr", "legl", "legr", "bp"];
        for (int i = 0; i < 9; i++)
        {
            wargear.Add(Entry(SmA[i], Gear(slots[i], i == 1 ? ["pkg_a_torso", "pkg_a_belt"] : ["pkg_a_" + smParts[i]], helmet: i == 0 ? "helmet" : "no_override")));
            wargear.Add(Entry(CsmBasic[i], Gear(slots[i], ["pkg_c_" + smParts[i]])));
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
        wargear.Add(Entry("csm_backpack_havoc", Gear("backpack", ["pkg_hv_bp"])));
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

    // ---- a rich profile ---------------------------------------------------------------------------------------------------------------------

    static byte[] EmblemBytes(byte seed)
    {
        var bytes = new byte[EmblemHash.BlobBytes];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + seed);
        return bytes;
    }

    /// <summary>A valid profile touching every field (the palette: blue_0 0, blue_1 1, gold 2, red_1 3; sm badges: sm_bar 0, sm_blank_badge 1, sm_foo_black 2, sm_foo_orig 3).</summary>
    static CustomizationProfile RichProfile(string emblemHex)
    {
        var look = new AppearanceProfile("sm-1", "space_marine", "Look", null,
            new Dictionary<string, string> { ["head"] = "sm_b_head", ["torso"] = "sm_b_torso" },
            new Dictionary<string, SlotPaint> { ["head"] = new("blue_1", "red_1", "gold", true), ["leftarm"] = new("gold", null, null, false) },
            "sm_x",
            new Dictionary<string, DecalChoice> { ["leftshoulder"] = DecalChoice.ForBadge("sm_foo_orig"), ["rightshoulder"] = DecalChoice.ForEmblem(emblemHex, "sm_bar") },
            new LensColour(10, 200, 30));
        var chaos = AppearanceProfile.Empty("csm-1", "chaos", "Warband") with
        {
            Paint = new Dictionary<string, SlotPaint> { ["torso"] = new("red_1", "gold", "blue_0", true) },
            Decals = new Dictionary<string, DecalChoice> { ["leftshoulder"] = DecalChoice.ForBadge("csm_star"), ["rightshoulder"] = DecalChoice.NoDecal },
        };
        var tactical = new CustomClass(3, "My Tactical", "tactical", "mp_ranged_plasma_gun", null, null, "mp_eq_frag_grenade", ["mp_perk_a", "mp_perk_weap_x"], new AppearanceRefs("sm-1", "csm-1"));
        var assault = new CustomClass(7, "Axe man", "assault", null, "mp_pistol_bolt_pistol", "mp_melee_chainsword", null, [null, null], AppearanceRefs.None);
        return new CustomizationProfile(1, Selection.CustomSlot(3), [tactical, assault], [look, chaos]);
    }

    // ===== store: round trip, defaults, emblems ===========================================================================================

    [Fact]
    public void A_profile_saves_and_loads_identically_with_no_repairs()
    {
        var store = NewStore();
        var hash = store.SaveEmblem(EmblemBytes(1));
        var profile = RichProfile(hash.ToHex());
        store.Save(profile);

        var (loaded, repairs) = NewStore().Load(Catalog);
        Assert.Empty(repairs);
        Assert.Equal(ProfileStore.Serialize(profile), ProfileStore.Serialize(loaded));
        Assert.Equal(Selection.CustomSlot(3), loaded.Selected);
        Assert.Equal(["mp_perk_a", "mp_perk_weap_x"], loaded.ClassAt(3)!.Perks);
        Assert.Equal(new LensColour(10, 200, 30), loaded.Appearance("sm-1")!.Lens);
        Assert.Equal(hash.ToHex(), loaded.Appearance("sm-1")!.Decals["rightshoulder"].Emblem);
        Assert.False(loaded.ReadOnly);

        string first = File.ReadAllText(store.ProfilePath);
        NewStore().Save(loaded);
        Assert.Equal(first, File.ReadAllText(store.ProfilePath));                      // a second save is byte-identical
        Assert.Empty(Directory.GetFiles(_profileDir, "*.tmp-*"));                      // the temp file is gone
        Assert.Contains("\"schema\": 1", first);
    }

    [Fact]
    public void A_missing_file_gives_the_default_profile_and_nothing_is_written()
    {
        var result = NewStore().Load(Catalog);
        Assert.Equal(ProfileLoadStatus.Missing, result.Status);
        Assert.Empty(result.Repairs);
        Assert.Equal(Selection.DefaultFor("tactical"), result.Profile.Selected);
        Assert.Empty(result.Profile.Classes);
        Assert.Empty(result.Profile.Appearances);
        Assert.False(Directory.Exists(_profileDir));
        Assert.Equal(1, result.Profile.FirstFreeSlot());
        Assert.Equal("sm-1", result.Profile.NewAppearanceId("space_marine"));
        Assert.Equal("csm-1", result.Profile.NewAppearanceId("chaos"));
        var roles = ProfileStore.Roles(Catalog);
        Assert.Equal("tactical", roles[0]);
        Assert.Equal(["assault", "heavy", "tactical"], roles.Order());
    }

    [Fact]
    public void Emblems_are_stored_by_hash_and_verified_on_read()
    {
        var store = NewStore();
        byte[] bytes = EmblemBytes(7);
        var hash = store.SaveEmblem(bytes);
        Assert.Equal(EmblemHash.Of(bytes), hash);
        Assert.Equal(Path.Combine(_profileDir, "emblems", hash.ToHex() + ".bc3"), store.EmblemPath(hash));
        Assert.Equal(bytes, store.LoadEmblem(hash));
        Assert.Equal(hash, store.SaveEmblem(bytes));                                   // idempotent
        Assert.Throws<ArgumentException>(() => store.SaveEmblem(new byte[100]));
        Assert.Null(store.LoadEmblem(EmblemHash.None));
        Assert.Null(store.LoadEmblem(EmblemHash.Of(EmblemBytes(8))));                  // not stored

        var damaged = (byte[])bytes.Clone();
        damaged[5] ^= 0xFF;
        File.WriteAllBytes(store.EmblemPath(hash), damaged);
        Assert.Null(store.LoadEmblem(hash));                                           // content does not hash to its name
        File.WriteAllBytes(store.EmblemPath(hash), bytes[..100]);
        Assert.False(store.HasEmblem(hash));                                           // wrong size
    }

    // ===== store: repair rules ===========================================================================================================

    const string BadProfile = """
    { "schema": 1, "selected": { "kind": "custom", "slot": 9 },
      "classes": [
        { "slot": 1, "name": "  \u0007Good   name that is far too long to fit the limit  ", "role": "tactical",
          "primary": "sm_mp_ranged_bolter", "pistol": "mp_pistol_bolt_pistol", "melee": "mp_melee_combat_knife", "equipment": "mp_eq_nonsense",
          "perks": ["MP_PERK_A", "mp_perk_a", "mp_perk_weap_x"], "appearance": { "space_marine": "sm-1", "chaos": "sm-1" } },
        { "slot": 1, "role": "tactical" },
        { "slot": 11, "role": "tactical" },
        { "slot": 2, "role": "nonsense" },
        { "slot": 3, "role": "assault", "melee": "mp_melee_lightning_claw", "pistol": "mp_pistol_bolter", "primary": "mp_ranged_bolter", "perks": [null, "mp_perk_not_a_perk"] },
        "garbage"
      ],
      "appearances": [
        { "id": "sm-1", "race": "space_marine", "name": "x", "predefinedSet": "nonexistent", "presetApplied": "nothing",
          "pieces": { "head": "csm_basic_head", "torso": "sm_b_torso", "backpack": "sm_zzz", "belt": "sm_b_belt" },
          "paint": { "head": { "primary": "blue_1", "pattern": "nope", "detail": "gold", "patternOn": true }, "legs": { }, "torso": { "primary": null } },
          "decals": { "leftshoulder": { "emblem": "00112233445566778899aabbccddeeff", "fallback": "sm_bar" }, "rightshoulder": { "badge": "csm_star" }, "head": { } },
          "lens": [1, 2, 300] },
        { "id": "bad id!", "race": "chaos" },
        { "id": "sm-1", "race": "space_marine" },
        { "race": "orks" }
      ] }
    """;

    ProfileLoadResult LoadText(string json)
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(Path.Combine(_profileDir, "customization.json"), json);
        return NewStore().Load(Catalog);
    }

    static bool Has(ProfileLoadResult r, string path, string text, bool informational = false) =>
        r.Repairs.Any(x => x.Path == path && x.Informational == informational && x.Message.Contains(text, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Unknown_wrong_race_and_not_offered_names_become_defaults_with_notes()
    {
        var r = LoadText(BadProfile);
        Assert.Equal(ProfileLoadStatus.Loaded, r.Status);
        Assert.True(r.Repaired);
        var p = r.Profile;
        Assert.Equal([1, 3], p.Classes.Select(c => c.Slot));                           // duplicate slot, slot 11, unknown role and garbage are dropped
        Assert.True(Has(r, "classes[1]", "used twice"));
        Assert.True(Has(r, "classes[2]", "1..10"));
        Assert.True(Has(r, "classes[3]", "unknown role"));
        Assert.True(Has(r, "classes[5]", "not an object"));

        var tactical = p.ClassAt(1)!;
        Assert.Equal("mp_ranged_bolter", tactical.Primary);                            // stored race-neutral
        Assert.Null(tactical.Pistol);                                                  // the tactical pistol and melee are fixed: no choice
        Assert.Null(tactical.Melee);
        Assert.Null(tactical.Equipment);                                               // unknown item
        Assert.True(Has(r, "classes[0].pistol", "no choice"));
        Assert.True(Has(r, "classes[0].equipment", "not offered"));
        Assert.Equal(new string?[] { "mp_perk_a", null }, tactical.Perks);             // spelled as the catalogue; the duplicate dropped; a third is not kept
        Assert.True(Has(r, "classes[0].perks[1]", "twice"));
        Assert.True(Has(r, "classes[0].perks", "only the first two"));
        Assert.Equal(new AppearanceRefs("sm-1", null), tactical.Appearance);           // sm-1 is a Space Marine look: not a Chaos one
        Assert.True(Has(r, "classes[0].appearance.chaos", "for space_marine"));

        var assault = p.ClassAt(3)!;
        Assert.Null(assault.Primary);                                                  // the assault class has no primary
        Assert.True(Has(r, "classes[4].primary", "no choice"));
        Assert.Equal(new string?[] { null, null }, assault.Perks);
        Assert.True(Has(r, "classes[4].perks[1]", "not a perk"));

        Assert.Equal(Selection.DefaultFor("tactical"), p.Selected);                    // slot 9 has no class
        Assert.True(Has(r, "selected", "no class"));
    }

    [Fact]
    public void Unsimulated_items_are_kept_and_flagged_not_repaired()
    {
        var r = LoadText(BadProfile);
        var assault = r.Profile.ClassAt(3)!;
        Assert.Equal("mp_melee_lightning_claw", assault.Melee);                             // the simulation has no combo for it: kept
        Assert.Equal("mp_pistol_bolter", assault.Pistol);
        Assert.True(Has(r, "classes[4].melee", "not simulated", informational: true));
        Assert.True(Has(r, "classes[4].pistol", "needs the perk mp_perk_bolter_in_place_of_pistol", informational: true));
        Assert.Equal([("melee", "mp_melee_lightning_claw")], ProfileStore.UnsimulatedItems(assault, Catalog));
        Assert.Empty(ProfileStore.UnsimulatedItems(RichProfile("").ClassAt(3)!, Catalog));
        var stims = new CustomClass(1, "s", "tactical", null, null, null, "mp_eq_stimpack", [null, null], AppearanceRefs.None);
        Assert.Equal([("equipment", "mp_eq_stimpack")], ProfileStore.UnsimulatedItems(stims, Catalog));
    }

    [Fact]
    public void Names_are_trimmed_cleaned_and_cut_and_empty_ones_get_the_default_name()
    {
        var r = LoadText(BadProfile);
        string name = r.Profile.ClassAt(1)!.Name;
        Assert.True(name.Length <= CustomizationProfile.MaxNameLength);
        Assert.Equal(name.Trim(), name);
        Assert.DoesNotContain('\u0007', name);
        Assert.StartsWith("Good   name that is far", name);
        Assert.Equal("CUSTOM LOADOUT 3", r.Profile.ClassAt(3)!.Name);                  // none given
        Assert.Equal("Appearance 1", r.Profile.Appearances[1].Name);
    }

    [Fact]
    public void Appearances_are_repaired_field_by_field()
    {
        var r = LoadText(BadProfile);
        Assert.Equal(["sm-1", "csm-1", "sm-2"], r.Profile.Appearances.Select(a => a.Id));   // the odd id and the duplicate are renumbered; unknown race dropped
        Assert.True(Has(r, "appearances[1].id", "'csm-1' assigned"));
        Assert.True(Has(r, "appearances[2].id", "'sm-2' assigned"));
        Assert.True(Has(r, "appearances[3]", "unknown race"));

        var a = r.Profile.Appearance("sm-1")!;
        Assert.Null(a.PredefinedSet);
        Assert.Null(a.PresetApplied);
        Assert.True(Has(r, "appearances[0].predefinedSet", "not a predefined"));
        Assert.True(Has(r, "appearances[0].presetApplied", "not a space_marine preset"));
        Assert.Equal(new Dictionary<string, string> { ["torso"] = "sm_b_torso" }, a.Pieces);
        Assert.True(Has(r, "appearances[0].pieces.head", "other race"));
        Assert.True(Has(r, "appearances[0].pieces.backpack", "not an offered"));
        Assert.True(Has(r, "appearances[0].pieces.belt", "not an armour slot"));
        Assert.Equal(new SlotPaint("blue_1", null, "gold", true), a.Paint["head"]);
        Assert.True(Has(r, "appearances[0].paint.head.pattern", "not a visible palette colour"));
        Assert.Single(a.Paint);                                                        // legs is no slot; torso set nothing
        Assert.True(Has(r, "appearances[0].paint.legs", "not an armour slot"));
        Assert.Null(a.Lens);
        Assert.True(Has(r, "appearances[0].lens", "0..255"));
        Assert.True(Has(r, "appearances[0].decals.rightshoulder.badge", "not an offered"));
        Assert.True(Has(r, "appearances[0].decals.head", "not a shoulder pad"));
        Assert.False(a.Decals.ContainsKey("rightshoulder"));
    }

    [Fact]
    public void A_missing_or_damaged_emblem_file_shows_the_fallback_badge()
    {
        var r = LoadText(BadProfile);
        Assert.Equal(DecalChoice.ForBadge("sm_bar"), r.Profile.Appearance("sm-1")!.Decals["leftshoulder"]);
        Assert.True(Has(r, "appearances[0].decals.leftshoulder.emblem", "missing or damaged"));

        // With the file present the emblem stays; damaged on disk, it is replaced again.
        var store = NewStore();
        var hash = store.SaveEmblem(EmblemBytes(3));
        string json = $$"""{ "schema": 1, "appearances": [ { "id": "sm-1", "race": "space_marine", "decals": { "leftshoulder": { "emblem": "{{hash.ToHex().ToUpperInvariant()}}", "fallback": "sm_bar" } } } ] }""";
        var kept = LoadText(json);
        Assert.Empty(kept.Repairs);
        Assert.Equal(DecalChoice.ForEmblem(hash.ToHex(), "sm_bar"), kept.Profile.Appearance("sm-1")!.Decals["leftshoulder"]);
        File.WriteAllBytes(store.EmblemPath(hash), new byte[EmblemHash.BlobBytes]);
        Assert.Equal(DecalChoice.ForBadge("sm_bar"), LoadText(json).Profile.Appearance("sm-1")!.Decals["leftshoulder"]);

        // No fallback and no file: the scheme's decal (no entry).
        var none = LoadText("""{ "schema": 1, "appearances": [ { "id": "sm-1", "race": "space_marine", "decals": { "leftshoulder": { "emblem": "00112233445566778899aabbccddeeff" } } } ] }""");
        Assert.Empty(none.Profile.Appearance("sm-1")!.Decals);
    }

    [Fact]
    public void A_look_carries_one_emblem_the_other_pad_falls_back()
    {
        var store = NewStore();
        string a = store.SaveEmblem(EmblemBytes(1)).ToHex(), b = store.SaveEmblem(EmblemBytes(2)).ToHex();
        var r = LoadText("{ \"schema\": 1, \"appearances\": [ { \"id\": \"sm-1\", \"race\": \"space_marine\", \"decals\": { " +
            $"\"leftshoulder\": {{ \"emblem\": \"{a}\", \"fallback\": \"sm_bar\" }}, \"rightshoulder\": {{ \"emblem\": \"{b}\", \"fallback\": \"sm_foo_orig\" }} " +
            "} } ] }");
        var decals = r.Profile.Appearance("sm-1")!.Decals;
        Assert.Equal(DecalChoice.ForEmblem(a, "sm_bar"), decals["leftshoulder"]);
        Assert.Equal(DecalChoice.ForBadge("sm_foo_orig"), decals["rightshoulder"]);
        Assert.True(Has(r, "appearances[0].decals.rightshoulder.emblem", "one custom emblem"));
    }

    [Fact]
    public void At_most_ten_appearances_per_race_and_ten_class_slots_are_kept()
    {
        var looks = string.Join(",", Enumerable.Range(1, 12).Select(i => $$"""{ "id": "sm-{{i}}", "race": "space_marine" }"""));
        var chaos = string.Join(",", Enumerable.Range(1, 3).Select(i => $$"""{ "id": "csm-{{i}}", "race": "chaos" }"""));
        var classes = string.Join(",", Enumerable.Range(1, 12).Select(i => $$"""{ "slot": {{i}}, "role": "heavy", "appearance": { "space_marine": "sm-{{(i == 11 ? 11 : 1)}}" } }"""));
        var r = LoadText($$"""{ "schema": 1, "classes": [{{classes}}], "appearances": [{{looks}},{{chaos}}] }""");
        Assert.Equal(10, r.Profile.AppearancesFor("space_marine").Count());
        Assert.Equal(3, r.Profile.AppearancesFor("chaos").Count());
        Assert.Equal(10, r.Profile.Classes.Count);
        Assert.Equal(Enumerable.Range(1, 10), r.Profile.Classes.Select(c => c.Slot));
        Assert.Equal(0, r.Profile.FirstFreeSlot());
        Assert.Equal(2, r.Repairs.Count(x => x.Path.StartsWith("appearances[") && x.Message.Contains("dropped")));
    }

    [Fact]
    public void Predefined_sets_presets_and_lens_survive_when_valid()
    {
        var r = LoadText("""{ "schema": 1, "appearances": [ { "id": "sm-1", "race": "space_marine", "predefinedSet": "BLACK_TEMPLARS", "presetApplied": "sm_x", "lens": [0, 128, 255] } ] }""");
        Assert.Empty(r.Repairs);
        var a = r.Profile.Appearance("sm-1")!;
        Assert.Equal(("black_templars", "sm_x", new LensColour(0, 128, 255)), (a.PredefinedSet, a.PresetApplied, a.Lens));
        Assert.Equal("Appearance 1", a.Name);
    }

    [Fact]
    public void A_selection_of_a_known_role_or_slot_is_kept()
    {
        Assert.Equal(Selection.DefaultFor("heavy"), LoadText("""{ "schema": 1, "selected": { "kind": "default", "role": "HEAVY" } }""").Profile.Selected);
        var r = LoadText("""{ "schema": 1, "selected": { "kind": "custom", "slot": 4 }, "classes": [ { "slot": 4, "role": "assault" } ] }""");
        Assert.Equal(Selection.CustomSlot(4), r.Profile.Selected);
        Assert.Same(r.Profile.ClassAt(4), r.Profile.SelectedClass);
        var bad = LoadText("""{ "schema": 1, "selected": { "kind": "default", "role": "medic" } }""");
        Assert.Equal(Selection.DefaultFor("tactical"), bad.Profile.Selected);
        Assert.True(Has(bad, "selected.role", "unknown role"));
    }

    // ===== store: newer schema, corrupt files ============================================================================================

    [Fact]
    public void A_newer_schema_is_read_only_and_never_overwritten()
    {
        const string future = """{ "schema": 2, "selected": { "kind": "default", "role": "heavy" }, "classes": [ { "slot": 2, "role": "assault", "name": "Future" } ], "somethingNew": { "a": 1 } }""";
        var r = LoadText(future);
        Assert.Equal(ProfileLoadStatus.NewerSchema, r.Status);
        Assert.True(r.Profile.ReadOnly);
        Assert.Equal(2, r.Profile.Schema);
        Assert.False(r.Repaired);                                                      // only an informational note
        Assert.Contains(r.Repairs, x => x.Informational && x.Message.Contains("never overwritten"));
        Assert.Equal("Future", r.Profile.ClassAt(2)!.Name);                            // read as far as understood

        var store = NewStore();
        Assert.Throws<InvalidOperationException>(() => store.Save(r.Profile));
        Assert.Equal(future, File.ReadAllText(store.ProfilePath));
        // A fresh profile is refused too while the newer file is on disk.
        Assert.Throws<InvalidOperationException>(() => store.Save(CustomizationProfile.Default(Catalog)));
        Assert.Equal(future, File.ReadAllText(store.ProfilePath));
        Assert.Empty(Directory.GetFiles(_profileDir, "*.bad-*"));
        Assert.Empty(Directory.GetFiles(_profileDir, "*.tmp-*"));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ }")]
    [InlineData("""{ "schema": "one" }""")]
    [InlineData("""{ "schema": 0 }""")]
    public void Corrupt_json_is_renamed_aside_and_defaults_are_returned(string text)
    {
        var r = LoadText(text);
        Assert.Equal(ProfileLoadStatus.Corrupt, r.Status);
        Assert.Equal(Selection.DefaultFor("tactical"), r.Profile.Selected);
        Assert.False(File.Exists(Path.Combine(_profileDir, "customization.json")));
        string bad = Path.Combine(_profileDir, "customization.json.bad-20261028T120000Z");
        Assert.True(File.Exists(bad));
        Assert.Equal(text, File.ReadAllText(bad));                                     // the player's bytes are kept
        Assert.Contains(r.Repairs, x => x.Message.Contains("renamed to customization.json.bad-20261028T120000Z"));

        // A second corrupt file in the same second does not overwrite the first.
        File.WriteAllText(Path.Combine(_profileDir, "customization.json"), "{ again");
        NewStore().Load(Catalog);
        Assert.True(File.Exists(bad + "-1"));
        Assert.Equal(text, File.ReadAllText(bad));
        // The next save starts a clean file.
        NewStore().Save(r.Profile);
        Assert.Equal(ProfileLoadStatus.Loaded, NewStore().Load(Catalog).Status);
    }

    [Fact]
    public void Saving_creates_the_directory_and_writes_only_inside_it()
    {
        var store = new ProfileStore(Path.Combine(_profileDir, "a", "b"));
        store.Save(CustomizationProfile.Default(Catalog));
        Assert.True(File.Exists(Path.Combine(_profileDir, "a", "b", "customization.json")));
        Assert.Equal(["customization.json"], Directory.GetFileSystemEntries(Path.Combine(_profileDir, "a", "b")).Select(Path.GetFileName));
    }

    // ===== wire: loadout =================================================================================================================

    static WeaponSpec Weapon(string name, WeaponSlot slot) => new(name, slot, TriggerKind.FullAuto, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
    static ThrownSpec Thrown(string name) => new(name, 1, 1, 1f, 1f, new ProjectileSpec("p", 1, 1, "raycast", 0, [], false, false, false, 0, 0, 0, 0, null, null));
    static MeleeCombo Melee(string name) => new(name, []);

    // Weapons: 0 sm bolter, 1 sm plasma, 2 sm bolt pistol, 3 csm plasma, 4 csm bolter, 5 csm bolt pistol, 6 sm pistol bolter. Thrown: 0 sm frag, 1 csm frag.
    // The sides list the same things in different orders; stims and the power axe are not in any list (dropped by the simulation).
    static SimConfig Config() => new()
    {
        Weapons = [Weapon("sm_mp_ranged_bolter", WeaponSlot.Ranged), Weapon("sm_mp_ranged_plasma_gun", WeaponSlot.Ranged), Weapon("sm_mp_pistol_bolt_pistol", WeaponSlot.Pistol),
            Weapon("csm_mp_ranged_plasma_gun", WeaponSlot.Ranged), Weapon("csm_mp_ranged_bolter", WeaponSlot.Ranged), Weapon("csm_mp_pistol_bolt_pistol", WeaponSlot.Pistol),
            Weapon("sm_mp_pistol_bolter", WeaponSlot.Pistol)],
        Thrown = [Thrown("sm_mp_eq_frag_grenade"), Thrown("csm_mp_eq_frag_grenade")],
        Perks = [new PerkSpec("mp_perk_a", null, PerkKind.Class, 1), new PerkSpec("mp_perk_weap_x", null, PerkKind.Weapon, 1), new PerkSpec("mp_perk_secondary_weapon", null, PerkKind.Class, 1)],
        Classes =
        [
            new ClassLoadout("sm - tactical", 0, 2, []) { Side = Team.SpaceMarines, Role = "tactical", PrimaryChoices = [0, 1], EquipmentChoices = [0], PerkChoices = [0, 1, 2], DefaultPerk = 0 },
            new ClassLoadout("csm - tactical", 4, 5, []) { Side = Team.Chaos, Role = "tactical", PrimaryChoices = [3, 4], EquipmentChoices = [1], PerkChoices = [2, 1, 0], DefaultPerk = 0 },
            new ClassLoadout("sm - assault marine", -1, 2, []) { Side = Team.SpaceMarines, Role = "assault", PistolChoices = [2, 6],
                MeleeChoices = [Melee("sm_mp_melee_chainsword"), Melee("sm_mp_melee_chainsword_gold")], EquipmentChoices = [0], PerkChoices = [0], DefaultPerk = 0 },
            new ClassLoadout("csm - raptor", -1, 5, []) { Side = Team.Chaos, Role = "assault", PistolChoices = [5],
                MeleeChoices = [Melee("csm_mp_melee_chain_axe"), Melee("csm_mp_melee_chainsword")], EquipmentChoices = [1], PerkChoices = [0], DefaultPerk = 0 },
        ],
    };

    static CustomClass Tactical(string? primary = null, string? equipment = null, string? perk1 = null, string? perk2 = null) =>
        new(1, "t", "tactical", primary, null, null, equipment, [perk1, perk2], AppearanceRefs.None);

    [Fact]
    public void ToLoadout_finds_items_by_name_in_each_sides_own_lists()
    {
        var config = Config();
        var custom = Tactical("mp_ranged_plasma_gun", "mp_eq_frag_grenade", "mp_perk_weap_x", "mp_perk_a");
        // Space Marines: plasma is the second primary, perk order as listed. Chaos lists the same things in other orders.
        Assert.Equal(new LoadoutChoice(0, Primary: 1, Pistol: -1, Melee: -1, Equipment: 0, Perk1: 1, Perk2: 0), ProfileWire.ToLoadout(custom, config, Team.SpaceMarines));
        Assert.Equal(new LoadoutChoice(1, Primary: 0, Pistol: -1, Melee: -1, Equipment: 0, Perk1: 1, Perk2: 2), ProfileWire.ToLoadout(custom, config, Team.Chaos));
        // The weapon names behind the indices are the right ones (a positional mapping would have picked the wrong gun on Chaos).
        var chaos = ProfileWire.ToLoadout(custom, config, Team.Chaos);
        Assert.Equal("csm_mp_ranged_plasma_gun", config.Weapons[Loadouts.Choices(config, chaos.Class, WeaponSlot.Ranged)[chaos.Primary]].Name);
        Assert.Equal("mp_perk_weap_x", config.Perks[config.Classes[1].PerkChoices[chaos.Perk1]].Name);
    }

    [Fact]
    public void ToLoadout_sends_the_default_for_items_the_simulation_dropped_or_never_had()
    {
        var config = Config();
        var stims = ProfileWire.ToLoadout(Tactical("mp_ranged_nothing", "mp_eq_stimpack"), config, Team.SpaceMarines);
        Assert.Equal(new LoadoutChoice(0, -1, -1, -1, -1, -1, -1), stims);              // the catalogue lists stims; the simulation dropped them
        Assert.Equal(LoadoutChoice.Default with { Class = 0 }, stims);
        var assault = new CustomClass(2, "a", "assault", null, "mp_pistol_bolt_pistol", "mp_melee_lightning_claw", null, [null, null], AppearanceRefs.None);
        var sm = ProfileWire.ToLoadout(assault, config, Team.SpaceMarines);
        Assert.Equal(new LoadoutChoice(2, -1, 0, -1, -1, -1, -1), sm);                 // the pistol is found, the (fictional) lightning claw is in no list
        // A perk the class cannot take is dropped too.
        Assert.Equal(-1, ProfileWire.ToLoadout(Tactical(perk1: "mp_perk_bolter_in_place_of_pistol"), config, Team.SpaceMarines).Perk1);
    }

    [Fact]
    public void ToLoadout_takes_the_other_sides_position_when_the_name_only_exists_there()
    {
        var config = Config();
        // The Chaos list has the chain axe first, the Space Marine list does not have it: position 0 of the Space Marine list (interpretation).
        var axe = new CustomClass(2, "a", "assault", null, null, "mp_melee_chain_axe", null, [null, null], AppearanceRefs.None);
        Assert.Equal(0, ProfileWire.ToLoadout(axe, config, Team.SpaceMarines).Melee);
        Assert.Equal(0, ProfileWire.ToLoadout(axe, config, Team.Chaos).Melee);         // found by name on its own side
        // The chainsword is on both lists at different positions: by name, never by position.
        var sword = new CustomClass(2, "a", "assault", null, null, "mp_melee_chainsword", null, [null, null], AppearanceRefs.None);
        Assert.Equal(0, ProfileWire.ToLoadout(sword, config, Team.SpaceMarines).Melee);
        Assert.Equal(1, ProfileWire.ToLoadout(sword, config, Team.Chaos).Melee);
        // A position past the end of the side's list gives the default: the Space Marine assault pistols are [bolt pistol, bolter], Chaos has one.
        var bolter = new CustomClass(2, "a", "assault", null, "mp_pistol_bolter", null, null, [null, null], AppearanceRefs.None);
        Assert.Equal(1, ProfileWire.ToLoadout(bolter, config, Team.SpaceMarines).Pistol);
        Assert.Equal(-1, ProfileWire.ToLoadout(bolter, config, Team.Chaos).Pistol);
        // The gold chainsword is only on the Space Marine list (position 1): Chaos takes its own position 1.
        var gold = new CustomClass(2, "a", "assault", null, null, "mp_melee_chainsword_gold", null, [null, null], AppearanceRefs.None);
        Assert.Equal(1, ProfileWire.ToLoadout(gold, config, Team.SpaceMarines).Melee);
        Assert.Equal(1, ProfileWire.ToLoadout(gold, config, Team.Chaos).Melee);        // (interpretation: same position)
    }

    [Fact]
    public void ToLoadout_maps_the_class_by_role_to_the_side_played()
    {
        var config = Config();
        var custom = Tactical();
        Assert.Equal(0, ProfileWire.ToLoadout(custom, config, Team.SpaceMarines).Class);
        Assert.Equal(1, ProfileWire.ToLoadout(custom, config, Team.Chaos).Class);
        Assert.Equal(LoadoutChoice.Default, ProfileWire.ToLoadout(custom, config, Team.None));
        var heavy = new CustomClass(1, "h", "heavy", null, null, null, null, [null, null], AppearanceRefs.None);
        Assert.Equal(LoadoutChoice.Default, ProfileWire.ToLoadout(heavy, config, Team.SpaceMarines));       // no heavy class in this config
        Assert.Equal(new LoadoutChoice(3, -1, -1, -1, -1, -1, -1), ProfileWire.DefaultLoadout("assault", config, Team.Chaos));
        Assert.Equal(LoadoutChoice.Default, ProfileWire.DefaultLoadout("heavy", config, Team.Chaos));
    }

    [Fact]
    public void A_profiles_selection_picks_the_custom_class_or_the_defaults_loadout()
    {
        var config = Config();
        var profile = RichProfile("00112233445566778899aabbccddeeff") with { Selected = Selection.CustomSlot(7) };
        Assert.Equal(new LoadoutChoice(3, -1, 0, 1, -1, -1, -1), ProfileWire.ToLoadout(profile, config, Team.Chaos, Catalog));
        var defaults = profile with { Selected = Selection.DefaultFor("assault") };
        Assert.Equal(new LoadoutChoice(2, -1, -1, -1, -1, -1, -1), ProfileWire.ToLoadout(defaults, config, Team.SpaceMarines, Catalog));
        var empty = profile with { Selected = Selection.CustomSlot(9) };
        Assert.Equal(new LoadoutChoice(0, -1, -1, -1, -1, -1, -1), ProfileWire.ToLoadout(empty, config, Team.SpaceMarines, Catalog));   // first role: tactical
        Assert.Equal("mp_ranged_bolter", ProfileWire.Neutral("sm_mp_ranged_bolter"));
        Assert.Equal("mp_ranged_bolter", ProfileWire.Neutral("CSM_mp_ranged_bolter"));
        Assert.Equal("mp_perk_a", ProfileWire.Neutral("mp_perk_a"));
    }

    // ===== wire: appearance code =========================================================================================================

    [Fact]
    public void ToCode_writes_catalogue_indices()
    {
        var hash = EmblemHash.Of(EmblemBytes(1));
        var code = ProfileWire.ToCode(RichProfile(hash.ToHex()).Appearance("sm-1")!, Catalog);
        Assert.Equal((byte)1, code.Piece(ArmourSlot.Head));                            // sm_b_head is the second head
        Assert.Equal((byte)1, code.Piece(ArmourSlot.Torso));
        Assert.Equal((byte)255, code.Piece(ArmourSlot.Backpack));                      // not named: the default set's
        Assert.Equal(((byte)1, (byte)3, (byte)2), (code.Palette(ArmourSlot.Head, 0), code.Palette(ArmourSlot.Head, 1), code.Palette(ArmourSlot.Head, 2)));
        Assert.True(code.PatternOn(ArmourSlot.Head));
        Assert.Equal(((byte)2, (byte)255, (byte)255), (code.Palette(ArmourSlot.LeftArm, 0), code.Palette(ArmourSlot.LeftArm, 1), code.Palette(ArmourSlot.LeftArm, 2)));
        Assert.False(code.PatternOn(ArmourSlot.LeftArm));
        Assert.Equal(((byte)3, AppearanceCode.DecalEmblem, (byte)0), (code.LeftDecal, code.RightDecal, code.FallbackBadge));   // sm_foo_orig 3; sm_bar 0
        Assert.Equal(hash, code.Emblem);
        Assert.True(code.HasLens);
        Assert.Equal(((byte)10, (byte)200, (byte)30), code.Lens);
        Assert.False(code.HasPredefinedSet);
        Assert.True(code.IsWellFormed());
        Assert.True(code.IsWithin(ProfileWire.LimitsFor(Catalog.SpaceMarines, Catalog)));
    }

    [Fact]
    public void ToCode_and_FromCode_round_trip()
    {
        var hash = EmblemHash.Of(EmblemBytes(1));
        var profile = RichProfile(hash.ToHex());
        foreach (var look in profile.Appearances)
        {
            var code = ProfileWire.ToCode(look, Catalog);
            var back = ProfileWire.FromCode(code, look.Race, Catalog, look.Id, look.Name) with { PresetApplied = look.PresetApplied };   // the code does not carry the preset
            Assert.Equal(ProfileStore.Serialize(profile with { Appearances = [look] }), ProfileStore.Serialize(profile with { Appearances = [back] }));
            Assert.Equal(code, ProfileWire.ToCode(back, Catalog));
        }
        var chaos = ProfileWire.ToCode(profile.Appearance("csm-1")!, Catalog);
        Assert.Equal((AppearanceCode.DecalNone, (byte)0), (chaos.RightDecal, chaos.LeftDecal));      // csm_star is badge 0
        Assert.Equal(AppearanceCode.Default, ProfileWire.ToCode(AppearanceProfile.Empty("sm-9", "space_marine", "n"), Catalog));
        Assert.True(ProfileWire.FromCode(AppearanceCode.Default, "space_marine", Catalog).Pieces.Count == 0);
    }

    [Fact]
    public void Codes_from_ToCode_pass_the_servers_rules_unchanged_and_round_trip_through_FromCode()
    {
        // One source of truth for the code's indices: the profile side uses the server's lists (CustomizationCatalog.LimitsFor / CodeSets).
        var rules = new CatalogAppearanceRules(Catalog.LimitsFor(CustomizationCatalog.SpaceMarine), Catalog.LimitsFor(CustomizationCatalog.Chaos));
        foreach (var race in new[] { Catalog.SpaceMarines, Catalog.ChaosMarines })
        {
            var mine = ProfileWire.LimitsFor(race, Catalog);
            var server = Catalog.LimitsFor(race.Race);
            Assert.Equal((server.PredefinedSets, server.Palette, server.Badges), (mine.PredefinedSets, mine.Palette, mine.Badges));
            Assert.Equal(server.PiecesPerSlot, mine.PiecesPerSlot);
            Assert.Same(CustomizationCatalog.CodeSets(race), ProfileWire.PredefinedSetsOf(race));
        }
        var hash = EmblemHash.Of(EmblemBytes(1));
        var profile = RichProfile(hash.ToHex());
        var looks = profile.Appearances.Append(profile.Appearance("sm-1")! with { PredefinedSet = "black_templars" }).ToList();
        foreach (var look in looks)
        {
            var code = ProfileWire.ToCode(look, Catalog);
            Assert.Equal(code, rules.Apply(code, ProfileWire.SideOf(look.Race), 0));                   // nothing the server would change
            Assert.Equal(code, ProfileWire.ToCode(ProfileWire.FromCode(code, look.Race, Catalog), Catalog));
        }
        var set = ProfileWire.ToCode(looks[^1], Catalog);
        Assert.Equal("black_templars", CustomizationCatalog.CodeSets(Catalog.SpaceMarines)[set.PredefinedSet].Name);
    }

    [Fact]
    public void A_store_can_be_given_the_profile_file_itself()
    {
        string file = Path.Combine(_dir, "profiles", "mine.json");
        var store = ProfileStore.ForFile(file);
        store.Save(CustomizationProfile.Default(Catalog));
        Assert.True(File.Exists(file));
        Assert.Equal(Path.GetFullPath(file), store.ProfilePath);
        Assert.Equal(ProfileLoadStatus.Loaded, store.Load(Catalog).Status);
        var hash = store.SaveEmblem(EmblemBytes(3));
        Assert.True(File.Exists(Path.Combine(_dir, "profiles", ProfileStore.EmblemDirectory, hash.ToHex() + ProfileStore.EmblemExtension)));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_dir, "profiles")), f => f.Contains(".tmp-"));   // no temp file left
    }

    [Fact]
    public void The_servers_limits_only_catalogue_gives_the_full_catalogues_limits()
    {
        var full = Catalog;   // writes the install
        var limits = CustomizationCatalog.LoadLimits(_dir);
        foreach (var race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
        {
            var a = full.LimitsFor(race);
            var b = limits.LimitsFor(race);
            Assert.Equal((a.PredefinedSets, a.Palette, a.Badges), (b.PredefinedSets, b.Palette, b.Badges));
            Assert.Equal(a.PiecesPerSlot, b.PiecesPerSlot);
            Assert.Equal(CustomizationCatalog.CodeSets(full.Race(race)!).Select(x => x.Name), CustomizationCatalog.CodeSets(limits.Race(race)!).Select(x => x.Name));
        }
    }

    [Fact]
    public void ToCode_gives_defaults_for_names_outside_the_lists()
    {
        var odd = AppearanceProfile.Empty("sm-1", "space_marine", "n") with
        {
            Pieces = new Dictionary<string, string> { ["head"] = "csm_basic_head", ["torso"] = "nothing" },
            Paint = new Dictionary<string, SlotPaint> { ["head"] = new("dev_pink", "nope", null, true) },
            Decals = new Dictionary<string, DecalChoice> { ["leftshoulder"] = DecalChoice.ForBadge("csm_star"), ["rightshoulder"] = DecalChoice.ForEmblem("zz", "sm_bar") },
        };
        Assert.Equal(AppearanceCode.Default, ProfileWire.ToCode(odd, Catalog));        // nothing valid: the default look, pattern bit and all
        Assert.Equal(AppearanceCode.Default, ProfileWire.ToCode(odd with { Race = "orks" }, Catalog));
    }

    [Fact]
    public void A_predefined_set_locks_pieces_paint_and_decals_but_not_the_lens()
    {
        var look = RichProfile("00112233445566778899aabbccddeeff").Appearance("sm-1")! with { PredefinedSet = "black_templars" };
        var code = ProfileWire.ToCode(look, Catalog);
        Assert.True(code.HasPredefinedSet);
        Assert.Equal((byte)0, code.PredefinedSet);
        Assert.Equal(AppearanceCode.DefaultIndex, code.Piece(ArmourSlot.Head));
        Assert.Equal(AppearanceCode.DefaultIndex, code.Palette(ArmourSlot.Head, 0));
        Assert.Equal((AppearanceCode.DecalSchemeDefault, AppearanceCode.DecalSchemeDefault), (code.LeftDecal, code.RightDecal));
        Assert.True(code.HasLens);
        Assert.Equal(look.PredefinedSet, ProfileWire.FromCode(code, "space_marine", Catalog).PredefinedSet);
    }

    [Fact]
    public void A_second_emblem_shows_its_fallback_and_decal_kinds_map_to_values()
    {
        string a = EmblemHash.Of(EmblemBytes(1)).ToHex(), b = EmblemHash.Of(EmblemBytes(2)).ToHex();
        var look = AppearanceProfile.Empty("sm-1", "space_marine", "n") with
        {
            Decals = new Dictionary<string, DecalChoice> { ["leftshoulder"] = DecalChoice.ForEmblem(a, null), ["rightshoulder"] = DecalChoice.ForEmblem(b, "sm_foo_black") },
        };
        var code = ProfileWire.ToCode(look, Catalog);
        Assert.Equal((AppearanceCode.DecalEmblem, (byte)2), (code.LeftDecal, code.RightDecal));      // sm_foo_black is badge 2
        Assert.Equal(AppearanceCode.FallbackSchemeDefault, code.FallbackBadge);
        Assert.Equal(a, code.Emblem.ToHex());
    }

    [Fact]
    public void Applying_a_preset_copies_its_colours_badges_and_pattern_flags()
    {
        var look = RichProfile("00112233445566778899aabbccddeeff").Appearance("sm-1")! with { PredefinedSet = "black_templars" };
        var preset = Catalog.SpaceMarines.Presets.Single(p => p.Name == "sm_x");
        var applied = ProfileWire.ApplyPreset(look, preset, Catalog);
        Assert.Equal(("sm_x", null), (applied.PresetApplied, applied.PredefinedSet));
        Assert.Equal(new SlotPaint("gold", "red_1", "blue_0", true), applied.Paint["leftshoulder"]);   // the preset's own shoulder
        Assert.Equal(new SlotPaint("gold", "gold", "blue_0", false), applied.Paint["head"]);           // its default paint
        Assert.Equal(DecalChoice.ForBadge("sm_foo_black"), applied.Decals["leftshoulder"]);
        Assert.False(applied.Decals.ContainsKey("rightshoulder"));                                    // no decal named: the old one is removed
        Assert.Equal(look.Pieces, applied.Pieces);
        Assert.Equal(look.Lens, applied.Lens);
    }

    [Fact]
    public void The_selected_classes_look_goes_on_the_wire_per_race()
    {
        var profile = RichProfile("00112233445566778899aabbccddeeff");
        Assert.True(ProfileWire.SelectedAppearance(profile, "space_marine", Catalog).HasLens);
        Assert.Equal(ProfileWire.ToCode(profile.Appearance("csm-1")!, Catalog), ProfileWire.SelectedAppearance(profile, "chaos", Catalog));
        Assert.Equal(AppearanceCode.Default, ProfileWire.SelectedAppearance(profile with { Selected = Selection.DefaultFor("tactical") }, "chaos", Catalog));
        Assert.Equal(AppearanceCode.Default, ProfileWire.SelectedAppearance(profile with { Selected = Selection.CustomSlot(7) }, "chaos", Catalog));   // that class names no look
    }

    // ===== resolver ======================================================================================================================

    [Fact]
    public void The_default_look_is_the_classs_default_set_painted_with_the_default_scheme()
    {
        var look = AppearanceResolver.Resolve(AppearanceCode.Default, "sm - tactical", Catalog);
        Assert.Equal(CustomizationCatalog.ArmourSlots, look.Pieces.Select(p => p.Slot));
        Assert.Equal(SmA, look.Pieces.Select(p => p.Name));
        Assert.Equal(["pkg_a_torso", "pkg_a_belt"], look.Piece("torso")!.Packages);     // the belt rides on the torso
        Assert.Equal("helmet", look.HelmetOverride);
        Assert.Empty(look.HiddenSlots);
        Assert.Empty(look.ForcedSlots);
        Assert.Null(look.PredefinedSet);
        Assert.Null(look.Lens);

        // The head's scheme has the pattern off: secondary equals primary. The shoulders have it on: their secondary stays.
        var head = look.Paint["head"];
        Assert.Equal(("blue_1", "blue_1", "gold", false), (head.Primary!.Name, head.Secondary!.Name, head.Tertiary!.Name, head.PatternEnabled));
        var shoulder = look.Paint["leftshoulder"];
        Assert.Equal(("blue_1", "gold", "red_1", true), (shoulder.Primary!.Name, shoulder.Secondary!.Name, shoulder.Tertiary!.Name, shoulder.PatternEnabled));
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, head.Primary.Diffuse);
        Assert.Equal("blue_1", look.Paint["backpack"].Primary!.Name);                  // a slot the scheme does not list: its default paint
        Assert.Equal(9, look.Paint.Count);

        // Decals: the scheme's badge on the left pad; the right pad names the blank badge: nothing.
        Assert.Equal((DecalKind.Badge, "sm_foo_orig"), (look.LeftDecal.Kind, look.LeftDecal.Badge!.Name));
        Assert.Equal(ResolvedDecal.None, look.RightDecal);
        // The Chaos default: its set, its scheme (no decals).
        var chaos = AppearanceResolver.Resolve(AppearanceCode.Default, "csm - tactical", Catalog);
        Assert.Equal(CsmBasic, chaos.Pieces.Select(p => p.Name));
        Assert.Equal(("red_1", "red_1", "gold"), (chaos.Paint["head"].Primary!.Name, chaos.Paint["head"].Secondary!.Name, chaos.Paint["head"].Tertiary!.Name));
        Assert.Equal(ResolvedDecal.None, chaos.LeftDecal);
    }

    [Fact]
    public void Chosen_pieces_colours_and_the_pattern_flag_apply_to_their_slot()
    {
        var code = AppearanceCode.Default.WithPiece(ArmourSlot.Head, 1).WithPalette(ArmourSlot.Head, 2, 3, 0).WithPattern(ArmourSlot.Head, true)
            .WithPalette(ArmourSlot.Torso, 2, 3, 0)                                    // pattern off: secondary equals primary
            .WithPalette(ArmourSlot.LeftArm, 255, 0, 255).WithPattern(ArmourSlot.LeftArm, true);
        var look = AppearanceResolver.Resolve(code, "sm - tactical", Catalog);
        Assert.Equal("sm_b_head", look.Piece("head")!.Name);
        Assert.Equal("no_helmet", look.HelmetOverride);
        Assert.Equal(("gold", "red_1", "blue_0", true), (look.Paint["head"].Primary!.Name, look.Paint["head"].Secondary!.Name, look.Paint["head"].Tertiary!.Name, look.Paint["head"].PatternEnabled));
        Assert.Equal(("gold", "gold", "blue_0", false), (look.Paint["torso"].Primary!.Name, look.Paint["torso"].Secondary!.Name, look.Paint["torso"].Tertiary!.Name, look.Paint["torso"].PatternEnabled));
        // A partly set slot keeps the scheme's other colours (the scheme's arm: blue_1 / blue_1 / gold).
        Assert.Equal(("blue_1", "blue_0", "gold", true), (look.Paint["leftarm"].Primary!.Name, look.Paint["leftarm"].Secondary!.Name, look.Paint["leftarm"].Tertiary!.Name, look.Paint["leftarm"].PatternEnabled));
        // The same through a stored profile.
        var viaProfile = AppearanceResolver.Resolve(RichProfile("00112233445566778899aabbccddeeff").Appearance("sm-1")!, "sm - tactical", Catalog);
        Assert.Equal("sm_b_torso", viaProfile.Piece("torso")!.Name);
        Assert.Equal((byte)200, viaProfile.Lens!.Value.G);
    }

    [Fact]
    public void The_raptors_head_and_legs_are_forced_and_the_jump_pack_hides_the_backpack()
    {
        // The player chose a Chaos head; the Raptor wears the Raptor one anyway.
        var code = AppearanceCode.Default.WithPiece(ArmourSlot.Head, 0).WithPiece(ArmourSlot.Backpack, 0);
        var look = AppearanceResolver.Resolve(code, "csm - raptor", Catalog);
        Assert.Equal("csm_raptor_head", look.Piece("head")!.Name);
        Assert.True(look.Piece("head")!.Forced);
        Assert.Equal(["pkg_r_helmet"], look.Piece("head")!.Packages);
        Assert.Equal("csm_raptor_leg_l", look.Piece("leftleg")!.Name);
        Assert.Equal("csm_raptor_leg_r", look.Piece("rightleg")!.Name);
        Assert.Equal(new[] { "head", "leftleg", "rightleg" }.Order(), look.ForcedSlots.Order());
        Assert.Equal("helmet", look.HelmetOverride);                                   // the forced head's
        Assert.Equal(["backpack"], look.HiddenSlots);
        Assert.Null(look.Piece("backpack"));
        Assert.Equal("csm_basic_torso", look.Piece("torso")!.Name);                    // the rest is the player's look
        // The Space Marine Assault class hides its backpack too.
        Assert.Null(AppearanceResolver.Resolve(AppearanceCode.Default, "sm - assault marine", Catalog).Piece("backpack"));
        Assert.NotNull(AppearanceResolver.Resolve(AppearanceCode.Default, "sm - tactical", Catalog).Piece("backpack"));
    }

    [Fact]
    public void A_carried_perks_own_model_is_drawn_last_and_only_while_carried()
    {
        var with = AppearanceResolver.Resolve(AppearanceCode.Default, "sm - devastator", Catalog, new ResolveOptions { Carried = ["sm_mp_exotic_las_cannon", "mp_perk_a"] });
        var last = with.Pieces[^1];
        Assert.Equal(("perk", "mp_perk_a"), (last.Slot, last.Name));
        Assert.Equal(["sm_acc_perk_a"], last.Packages);
        var without = AppearanceResolver.Resolve(AppearanceCode.Default, "sm - devastator", Catalog, new ResolveOptions { Carried = ["sm_mp_exotic_las_cannon"] });
        Assert.DoesNotContain(without.Pieces, p => p.Slot == "perk");
    }

    [Fact]
    public void A_chaos_havocs_and_raptors_default_look_follows_the_data_not_the_class_name()
    {
        // Owner's decision 2026-10-28 (a change from M17, which picked the havoc_01 / raptor_01 sets by the class name): the default is the class's default_armor_set
        // (chaos_default: the basic pieces) plus the class's overrides, whatever the class is called.
        var havoc = AppearanceResolver.Resolve(AppearanceCode.Default, "csm - havoc", Catalog);
        Assert.Equal(CsmBasic.Take(8).Append("csm_backpack_havoc"), havoc.Pieces.Select(p => p.Name));   // the basic set, only the pack is the class's
        Assert.Equal(["backpack"], havoc.ForcedSlots);
        Assert.Null(havoc.PredefinedSet);
        var raptor = AppearanceResolver.Resolve(AppearanceCode.Default, "csm - raptor", Catalog);
        Assert.Equal(["csm_raptor_head", "csm_basic_torso", "csm_basic_shoulder_left", "csm_basic_shoulder_right", "csm_basic_arm_left", "csm_basic_arm_right", "csm_raptor_leg_l", "csm_raptor_leg_r"],
            raptor.Pieces.Select(p => p.Name));   // no backpack: the jump pack
        Assert.DoesNotContain(havoc.Pieces.Concat(raptor.Pieces), p => p.Name.Contains("havoc_01") || p.Name.Contains("raptor_01"));
    }

    [Fact]
    public void The_devastators_weapon_pack_needs_the_heavy_weapon_it_is_for()
    {
        var plain = AppearanceResolver.Resolve(AppearanceCode.Default, "sm - devastator", Catalog);
        Assert.Equal("sm_a_backpack", plain.Piece("backpack")!.Name);                  // carried unknown: only unconditional overrides apply
        var las = AppearanceResolver.Resolve(AppearanceCode.Default, "sm - devastator", Catalog, new ResolveOptions { Carried = ["sm_mp_exotic_las_cannon"] });
        Assert.Equal("sm_a_backpack", las.Piece("backpack")!.Name);
        var heavy = AppearanceResolver.Resolve(AppearanceCode.Default, "sm - devastator", Catalog, new ResolveOptions { Carried = ["csm_mp_exotic_heavy_bolter"] });
        Assert.Equal(("sm_backpack_heavy_bolter", true), (heavy.Piece("backpack")!.Name, heavy.Piece("backpack")!.Forced));   // side prefixes do not matter
        Assert.Equal(["pkg_hb_bp"], heavy.Piece("backpack")!.Packages);
        Assert.Equal(["backpack"], heavy.ForcedSlots);
        Assert.Empty(heavy.HiddenSlots);
    }

    [Fact]
    public void The_pattern_flag_switch_makes_a_scheme_slot_hide_or_show_its_secondary()
    {
        var code = AppearanceCode.Default.WithPredefinedSet(0);   // black_templars: the head's scheme slot has the pattern off and a secondary that differs from the primary
        bool saved = AppearanceResolver.SchemePatternFlagHidesSecondary;
        try
        {
            AppearanceResolver.SchemePatternFlagHidesSecondary = true;
            var hidden = AppearanceResolver.Resolve(code, "sm - tactical", Catalog).Paint["head"];
            Assert.False(hidden.PatternEnabled);
            Assert.Equal(hidden.Primary!.Name, hidden.Secondary!.Name);
            AppearanceResolver.SchemePatternFlagHidesSecondary = false;      // "keep gold": the flag is ignored
            var shown = AppearanceResolver.Resolve(code, "sm - tactical", Catalog).Paint["head"];
            Assert.True(shown.PatternEnabled);
            Assert.Equal(hidden.Primary.Name, shown.Primary!.Name);
            Assert.NotEqual(shown.Primary.Name, shown.Secondary!.Name);       // the scheme's own secondary shows
        }
        finally { AppearanceResolver.SchemePatternFlagHidesSecondary = saved; }
    }

    [Fact]
    public void A_predefined_set_fills_every_slot_with_its_scheme_and_ignores_the_rest()
    {
        var code = AppearanceCode.Default.WithPredefinedSet(0).WithPiece(ArmourSlot.Head, 1).WithPalette(ArmourSlot.Head, 2, 2, 2).WithDecals(2, 2).WithLens((1, 2, 3));
        var look = AppearanceResolver.Resolve(code, "sm - tactical", Catalog);
        Assert.Equal("black_templars", look.PredefinedSet);
        Assert.Equal("sm_bt_head", look.Piece("head")!.Name);                           // the set's pieces, not the chosen one
        Assert.Equal("sm_bt_torso", look.Piece("torso")!.Name);
        Assert.Equal(("red_1", "red_1", "blue_0", false), (look.Paint["head"].Primary!.Name, look.Paint["head"].Secondary!.Name, look.Paint["head"].Tertiary!.Name, look.Paint["head"].PatternEnabled));   // the scheme's pattern is off
        Assert.Equal("red_1", look.Paint["torso"].Primary!.Name);
        Assert.Equal("red_1", look.Paint["backpack"].Primary!.Name);
        Assert.Equal("sm_bar", look.LeftDecal.Badge!.Name);                              // the scheme's decal, not the chosen badge
        Assert.Equal(((byte)1, (byte)2, (byte)3), look.Lens!.Value);                     // the lens is the one thing a set leaves free
        // The set's own pieces (ArmourSet.Pieces) resolve without a lookup.
        Assert.Equal(["pkg_bt_helmet"], look.Piece("head")!.Packages);
        Assert.DoesNotContain(look.Notes, n => n.Contains("sm_bt_head"));
        Assert.Equal(["sm_bt_head", "sm_bt_shoulder_left", "sm_bt_shoulder_right", "sm_bt_torso"], Catalog.SpaceMarines.Set("black_templars")!.Pieces.Values.Select(p => p.Name).Order());
        // A set that lacks a slot leaves it without a piece.
        Assert.Null(look.Piece("leftarm"));
    }

    [Fact]
    public void Pads_that_cannot_take_a_decal_get_none_and_blank_badges_are_none()
    {
        var code = AppearanceCode.Default.WithDecals(3, 2);                            // sm_foo_orig, sm_foo_black
        var both = AppearanceResolver.Resolve(code, "sm - tactical", Catalog);
        Assert.Equal(("sm_foo_orig", "sm_foo_black"), (both.LeftDecal.Badge!.Name, both.RightDecal.Badge!.Name));
        var options = new ResolveOptions { PadTakesDecal = piece => piece.Slot != "rightshoulder" };
        var one = AppearanceResolver.Resolve(code, "sm - tactical", Catalog, options);
        Assert.Equal("sm_foo_orig", one.LeftDecal.Badge!.Name);
        Assert.Equal(ResolvedDecal.None, one.RightDecal);                              // its pad has no decal UVs
        Assert.Equal(ResolvedDecal.None, AppearanceResolver.Resolve(AppearanceCode.Default.WithDecals(1, 255), "sm - tactical", Catalog).LeftDecal);   // the blank badge
        Assert.Equal(ResolvedDecal.None, AppearanceResolver.Resolve(AppearanceCode.Default.WithDecals(255, 255), "sm - tactical", Catalog).LeftDecal);   // none
        // A predefined set's own scheme names no decal for the right pad.
        Assert.Equal(ResolvedDecal.None, AppearanceResolver.Resolve(AppearanceCode.Default.WithPredefinedSet(0), "sm - tactical", Catalog).RightDecal);
    }

    [Fact]
    public void An_emblem_shows_its_fallback_badge_until_it_arrives()
    {
        var hash = EmblemHash.Of(EmblemBytes(4));
        var withFallback = AppearanceCode.Default.WithDecals(AppearanceCode.DecalEmblem, 255, 2).WithEmblem(hash);       // fallback sm_foo_black
        var look = AppearanceResolver.Resolve(withFallback, "sm - tactical", Catalog);
        Assert.Equal((DecalKind.Emblem, hash, "sm_foo_black"), (look.LeftDecal.Kind, look.LeftDecal.Emblem, look.LeftDecal.Badge!.Name));
        // Fallback 255: the scheme's own decal for the pad.
        var schemeFallback = AppearanceResolver.Resolve(AppearanceCode.Default.WithDecals(AppearanceCode.DecalEmblem, 255).WithEmblem(hash), "sm - tactical", Catalog);
        Assert.Equal((DecalKind.Emblem, "sm_foo_orig"), (schemeFallback.LeftDecal.Kind, schemeFallback.LeftDecal.Badge!.Name));
        // No fallback anywhere (the scheme's decal for that pad is the blank one): the pad is empty until the emblem is there.
        var empty = AppearanceResolver.Resolve(AppearanceCode.Default.WithDecals(255, AppearanceCode.DecalEmblem).WithEmblem(hash), "sm - tactical", Catalog);
        Assert.Equal((DecalKind.Emblem, (string?)null), (empty.RightDecal.Kind, empty.RightDecal.Badge?.Name));
        // A pad that cannot take decals shows no emblem either.
        Assert.Equal(ResolvedDecal.None, AppearanceResolver.Resolve(withFallback, "sm - tactical", Catalog, new ResolveOptions { PadTakesDecal = _ => false }).LeftDecal);
    }

    [Fact]
    public void A_look_of_the_other_race_or_an_unknown_class_is_handled()
    {
        var chaosLook = AppearanceProfile.Empty("csm-1", "chaos", "n") with { Paint = new Dictionary<string, SlotPaint> { ["head"] = new("gold", null, null, false) } };
        var onMarine = AppearanceResolver.Resolve(chaosLook, "sm - tactical", Catalog);
        Assert.Equal(SmA, onMarine.Pieces.Select(p => p.Name));                         // resolves as the default look, not as indices of the wrong race
        Assert.Equal("blue_1", onMarine.Paint["head"].Primary!.Name);
        Assert.Equal("gold", AppearanceResolver.Resolve(chaosLook, "csm - tactical", Catalog).Paint["head"].Primary!.Name);
        Assert.Throws<ArgumentException>(() => AppearanceResolver.Resolve(AppearanceCode.Default, "sm - nobody", Catalog));
        // Out-of-range indices in a received code read as defaults instead of throwing.
        var wild = AppearanceCode.FromBytes(Enumerable.Repeat((byte)100, AppearanceCode.Size).ToArray());
        var look = AppearanceResolver.Resolve(wild, "sm - tactical", Catalog);
        Assert.Equal(SmA, look.Pieces.Select(p => p.Name));
    }
}
