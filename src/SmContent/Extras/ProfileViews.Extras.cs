using SmContent.Extras.Musou;

namespace SmContent.Customization;

/// <summary>
/// The profile helpers' added-content part (docs/added-content.md): the one implementation of <see cref="ExtrasLookRemoved"/>, a dispatcher to each added content that keeps references to looks
/// in the profile's extras, and of <see cref="ExtrasSetCatalog"/>, the catalogue a content's own loadout set is read with. Musou: the Librarian's own look (<see cref="MusouSlots.LookRemoved"/>);
/// its set's catalogue (<see cref="MusouSlots.SetCatalog"/>: Musou's arsenal).
/// </summary>
public static partial class ProfileViews
{
    static partial void ExtrasLookRemoved(ref CustomizationProfile profile, string id) => profile = MusouSlots.LookRemoved(profile, id);

    // Musou's arsenal (W0): Musou's set is read with Musou's catalogue, so a Musou slot keeps an arsenal item.
    static partial void ExtrasSetCatalog(string set, ref CustomizationCatalog catalog) => catalog = MusouSlots.SetCatalog(set, catalog);
}
