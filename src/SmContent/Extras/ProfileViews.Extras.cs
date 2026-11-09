using SmContent.Extras.Musou;

namespace SmContent.Customization;

/// <summary>
/// The profile helpers' added-content part (docs/added-content.md): the one implementation of <see cref="ExtrasLookRemoved"/>, a dispatcher to each added content that keeps references to looks
/// in the profile's extras. Musou: the Librarian's own look (<see cref="MusouSlots.LookRemoved"/>).
/// </summary>
public static partial class ProfileViews
{
    static partial void ExtrasLookRemoved(ref CustomizationProfile profile, string id) => profile = MusouSlots.LookRemoved(profile, id);
}
