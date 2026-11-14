using SmContent;
using SmSim;
using Xunit;

namespace SmNet.Tests;

/// <summary>fix/versatility-knife-stow: which primary goes to the left thigh during a melee strike (owner, 2026-11-07).</summary>
public class MeleeHolsterTests
{
    [Theory]
    [InlineData(HandRole.Primary, WeaponSlot.Ranged, true, true, false, true)]    // the primary selected: thigh
    [InlineData(HandRole.Second, WeaponSlot.Ranged, true, true, false, false)]    // the other one hangs on the back
    [InlineData(HandRole.Second, WeaponSlot.Second, true, true, false, true)]     // the second selected: thigh
    [InlineData(HandRole.Primary, WeaponSlot.Second, true, true, false, false)]
    [InlineData(HandRole.Primary, WeaponSlot.Ranged, false, true, false, true)]   // no Weapon Versatility: the single primary goes to the thigh too (fix/ff-weapon-flying)
    [InlineData(HandRole.Second, WeaponSlot.Ranged, false, true, false, false)]   // no second primary carried: nothing to place
    [InlineData(HandRole.Primary, WeaponSlot.Ranged, true, false, false, false)]  // no strike shown
    [InlineData(HandRole.Primary, WeaponSlot.Ranged, true, true, true, false)]    // both guns already away (switch gap, stim)
    [InlineData(HandRole.Primary, WeaponSlot.Pistol, true, true, false, false)]   // pistol selected: both primaries are away anyway
    [InlineData(HandRole.Pistol, WeaponSlot.Pistol, true, true, false, false)]
    [InlineData(HandRole.Melee, WeaponSlot.Ranged, true, true, false, false)]
    public void Thigh_only_for_the_selected_primary_of_a_versatility_tactical_during_a_strike(HandRole role, WeaponSlot shown, bool hasSecond, bool meleeOut, bool away, bool expected) =>
        Assert.Equal(expected, MeleeHolster.OnThigh(role, shown, hasSecond, meleeOut, away));

    /// <summary>Where a carried gun is drawn while the marine is on his feet.</summary>
    enum Place { InHand, Thigh, Back, NotDrawn }

    /// <summary>The rig's decision for one primary item (the logic of <c>WeaponRig.Update</c> / <c>Apply</c>: thigh first, then in hand, then the back marker or hidden).</summary>
    static Place PlacePrimary(HandRole role, WeaponSlot shown, bool hasSecond, bool melee, bool away, WeaponSlot lastPrimary)
    {
        bool inHand = !away && WeaponHands.InHand(role, shown, melee, false, false, "RIG_Prop_M_Chest", melee ? "RIG_Prop_M_Chest" : null);
        if (MeleeHolster.OnThigh(role, shown, hasSecond, melee, away)) return Place.Thigh;
        if (inHand) return Place.InHand;
        bool hidden = hasSecond && BackWeapon.Owner(shown, hasSecond, lastPrimary, away) != role;
        return hidden ? Place.NotDrawn : Place.Back;
    }

    /// <summary>
    /// fix/ff-weapon-flying (owner, 2026-11-14): through every strike of an F + F combo, with or without Weapon Versatility, whatever slot was selected, no primary is "in hand" on the chest
    /// prop the strike clips throw about: each is on the thigh, the back marker or not drawn, and the selected primary is on the thigh whenever the marine has one in hand outside strikes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_primary_stays_on_the_swung_chest_prop_during_any_strike(bool hasSecond)
    {
        foreach (var shown in new[] { WeaponSlot.Ranged, WeaponSlot.Second, WeaponSlot.Pistol })
        foreach (var away in new[] { false, true })
        foreach (var last in new[] { WeaponSlot.Ranged, WeaponSlot.Second })
        {
            if (!hasSecond && (shown == WeaponSlot.Second || last == WeaponSlot.Second)) continue;
            var roles = hasSecond ? new[] { HandRole.Primary, HandRole.Second } : new[] { HandRole.Primary };
            foreach (var role in roles)
            {
                Assert.NotEqual(Place.InHand, PlacePrimary(role, shown, hasSecond, melee: true, away, last));
                // Without a strike the old rule holds: the selected primary is in hand.
                if (!away && ((role == HandRole.Primary && shown == WeaponSlot.Ranged) || (role == HandRole.Second && shown == WeaponSlot.Second)))
                {
                    Assert.Equal(Place.InHand, PlacePrimary(role, shown, hasSecond, melee: false, away, last));
                    Assert.Equal(Place.Thigh, PlacePrimary(role, shown, hasSecond, melee: true, away, last));
                }
            }
        }
    }
}
