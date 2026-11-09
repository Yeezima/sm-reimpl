using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Customization;
using SmContent.Extras.Musou;

namespace SmClient.Customizer;

/// <summary>
/// Musou Mode's customizer layout (owner, 2026-11-10; <see cref="MusouCustomizer"/>): whether the screens show it, and the one side they edit. Musou Mode's players are always Space Marines
/// (<c>World.Musou</c> sets the side), so the creator and the appearance editor offer the Space Marine side alone there. Classic PvP is untouched: nothing here acts unless the content profile
/// the customizer serves is Musou's, and <c>--musou-old-customizer</c> brings back the shared layout in Musou too.
/// </summary>
/// <remarks>
/// musou/loadout-slots (2026-11-10, the owner: "Musou own loadout spans approved"): in the Musou layout the screens edit Musou's own loadout slots and the Librarian's loadout
/// (<see cref="MusouProfileView"/>, <see cref="MusouSlots"/>), not Classic's. <c>--musou-shared-slots</c> (restore switch) and <c>--musou-old-customizer</c> keep the shared slots of before.
/// </remarks>
public sealed partial class CustomizerServices
{
    /// <summary><c>--musou-old-customizer</c> (restore switch): Musou Mode's customizer is the shared Classic one again (the faction row, both sides).</summary>
    public static readonly bool MusouOldCustomizer = OS.GetCmdlineUserArgs().Contains("--" + MusouCustomizer.OldCustomizerFlag);

    /// <summary><c>--musou-shared-slots</c> (restore switch, musou/loadout-slots): Musou's pages edit Classic's loadout slots again, as before they had their own.</summary>
    public static readonly bool MusouSharedSlots = OS.GetCmdlineUserArgs().Contains("--" + MusouCustomizer.SharedSlotsFlag);

    /// <summary>The screens show Musou Mode's layout now: the profile is Musou's (its pages in the front end, a Musou match's picker) and the restore switch is off.</summary>
    public bool MusouLayout => MusouCustomizer.Uses(Content, MusouOldCustomizer);

    /// <summary>The screens edit Musou's own slots (the Musou layout, without <c>--musou-shared-slots</c>).</summary>
    public bool MusouOwnSlots => MusouLayout && !MusouSharedSlots;

    /// <summary>The Librarian's page is open: the view shows his loadout as a class too (<see cref="MusouProfileView.WithLibrarian"/>). Set by the creator; then <see cref="ApplyProfileView()"/>.</summary>
    public bool MusouLibrarianShown { get; set; }

    partial void ExtrasFactions(ref List<(string Race, string Label)> factions)
    {
        if (!MusouLayout) return;
        factions = factions.Where(f => f.Race == CustomizationCatalog.SpaceMarine).ToList();
    }

    partial void ExtrasProfileView(ref IProfileView? view)
    {
        if (!MusouOwnSlots) return;
        view = MusouLibrarianShown ? MusouProfileView.WithLibrarian : MusouProfileView.Marines;
    }
}
