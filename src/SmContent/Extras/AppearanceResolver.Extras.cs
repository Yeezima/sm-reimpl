using SmContent.Extras.Musou.Arsenal;

namespace SmContent.Customization;

/// <summary>
/// The appearance resolver's added-content part (docs/added-content.md): the one implementation of <see cref="ExtrasWornAs"/>. The arsenal's autocannon wears the heavy bolter's class overrides
/// (<see cref="Arsenal.WornAs"/>: the Devastator's <c>sm_backpack_heavy_bolter</c>; the owner, 2026-11-11: "equipping the autocannon should also change the devastator/havoc's backpack to be the same
/// one as the heavy bolter"), in Musou Mode and in Classic PvP alike: only those profiles' lists carry the autocannon, and every name of the data stays its own.
/// </summary>
public static partial class AppearanceResolver
{
    static partial void ExtrasWornAs(ref string wargear) => wargear = Arsenal.WornAs(wargear);
}
