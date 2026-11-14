using SmSim;

namespace SmContent;

/// <summary>What a carried item is for (the client's <c>WeaponRig</c> roles): the ranged slot, the pistol slot, the melee weapon, or a thrown item (Ork O6). <see cref="Second"/> is Weapon Versatility's second primary (protocol 44; Tactical only), in hand when the slot shown is <see cref="WeaponSlot.Second"/>.</summary>
public enum HandRole { Primary, Pistol, Melee, Grenade, Second }

/// <summary>
/// Which carried items are in hand (Ork O6; engine-free so it is tested): the M24 rules of <c>WeaponRig.Update</c> plus the Ork's. The slot shown decides the primary
/// and the pistol, a melee attack shows the melee weapon, a thrown item is in hand only while thrown; a melee weapon wielded on the bone another item uses puts
/// that item away (two-handed weapons on the chest prop), and so does a two-handed item whatever bone it uses (**ours**, the Ork's shoota).
/// </summary>
public static class WeaponHands
{
    /// <param name="role">The item's role.</param>
    /// <param name="shown">The ranged slot in hand (after the switch delay).</param>
    /// <param name="meleeOut">A melee attack is shown or the class always holds its melee weapon.</param>
    /// <param name="grenadeHeld">A thrown item is being thrown.</param>
    /// <param name="twoHanded">The item needs both hands.</param>
    /// <param name="wieldBone">The bone the item is wielded on.</param>
    /// <param name="meleeBone">The bone the melee weapon is wielded on while it is out; null when it is not out (or has no wield bone).</param>
    public static bool InHand(HandRole role, WeaponSlot shown, bool meleeOut, bool grenadeHeld, bool twoHanded, string? wieldBone, string? meleeBone)
    {
        bool inHand = role switch
        {
            HandRole.Primary => shown == WeaponSlot.Ranged,
            HandRole.Second => shown == WeaponSlot.Second,
            HandRole.Pistol => shown == WeaponSlot.Pistol,
            HandRole.Grenade => grenadeHeld,
            _ => meleeOut,
        };
        if (!inHand || role == HandRole.Melee || meleeBone is null) return inHand;
        if (string.Equals(wieldBone, meleeBone, StringComparison.OrdinalIgnoreCase)) return false;
        return !twoHanded;
    }
}

/// <summary>
/// Which of the two primaries (the primary and Weapon Versatility's second primary) hangs on the back (fx/character-visuals; owner, 2026-10-30: "the weapon most recently swapped
/// out is the one stowed on the back"). Engine-free so it is tested. The other primary is not drawn (the one back marker holds one gun); the pistol hangs on its own holster.
/// </summary>
public static class BackWeapon
{
    /// <summary>
    /// The primary role on the back, or null when none is shown there.
    /// </summary>
    /// <param name="shown">The slot in hand (after the switch delay).</param>
    /// <param name="hasSecond">The character carries a second primary.</param>
    /// <param name="lastPrimary">The primary slot (Ranged or Second) that was in hand most recently.</param>
    /// <param name="between">Between a switch's release and take (both guns are away): the old weapon is the one going to the back.</param>
    public static HandRole? Owner(WeaponSlot shown, bool hasSecond, WeaponSlot lastPrimary, bool between)
    {
        if (!hasSecond) return HandRole.Primary;   // one primary: as before (in hand, or on the back)
        var gun = between || shown == WeaponSlot.Pistol ? lastPrimary : shown == WeaponSlot.Ranged ? WeaponSlot.Second : WeaponSlot.Ranged;
        return gun == WeaponSlot.Second ? HandRole.Second : HandRole.Primary;
    }
}

/// <summary>
/// fix/versatility-knife-stow (owner, 2026-11-07: "with weapon versatility, the primary weapon should probably be holstered on top left of the left leg, pointing down, during all
/// knife and stun strike animations instead of on the chest"). Engine-free so it is tested. No Space Marine weapon is flagged two-handed, so a melee strike left the selected primary
/// "in hand" on the chest prop, which the strike's clip throws about (it flew through the air). **Ours**: a Tactical that carries Weapon Versatility holsters the selected primary
/// on the left thigh for as long as a melee strike is shown; without the perk nothing changes.
/// </summary>
public static class MeleeHolster
{
    /// <summary>
    /// True when the item (a primary role) is the selected one and goes to the left thigh for the strike. fix/ff-weapon-flying (owner, 2026-11-14: "there should be no weapon models flying around
    /// in the air away from the marine during any F + F stun strike combo ever - no matter if weapon versatility is equipped or not"): the same holster now applies without Weapon Versatility
    /// (one primary), for every class that carries a primary; <paramref name="hasSecond"/> no longer matters.
    /// </summary>
    /// <param name="role">The item's role.</param>
    /// <param name="shown">The ranged slot in hand (after the switch delay).</param>
    /// <param name="hasSecond">The character carries Weapon Versatility's second primary.</param>
    /// <param name="meleeOut">A melee strike is shown.</param>
    /// <param name="away">Both guns are already away (between a switch's release and take, or a stim).</param>
    public static bool OnThigh(HandRole role, WeaponSlot shown, bool hasSecond, bool meleeOut, bool away) =>
        meleeOut && !away
        && ((role == HandRole.Primary && shown == WeaponSlot.Ranged) || (role == HandRole.Second && shown == WeaponSlot.Second));

    /// <summary>
    /// The holster decision for one primary item once <see cref="OnThigh"/> said it leaves the chest prop (fix/ff-weapon-flying review: "no weapon flies ever"): with a thigh spot it hangs
    /// there; without one (no stow mount in the data, or a model supplied by an extra such as Musou's arsenal) it is simply not in hand, so the rig shows it on its stow marker or not at all, never on the swung chest prop.
    /// </summary>
    /// <returns>The item's new in-hand state and whether it is on the thigh.</returns>
    public static (bool InHand, bool Thigh) Resolve(bool inHand, bool holster, bool hasThighMount) =>
        holster ? (false, hasThighMount) : (inHand, false);
}
