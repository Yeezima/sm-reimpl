namespace SmContent;

/// <summary>
/// fx/bolt-smoke (the owner, 2026-11-15, a deliberate change from the original, Classic PvP and Musou Mode: "ensure those black smoke effects from bolt weapons also aren't in classic pvp either"; the
/// bolter, the stalker bolter and the other bolt weapons, not the autocannon, which has its own rule). A bolt weapon's muzzle, hit and blast effects draw none of their alpha-blended sprites (the black smoke
/// and the dark flakes of debris that fly with it: <see cref="FxEffect.DustShare"/> 0); their additive flash, sparks, fire, light and decal stay. The data is read as before and the install is
/// never changed; <c>--old-bolt-smoke</c> (test aid, before / after) draws them as the data has them. Engine-free so the weapon list is tested.
/// </summary>
public static class BoltSmoke
{
    /// <summary>The share of a bolt weapon's alpha sprites that is drawn (0: none).</summary>
    public const float Share = 0f;

    /// <summary>Whether a wargear file is a bolt weapon for this rule: a bolter, bolt pistol, storm or heavy bolter, or the stalker (its own name, or the sniper rifle's), never the autocannon.</summary>
    public static bool IsBoltWeapon(string? wargear)
    {
        if (string.IsNullOrEmpty(wargear) || wargear.Contains("auto_cannon", StringComparison.OrdinalIgnoreCase) || wargear.Contains("autocannon", StringComparison.OrdinalIgnoreCase)) return false;
        return wargear.Contains("bolt", StringComparison.OrdinalIgnoreCase) || wargear.Contains("stalker", StringComparison.OrdinalIgnoreCase) || wargear.Contains("sniper_rifle", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Musou Mode's kill gore after a bolt weapon's kill (owner 2026-11-15, "no black smoke clouds ... when shooting bolt weapons in musou mode"): whether the death's blood effects (<c>body_explode</c>, <c>body_dismemberment</c>,
    /// <c>body_dismemberment_limb</c>) are played without their smoke-classed parts (the <c>mist</c>'s <c>dark</c> and <c>light</c> rings, <c>smoke</c>, <c>smoke dissipating</c>), the blood, gibs and limbs staying: the kill was by a bolt
    /// weapon's shot or its bolt blast (<paramref name="byShotOrBoltBlast"/>), not melee, a charge, a grenade or the autocannon, unless <paramref name="old"/>.
    /// </summary>
    public static bool KillDropsSmoke(string? wargear, bool byShotOrBoltBlast, bool old) => !old && byShotOrBoltBlast && IsBoltWeapon(wargear);

    /// <summary>
    /// Musou Mode's blast (owner 2026-11-15, "Flash only": a bolt weapon's AoE blast draws a bright flash and nothing else, as the autocannon's does): whether a blast draws only its bright flash
    /// (<see cref="FxEffect.BrightFlash"/>; <see cref="BrightFlash.Keeps"/>: no fire puff, no smoke, no debris). The autocannon's blast (<paramref name="autocannonBlast"/>) unless <paramref name="oldAutocannon"/>, a bolt
    /// weapon's unless <paramref name="oldBolt"/> (<c>--old-bolt-smoke</c> restores the whole frag effect).
    /// </summary>
    public static bool BlastFlashOnly(bool autocannonBlast, bool oldAutocannon, bool oldBolt) => autocannonBlast ? !oldAutocannon : !oldBolt;

    /// <summary>The dust share for a wargear's effects: <see cref="Share"/> for a bolt weapon unless <paramref name="old"/>, else 1.</summary>
    public static float ShareFor(string? wargear, bool old) => !old && IsBoltWeapon(wargear) ? Share : 1f;
}
