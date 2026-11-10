using System;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// Musou Mode's swing arc (owner, 2026-11-10: "can we make slash strokes appear as a flat curved line that matches the cleave of the weapon swings hitbox? so it visually overlaps cleave targets
/// in one cohesive tear line as opposed to many random small lines that don't line up"). One arc per melee swing in place of a stroke on every enemy struck. It is a thin glowing line along the
/// swing's cleave (<see cref="MusouCleaveShape"/>): a circle segment round the swinger as wide as the cleave's cone, level, through the enemies struck (<see cref="Radius"/>: their mean
/// distance, within the cleave's reach) at about their chest height. It sweeps in the swing's direction, holds for a moment, then fades fast. The colours (red, blue) and the 30 m draw limit are the strokes'
/// (<see cref="MusouSlashRules"/>). Client presentation only, Musou only. The shape comes from the cleave's rules; every timing, size and direction here is ours (kind: ours, confidence: low,
/// to refine in a playtest).
/// </summary>
public static class MusouSlashArcRules
{
    /// <summary>Seconds the line takes to sweep from one end of the arc to the other, how long it then holds at full brightness, and how long it takes to fade out.</summary>
    public const float SweepSeconds = 0.12f, HoldSeconds = 0.05f, FadeSeconds = 0.22f;

    /// <summary>Seconds an arc lives in all.</summary>
    public const float Lifetime = SweepSeconds + HoldSeconds + FadeSeconds;

    /// <summary>Seconds the line takes to brighten at the start (so the first frame does not pop).</summary>
    public const float RiseSeconds = 0.03f;

    /// <summary>The line's thickness (metres) for a light step and a heavy (stun) strike; every later light step of a combo is <see cref="StepGrowth"/> thicker, at most <see cref="StepGrowthMost"/> times.</summary>
    public const float LightWidth = 0.16f, HeavyWidth = 0.26f, StepGrowth = 0.08f;
    public const int StepGrowthMost = 4;

    /// <summary>The nearest the line comes to the swinger (metres): enemies struck at its feet still get a line round the body, not a knot.</summary>
    public const float RadiusMin = 1.5f;

    /// <summary>Seconds within which a second report of the same player's same step is the same swing (a cleave reported over two server ticks draws one arc).</summary>
    public const float SameSwingSeconds = 0.25f;

    /// <summary>The arc's height above the swinger's feet when no enemy struck tells it (metres; about an Ork's chest).</summary>
    public const float DefaultHeight = 1.1f;

    /// <summary>The most arcs alive at once (the oldest is replaced).</summary>
    public const int Pool = 32;

    /// <summary>
    /// The Compatibility renderer (no stencil, so no model can be cut out of an arc that has no depth test): the own player's arcs are drawn depth tested instead, every vertex this many metres
    /// nearer the eye along its sight line (its place on the screen unchanged). The Orks the line runs through (about 0.6 m round) do not hide it; the own model, at least 1.5 m in front of the
    /// part of the line behind it, does; a wall or a model farther than this in front of the line hides it too. Ours.
    /// </summary>
    public const float CompatPullMetres = 1f;

    /// <summary>Degrees of arc per segment of the line, and the fewest and most segments an arc has.</summary>
    public const float DegreesPerSegment = 5f;
    public const int SegmentsMin = 12, SegmentsMax = 72;

    static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>How far along the arc the sweeping line has come at <paramref name="age"/> (0 the start, 1 the end; a little past 1 so the whole line is lit, eased so the head slows at the end).</summary>
    public static float Head(float age) => 1.06f * (1f - (1f - Math.Clamp(age / SweepSeconds, 0f, 1f)) * (1f - Math.Clamp(age / SweepSeconds, 0f, 1f)));

    /// <summary>The line's brightness at <paramref name="age"/>: a short rise, full through the sweep and the hold, then an eased fade to 0 at <see cref="Lifetime"/>.</summary>
    public static float Fade(float age) => Ease(age / RiseSeconds) * (1f - Ease((age - SweepSeconds - HoldSeconds) / FadeSeconds));

    /// <summary>The line's thickness for melee step <paramref name="step"/> (1 the first) of a light chain, or a heavy strike.</summary>
    public static float Width(bool heavy, int step) => heavy ? HeavyWidth : LightWidth * (1f + StepGrowth * Math.Clamp(step - 1, 0, StepGrowthMost));

    /// <summary>
    /// The swing's direction: +1 sweeps from the swinger's right to the left, -1 from left to right. The light chain alternates (the first step from the right, a right-handed forehand),
    /// and a heavy strike comes from the right. Ours: no swing direction is known per step.
    /// </summary>
    public static int Direction(bool heavy, int step) => heavy || step % 2 == 1 ? 1 : -1;

    /// <summary>
    /// The arc's radius (metres) for a cleave of <paramref name="shape"/>: through the enemies struck, at their mean flat distance <paramref name="struck"/> from the swinger's feet, no nearer
    /// than <see cref="RadiusMin"/> and no farther than the cleave's reach (unknown: the reach). A fixed <paramref name="share"/> of the reach instead when above 0 (test aid
    /// <c>--musou-slash-arc-radius f</c>; 1 puts the line on the hitbox's edge).
    /// </summary>
    public static float Radius(MusouCleaveShape.Shape shape, float struck, float share = 0f)
    {
        if (share > 0f) return shape.Reach * share;
        if (!float.IsFinite(struck) || struck <= 0f) return shape.Reach;
        return Math.Clamp(struck, Math.Min(RadiusMin, shape.Reach), shape.Reach);
    }

    /// <summary>
    /// The angle (radians, positive to the swinger's left) of the point <paramref name="along"/> the arc (0 its start, 1 its end) for a cleave of <paramref name="shape"/> swung in
    /// <paramref name="direction"/>: from one edge of the cone to the other.
    /// </summary>
    public static float AngleAt(float along, MusouCleaveShape.Shape shape, int direction)
    {
        float half = shape.HalfAngleDegrees * MathF.PI / 180f;
        return (direction >= 0 ? 1f : -1f) * (-half + 2f * half * along);
    }

    /// <summary>The segments of the line for a cleave of <paramref name="shape"/>.</summary>
    public static int Segments(MusouCleaveShape.Shape shape) => Math.Clamp((int)MathF.Ceiling(shape.SpanDegrees / DegreesPerSegment), SegmentsMin, SegmentsMax);
}
