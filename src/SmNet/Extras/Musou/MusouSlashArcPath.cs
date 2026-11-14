using System;
using System.Collections.Generic;
using System.Numerics;

namespace SmNet.Extras.Musou;

/// <summary>
/// The shape of one melee swing's arc (owner, 2026-11-14: "i also want the red and blue weapon swing cleave visual effects to match the path of the tip of the weapon through the air (in terms of
/// their orientation and shape). they need to show at the cleave distance still though. so no line following the tip of the weapon exactly, just that the slash visuals need to match the trajectory
/// of the weapon tip in motion"). Pure math, no engine types: the client samples the weapon tip's position over the swing's active window from the swing's animation (in the swinger's frame:
/// X forward, Y left, Z up, metres) and <see cref="Fit"/> turns that path into a polyline of unit directions as seen from the swinger's chest: the swing's tilt (a diagonal, overhead or rising
/// swing), its curve and its direction. <see cref="Scale"/> then puts those directions out at the cleave's radius, so the line sits where the cleave reaches and not on the tip itself.
/// Every number here is ours (kind: ours, confidence: low; <c>spec/behavior.md</c>, the refine register). Client presentation only, Musou only.
/// </summary>
public static class MusouSlashArcPath
{
    /// <summary>A fitted arc: unit directions from the swinger's chest (X forward, Y left, Z up), the first where the swing starts, evenly spaced along the swing; and two summary angles.</summary>
    public sealed class Path
    {
        public Vector3[] Directions = Array.Empty<Vector3>();
        /// <summary>The angle (degrees) the swing turns through, as seen from the chest.</summary>
        public float TravelDegrees;
        /// <summary>The slope (degrees) of the line from the swing's start to its end: 0 level, positive rising toward its end, negative falling (an overhead chop).</summary>
        public float TiltDegrees;
        /// <summary>Degrees the line bows away from the straight chord at its widest (its curve).</summary>
        public float BowDegrees;
    }

    /// <summary>
    /// Seconds of the animation before and after the strike's hit time the tip is sampled over, and the samples taken (evenly spaced in time). Wide on purpose: <see cref="ActiveSpan"/> then keeps
    /// the part where the tip is moving fast, the swing itself without its wind-up and its recovery. Ours.
    /// </summary>
    public const float WindowBefore = 0.30f, WindowAfter = 0.12f;
    public const int Samples = 28;

    /// <summary>The swing's active part: the samples around the tip's fastest step whose steps are at least this share of it. Ours.</summary>
    public const float ActiveShare = 0.35f;

    /// <summary>A sample nearer the chest than this (metres) says nothing of a direction and is left out; the fewest usable samples and the least travel (degrees) a path needs to be used.</summary>
    public const float MinReach = 0.25f;
    public const int MinSamples = 4;
    public const float MinTravelDegrees = 30f;

    /// <summary>The steepest the line may climb or fall (degrees above and below the chest's level): a stray sample cannot send it through the floor or over the swinger's head.</summary>
    public const float ElevationMax = 75f, ElevationMin = -60f;

    const float Rad = MathF.PI / 180f;

    /// <summary>
    /// The clip times (seconds) the tip is sampled over for a strike whose hit falls at <paramref name="hitTime"/> in a clip of <paramref name="clipLength"/>: from <see cref="WindowBefore"/> before to
    /// <see cref="WindowAfter"/> after it, kept inside the clip. False when the clip or the hit time is unusable.
    /// </summary>
    public static bool Window(float hitTime, float clipLength, out float from, out float to)
    {
        from = to = 0f;
        if (!float.IsFinite(hitTime) || !float.IsFinite(clipLength) || clipLength <= 0.05f || hitTime < 0f) return false;
        from = Math.Clamp(hitTime - WindowBefore, 0f, clipLength);
        to = Math.Clamp(hitTime + WindowAfter, 0f, clipLength);
        return to - from > 0.04f;
    }

