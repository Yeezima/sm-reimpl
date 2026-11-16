using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;

namespace SmContent.Extras.Musou;

/// <summary>
/// The Dreadnought's guns on the HUD (D6; docs/musou-dreadnought-design.md section 7): the wargear names the data gives them are not in the HUD's name rules, so the reticle and weapon box are named here: the cannon takes the
/// autocannon's reticle (<c>ui_data/reticule_type</c> "autocannon", data) and the heavy bolter's ammunition display (the rounds of its clip of 140; the reserve is endless and not shown), the melta the melta's reticle and
/// box (data names "melta"). Musou only.
/// </summary>
public static class DreadnoughtPresentation
{
    static readonly ReticleSpec CannonReticle = new("autocannon", "autocannon_mc", "autocannon_active_mc", false);
    static readonly WeaponBoxSpec? MeltaBox = WeaponBoxRules.For(DreadnoughtRules.MeltaWargear, "melta", false);

    // The cannon no longer borrows the autocannon's sounds and effects (owner, 2026-11-15, "DA's own"): the shot path reads the wargear's own lists (see DreadnoughtFeel.Cannon*); the autocannon's damage and blast stay in the rules.

    public static ReticleSpec? Reticle(ContentExtras extras, string wargear) =>
        !extras.HasFlag(ContentExtras.Musou) ? null : wargear == DreadnoughtRules.CannonWargear ? CannonReticle : wargear == DreadnoughtRules.MeltaWargear ? ReticleRules.For(wargear, "melta") : null;

    public static WeaponBoxSpec? WeaponBox(ContentExtras extras, string wargear) =>
        !extras.HasFlag(ContentExtras.Musou) ? null : wargear == DreadnoughtRules.CannonWargear ? EndlessCannonBox : wargear == DreadnoughtRules.MeltaWargear ? MeltaBox : null;

    /// <summary>The cannon's box: the autocannon's (the endless sign and the heat bar, no count; owner 2026-11-15: unlimited ammunition, a heat meter).</summary>
    static readonly WeaponBoxSpec EndlessCannonBox = Arsenal.Arsenal.EndlessAutocannonBox;

    /// <summary>A reserve this large is the data's "infinite" (the cannon's): the HUD shows the clip, not the reserve.</summary>
    public static bool EndlessReserve(int reserve) => HudRules.EndlessReserve(reserve);
}
