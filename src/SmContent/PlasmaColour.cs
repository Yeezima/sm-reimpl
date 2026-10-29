namespace SmContent;

/// <summary>
/// <b>The Chaos plasma is purple, the loyalists' is blue</b> (owner's knowledge, 2026-10-28: "the plasma of the chaos marines is purple while the marines is blue").
/// The install already says so for most of it: the Chaos plasma gun, pistol and cannon play their own <c>chaos_plasma_*</c> / <c>csm_plasma_*</c> effects (muzzle,
/// tracer, projectile trails, overheat, vents: purple and magenta keys) next to the loyalists' <c>plasma_*</c> ones (cyan, blue and green keys). Verified-local
/// with <c>smtool race-parity auto --fx-colours plasma</c>. But the Chaos weapons' <b>charged-shot and some impact containers</b> are shared with the
/// loyalists' and still play loyalist-named <c>plasma_*</c> effects (the charged blast <c>plasma_gun_charged_dmg</c>, <c>plasma_gun_mp_charged_dmg</c>, the
/// <c>plasma_dmg_*</c> body and armour impacts). Those are recolured purple when a Chaos weapon plays them
/// (<see cref="TintFor"/>; the client passes it to <c>FxEffect.Tint</c>, which recolours sprites, beams, streamers, lights and additive glow decals): ours, applied only where the data are not already Chaos.
/// The charging glow (<c>plasma_charging*</c>, <c>plasma_gun_innate_secondary</c>) is covered by the rule too, but the client does not play charging, innate or heat effects at all yet, so nothing shows it.
/// </summary>
public static class PlasmaColour
{
    /// <summary>The Chaos plasma's purple (0..1 RGB; ours, taken from the Chaos effects' own purple keys, e.g. (216, 60, 255), (224, 101, 255), (167, 21, 232)).</summary>
    public static readonly (float R, float G, float B) ChaosPurple = (0.85f, 0.36f, 1f);

    /// <summary>The placeholder plasma ball's colour for each side (client placeholder projectiles): blue-white for the loyalists (as before), purple for Chaos.</summary>
    public static (float R, float G, float B) BallColour(bool chaos) => chaos ? (0.82f, 0.5f, 1f) : (0.6f, 0.85f, 1f);

    /// <summary>The loyalist effects that stay neutral on a Chaos weapon: metal sparks (white and grey in the data), not a plasma colour.</summary>
    static readonly HashSet<string> Neutral = new(StringComparer.OrdinalIgnoreCase) { "plasma_pistol_charged_pen_metal" };

    /// <summary>Whether a wargear file is a Chaos plasma weapon (<c>csm_mp_ranged_plasma_gun</c>, <c>csm_mp_pistol_plasma_pistol</c>, <c>csm_mp_exotic_plasma_cannon</c>).</summary>
    public static bool IsChaosPlasma(string wargear) =>
        wargear.StartsWith("csm_", StringComparison.OrdinalIgnoreCase) && wargear.Contains("plasma", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tint (0..1 RGB) a Chaos plasma weapon gives the effect it plays, or null (the data's colours): the effect is a loyalist plasma effect (named
    /// <c>plasma_*</c>; the Chaos ones are <c>chaos_*</c> / <c>csm_*</c> and already purple), not metal sparks. Other weapons, other races and the
    /// shared non-plasma effects (<c>bolter_dmg_*</c>, <c>light_led_*</c>) are never tinted.
    /// </summary>
    public static (float R, float G, float B)? TintFor(string wargear, string effect) =>
        IsChaosPlasma(wargear) && effect.StartsWith("plasma_", StringComparison.OrdinalIgnoreCase) && !Neutral.Contains(effect) ? ChaosPurple : null;
}
