using SmContent;
using SmContent.Customization;
using SmContent.Extras.Musou;
using Xunit;

namespace SmFormats.Tests;

/// <summary>armour-pieces-1125 Series A, the part that reads Musou's Librarian look (moved under Extras/ at the series-37 integration: ExtrasBoundaryTests keeps extras namespaces out of core files).</summary>
public partial class CustomizationCatalogTests
{
    [Fact]
    public void The_Librarian_pieces_keep_the_games_masks_and_Musou_dresses_the_Librarian_as_before()
    {
        var c = LoadSeriesA();
        foreach (var (slot, name, _) in LibrarianLook.Pieces)
            Assert.False(c.SpaceMarines.PiecesFor(slot).Single(p => p.Name == name).BuiltMasks, $"{name} must load exactly as before (no built mask)");
        Assert.True(c.SpaceMarines.PiecesFor("leftarm").Single(p => p.Name == "sm_dlc_bloodravens_arm_left").BuiltMasks);
        Assert.False(c.ChaosMarines.PiecesFor("head").Single(p => p.Name == "csm_armour_mp_raptor_01_head").BuiltMasks);   // masked by the game
        // Musou's Librarian: the same three pieces, names and packages, over a look that now may also offer them.
        var look = AppearanceResolver.Resolve(AppearanceProfile.Empty("t", CustomizationCatalog.SpaceMarine, "t"), "sm - tactical", c, null);
        var dressed = LibrarianLook.Dress(look);
        Assert.True(LibrarianLook.Wears(dressed));
        foreach (var (slot, name, packages) in LibrarianLook.Pieces)
        {
            var piece = dressed.Piece(slot)!;
            Assert.Equal((name, true), (piece.Name, piece.Forced));
            Assert.Equal(packages, piece.Packages);
            var offered = c.SpaceMarines.PiecesFor(slot).Single(p => p.Name == name);
            Assert.Equal(packages, offered.Packages);   // the customizer's piece draws the very packages Musou puts on
        }
    }
}
