using SmContent.Customization;

namespace SmContent.Extras.Musou;

/// <summary>
/// The Librarian's look (Musou Mode only; docs/musou-expansion.md "Librarian", the second pass): the install's multiplayer Librarian armour set worn over the player's own Tactical look. The install has the
/// packages <c>sm_armour_mp_librarian_torso</c> (with <c>_belt</c>, the torso's second visual as on every MP torso), <c>_shldrr</c> (the right pad) and <c>_backpack</c> (the psychic hood and power pack,
/// <c>lib_backpack</c>), each with the usual texture packages (<c>_wg</c>, <c>_tt1/_tt2</c>, <c>_tsf/_tsh/_tst</c>, <c>_v</c>), but no <c>mp_armor_set</c> lists them and no class offers them, so the
/// customization catalogue never reads them: they are put on here by package name, in the slots their names say, and painted with the look's own paint of those slots (the scheme's colours, as any piece).
/// The other six slots (helmet, left pad, arms, legs) stay the player's. *Original asset loaded*; which pieces the original's Librarian (singleplayer/DLC data, not a playable MP class) wore together is
/// not in the data (kind: ours).
/// </summary>
public static class LibrarianLook
{
    /// <summary>The pieces put on: (armour slot, wargear name of the install's attribute file, visual packages in the data's torso order: torso, then belt).</summary>
    public static readonly IReadOnlyList<(string Slot, string Name, string[] Packages)> Pieces =
    [
        ("torso", "sm_librarian_torso", ["sm_armour_mp_librarian_torso", "sm_armour_mp_librarian_belt"]),
        ("rightshoulder", "sm_librarian_shoulder_right", ["sm_armour_mp_librarian_shldrr"]),
        ("backpack", "sm_librarian_backpack", ["sm_armour_mp_librarian_backpack"]),
    ];

    /// <summary>
    /// <paramref name="look"/> with the Librarian's pieces in place of its torso, right pad and backpack (same order; a slot the look hid, a jump pack's back, is shown: the Librarian has no pack). The pad's
    /// decal stays as resolved (the drawing puts a decal only on surfaces with a decal layout). A predefined set is worn under them the same way.
    /// </summary>
    public static ResolvedAppearance Dress(ResolvedAppearance look)
    {
        var pieces = new List<ResolvedPiece>(look.Pieces.Count + Pieces.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in look.Pieces)
        {
            int k = IndexOf(piece.Slot);
            if (k < 0) { pieces.Add(piece); continue; }
            if (placed.Add(piece.Slot)) pieces.Add(Piece(k));
        }
        for (int k = 0; k < Pieces.Count; k++)
            if (placed.Add(Pieces[k].Slot)) pieces.Add(Piece(k));
        var hidden = new HashSet<string>(look.HiddenSlots, StringComparer.Ordinal);
        foreach (var p in Pieces) hidden.Remove(p.Slot);
        return look with { Pieces = pieces, HiddenSlots = hidden };
    }

    /// <summary>Whether <paramref name="look"/> wears the Librarian's pieces.</summary>
    public static bool Wears(ResolvedAppearance look) => Pieces.All(p => look.Piece(p.Slot)?.Name == p.Name);

    static int IndexOf(string slot)
    {
        for (int k = 0; k < Pieces.Count; k++) if (Pieces[k].Slot == slot) return k;
        return -1;
    }

    static ResolvedPiece Piece(int k) => new(Pieces[k].Slot, Pieces[k].Name, Pieces[k].Packages, Forced: true);
}
