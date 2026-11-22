using System.Text.Json.Nodes;
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
public static partial class ProfileViews
{
    /// <summary>
    /// <paramref name="profile"/> without the look <paramref name="id"/>: every class that wore it, in the profile's own slots and in every loadout set (<see cref="CustomizationProfile.Sets"/>),
    /// falls back to the race's default look, and added content clears its own references (<see cref="ExtrasLookRemoved"/>: the Musou Librarian's look). So no reference is left to a look a
    /// later new look could take the id of (review of musou/loadout-slots: the lowest free id is reused).
    /// </summary>
    public static CustomizationProfile RemoveLook(CustomizationProfile profile, string id)
    {
        var appearances = profile.Appearances.Where(a => a.Id != id).ToList();
        var result = profile with { Appearances = appearances, Classes = DropMissingLooks(profile.Classes, appearances) };
        foreach (var (name, set) in profile.Sets)
        {
            if (set.Raw is not null)
            {
                if (ClearRawLook(set.Raw, id) is { } rawCleared) result = result.WithSet(name, set with { Raw = rawCleared });   // a set this build keeps unread must not point at the id either
            }
            else if (DropMissingLooks(set.Classes, appearances) is var cleared && !ReferenceEquals(cleared, set.Classes))
                result = result.WithSet(name, set with { Classes = cleared });
        }
        ExtrasLookRemoved(ref result, id);   // EXTRAS-HOOK: added content's own references to the look
        return result;
    }

    /// <summary>A kept-as-written set's JSON (<see cref="LoadoutSet.Raw"/>) without its classes' references to the look <paramref name="id"/> (set to null: the default look); null when it holds none.</summary>
    static string? ClearRawLook(string raw, string id)
    {
        if (JsonNode.Parse(raw) is not JsonObject set || set["classes"] is not JsonArray classes) return null;
        bool changed = false;
        foreach (var c in classes)
            if (c is JsonObject cls && cls["appearance"] is JsonObject look)
                foreach (var race in look.Select(kv => kv.Key).ToList())
                    if (look[race] is JsonValue v && v.TryGetValue<string>(out var text) && text.Trim() == id) { look[race] = null; changed = true; }
        return changed ? set.ToJsonString() : null;
    }

    /// <summary>
    /// Hook for added content (docs/added-content.md; src/SmContent/Extras/ProfileViews.Extras.cs): clears the content's own references to the look <paramref name="id"/> in
    /// <see cref="CustomizationProfile.Extras"/> (the Musou Librarian's look). Leaves the profile as it is without added content.
    /// </summary>
    static partial void ExtrasLookRemoved(ref CustomizationProfile profile, string id);

    /// <summary>
    /// The catalogue loadout set <paramref name="set"/> is read and repaired with (<see cref="ProfileStore"/>): the content's own that owns the set (Musou's set lists Musou's arsenal,
    /// docs/new-weapons-design.md section 8, hook 3), else <paramref name="catalog"/> itself. The profile's own slots (Classic PvP's) are always read with the data's.
    /// </summary>
    public static CustomizationCatalog SetCatalog(string set, CustomizationCatalog catalog)
    {
        var result = catalog;
        ExtrasSetCatalog(set, ref result);   // EXTRAS-HOOK added content (Musou's arsenal): its set keeps its own items
        return result;
    }

    /// <summary>Hook for added content: the catalogue of the content that owns loadout set <paramref name="set"/>; the data's without added content.</summary>
    static partial void ExtrasSetCatalog(string set, ref CustomizationCatalog catalog);

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
