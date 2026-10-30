namespace SmNet;

/// <summary>
/// feel/jump-pack-2: where the jump pack's sword slash clip (<c>SM_MEL_CS_XATK_Hv1_LS_2_7m</c>) is posed (client presentation; engine-free, tested).
/// The owner (2026-10-29): "the air slash landing currently plays the beginning of the animation correctly, then performs the air slash backwards".
/// The cause: the graph plays the clip in two cropped parts (read from the clip generators: <c>SlashSpec.FlightClipEnd</c> /
/// <c>SlashSpec.ImpactClipStart</c>, both 0.4667 s on the install): the flight sequencer the wind-up from 0 to 0.4667 s (the blade drawn low, then
/// raised overhead), the impact sequencer from 0.4667 s on (the overhead strike: the hand comes down 0.55 to 0.65 s into the clip, the follow-through
/// after). The client held frame 0 through the dive and restarted the clip at 0 on landing, so the landing showed the wind-up (the blade rising: the
/// swing seen backwards) and the 0.3 s recovery ended before the strike was ever shown.
/// <list type="bullet">
/// <item>The dive plays the wind-up forward from its start, sped up to fit the dive (ours: the simulation's straight dive lasts about 0.2 s against the
/// wind-up's 0.4667 s), held at its end if the dive lasts longer. During the cross-fade from the flight pose (the entry blend) it runs at the clip's own
/// rate: a faster-moving target under the fade swung the arm's blend through the far side in one frame (measured: the hand 1.3 m in a frame); after the
/// fade the rest of the wind-up is fitted to the rest of the dive (at most <see cref="MaxDiveRate"/>).</item>
/// <item>The impact (and the get-up after it) plays from <c>ImpactClipStart</c> on at the clip's own rate, so the strike lands with the simulation's hit
/// (0.133 s after the impact: clip 0.60 s, the hand halfway down).</item>
/// </list>
/// </summary>
public static class SlashPose
{
    /// <summary>Fastest the dive plays the wind-up (× the clip's rate; ours).</summary>
    public const float MaxDiveRate = 4f;

    /// <summary>The dive's expected length (s): the height above the ground over the dive's vertical speed (at least 1 m/s); infinity without a ground.</summary>
    public static float ExpectedDive(float aboveGround, float verticalSpeed) =>
        float.IsFinite(aboveGround) && aboveGround >= 0f ? aboveGround / MathF.Max(1f, -verticalSpeed) : float.PositiveInfinity;

    /// <summary>The wind-up's playback rate for a dive of <paramref name="expectedDive"/> s: <paramref name="flightEnd"/> over it, clamped to 1 .. <see cref="MaxDiveRate"/>.</summary>
    public static float DiveRate(float expectedDive, float flightEnd) =>
        !(flightEnd > 0f) || !float.IsFinite(expectedDive) ? 1f : expectedDive <= 1e-3f ? MaxDiveRate : Math.Clamp(flightEnd / expectedDive, 1f, MaxDiveRate);

    /// <summary>
    /// The clip time in the dive, <paramref name="sinceDive"/> s after it started: the clip's own rate for the first <paramref name="entryBlend"/> s (the
    /// cross-fade), then the rest of the wind-up at <see cref="DiveRate"/> over the rest of the expected dive; held at <paramref name="flightEnd"/> (0: frame 0, the old hold).
    /// </summary>
    public static float DiveTime(float sinceDive, float expectedDive, float flightEnd, float entryBlend = 0f)
    {
        if (!(flightEnd > 0f)) return 0f;
        float t = MathF.Max(0f, sinceDive), fade = Math.Clamp(entryBlend, 0f, flightEnd);
        if (t <= fade) return t;
        return Math.Clamp(fade + (t - fade) * DiveRate(expectedDive - fade, flightEnd - fade), 0f, flightEnd);
    }

