namespace SmContent.Customization;

/// <summary>
/// A projection of the profile the screens edit (musou/loadout-slots, 2026-11-10): an added mode shows its own loadout slots (<see cref="CustomizationProfile.Sets"/>) as the profile's
/// <see cref="CustomizationProfile.Classes"/> and <see cref="CustomizationProfile.Selected"/>, so the creator, the appearance editor and a Join work on them unchanged. The stored profile (what is
/// written) keeps both: <see cref="Show"/> gives the screens their profile, <see cref="Keep"/> folds the screens' edited profile back into the stored one. The client's profile session holds the view;
/// without one the screens edit the stored profile itself (Classic PvP).
/// </summary>
public interface IProfileView
{
    /// <summary>The stored profile as the screens see it.</summary>
    CustomizationProfile Show(CustomizationProfile stored);

    /// <summary>The stored profile with the screens' profile <paramref name="shown"/> (an edit of <see cref="Show"/>'s) folded back into it.</summary>
    CustomizationProfile Keep(CustomizationProfile stored, CustomizationProfile shown);

    /// <summary>
    /// The stored profile as it is once the view is in use (a mode's own slots made from the profile's the first time); the session applies it to the saved and the working profile alike,
    /// so it marks nothing unsaved. The stored profile itself when nothing needs making.
    /// </summary>
    CustomizationProfile Open(CustomizationProfile stored) => stored;
}

/// <summary>Helpers the views share.</summary>
public static class ProfileViews
{
    /// <summary>
    /// <paramref name="classes"/> with every look reference that <paramref name="appearances"/> no longer holds cleared (the default look), the same list when none is: the screens deleted a look
    /// that classes outside their view still named.
    /// </summary>
    public static IReadOnlyList<CustomClass> DropMissingLooks(IReadOnlyList<CustomClass> classes, IReadOnlyList<AppearanceProfile> appearances)
    {
        bool Missing(string? id) => id is not null && !appearances.Any(a => a.Id == id);
        if (!classes.Any(c => Missing(c.Appearance.SpaceMarine) || Missing(c.Appearance.Chaos))) return classes;
        return classes.Select(c => Missing(c.Appearance.SpaceMarine) || Missing(c.Appearance.Chaos)
            ? c with { Appearance = new AppearanceRefs(Missing(c.Appearance.SpaceMarine) ? null : c.Appearance.SpaceMarine, Missing(c.Appearance.Chaos) ? null : c.Appearance.Chaos) }
            : c).ToList();
    }
}
