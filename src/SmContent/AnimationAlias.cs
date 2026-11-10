namespace SmContent;

/// <summary>
/// The wargear name the animation rules read (W0, docs/new-weapons-design.md section 8, hook 5). The client's animation families are chosen by name (<see cref="HipFireAnimations.FamilyOf"/>,
/// <see cref="LocomotionAnimations.SprintFamilyOf"/>, <see cref="ReloadAnimations.FamilyOf"/>, <see cref="FlinchRules.SetFor"/>, the falls through the hip-fire family, the client's hammer stance);
/// a weapon only added content carries borrows a data weapon's name for them (Musou's arsenal: the autocannon the heavy bolter's, the claw the power sword's, the spear the thunder hammer's).
/// Every name of the install is its own.
/// </summary>
public static partial class AnimationAlias
{
    /// <summary>The name the animation rules use for <paramref name="wargear"/>: itself, or the data weapon an added one borrows its animations from.</summary>
    public static string Of(string wargear)
    {
        string name = wargear;
        ExtrasAnimationAlias(ref name);   // EXTRAS-HOOK added content (Musou's arsenal): its weapons' animations are a data weapon's
        return name;
    }

    /// <summary>Added content: the data weapon whose animations an added weapon plays; nothing without added content.</summary>
    static partial void ExtrasAnimationAlias(ref string wargear);
}
