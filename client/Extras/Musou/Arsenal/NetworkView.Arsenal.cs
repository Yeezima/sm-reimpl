using SmContent;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>
/// Musou's arsenal in the networked view (W0, the base; docs/new-weapons-design.md section 8, hook 4, and the animation seam): the one implementation of <see cref="ExtrasThrownVisual"/>, a dispatcher to the thrown
/// item's own model, which W4 implements in its own file (<c>Arsenal/Charge/</c>: the Remote Detonators drawn as the melta bomb, not the blueprint's Ork stikkbomb). Asked only in a Musou match
/// (<see cref="ContentExtras.Musou"/> in the match's config: the charge is an install name, <c>sm_mp_eq_demolition_charge</c>) and only for the arsenal's names. Until then the item is drawn with
/// its blueprint's model, as before.
/// </summary>
public partial class NetworkView
{
    partial void ExtrasThrownVisual(ThrownSpec item, ref ProjectileVisual? visual)
    {
        if (_config is null || !_config.Extras.HasFlag(ContentExtras.Musou) || !ArsenalRules.IsArsenal(item.Name)) return;
        RemoteDetonatorsVisual(item, ref visual);
    }

    /// <summary>W4 (<c>Arsenal/Charge/</c>): the charge's model in flight and at rest.</summary>
    partial void RemoteDetonatorsVisual(ThrownSpec item, ref ProjectileVisual? visual);

    // ---- the animation seam (W0; lead 2026-11-10: the owner wants custom animations for the claw and the spear, authored to the design's hit times, approved before use)

    /// <summary>
    /// The one place a custom clip drops in (<see cref="ExtrasMeleeClip"/>): the claw's and the spear's strikes play the power sword's and the thunder hammer's clips as stand-ins (their maps,
    /// <c>MpClasses.ExtrasMeleeMap</c>, and their clip lists through <c>AnimationAlias</c>) until an entry here gives a strike its own clip and rate. The simulation never reads it: the hits,
    /// windows and durations stay the design's. Each weapon's table is its own milestone's file.
    /// </summary>
    partial void ExtrasMeleeClip(string wargear, bool heavy, int step, ref string? clip, ref float rate)
    {
        if (_config is null || !_config.Extras.HasFlag(ContentExtras.Musou) || !ArsenalRules.IsArsenal(wargear)) return;
        LightningClawClip(wargear, heavy, step, ref clip, ref rate);
        PowerSpearClip(wargear, heavy, step, ref clip, ref rate);
    }

    /// <summary>W2 (<c>Arsenal/Claw/</c>): the claw's custom clip of a strike, once approved; none: the sword's clip at 1.25.</summary>
    partial void LightningClawClip(string wargear, bool heavy, int step, ref string? clip, ref float rate);

    /// <summary>W3 (<c>Arsenal/Spear/</c>): the spear's custom clip of a strike, once approved; none: the hammer's clip at 0.85.</summary>
    partial void PowerSpearClip(string wargear, bool heavy, int step, ref string? clip, ref float rate);
}
