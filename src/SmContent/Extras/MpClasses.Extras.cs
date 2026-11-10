using SmContent.Extras.Musou.Arsenal;

namespace SmContent;

/// <summary>
/// The melee maps' added-content part (docs/added-content.md): the one implementation of <see cref="ExtrasMeleeMap"/>, a dispatcher over each added content's own melee weapons.
/// Musou: its arsenal's (<see cref="Arsenal.MeleeMap"/>: the claw and the spear, W2 and W3). Those names reach a config only under Musou (<see cref="Arsenal.Append"/>), and none is an
/// install name, so a map is never found for anything else.
/// </summary>
public sealed partial record MpClasses
{
    static partial void ExtrasMeleeMap(string wargear, ref MeleeTuning.WeaponMap? map) => map ??= Arsenal.MeleeMap(wargear);
}
