namespace SmClient;

/// <summary>
/// Reined-in weapon smoke (branch feel/muzzle-smoke; owner, 2026-10-29: "the smoke effects from firing a gun are too spread out from the exact line of travel out of the barrel.
/// Rein them in a bit.").
/// <para>
/// What was checked first (the data and our reading of it). The muzzle locators' +Y is the barrel (the effects are placed on it: the own muzzle is a few degrees off the aim), the
/// spray deviations read as radians (the values are 90, 10, 2 and 4 degrees as radians: 1.571, 0.175, 0.035, 0.07; as degrees they would be fractions of one), the particles do not
/// inherit the player's velocity, and the effects' axes are the barrel's, not the world's up. So nothing was misread; the spread is in the data's own elements: a bullet's
/// tracer effect carries <c>smoke thin</c> beams (<c>alpha_trail_curvy</c> / <c>alpha_trail_soft</c>) whose width grows over their life to 0.99 m for the bolter (x1.5 scale: 1.5 m)
/// and 1.5 m for the storm bolter's soft smoke, the ignition puffballs (<c>lingering_smoke_cloud</c>) along the line, the heavy bolter's muzzle puff is a spray over a whole
/// hemisphere (deviation 90 degrees, 1 to 2.3 m/s) around the barrel, and the stop-fire <c>bolter_smoke</c> streamers spray 10 degrees. (Whether a beam's <c>Width</c> is its full
/// width or half of it is not established: the full width is what is drawn, as for the tracers.)
/// </para>
/// <para>
/// The change (the owner's, ours): for the effects a weapon plays when it fires (muzzle, tracer, stop-fire smoke) the smoke elements keep <see cref="SmokeReinFactor"/> of their width, radius
/// and sideways speed (<see cref="FxEffect.SmokeRein"/>, <c>FxPlayer</c>): smoke beams, streamers and puffballs narrower, the heavy bolter's puff and the stop-fire smoke
/// kept closer to the barrel's line. Flashes, shells, tracer streaks, impacts and every other effect are untouched. <c>--old-smoke</c> restores the data's look
/// (<c>--smoke-rein f</c> sets the fraction).
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary>The fraction of its width, radius and sideways speed a weapon's smoke element keeps (the owner's "rein them in a bit": 0.55 = 45 % less lateral spread; ours).</summary>
    public const float SmokeReinFactor = 0.55f;

    float? _smokeRein;

    /// <summary>What a weapon's smoke effects are played with: 1 with <c>--old-smoke</c> (the data's look), else <c>--smoke-rein f</c> or <see cref="SmokeReinFactor"/>.</summary>
    float SmokeRein => _smokeRein ??= CmdFlag("old-smoke") ? 1f : System.Math.Clamp(FloatArg("smoke-rein") ?? SmokeReinFactor, 0.05f, 1f);
}
