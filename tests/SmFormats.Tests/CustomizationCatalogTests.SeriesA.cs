using SmContent;
using SmContent.Customization;
using SmFormats.Bod;
using Xunit;

namespace SmFormats.Tests;

/// <summary>armour-pieces-1125 Series A: the further pieces (owner's pick of 2026-11-25) on the synthetic install; no original data.</summary>
public partial class CustomizationCatalogTests
{
    /// <summary>The install plus Series A's wargear files (MP extras and the Librarian's) and, when asked, empty package files for the campaign armour.</summary>
    CustomizationCatalog LoadSeriesA(bool campaignPackages = true, List<string>? notes = null)
    {
        WriteInstall(true, true,
            Entry("sm_dlc_bloodravens_arm_left", Gear("leftarm", 1, 0, packages: ["sm_armour_mp_bloodravens_arml"], visible: false)),
            Entry("sm_dlc_bloodravens_arm_right", Gear("rightarm", 1, 0, packages: ["sm_armour_mp_bloodravens_armr"], visible: false)),
            Entry("csm_armour_mp_raptor_01_head", Gear("head", 1, 0, packages: ["csm_armour_mp_raptor_01_helmet"], helmet: "helmet")),
            Entry("csm_armour_mp_raptor_01_leg_left", Gear("leftleg", 1, 0, packages: ["csm_armour_mp_raptor_01_legl"])),
            Entry("csm_berserker_helmet", Gear("head", 1, 0, packages: ["csm_armor_berserk_001_helmet"], helmet: "helmet", visible: false)),
            Entry("csm_berserker_backpack", Gear("backpack", 1, 0, packages: ["csm_armor_berserk_001_backpack"], visible: false)),
            Entry("sm_librarian_torso", Gear("torso", 1, 0, packages: ["sm_armour_mp_librarian_torso", "sm_armour_mp_librarian_belt"], visible: false)),
            Entry("sm_librarian_shoulder_right", Gear("rightshoulder", 1, 0, packages: ["sm_armour_mp_librarian_shldrr"], visible: false)),
            Entry("sm_librarian_backpack", Gear("backpack", 1, 0, packages: ["sm_armour_mp_librarian_backpack"], visible: false)));
        if (campaignPackages)
            foreach (var spec in ExtraArmourPieces.All.Where(s => s.Packages is not null && !s.Name.StartsWith("sm_armour_leandros", StringComparison.Ordinal)))
                foreach (var p in spec.Packages!) File.WriteAllBytes(Path.Combine(Packages, p + ".oppc"), []);
        return CustomizationCatalog.Load(_dir, notes ?? []);
    }

