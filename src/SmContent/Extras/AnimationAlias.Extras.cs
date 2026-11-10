using SmContent.Extras.Musou.Arsenal;

namespace SmContent;

/// <summary>
/// The animation names' added-content part (docs/added-content.md): the one implementation of <see cref="ExtrasAnimationAlias"/>, a dispatcher over each added content's own weapons.
/// Musou: its arsenal's (<see cref="Arsenal.AliasOf"/>, W1 to W3). None of those names is an install name, so every name of the data stays its own.
/// </summary>
public static partial class AnimationAlias
{
    static partial void ExtrasAnimationAlias(ref string wargear) => wargear = Arsenal.AliasOf(wargear);
}
