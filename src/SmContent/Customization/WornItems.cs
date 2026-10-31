namespace SmContent.Customization;

/// <summary>
/// Which worn (bone-following) attachments of a character's wargear are drawn. The heavy classes' fixed sidearm (<c>sm_mp_pistol_bolt_pistol_devastator</c> and its Chaos copy) carries an
/// attachment "backpack" showing the loyalist <c>sm_lascannon_backpack_001</c>, but the back of those classes is the armour's backpack slot, which the class overrides set
/// (the Devastator's pack for the chosen weapon, <c>csm_backpack_havoc</c>). Drawing the sidearm's copy too put the loyalist pack on the Havoc (owner, 2026-11-01). **Interpretation:** the slot wins.
/// </summary>
public static class WornItems
{
    /// <summary>Whether a worn attachment is drawn: the worn pack wargear's parts always, another wargear's unless it is a <c>backpack</c> attachment.</summary>
    /// <param name="attachment">The attachment's key in the wargear file.</param>
    /// <param name="packWargear">The attachment belongs to the jump pack (worn wargear), not to a weapon.</param>
    public static bool Draws(string attachment, bool packWargear) => packWargear || !attachment.Equals("backpack", StringComparison.OrdinalIgnoreCase);
}
