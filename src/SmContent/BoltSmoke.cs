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

    /// <summary>The dust share for a wargear's effects: <see cref="Share"/> for a bolt weapon unless <paramref name="old"/>, else 1.</summary>
    public static float ShareFor(string? wargear, bool old) => !old && IsBoltWeapon(wargear) ? Share : 1f;
}