    /// <summary>
    /// anim/feel-pass (the owner, 2026-10-30: "the Assault air slash landing still feels janky"): the wind-up's clip time in the dive on a curve whose rate never steps. <see cref="DiveTime"/>
    /// played the fade at the clip's rate, then jumped to up to <see cref="MaxDiveRate"/> times it (a velocity step of the arm 0.125 s into the dive, the transition probe's largest jerk) and
    /// fell back to the clip's rate at the landing (a second step). Here the time is a cubic Hermite from 0 to <paramref name="flightEnd"/> over the expected dive with the clip's own rate
    /// (slope 1) at both ends, so the swing speeds up and slows down smoothly and the strike after the landing carries on at the rate the dive ended with, as the roll's clip runs on at
    /// one rate through its exit. The dive used for the curve is at least a third of the wind-up (the curve's middle rate, 1.5 × wind-up / dive − 0.5, stays at most 4); a dive that
    /// lands sooner is caught up by <see cref="ImpactTime(float, float, float, float, float)"/>. A dive longer than the wind-up plays it at the clip's rate and holds its end, as
    /// before. Ours (the graph plays the flight part at its own rate; the simulation's straight 40 m/s dive is ours too).
    /// </summary>
    public static float SmoothDiveTime(float sinceDive, float expectedDive, float flightEnd)
    {
        if (!(flightEnd > 0f)) return 0f;
        float t = MathF.Max(0f, sinceDive);
        if (!float.IsFinite(expectedDive) || expectedDive >= flightEnd) return MathF.Min(t, flightEnd);
        float d = MathF.Max(expectedDive, flightEnd / 3f);
        if (t >= d) return flightEnd;
        float u = t / d, u2 = u * u, u3 = u2 * u;
        // Hermite basis: p0 = 0, m0 = 1 (clip rate), p1 = flightEnd, m1 = 1; tangents scaled by the interval d.
        float h10 = u3 - 2f * u2 + u, h01 = -2f * u3 + 3f * u2, h11 = u3 - u2;
        return Math.Clamp(h10 * d + h01 * flightEnd + h11 * d, 0f, flightEnd);
    }

    /// <summary>The graph's flight-to-impact transition ("Blend Slo-mo", 0.033 s): a dive that landed before its wind-up's end catches up over it.</summary>
    public const float ImpactCatchUp = 1f / 30f;

    /// <summary>
    /// The clip time <paramref name="sinceImpact"/> s after the landing: from <paramref name="impactStart"/> at the clip's rate, at most <paramref name="clipLength"/>.
    /// A dive that landed with its wind-up at <paramref name="landedAt"/> (earlier than the impact's start: the dive was shorter than expected) moves from it to
    /// that time over <see cref="ImpactCatchUp"/> (the time eased toward the strike's, never backwards), so the landing has no pop.
    /// </summary>
    public static float ImpactTime(float sinceImpact, float impactStart, float clipLength, float landedAt = float.NaN) =>
        ImpactTime(sinceImpact, impactStart, clipLength, landedAt, ImpactCatchUp);

    /// <summary>
    /// anim/melee-flow: <see cref="ImpactTime(float, float, float, float)"/> with the catch-up over <paramref name="catchUp"/> seconds on the smooth curve (the client's
    /// <see cref="EasedCatchUp"/>: the 1/30 s catch-up played up to 0.17 s of the strike in two frames, the transition probe's largest jerk at the slash's landing).
    /// </summary>
    public static float ImpactTime(float sinceImpact, float impactStart, float clipLength, float landedAt, float catchUp)
    {
        float since = MathF.Max(0f, sinceImpact), target = impactStart + since;
        if (float.IsFinite(landedAt) && landedAt < impactStart && catchUp > 0f && since < catchUp)
        {
            float x = since / catchUp;
            float k = catchUp > ImpactCatchUp + 1e-4f ? x * x * (3f - 2f * x) : x;   // the graph's own catch-up stays linear
            target = landedAt + (target - landedAt) * k;
        }
        return Math.Clamp(target, 0f, MathF.Max(clipLength, impactStart));
    }

    /// <summary>anim/melee-flow (ours): the client's catch-up of a slash that landed before its wind-up's end: 0.1 s on the smooth curve.</summary>
    public const float EasedCatchUp = 0.1f;

    /// <summary>Seconds after the landing until the clip's end (what the get-up can show of it).</summary>
    public static float AfterImpact(float impactStart, float clipLength) => MathF.Max(0f, clipLength - impactStart);
}
