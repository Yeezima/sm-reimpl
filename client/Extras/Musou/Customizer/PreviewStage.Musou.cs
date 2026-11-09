using System;
using SmContent.Customization;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient.Customizer;

/// <summary>
/// The Librarian in the customizer's preview (Musou Mode only; owner, 2026-11-10): the look he wears in a match, dressed as the match dresses it (<see cref="LibrarianLook.Dress"/>: the install's
/// multiplayer Librarian torso and belt, right pad and psychic hood over the player's own look; *original asset loaded*), with the force sword in hand and no gun or pistol. The creator's Librarian
/// page sets <see cref="MusouLibrarian"/>; nothing changes unless the customizer serves the Musou profile.
/// </summary>
public sealed partial class PreviewStage
{
    bool _musouLibrarian;

    /// <summary>The preview draws the Librarian (the creator's Librarian page in Musou Mode); false everywhere else.</summary>
    public bool MusouLibrarian
    {
        get => _musouLibrarian;
        set { if (_musouLibrarian == value) return; _musouLibrarian = value; _dirty = true; }
    }

    bool DrawsLibrarian => _musouLibrarian && _services.Content.HasFlag(ContentExtras.Musou);

    /// <summary>The model on stage wears the Librarian's pieces (test aid).</summary>
    public bool ShowsLibrarian => DrawsLibrarian && _shownKey.EndsWith("|librarian", StringComparison.Ordinal);

    partial void ExtrasDress(ref Func<ResolvedAppearance, ResolvedAppearance>? dress, ref string key)
    {
        if (!DrawsLibrarian) return;
        dress = LibrarianLook.Dress;
        key += "|librarian";
    }

    partial void ExtrasWeapons(ref string? primary, ref string? pistol, ref string? melee, ref bool meleeInHand)
    {
        if (!DrawsLibrarian) return;
        // His class carries no gun and no pistol; the force sword is the power sword (MusouLoadout.AppendLibrarian), in hand as on a jump-pack class.
        primary = null;
        pistol = null;
        melee = LibrarianRules.PowerSwordWargear;
        meleeInHand = true;
    }
}
