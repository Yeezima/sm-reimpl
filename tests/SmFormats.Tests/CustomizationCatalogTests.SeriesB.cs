using SmContent;
using SmContent.Customization;
using SmFormats.Obpk;
using Xunit;

namespace SmFormats.Tests;

/// <summary>armour Series B (owner's picks of 2026-11-27: hidden shoulder badges, bare campaign heads) on the synthetic install; no original data.</summary>
public partial class CustomizationCatalogTests
{
    /// <summary>The Series A install plus synthetic hidden badge entries named as the table's (and one left-out duplicate), and empty bare-head packages.</summary>
    CustomizationCatalog LoadSeriesB(bool headPackages = true)
    {
        LoadSeriesA();
        var decals = new List<SyntheticObpkWriter.File>
        {
            Entry("sm_foo_orig", Decal("space_marine", 1600, true)), Entry("sm_bar", Decal("space_marine", 1603, true)),
            Entry("sm_ultramarines", Decal("space_marine", 0, false)),   // a left-out duplicate: never offered
            Entry("csm_black_templars_orig", Decal("chaos", 0, false, "blank")),
            Entry("csm_star", Decal("chaos", 1607, true)),
        };
        // The table's entries except the last (it then keeps its place as a blank).
        foreach (var spec in HiddenBadges.Offered.SkipLast(1)) decals.Add(Entry(spec.Name, Decal(spec.Race, 0, false, spec.Name + "_dif")));
        WritePackage("custom_paint_decal", [.. decals]);
        // LoadSeriesA writes an empty package for every package piece of the table, the bare heads included.
        foreach (var (package, _) in ExtraArmourPieces.BareHeads)
            if (headPackages) File.WriteAllBytes(Path.Combine(Packages, package + ".oppc"), []); else File.Delete(Path.Combine(Packages, package + ".oppc"));
        return CustomizationCatalog.Load(_dir, []);
    }

    [Fact]
    public void Hidden_badges_follow_the_visible_ones_in_table_order_and_old_codes_keep_their_badges()
    {
        var with = LoadSeriesB();
        CustomizationCatalog without;
        using (CustomizationCatalog.OverrideExtraPieces(false)) without = CustomizationCatalog.Load(_dir, []);
        var before = without.SpaceMarines.Decals.Select(d => d.Name).ToList();
        var after = with.SpaceMarines.Decals;
        Assert.Equal(["sm_bar", "sm_foo_orig"], before);                                  // --no-extra-pieces: the original list
        Assert.Equal(before, after.Take(before.Count).Select(d => d.Name));                 // the same index names the same badge
        Assert.Equal(HiddenBadges.Offered.Select(s => s.Name), after.Skip(before.Count).Select(d => d.Name));
        Assert.All(after.Skip(before.Count), d => Assert.True(d.FromHidden));
        // Our names for entries without text; the missing last entry is a blank (kept place, never offered).
        Assert.Equal("Command Squad", after.Single(d => d.Name == "sm_commandsquad").DisplayName);
        var last = after[^1];
        Assert.True(last.IsBlank);
        Assert.DoesNotContain(after.Take(after.Count - 1).Skip(before.Count), d => d.IsBlank);
        // Left out: duplicates and the Chaos blanks; no Chaos badge is added.
        Assert.DoesNotContain(after, d => HiddenBadges.LeftOut.ContainsKey(d.Name));
        Assert.Equal(["csm_star"], with.ChaosMarines.Decals.Select(d => d.Name));
        Assert.Equal(16, HiddenBadges.Offered.Count + HiddenBadges.LeftOut.Count);         // every hidden entry of the install is accounted for
        Assert.Empty(HiddenBadges.Offered.Select(s => s.Name).Intersect(HiddenBadges.LeftOut.Keys));
        // The appearance limits grow with the list; a hidden badge round-trips through the wire and reads as the scheme's badge on a build without it.
        Assert.Equal(after.Count, with.LimitsFor(CustomizationCatalog.SpaceMarine).Badges);
        var look = ProfileWire.FromCode(SmSim.AppearanceCode.Default, CustomizationCatalog.SpaceMarine, with)
            with { Decals = new Dictionary<string, DecalChoice> { ["leftshoulder"] = DecalChoice.ForBadge("sm_identifier_assault") } };
        var wire = ProfileWire.ToCode(look, with);
        Assert.Equal("sm_identifier_assault", ProfileWire.FromCode(wire, CustomizationCatalog.SpaceMarine, with).Decals["leftshoulder"].Badge);
        Assert.True(wire.IsWithin(with.LimitsFor(CustomizationCatalog.SpaceMarine)));
        Assert.False(wire.IsWithin(without.LimitsFor(CustomizationCatalog.SpaceMarine)));       // both sides must agree on the switch
        Assert.False(ProfileWire.FromCode(wire.Sanitized(without.LimitsFor(CustomizationCatalog.SpaceMarine)), CustomizationCatalog.SpaceMarine, without).Decals.ContainsKey("leftshoulder"));
    }

    [Fact]
    public void Bare_heads_are_unpainted_helmetless_Space_Marine_head_picks_after_every_earlier_head()
    {
        var with = LoadSeriesB();
        CustomizationCatalog without;
        using (CustomizationCatalog.OverrideExtraPieces(false)) without = CustomizationCatalog.Load(_dir, []);
        var heads = with.SpaceMarines.PiecesFor("head");
        var tail = heads.TakeLast(ExtraArmourPieces.BareHeads.Count).ToList();
        Assert.Equal(ExtraArmourPieces.BareHeads.Select(b => b.Package), tail.Select(p => p.Name));   // appended last: earlier head indices keep their meaning
        Assert.All(tail, p =>
        {
            Assert.Equal(("barehead", "no_helmet", false, false), (p.ExtraGroup, p.HelmetOverride, p.BuiltMasks, p.Reserved));
            Assert.Equal([p.Name], p.Packages);
        });
        Assert.Equal("Titus (Bare Head)", tail[0].DisplayName);
        Assert.DoesNotContain(with.ChaosMarines.PiecesFor("head"), p => p.ExtraGroup == "barehead");
        Assert.DoesNotContain(without.SpaceMarines.PiecesFor("head"), p => p.ExtraGroup == "barehead");
        Assert.DoesNotContain(heads, p => ExtraArmourPieces.BareHeadsLeftOut.ContainsKey(p.Name));     // Drogan: left out
        // A bare head round-trips through the appearance code and resolves without paint masks.
        var look = ProfileWire.FromCode(SmSim.AppearanceCode.Default, CustomizationCatalog.SpaceMarine, with)
            with { Pieces = new Dictionary<string, string> { ["head"] = "ingame_sidonus" } };
        var wire = ProfileWire.ToCode(look, with);
        Assert.Equal("ingame_sidonus", ProfileWire.FromCode(wire, CustomizationCatalog.SpaceMarine, with).Pieces["head"]);
        Assert.False(ProfileWire.FromCode(wire.Sanitized(without.LimitsFor(CustomizationCatalog.SpaceMarine)), CustomizationCatalog.SpaceMarine, without).Pieces.ContainsKey("head"));
    }

    [Fact]
    public void A_bare_head_whose_package_is_missing_keeps_its_place_but_is_not_offered()
    {
        var c = LoadSeriesB(headPackages: false);
        Assert.All(c.SpaceMarines.PiecesFor("head").Where(p => p.ExtraGroup == "barehead"), p => Assert.True(p.Reserved));
        Assert.DoesNotContain(c.SpaceMarines.OfferedPiecesFor("head"), p => p.ExtraGroup == "barehead");
    }
}
