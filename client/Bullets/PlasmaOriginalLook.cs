using System;

namespace SmClient.Bullets;

/// <summary>
/// plasma-original-fx (owner, 2026-11-09, Musou Mode and Classic PvP: "make the plasma projectiles for plasma gun and plasma pistol to be the visuals from the original game files"; client
/// presentation only, no simulation, wire, protocol or config-hash change; engine-free, tested).
/// <para>
/// What the install binds (docs/discovery.md "Plasma projectile visuals"): the light shot of the plasma gun (primary) and the plasma pistol (secondary) plays the data tracer
/// <c>plasma_gun_tracer</c> (Chaos <c>chaos_plasma_gun_tracer</c>) from <c>fx_muzzle</c> to the hit, in its <c>hit_fx</c>; the charged shot launches a projectile blueprint
/// (<c>plasma_altfire_mp</c>, <c>plasma_pistol_altfire_mp</c>, Chaos <c>plasma_altfire_chaos_mp</c>, <c>plasma_pistol_altfire_chaos_mp</c>) whose model is <c>invisible_projectile</c>
/// (no mesh) and whose whole look is its <c>lifetime_action</c> effect at scale 1: <c>plasma_gun_charged_trail</c> (Chaos <c>chaos_plasma_gun_trail</c>), a soft cyan billboard ball, a
/// distortion ball, two streamers, sparks and a light.
/// </para>
/// <para>
/// The original look is now the default: the data tracer with its own colours and sizes for the light shot, the data trail alone for the charged shot. Gone by default: the light ball
/// (<see cref="PlasmaLightShot"/>), the recoloured thin bolt (<see cref="PlasmaBolt"/>), the solid roiling ball mesh and the softened ribbon head (<c>FxEffect.SoftHead</c>), all ours.
/// <c>--old-plasma-look</c> restores all of them.
/// </para>
/// <para>
/// One reading of the data changes (interpretation, <see cref="StreamerWidth"/>): a streamer's width keys run along the ribbon, head (0) to tail (1), like its colour keys, not over the
/// element's loop time. The charged trail's <c>blue trail</c> is 1 m wide at the ball and 6 m at its tail, where its colour has faded to black; read over loop time it was a 6 m slab once
/// a second (the "slab" of fix/plasma-trail-slab).
/// </para>
/// </summary>
public static class PlasmaOriginalLook
{
    /// <summary>Whether the original look is drawn (default) or the look before plasma-original-fx (<c>--old-plasma-look</c>).</summary>
    public static bool Original(bool oldLookFlag) => !oldLookFlag;

    /// <summary>Whether a projectile trail's streamers read their width along the ribbon: the plasma gun's and pistol's charged trails, either side, in the original look. The plasma cannon keeps the old reading (not in this pass).</summary>
    public static bool WidthAlongTrail(string wargear, bool original) =>
        original && PlasmaBolt.IsBoltWeapon(wargear);

    /// <summary>
    /// A streamer segment's width (before the effect's scale), at <paramref name="along"/> (0 head .. 1 tail) of the ribbon and <paramref name="loopTime"/> (0..1) of the element's
    /// duration, given its width curve. Along the trail: the curve at the segment's place. Otherwise (the old reading): the curve at loop time, tapered to 0.3 at the tail.
    /// </summary>
    public static float StreamerWidth(Func<float, float> widthCurve, float along, float loopTime, bool alongTrail) =>
        alongTrail ? MathF.Max(0f, widthCurve(Math.Clamp(along, 0f, 1f))) : widthCurve(loopTime) * (1f - 0.7f * along);

    /// <summary>
    /// The Compatibility renderer's safety streak under the light shot's data tracer (fx/plasma-tracers-safe-mode's mitigation, kept: ours) takes the data tracer's soft smoke head colour
    /// in the original look (0..1 RGB): loyalist (113, 255, 255) from <c>plasma_gun_tracer</c>; Chaos the one Chaos plasma hue (<c>SmContent.PlasmaColour.ChaosPurple</c>, the same as the tinted data tracer: the data's smoke (224, 101, 255) differed from it).
    /// </summary>
    public static (float R, float G, float B) SafeStreakColour(bool chaos) => chaos ? SmContent.PlasmaColour.ChaosPurple : (113f / 255f, 1f, 1f);
}