    /// <summary>
    /// Fits the swing's arc to the weapon tip's positions <paramref name="tip"/> (in the order of the swing, in the swinger's frame) seen from <paramref name="chest"/>, as a path of
    /// <paramref name="segments"/> + 1 evenly spaced directions. Only the part of the swing inside the cleave's cone is kept (<paramref name="halfAngleDegrees"/> to either side of the heading: the
    /// line is the cleave's, so it does not run behind the swinger with the wind-up); where the swing leaves the cone and comes back, its longest part inside. Null when there is no usable path (too few samples, too little travel, non-finite numbers): the caller draws the level arc.
    /// </summary>
    public static Path? Fit(IReadOnlyList<Vector3> tip, Vector3 chest, int segments, float halfAngleDegrees = 180f)
    {
        if (tip is null || segments < 1 || !IsFinite(chest)) return null;
        var kept = new List<Vector3>(tip.Count);
        foreach (var p in ActiveSpan(tip))
        {
            if (!IsFinite(p)) continue;
            var d = p - chest;
            float length = d.Length();
            if (length < MinReach) continue;
            d = Clamp(d / length);
            if (kept.Count > 0 && AngleBetween(kept[^1], d) < 0.5f * Rad) continue;   // a held pose repeats itself
            kept.Add(d);
        }
        if (halfAngleDegrees < 180f) kept = LongestRunInside(kept, halfAngleDegrees);
        if (kept.Count < MinSamples) return null;
        // One light smoothing pass over the interior (the samples are a few centimetres of noise on a metre of swing).
        var smooth = kept.ToArray();
        for (int i = 1; i < kept.Count - 1; i++) smooth[i] = Vector3.Normalize(kept[i - 1] * 0.25f + kept[i] * 0.5f + kept[i + 1] * 0.25f);
        var cumulative = new float[smooth.Length];
        for (int i = 1; i < smooth.Length; i++) cumulative[i] = cumulative[i - 1] + AngleBetween(smooth[i - 1], smooth[i]);
        float travel = cumulative[^1];
        if (!(travel >= MinTravelDegrees * Rad)) return null;

        var result = new Path { Directions = new Vector3[segments + 1], TravelDegrees = travel / Rad };
        int seg = 0;
        for (int k = 0; k <= segments; k++)
        {
            float at = travel * k / segments;
            while (seg < smooth.Length - 2 && cumulative[seg + 1] < at) seg++;
            float span = cumulative[seg + 1] - cumulative[seg];
            float t = span > 1e-6f ? Math.Clamp((at - cumulative[seg]) / span, 0f, 1f) : 0f;
            var v = Vector3.Lerp(smooth[seg], smooth[seg + 1], t);
            result.Directions[k] = v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : smooth[seg];
        }
        var first = result.Directions[0];
        var last = result.Directions[segments];
        var chord = last - first;
        float horizontal = MathF.Sqrt(chord.X * chord.X + chord.Y * chord.Y);
        result.TiltDegrees = MathF.Atan2(chord.Z, horizontal) / Rad;
        // The bow: the widest angle between the line and the great-circle chord between its ends.
        float chordLength = AngleBetween(first, last);
        float bow = 0f;
        for (int k = 1; k < segments; k++)
            bow = MathF.Max(bow, MathF.Min(AngleBetween(first, result.Directions[k]) + AngleBetween(result.Directions[k], last) - chordLength, 180f * Rad));
        result.BowDegrees = bow / Rad;
        return result;
    }

    /// <summary>
    /// The part of the evenly timed samples <paramref name="tip"/> where the tip is moving fast: from the fastest step outwards while a step is at least <see cref="ActiveShare"/> of it. The wind-up
    /// and the recovery, slow beside the swing, are left out. All of them when no step is usable.
    /// </summary>
    public static IReadOnlyList<Vector3> ActiveSpan(IReadOnlyList<Vector3> tip)
    {
        if (tip.Count < 3) return tip;
        var speed = new float[tip.Count - 1];
        int peak = -1;
        for (int i = 0; i < speed.Length; i++)
        {
            speed[i] = IsFinite(tip[i]) && IsFinite(tip[i + 1]) ? Vector3.Distance(tip[i], tip[i + 1]) : 0f;
            if (peak < 0 || speed[i] > speed[peak]) peak = i;
        }
        if (!(speed[peak] > 1e-4f)) return tip;
        float floor = speed[peak] * ActiveShare;
        int first = peak, last = peak;
        while (first > 0 && speed[first - 1] >= floor) first--;
        while (last < speed.Length - 1 && speed[last + 1] >= floor) last++;
        var span = new List<Vector3>(last - first + 2);
        for (int i = first; i <= last + 1; i++) span.Add(tip[i]);
        return span;
    }

    /// <summary>The path's points at <paramref name="radius"/> metres from the chest (offsets from it, X forward, Y left, Z up): the swing's tilt, curve and direction at the cleave's distance.</summary>
    public static Vector3[] Scale(Path path, float radius)
    {
        var points = new Vector3[path.Directions.Length];
        for (int i = 0; i < points.Length; i++) points[i] = path.Directions[i] * radius;
        return points;
    }

    /// <summary>The longest run (by the angle it turns through, else by its samples) of consecutive <paramref name="dirs"/> whose azimuth is within <paramref name="halfAngleDegrees"/> of the heading.</summary>
    static List<Vector3> LongestRunInside(List<Vector3> dirs, float halfAngleDegrees)
    {
        float limit = halfAngleDegrees * Rad;
        List<Vector3> best = new(), run = new();
        float bestTurn = -1f, turn = 0f;
        void End()
        {
            if (run.Count > 0 && (turn > bestTurn + 1e-6f || (Math.Abs(turn - bestTurn) <= 1e-6f && run.Count > best.Count))) { best = run; bestTurn = turn; }
            run = new(); turn = 0f;
        }
        foreach (var d in dirs)
        {
            if (MathF.Abs(MathF.Atan2(d.Y, d.X)) > limit) { End(); continue; }
            if (run.Count > 0) turn += AngleBetween(run[^1], d);
            run.Add(d);
        }
        End();
        return best;
    }

    /// <summary>One direction clamped to the elevation limits (unit length).</summary>
    static Vector3 Clamp(Vector3 unit)
    {
        float elevation = Math.Clamp(MathF.Asin(Math.Clamp(unit.Z, -1f, 1f)), ElevationMin * Rad, ElevationMax * Rad);
        float horizontal = MathF.Sqrt(unit.X * unit.X + unit.Y * unit.Y);
        float azimuth = horizontal > 1e-4f ? MathF.Atan2(unit.Y, unit.X) : 0f;
        float c = MathF.Cos(elevation);
        return new Vector3(c * MathF.Cos(azimuth), c * MathF.Sin(azimuth), MathF.Sin(elevation));
    }

    static float AngleBetween(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f));

    static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
