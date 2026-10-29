using SmContent;
using SmFormats.Baf;
using SmFormats.Obpk;
using Xunit;

namespace SmFormats.Tests;

/// <summary>Paint schemes and colours: parsed from synthetic attribute files shaped like the install's (no original data).</summary>
public class PaintSchemesTests
{
    static (string, object)[] ColourTable(float r, float g, float b, bool visible = true) =>
    [
        ("diffuse", new (string, object)[] { ("red", r), ("green", g), ("blue", b), ("alpha", 0f) }),
        ("specular", new (string, object)[] { ("red", r * 2), ("green", g * 2), ("blue", b * 2), ("alpha", 0f) }),
        ("visible_in_customizer", visible),
    ];

    static (string, object)[] PaintTable(string primary, string secondary, string tertiary, bool pattern = false, string? decal = null) =>
    [
        ("primary_colour", @"custom_paint\custom_paint_colour\citadel_paints\" + primary),
        ("secondary_colour", @"custom_paint\custom_paint_colour\citadel_paints\" + secondary),
        ("tertiary_colour", @"custom_paint\custom_paint_colour\citadel_paints\" + tertiary),
        ("decals", decal is null ? Array.Empty<(string, object)>() : new (string, object)[] { ("decal", @"custom_paint\custom_paint_decal\mp\" + decal) }),
        ("pattern_enabled", pattern),
    ];

    static IReadOnlyDictionary<string, PaintColour> Colours() => new Dictionary<string, PaintColour>
    {
        ["blue"] = new("blue", [0.1f, 0.2f, 0.4f], [0.4f, 0.5f, 0.6f], true),
        ["gold"] = new("gold", [0.6f, 0.4f, 0.2f], [0.9f, 0.8f, 0.5f], true),
        ["red"] = new("red", [0.3f, 0.1f, 0.0f], [0.5f, 0.4f, 0.4f], true),
    };

    static BafDocument Scheme() => BafDocument.Read(SyntheticBafWriter.Build(
        ("name", 1), ("race", "space_marine"),
        ("paints", new (string, object)[]
        {
            ("head", PaintTable("blue", "blue", "gold")),
            ("leftshoulder", PaintTable("blue", "gold", "gold", true, "sm_x_orig")),
            ("torso", Array.Empty<(string, object)>()),
            ("melee", Array.Empty<(string, object)>()),
        }),
        ("default_paint", PaintTable("red", "red", "gold"))));

    [Fact]
    public void A_colour_reads_diffuse_specular_and_visibility()
    {
        var c = PaintSchemes.ParseColour("blue", BafDocument.Read(SyntheticBafWriter.Build(ColourTable(0.166f, 0.321f, 0.451f))))!;
        Assert.Equal([0.166f, 0.321f, 0.451f], c.Diffuse);
        Assert.Equal([0.332f, 0.642f, 0.902f], c.Specular);
        Assert.True(c.VisibleInCustomizer);
        Assert.False(PaintSchemes.ParseColour("hidden", BafDocument.Read(SyntheticBafWriter.Build(ColourTable(0, 0, 0, visible: false))))!.VisibleInCustomizer);
    }

    [Fact]
    public void A_colour_without_a_diffuse_table_is_not_a_colour() =>
        Assert.Null(PaintSchemes.ParseColour("x", BafDocument.Read(SyntheticBafWriter.Build(("visible_in_customizer", true)))));

    [Fact]
    public void A_scheme_names_colours_by_the_last_path_segment_and_falls_back_to_default_paint_for_empty_slots()
    {
        var notes = new List<string>();
        var scheme = PaintSchemes.ParseScheme("sm_test", Scheme(), Colours(), notes)!;
        Assert.Empty(notes);
        Assert.Equal("space_marine", scheme.Race);
        Assert.Equal(["head", "leftshoulder"], scheme.Slots.Keys.Order());   // torso is empty: no slot entry
        var shoulder = scheme.For("leftshoulder")!;
        Assert.Equal(("blue", "gold", "gold", true, "sm_x_orig"), (shoulder.Primary.Name, shoulder.Secondary.Name, shoulder.Tertiary.Name, shoulder.PatternEnabled, shoulder.Decal));
        Assert.Equal("red", scheme.For("torso")!.Primary.Name);          // default_paint
        Assert.Equal("red", scheme.For("backpack")!.Primary.Name);       // a slot the scheme does not list
        Assert.Null(scheme.For("head")!.Decal);
    }

    [Fact]
    public void An_unknown_colour_is_noted_and_the_set_takes_the_primary_for_it()
    {
        var notes = new List<string>();
        var colours = Colours().Where(c => c.Key != "gold").ToDictionary(c => c.Key, c => c.Value);
        var scheme = PaintSchemes.ParseScheme("sm_test", Scheme(), colours, notes)!;
        Assert.Contains(notes, n => n.Contains("'gold'"));
        Assert.Equal("blue", scheme.For("head")!.Primary.Name);
        Assert.Equal("blue", scheme.For("head")!.Tertiary.Name);   // a missing secondary/tertiary takes the primary
    }

    [Fact]
    public void A_scheme_with_no_paint_at_all_is_not_a_scheme() =>
        Assert.Null(PaintSchemes.ParseScheme("empty", BafDocument.Read(SyntheticBafWriter.Build(("race", "chaos"))), Colours()));

    [Fact]
    public void The_default_scheme_comes_from_the_player_blueprint()
    {
        static BafDocument Blueprint(string path) => BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[]
        {
            ("wargear_ext", new (string, object)[] { ("default_custom_paint", path) }),
        })));
        Assert.Equal("sm_ultramarines", PaintSchemes.DefaultSchemeName(Blueprint(@"custom_paint\custom_paint_scheme\sm_ultramarines")));
        Assert.Null(PaintSchemes.DefaultSchemeName(Blueprint("")));
    }

    [Theory]
    [InlineData("sm_armour_mp_basic_helmet", "head")]
    [InlineData("csm_armour_mp_havoc_01_shldrl", "leftshoulder")]
    [InlineData("csm_armour_mp_havoc_01_shldrr", "rightshoulder")]
    [InlineData("sm_armour_mp_basic_arml", "leftarm")]
    [InlineData("sm_armour_mp_basic_armr", "rightarm")]
    [InlineData("sm_armour_mp_basic_torso", "torso")]
    [InlineData("sm_armour_mp_basic_legl", "leftleg")]
    [InlineData("sm_armour_mp_basic_legr", "rightleg")]
    [InlineData("sm_armour_mp_basic_backpack", "backpack")]
    [InlineData("csm_armour_mp_berserk_01_bp", "backpack")]
    [InlineData("sm_lascannon_mp_bp_001", "backpack")]
    [InlineData("sm_jumppack_mp_basic_001", "backpack")]
    [InlineData("csm_jumpack_mp_basic", "backpack")]
    [InlineData("sm_armour_mp_veteran_belt", null)]
    public void A_piece_maps_to_its_paint_slot_by_name(string piece, string? slot) => Assert.Equal(slot, PaintSchemes.SlotOf(piece));

    [Fact]
    public void A_slotless_piece_takes_the_default_paint()
    {
        var scheme = PaintSchemes.ParseScheme("sm_test", Scheme(), Colours())!;
        Assert.Equal("red", PaintSchemes.For(scheme, "sm_armour_mp_veteran_belt")!.Primary.Name);
        Assert.Equal("gold", PaintSchemes.For(scheme, "sm_armour_mp_basic_shldrl")!.Secondary.Name);
    }

    [Fact]
    public void The_marine_uses_the_paintable_multiplayer_armour_only_when_the_install_has_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-paint-" + Guid.NewGuid().ToString("N"));
        string packages = Path.Combine(dir, "data", "packages");
        Directory.CreateDirectory(packages);
        try
        {
            File.WriteAllBytes(Path.Combine(packages, "sm_armour_mp_basic_torso.oppc"), []);
            File.WriteAllBytes(Path.Combine(packages, "sm_armour_basic_001_torso.oppc"), []);
            Assert.Equal(["sm_armour_mp_basic_torso.oppc"], CharacterModel.MarineMpArmourFiles(dir).Select(Path.GetFileName));
            Assert.Empty(CharacterModel.MarineMpArmourFiles(Path.Combine(dir, "nowhere")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(@"custom_paint\custom_paint_colour\citadel_paints\burnished_gold", "burnished_gold")]
    [InlineData("custom_paint/x/y", "y")]
    [InlineData("plain", "plain")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Leaf_is_the_last_path_segment(string? path, string? leaf) => Assert.Equal(leaf, PaintSchemes.Leaf(path));
}

/// <summary>
/// <see cref="PaintSchemes.Load"/> on synthetic installs: bad entries and bad or missing packages leave the rest and the built-in defaults, and the
/// Space Marine armour set choice. Synthetic archives only (<see cref="SyntheticObpkWriter"/>).
/// </summary>
public class PaintSchemesLoadTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-paint-load-" + Guid.NewGuid().ToString("N"));
    string Packages => Path.Combine(_dir, "data", "packages");

    public PaintSchemesLoadTests() => Directory.CreateDirectory(Packages);
    public void Dispose() => Directory.Delete(_dir, true);

    static SyntheticObpkWriter.File Entry(string name, byte[] data) => new("attr_pc", name, data);

    static byte[] ColourBaf(float r, float g, float b, bool visible) => SyntheticBafWriter.Build(
        ("diffuse", new (string, object)[] { ("red", r), ("green", g), ("blue", b), ("alpha", 0f) }),
        ("specular", new (string, object)[] { ("red", 0.5f), ("green", 0.5f), ("blue", 0.5f), ("alpha", 0f) }),
        ("visible_in_customizer", visible));

    static byte[] SchemeBaf(string colour) => SyntheticBafWriter.Build(("race", "space_marine"),
        ("default_paint", new (string, object)[]
        {
            ("primary_colour", @"custom_paint\custom_paint_colour\citadel_paints\" + colour),
            ("secondary_colour", @"custom_paint\custom_paint_colour\citadel_paints\" + colour),
            ("tertiary_colour", @"custom_paint\custom_paint_colour\citadel_paints\" + colour),
        }));

    static byte[] Blueprint(string scheme) => SyntheticBafWriter.Build(("extensions", new (string, object)[]
    {
        ("wargear_ext", new (string, object)[] { ("default_custom_paint", @"custom_paint\custom_paint_scheme\" + scheme) }),
    }));

    static readonly byte[] CorruptBaf = [.. "BAF "u8, 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3];
    static readonly byte[] NotBaf = [1, 2, 3, 4, 5, 6, 7, 8];

    void Write(string package, params SyntheticObpkWriter.File[] files) =>
        File.WriteAllBytes(Path.Combine(Packages, package + ".oppc"), SyntheticObpkWriter.Build(files));

    [Fact]
    public void Missing_packages_leave_the_built_in_defaults_and_note_what_is_missing()
    {
        var notes = new List<string>();
        var schemes = PaintSchemes.Load(_dir, notes);
        Assert.Equal((PaintSchemes.FallbackMarineScheme, PaintSchemes.FallbackChaosScheme), (schemes.MarineDefault, schemes.ChaosDefault));
        Assert.Equal(("sm_ultramarines", "chaos_multiplayer"), (schemes.MarineDefault, schemes.ChaosDefault));
        Assert.Empty(schemes.Names);
        Assert.Contains(notes, n => n.Contains("custom_paint_colour"));
        Assert.Contains(notes, n => n.Contains("custom_paint_scheme"));
    }

    [Fact]
    public void An_unreadable_package_keeps_what_the_others_gave_and_the_defaults()
    {
        File.WriteAllBytes(Path.Combine(Packages, "ebps.oppc"), [1, 2, 3, 4]);   // not an archive
        Write("custom_paint_colour", Entry("blue", ColourBaf(0.1f, 0.2f, 0.4f, true)));
        Write("custom_paint_scheme", Entry("sm_a", SchemeBaf("blue")));
        var notes = new List<string>();
        var schemes = PaintSchemes.Load(_dir, notes);
        Assert.Equal(["sm_a"], schemes.Names);
        Assert.Equal(PaintSchemes.FallbackMarineScheme, schemes.MarineDefault);
        Assert.Contains(notes, n => n.Contains("ebps"));
    }

    [Fact]
    public void A_bad_entry_is_skipped_and_the_other_entries_are_kept()
    {
        Write("custom_paint_colour", Entry("junk", NotBaf), Entry("corrupt", CorruptBaf), Entry("blue", ColourBaf(0.1f, 0.2f, 0.4f, true)));
        Write("custom_paint_scheme", Entry("junk", NotBaf), Entry("corrupt", CorruptBaf), Entry("sm_a", SchemeBaf("blue")), Entry("sm_b", SchemeBaf("missing")));
        var notes = new List<string>();
        var schemes = PaintSchemes.Load(_dir, notes);
        Assert.Equal(["sm_a"], schemes.Names);   // sm_b names an unknown colour: it has no usable paint, so it is no scheme (noted)
        Assert.Equal("blue", schemes.Scheme("sm_a")!.For("torso")!.Primary.Name);
        Assert.Contains(notes, n => n.Contains("'missing'"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_customizer_visible_duplicate_colour_wins_in_either_order(bool visibleFirst)
    {
        var visible = Entry("ultramarines_blue", ColourBaf(0.5f, 0.5f, 0.5f, true));
        var hidden = Entry("ultramarines_blue", ColourBaf(0.1f, 0.1f, 0.1f, false));
        Write("custom_paint_colour", visibleFirst ? [visible, hidden] : [hidden, visible]);
        Write("custom_paint_scheme", Entry("sm_a", SchemeBaf("ultramarines_blue")));
        var colour = PaintSchemes.Load(_dir).Scheme("sm_a")!.For("head")!.Primary;
        Assert.Equal(0.5f, colour.Diffuse[0]);
        Assert.True(colour.VisibleInCustomizer);
    }

    [Fact]
    public void The_player_blueprints_name_each_sides_default_scheme()
    {
        Write("ebps", Entry("sm_space_marine_player", Blueprint("sm_other")), Entry("csm_chaos_marine_player", Blueprint("csm_legion")), Entry("boy", Blueprint("ignored")));
        var schemes = PaintSchemes.Load(_dir);
        Assert.Equal(("sm_other", "csm_legion"), (schemes.MarineDefault, schemes.ChaosDefault));
    }

    [Fact]
    public void A_corrupt_player_blueprint_keeps_that_sides_built_in_default()
    {
        Write("ebps", Entry("sm_space_marine_player", CorruptBaf), Entry("csm_chaos_marine_player", Blueprint("csm_legion")));
        var notes = new List<string>();
        var schemes = PaintSchemes.Load(_dir, notes);
        Assert.Equal((PaintSchemes.FallbackMarineScheme, "csm_legion"), (schemes.MarineDefault, schemes.ChaosDefault));
        Assert.Contains(notes, n => n.Contains("sm_space_marine_player"));
    }

    [Fact]
    public void The_marine_armour_is_the_paintable_set_only_when_all_nine_parts_exist()
    {
        foreach (var part in CharacterModel.MarineBasicArmour.Take(8)) File.WriteAllBytes(Path.Combine(Packages, $"sm_armour_mp_basic_{part}.oppc"), []);
        var files = CharacterModel.MarineArmourFiles(_dir, legacyArmour: false, out bool paintable);
        Assert.False(paintable);   // 8 of 9
        Assert.All(files, f => Assert.StartsWith("sm_armour_basic_001_", Path.GetFileName(f)));
        Assert.Equal(9, files.Count);

        File.WriteAllBytes(Path.Combine(Packages, $"sm_armour_mp_basic_{CharacterModel.MarineBasicArmour[8]}.oppc"), []);
        files = CharacterModel.MarineArmourFiles(_dir, legacyArmour: false, out paintable);
        Assert.True(paintable);
        Assert.All(files, f => Assert.StartsWith("sm_armour_mp_basic_", Path.GetFileName(f)));
        Assert.Equal(9, files.Count);

        files = CharacterModel.MarineArmourFiles(_dir, legacyArmour: true, out paintable);   // forced
        Assert.False(paintable);
        Assert.All(files, f => Assert.StartsWith("sm_armour_basic_001_", Path.GetFileName(f)));
    }

    // ---------------------------------------------------------------- decals (M30)

    static byte[] DecalBaf(string texture, string? package = null, string race = "space_marine") => SyntheticBafWriter.Build(
        ("name", 1), ("decal", texture), ("decal_package", package ?? texture), ("visible_in_customizer", true), ("race", race));

    static byte[] DecalSchemeBaf(string leftDecal) => SyntheticBafWriter.Build(("race", "space_marine"),
        ("paints", new (string, object)[]
        {
            ("leftshoulder", new (string, object)[]
            {
                ("primary_colour", @"custom_paint\custom_paint_colour\citadel_paints\blue"),
                ("decals", new (string, object)[] { ("decal", @"custom_paint\custom_paint_decal\multiplayer\" + leftDecal) }),
            }),
            ("rightshoulder", new (string, object)[] { ("primary_colour", @"custom_paint\custom_paint_colour\citadel_paints\blue"), ("decals", Array.Empty<(string, object)>()) }),
        }));

    [Fact]
    public void A_decal_entry_names_its_texture_and_package_and_a_bad_one_is_skipped()
    {
        Write("custom_paint_colour", Entry("blue", ColourBaf(0.1f, 0.2f, 0.4f, true)));
        Write("custom_paint_decal", Entry("sm_x_orig", DecalBaf("sm_x_dif")), Entry("csm_y", DecalBaf("csm_y", "csm_y_pkg", "chaos")), Entry("junk", NotBaf), Entry("corrupt", CorruptBaf),
            Entry("no_texture", SyntheticBafWriter.Build(("race", "chaos"))));
        Write("custom_paint_scheme", Entry("sm_a", DecalSchemeBaf("sm_x_orig")));
        var schemes = PaintSchemes.Load(_dir);
        Assert.Equal(2, schemes.DecalCount);
        Assert.Equal(new PaintDecal("sm_x_orig", "sm_x_dif", "sm_x_dif", "space_marine") { NameId = 1 }, schemes.Decal("sm_x_orig"));
        Assert.Equal(new PaintDecal("csm_y", "csm_y", "csm_y_pkg", "chaos") { NameId = 1 }, schemes.Decal("csm_y"));
        Assert.Null(schemes.Decal("missing"));
        Assert.Null(schemes.Decal(null));
    }

    [Fact]
    public void A_scheme_gives_the_decal_of_the_shoulder_slot_only()
    {
        Write("custom_paint_colour", Entry("blue", ColourBaf(0.1f, 0.2f, 0.4f, true)));
        Write("custom_paint_decal", Entry("sm_x_orig", DecalBaf("sm_x_dif")));
        Write("custom_paint_scheme", Entry("sm_a", DecalSchemeBaf("sm_x_orig")), Entry("sm_b", DecalSchemeBaf("not_a_decal")));
        var schemes = PaintSchemes.Load(_dir);
        var a = schemes.Scheme("sm_a")!;
        Assert.Equal("sm_x_dif", schemes.DecalFor(a, "sm_armour_mp_basic_shldrl")!.Texture);
        Assert.Null(schemes.DecalFor(a, "sm_armour_mp_basic_shldrr"));   // that slot names none
        Assert.Null(schemes.DecalFor(a, "sm_armour_mp_basic_torso"));    // slot not in the scheme, no default_paint
        Assert.Null(schemes.DecalFor(schemes.Scheme("sm_b")!, "sm_armour_mp_basic_shldrl"));   // names a decal the install lacks
        Assert.Equal("sm_x_orig", a.For("leftshoulder")!.Decal);
    }

    [Fact]
    public void A_missing_decal_package_leaves_the_rest_and_is_noted()
    {
        Write("custom_paint_colour", Entry("blue", ColourBaf(0.1f, 0.2f, 0.4f, true)));
        Write("custom_paint_scheme", Entry("sm_a", DecalSchemeBaf("sm_x_orig")));
        var notes = new List<string>();
        var schemes = PaintSchemes.Load(_dir, notes);
        Assert.Equal(0, schemes.DecalCount);
        Assert.Contains(notes, n => n.Contains("custom_paint_decal"));
        Assert.Equal("sm_x_orig", schemes.Scheme("sm_a")!.For("leftshoulder")!.Decal);   // the scheme itself is intact
    }

    [Fact]
    public void A_decal_texture_is_read_from_its_package_and_is_null_when_the_package_or_entry_is_absent()
    {
        byte[] dds = [0x44, 0x44, 0x53, 0x20, 1, 2, 3];
        File.WriteAllBytes(Path.Combine(Packages, "sm_x_dif.oppc"), SyntheticObpkWriter.Build([new("dds", "sm_x_dif", dds), new("dds", "other", [9])]));
        Assert.Equal(dds, PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("sm_x_orig", "sm_x_dif", "sm_x_dif", null)));
        Assert.Null(PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("a", "sm_missing_dif", "sm_x_dif", null)));   // not in the package
        Assert.Null(PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("blank", "blank", "blank", null)));          // no such package
        File.WriteAllBytes(Path.Combine(Packages, "broken.oppc"), [1, 2, 3]);
        Assert.Null(PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("b", "x", "broken", null)));                  // not an archive
    }

    [Fact]
    public void The_uv_check_refuses_degenerate_thin_and_tiled_sets_and_leaves_the_slot_as_the_gate()
    {
        static System.Numerics.Vector2[] Uv(params (float, float)[] p) => p.Select(v => new System.Numerics.Vector2(v.Item1, v.Item2)).ToArray();
        Assert.True(PaintSchemes.HasDecalUv(Uv((-0.08f, 0.007f), (1.08f, 1.0f), (0.5f, 0.5f))));   // the Space Marine shoulder pad
        Assert.True(PaintSchemes.HasDecalUv(Uv((0f, 0.002f), (1.001f, 1.018f))));                   // the basic Chaos left pad
        Assert.False(PaintSchemes.HasDecalUv(Uv((0f, 0f), (0f, 0f), (0f, 0f))));                    // Havok, Raptor, Chaos right pad: all zero
        Assert.False(PaintSchemes.HasDecalUv(Uv((0f, 0f), (1.25f, 0.3f))));                          // thin
        Assert.False(PaintSchemes.HasDecalUv(Uv((0f, 0f), (4f, 4f))));                               // tiled: a detail layout
        Assert.False(PaintSchemes.HasDecalUv([]));
        // Not a gate against other layouts of that size: the Space Marine torso and helmet sets (up to 1.25) pass. The shoulder slot is the gate.
        Assert.True(PaintSchemes.HasDecalUv(Uv((0f, 0f), (1.25f, 0.834f))));
        Assert.True(PaintSchemes.HasDecalUv(Uv((0.011f, 0.441f), (1.25f, 1f))));
    }

    [Fact]
    public void A_scheme_without_a_slot_decal_gives_its_default_paint_decal()
    {
        Write("custom_paint_colour", Entry("blue", ColourBaf(0.1f, 0.2f, 0.4f, true)));
        Write("custom_paint_decal", Entry("sm_x_orig", DecalBaf("sm_x_dif")));
        string colour = @"custom_paint\custom_paint_colour\citadel_paints\blue";
        Write("custom_paint_scheme", Entry("sm_a", SyntheticBafWriter.Build(("race", "space_marine"),
            ("paints", new (string, object)[] { ("leftshoulder", new (string, object)[] { ("primary_colour", colour), ("decals", Array.Empty<(string, object)>()) }) }),
            ("default_paint", new (string, object)[] { ("primary_colour", colour), ("decals", new (string, object)[] { ("decal", @"custom_paint\custom_paint_decal\sm_x_orig") }) }))));
        var schemes = PaintSchemes.Load(_dir);
        var scheme = schemes.Scheme("sm_a")!;
        Assert.Equal("sm_x_dif", schemes.DecalFor(scheme, "sm_armour_mp_basic_torso")!.Texture);     // slot not listed: default_paint
        Assert.Equal("sm_x_dif", schemes.DecalFor(scheme, "sm_armour_mp_veteran_belt")!.Texture);   // no slot at all: default_paint
        Assert.Null(schemes.DecalFor(scheme, "sm_armour_mp_basic_shldrl"));                            // its own slot names none and wins
    }

    [Fact]
    public void A_blank_decal_is_recognised_and_has_no_texture()
    {
        Assert.True(new PaintDecal("a", "blank", "blank", null).IsBlank);
        Assert.True(new PaintDecal("a", "x", "BLANK", null).IsBlank);
        Assert.False(new PaintDecal("a", "sm_x_dif", "sm_x_dif", null).IsBlank);
        File.WriteAllBytes(Path.Combine(Packages, "blank.oppc"), SyntheticObpkWriter.Build([new("dds", "blank", [1, 2, 3])]));
        Assert.Null(PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("a", "blank", "blank", null)));   // even if such a package existed
    }

    [Theory]
    [InlineData(@"..\evil")]
    [InlineData("../evil")]
    [InlineData("sub/x")]
    [InlineData(@"sub\x")]
    [InlineData("C:evil")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("")]
    public void A_package_name_that_leaves_the_packages_folder_is_refused(string package)
    {
        byte[] dds = [1, 2, 3];
        File.WriteAllBytes(Path.Combine(_dir, "data", "evil.oppc"), SyntheticObpkWriter.Build([new("dds", "x", dds)]));
        Directory.CreateDirectory(Path.Combine(Packages, "sub"));
        File.WriteAllBytes(Path.Combine(Packages, "sub", "x.oppc"), SyntheticObpkWriter.Build([new("dds", "x", dds)]));
        File.WriteAllBytes(Path.Combine(Packages, "good.oppc"), SyntheticObpkWriter.Build([new("dds", "x", dds)]));
        Assert.Null(PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("a", "x", package, null)));
        Assert.Equal(dds, PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("a", "x", "good", null)));   // the same texture is read from a plain name
    }

    [Fact]
    public void A_decal_whose_texture_is_not_in_its_package_has_no_texture()
    {
        Write("custom_paint_decal", Entry("sm_x_orig", DecalBaf("sm_x_dif")));
        File.WriteAllBytes(Path.Combine(Packages, "sm_x_dif.oppc"), SyntheticObpkWriter.Build([new("dds", "something_else", [1, 2, 3])]));
        var schemes = PaintSchemes.Load(_dir);
        Assert.NotNull(schemes.Decal("sm_x_orig"));
        Assert.Null(PaintSchemes.ReadDecalTexture(_dir, schemes.Decal("sm_x_orig")!));
    }

    [Fact]
    public void A_package_with_a_corrupt_data_block_gives_no_texture_instead_of_throwing()
    {
        byte[] dds = Enumerable.Range(0, 4000).Select(i => (byte)(i * 31 % 251)).ToArray();   // enough data that the compressed block is not tiny
        byte[] archive = SyntheticObpkWriter.Build([new("dds", "sm_x_dif", dds)]);
        var pristine = Path.Combine(Packages, "pristine.oppc");
        File.WriteAllBytes(pristine, archive);
        Assert.Equal(dds, PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("a", "sm_x_dif", "pristine", null)));
        // Corrupt the compressed block (the end of the file), and separately truncate the file.
        byte[] corrupt = (byte[])archive.Clone();
        for (int i = corrupt.Length - 40; i < corrupt.Length; i++) corrupt[i] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(Packages, "corrupt.oppc"), corrupt);
        File.WriteAllBytes(Path.Combine(Packages, "short.oppc"), archive[..(archive.Length - 60)]);
        Assert.Null(PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("a", "sm_x_dif", "corrupt", null)));
        Assert.Null(PaintSchemes.ReadDecalTexture(_dir, new PaintDecal("a", "sm_x_dif", "short", null)));
    }
}
