using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Extras.Musou;

namespace SmClient.Customizer;

/// <summary>
/// Musou Mode's customizer layout (owner, 2026-11-10; <see cref="MusouCustomizer"/>): whether the screens show it, and the one side they edit. Musou Mode's players are always Space Marines
/// (<c>World.Musou</c> sets the side), so the creator and the appearance editor offer the Space Marine side alone there. Classic PvP is untouched: nothing here acts unless the content profile
/// the customizer serves is Musou's, and <c>--musou-old-customizer</c> brings back the shared layout in Musou too.
/// </summary>
public sealed partial class CustomizerServices
{
    /// <summary><c>--musou-old-customizer</c> (restore switch): Musou Mode's customizer is the shared Classic one again (the faction row, both sides).</summary>
    public static readonly bool MusouOldCustomizer = OS.GetCmdlineUserArgs().Contains("--" + MusouCustomizer.OldCustomizerFlag);

    /// <summary>The screens show Musou Mode's layout now: the profile is Musou's (its pages in the front end, a Musou match's picker) and the restore switch is off.</summary>
    public bool MusouLayout => MusouCustomizer.Uses(Content, MusouOldCustomizer);

    partial void ExtrasFactions(ref List<(string Race, string Label)> factions)
    {
        if (!MusouLayout) return;
        factions = factions.Where(f => f.Race == CustomizationCatalog.SpaceMarine).ToList();
    }
}
