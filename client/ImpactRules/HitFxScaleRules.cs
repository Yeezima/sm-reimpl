using System;

namespace SmClient.ImpactRules;

/// <summary>
/// onhit-range-intensity (owner, 2026-11-13: "i'm not seeing at range when shooting with lascannon and stalker bolter any on hit effects - armor and health yellow and red splashes and particles
/// respectively ... in general increase the intensity of both by 30%"; client presentation only, no wire, sim or config-hash change). Two rules, both ours (refine register):
/// <list type="number">
/// <item><b>Range</b>: every on-player hit effect (the armour's yellow hit spot, the red break and bare-armour glow, the armour-hit and health-hit particles, the sparks) was a fixed size in metres, so at
/// 40 to 80 m it covered a few pixels or none. It is now kept at least as large on screen as it is at <see cref="RefDistance"/> m with the <see cref="RefFov"/> degree view
/// (<see cref="RangeScale"/>: the distance times the tangent of half the camera's field of view, over the same product at the reference), up to <see cref="MaxSpot"/> times for the hit spot, <see cref="MaxParticle"/> for particles and
/// <see cref="MaxGlow"/> for the big red glow card. A scoped view (the Stalker's 10 degrees, the lascannon's 5.75) is already magnified, so it grows little or none.</item>
/// <item><b>Intensity</b>: every one of these effects is <see cref="Intensity"/> (1.3) times as strong: shader brightness of the yellow hit glow and the red break and bare-armour glow, and the particle
/// effects' size (their count is the data's).</item>
/// </list>
/// <c>--old-hit-fx-range</c> and <c>--old-hit-fx-intensity</c> restore either alone.
/// </summary>
public static class HitFxScaleRules
{
    /// <summary>Owner 2026-11-13: both effects 30 % stronger.</summary>
    public const float Intensity = 1.3f;
    /// <summary>The distance (m) and vertical field of view (degrees) at which an effect keeps its data size on screen: the close-range look the owner sees working.</summary>
    public const float RefDistance = 12f, RefFov = 70f;
    /// <summary>The most an effect grows with range: the yellow hit spot, the particle effects (bigger ones fill the screen), and the red glow card (a 2.5 m quad that is already seen at 80 m).</summary>
    public const float MaxSpot = 6f, MaxParticle = 3f, MaxGlow = 1.5f;
    public const string OldRangeFlag = "old-hit-fx-range", OldIntensityFlag = "old-hit-fx-intensity";

    /// <summary>The growth (at least 1) that keeps an effect's size on screen from falling below its size at the reference: distance and field of view of the camera that sees it.</summary>
    public static float RangeScale(float distance, float fovDegrees, float max)
    {
        if (!float.IsFinite(distance) || !float.IsFinite(fovDegrees) || fovDegrees <= 0f || fovDegrees >= 179f) return 1f;
        float seen = distance * MathF.Tan(fovDegrees * MathF.PI / 360f);
        float reference = RefDistance * MathF.Tan(RefFov * MathF.PI / 360f);
        return Math.Clamp(seen / reference, 1f, max);
    }

    /// <summary>The factor on a particle effect's size: the range growth times the intensity, each unless its restore switch is set.</summary>
    public static float ParticleScale(float distance, float fovDegrees, bool oldRange, bool oldIntensity) =>
        (oldRange ? 1f : RangeScale(distance, fovDegrees, MaxParticle)) * (oldIntensity ? 1f : Intensity);
}
