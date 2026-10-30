namespace SmContent.Customization;

/// <summary>
/// Which armour slot's paint a worn jump pack takes (the Assault Marine's and the Chaos Raptor's pack, a model of its own: <c>sm_jumppack_mp_basic_001</c>,
/// <c>csm_jumpack_mp_basic</c>). **Owner's decision (2026-10-30, replacing the chest rule of 2026-10-29):** the pack follows the colours of the **backpack** slot.
/// <para>
/// What the data says: the jump pack ability (<c>sm_abil_jumppack</c>) removes the wargear of slot <c>backpack</c> and disables it, so the pack takes the
/// backpack's place; the data's own binding is the paint scheme's <c>backpack</c> entry. The customizer lists the Backpack under Colours on the jump-pack classes
/// (the piece is not drawn there, its colours paint the pack). In both default schemes (<c>sm_ultramarines</c>, <c>chaos_multiplayer</c>) the backpack and chest
/// entries are the same colours, so the default look does not change. Kind: owner's decision.
/// </para>
/// </summary>
public static class WornPackPaint
{
    /// <summary>The paint slot a worn jump pack follows.</summary>
    public const string Slot = "backpack";

    /// <summary>
    /// The paint of the worn jump pack of <paramref name="look"/>: the backpack's, or null when the look has no backpack paint (the pack then keeps the side's default
    /// scheme, as before). A single-set look (<c>"default"</c> entry only) paints the pack with that.
    /// </summary>
    public static ResolvedPaint? Of(ResolvedAppearance look) =>
        look.Paint.TryGetValue(Slot, out var pack) ? pack : look.Paint.GetValueOrDefault("default");
}