    [Fact]
    public void Series_A_pieces_follow_every_earlier_piece_and_old_codes_keep_their_pieces()
    {
        var with = LoadSeriesA();
        CustomizationCatalog without;
        using (CustomizationCatalog.OverrideExtraPieces(false)) without = CustomizationCatalog.Load(_dir, []);
        foreach (var race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
            foreach (var slot in CustomizationCatalog.ArmourSlots)
            {
                var before = without.Race(race)!.PiecesFor(slot).Select(p => p.Name).ToList();
                var after = with.Race(race)!.PiecesFor(slot);
                Assert.Equal(before, after.Take(before.Count).Select(p => p.Name));   // the same index names the same piece
                Assert.All(after.Skip(before.Count), p => Assert.NotNull(p.ExtraGroup));
                Assert.DoesNotContain(without.Race(race)!.PiecesFor(slot), p => p.ExtraGroup is not null);
                // In the table's order, each once.
                var expected = ExtraArmourPieces.All.Where(s => s.Race == race && s.Slot == slot).Select(s => s.Name).ToList();
                var added = after.Skip(before.Count).Select(p => p.Name).ToList();
                Assert.Equal(expected.Where(added.Contains), added);
                Assert.Equal(added.Count, added.Distinct().Count());
            }
        // A code written against the earlier lists resolves to the same pieces.
        var code = SmSim.AppearanceCode.Default.WithPiece(SmSim.ArmourSlot.Head, 2).WithPiece(SmSim.ArmourSlot.Torso, 1).WithPiece(SmSim.ArmourSlot.LeftArm, 0);
        Assert.Equal(ProfileWire.FromCode(code, CustomizationCatalog.SpaceMarine, without).Pieces, ProfileWire.FromCode(code, CustomizationCatalog.SpaceMarine, with).Pieces);
        // A Series A pick round-trips through the wire and reads as the default piece on a build without it.
        var look = ProfileWire.FromCode(code, CustomizationCatalog.SpaceMarine, with) with { Pieces = new Dictionary<string, string> { ["torso"] = "sm_librarian_torso" } };
        var wire = ProfileWire.ToCode(look, with);
        Assert.Equal("sm_librarian_torso", ProfileWire.FromCode(wire, CustomizationCatalog.SpaceMarine, with).Pieces["torso"]);
        Assert.False(ProfileWire.FromCode(wire, CustomizationCatalog.SpaceMarine, without).Pieces.ContainsKey("torso"));
    }

    [Fact]
    public void Every_Series_A_piece_the_install_has_is_offered_with_its_group_slot_and_our_name()
    {
        var c = LoadSeriesA();
        var sm = c.SpaceMarines; var csm = c.ChaosMarines;
        Assert.Contains(sm.PiecesFor("leftarm"), p => p.Name == "sm_dlc_bloodravens_arm_left" && p.ExtraGroup == "mp" && p.DisplayName == "Blood Ravens Left Arm");
        Assert.Contains(csm.PiecesFor("head"), p => p.Name == "csm_armour_mp_raptor_01_head" && p.HelmetOverride == "helmet");   // every Chaos class: the list is the race's
        // Round 5 (owner, 2026-11-26: "Hide the 2 pieces"): the co-op elite Berserker's helmet and backpack keep their places (reserved) but are never offered.
        var berserker = csm.PiecesFor("head").Single(p => p.Name == "csm_berserker_helmet");
        Assert.Equal("Khorne Berserker Elite Helmet", berserker.DisplayName);   // not the data's "DEV" label
        Assert.True(berserker.Reserved);
        Assert.True(csm.PiecesFor("backpack").Single(p => p.Name == "csm_berserker_backpack").Reserved);
        Assert.DoesNotContain(csm.OfferedPiecesFor("head"), p => p.Name == "csm_berserker_helmet");
        Assert.DoesNotContain(csm.OfferedPiecesFor("backpack"), p => p.Name == "csm_berserker_backpack");
        var lib = sm.PiecesFor("torso").Single(p => p.Name == "sm_librarian_torso");
        Assert.Equal(["sm_armour_mp_librarian_torso", "sm_armour_mp_librarian_belt"], lib.Packages);
        Assert.Equal("librarian", lib.ExtraGroup);
        // Campaign armour: by package, our names, only what the install has (no Leandros packages were written; Sidonus has no helmet in the table's install).
        var sid = sm.PiecesFor("torso").Single(p => p.Name == "sm_armour_sidonus_torso");
        Assert.Equal(("campaign", "Sidonus Torso", 1), (sid.ExtraGroup, sid.DisplayName, sid.Level));
        Assert.Equal(["sm_armour_sidonus_torso"], sid.Packages);
        Assert.DoesNotContain(sm.OfferedPiecesFor("torso"), p => p.Name == "sm_armour_leandros_torso");
        Assert.True(sm.PiecesFor("torso").Single(p => p.Name == "sm_armour_leandros_torso").Reserved);   // its place is kept (finding 6)
        Assert.Contains(sm.PiecesFor("head"), p => p.Name == "sm_armour_corvus_helmet" && p.HelmetOverride == "helmet");
        Assert.Contains(csm.PiecesFor("leftleg"), p => p.Name == "csm_armour_havoc_legl" && p.DisplayName == "Havoc (campaign) Left Leg");
        Assert.DoesNotContain(sm.PiecesFor("leftshoulder"), p => p.Name == "sm_armour_basic_upres_shldrl");   // unreadable mesh: never offered
    }

    [Fact]
    public void Campaign_pieces_need_their_packages_and_missing_wargear_adds_no_notes()
    {
        var notes = new List<string>();
        var c = LoadSeriesA(campaignPackages: false, notes);
        Assert.DoesNotContain(c.SpaceMarines.OfferedPiecesFor("torso"), p => p.ExtraGroup == "campaign");
        Assert.All(c.SpaceMarines.PiecesFor("torso").Where(p => p.ExtraGroup == "campaign"), p => Assert.True(p.Reserved && p.Packages.Count == 0));
        Assert.DoesNotContain(notes, n => n.Contains("sm_armour_", StringComparison.Ordinal) || n.Contains("csm_armour_basic", StringComparison.Ordinal));
        // The plain install (no Series A wargear): nothing added, nothing noted.
        var plain = Load(notes);
        Assert.DoesNotContain(plain.SpaceMarines.OfferedPiecesFor("torso"), p => p.ExtraGroup is not null);
        Assert.DoesNotContain(notes, n => n.Contains("librarian", StringComparison.Ordinal) || n.Contains("bloodravens", StringComparison.Ordinal));
    }

    [Fact]
    public void Developer_materials_get_their_packages_textures_and_others_are_untouched()
    {
        var pink = new BmatMaterial("helmet", "pink", new Dictionary<string, string>());
        var fix = BakedArmourPaint.FixMaterial("csm_armor_berserk_001_helmet")!;
        var fixedPink = fix(pink);
        Assert.Equal("character_wargear", fixedPink.ShaderName);
        Assert.Equal("csm_armour_mp_basic_helmet_dif", fixedPink.Textures["DiffuseMap"]);
        Assert.True(BakedArmourPaint.NeedsMask(fixedPink));   // a mask is then built
        var pack = BakedArmourPaint.FixMaterial("csm_armor_berserk_001_backpack")!(new BmatMaterial("Standard_2", "character_wargear", new Dictionary<string, string>()));
        Assert.Equal(("gray_dif", "blank_nrm", false), (pack.Textures["DiffuseMap"], pack.Textures["NormalMap"], pack.Textures.ContainsKey("PaintMask")));
        var normal = new BmatMaterial("m", "diff_norm_spec_lp", new Dictionary<string, string> { ["DiffuseMap"] = "d" });
        Assert.Same(normal, fix(normal));
        Assert.Null(BakedArmourPaint.FixMaterial("sm_armour_mp_bloodravens_arml"));
    }
}

public partial class CustomizationCatalogTests
{
    [Fact]
    public void A_piece_the_install_lacks_keeps_its_index_so_codes_mean_the_same_on_every_install()
    {
        // Finding 6: a missing package used to shift every later index, and a server without the campaign packages reset those pieces.
        var lacking = LoadSeriesA(campaignPackages: false);   // first: the full load writes the package files
        var full = LoadSeriesA();
        foreach (var race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
            foreach (var slot in CustomizationCatalog.ArmourSlots)
                Assert.Equal(full.Race(race)!.PiecesFor(slot).Select(p => p.Name), lacking.Race(race)!.PiecesFor(slot).Select(p => p.Name));
        Assert.Equal(full.LimitsFor(CustomizationCatalog.SpaceMarine).PiecesPerSlot, lacking.LimitsFor(CustomizationCatalog.SpaceMarine).PiecesPerSlot);
        // A code naming a campaign torso: the lacking install keeps the index (not sanitised away) and draws the class's default torso.
        var torsos = full.SpaceMarines.PiecesFor("torso");
        int index = torsos.ToList().FindIndex(p => p.Name == "sm_armour_sidonus_torso");
        var code = SmSim.AppearanceCode.Default.WithPiece(SmSim.ArmourSlot.Torso, (byte)index);
        Assert.Equal(code, code.Sanitized(lacking.LimitsFor(CustomizationCatalog.SpaceMarine)));
        var drawn = AppearanceResolver.Resolve(code, "sm - tactical", lacking);
        Assert.NotEqual("sm_armour_sidonus_torso", drawn.Piece("torso")!.Name);
        Assert.NotEmpty(drawn.Piece("torso")!.Packages);
        Assert.Equal("sm_armour_sidonus_torso", AppearanceResolver.Resolve(code, "sm - tactical", full).Piece("torso")!.Name);
        Assert.True(AppearanceResolver.Resolve(code, "sm - tactical", full).Piece("torso")!.BuiltMasks);
        // The name survives a round trip through the wire on both.
        Assert.Equal("sm_armour_sidonus_torso", ProfileWire.FromCode(code, CustomizationCatalog.SpaceMarine, lacking).Pieces["torso"]);
    }
}
