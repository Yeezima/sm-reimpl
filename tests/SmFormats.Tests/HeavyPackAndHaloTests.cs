using SmContent.Customization;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// What shows on the heavy classes' backs (visual/pack-halo; made-up names, no original data): a weapon's "backpack" attachment is never drawn over the armour's backpack slot,
/// and a carried perk's own model is worn unless the class swaps it for its item.
/// </summary>
public class HeavyPackAndHaloTests
{
    [Fact]
    public void A_weapons_backpack_attachment_is_not_drawn_but_the_jump_pack_and_other_worn_parts_are()
    {
        Assert.False(WornItems.Draws("backpack", packWargear: false));   // the sidearm's copy: the slot's pack is the only one
        Assert.False(WornItems.Draws("BackPack", packWargear: false));
        Assert.True(WornItems.Draws("backpack", packWargear: true));     // the jump pack itself
        Assert.True(WornItems.Draws(@"wargear\attachment_entry", packWargear: true));
        Assert.True(WornItems.Draws("ammo_belt", packWargear: false));
    }

    static IReadOnlyList<string>? PackagesOf(string name) => name switch
    {
        "perk_halo" => ["sm_halo"],
        "perk_plain" => [],
        _ => null,
    };

    [Fact]
    public void A_carried_perk_with_a_model_is_worn_painted_as_a_perk_item()
    {
        var piece = Assert.Single(AppearanceResolver.PerkPieces(["weapon", "perk_plain", "perk_halo"], PackagesOf, []));
        Assert.Equal(("perk", "perk_halo", true), (piece.Slot, piece.Name, piece.Forced));
        Assert.Equal(["sm_halo"], piece.Packages);
    }

    [Fact]
    public void A_perk_the_class_swaps_for_its_own_item_shows_only_that_item()
    {
        Assert.Empty(AppearanceResolver.PerkPieces(["PERK_HALO"], PackagesOf, ["perk_halo"]));   // the Havoc: its icon, never the loyalist halo
    }

    [Fact]
    public void Nothing_carried_or_the_same_perk_twice_gives_at_most_one_item()
    {
        Assert.Empty(AppearanceResolver.PerkPieces(null, PackagesOf, []));
        Assert.Single(AppearanceResolver.PerkPieces(["perk_halo", "perk_halo"], PackagesOf, []));
    }
}
