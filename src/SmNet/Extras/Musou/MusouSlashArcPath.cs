using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>The limited fit's measure: degrees between the strike's first and last direction (BowDegrees then holds the sideways deviation from the great circle).</summary>
        public float SweepDegrees;
        /// <summary>The limited arc (owner 2026-11-15; <see cref="FitLimited"/>): null for the older full-swing path (<see cref="Fit"/>).</summary>
        public Limited? Arc;
    }

    /// <summary>
    /// A swing's limited arc (owner, 2026-11-15): one clean stroke of fixed length (<see cref="ArcLength"/>) and constant curvature. <see cref="Mid"/> is the unit direction from the chest to the
    /// stroke's middle, <see cref="Tangent"/> the unit direction of travel there, <see cref="Bow"/> the unit direction the stroke bulges toward (perpendicular to the tangent), <see cref="TurnDegrees"/>
    /// the angle the stroke turns through end to end. Scale free: <see cref="Points"/> puts it out at a radius.
    /// </summary>
    public sealed record Limited(Vector3 Mid, Vector3 Tangent, Vector3 Bow, float TurnDegrees);

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
        Summarise(result);
        return result;
    }

    /// <summary>Sets the path's tilt and bow from its directions.</summary>
    static void Summarise(Path path)
    {
        int segments = path.Directions.Length - 1;
        var first = path.Directions[0];
        var last = path.Directions[segments];
        var chord = last - first;
        float horizontal = MathF.Sqrt(chord.X * chord.X + chord.Y * chord.Y);
        path.TiltDegrees = MathF.Atan2(chord.Z, horizontal) / Rad;
        // The bow: the widest angle between the line and the great-circle chord between its ends.
        float chordLength = AngleBetween(first, last);
        float bow = 0f;
        for (int k = 1; k < segments; k++)
            bow = MathF.Max(bow, MathF.Min(AngleBetween(first, path.Directions[k]) + AngleBetween(path.Directions[k], last) - chordLength, 180f * Rad));
        path.BowDegrees = bow / Rad;
    }

    /// <summary>The widest a path is stretched (times) and the least horizontal sweep (degrees) a path needs to be stretched at all: an overhead chop has none to stretch.</summary>
    public const float StretchMost = 2.5f, StretchMinSweep = 15f;

    /// <summary>
    /// A path that sweeps less than the cleave's <paramref name="spanDegrees"/> (a short active window) stretched over it, so the arc still covers what the cleave hits: its angles (azimuth and
    /// elevation alike, so its tilt and curve stay) are scaled about the path's middle by span / sweep, at most <see cref="StretchMost"/>, and held inside the cone and the elevation limits.
    /// The path itself when it already sweeps the span, or sweeps under <see cref="StretchMinSweep"/>.
    /// </summary>
    public static Path Stretch(Path path, float spanDegrees)
    {
        var azimuth = new float[path.Directions.Length];
        var elevation = new float[path.Directions.Length];
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < azimuth.Length; i++)
        {
            var d = path.Directions[i];
            azimuth[i] = MathF.Atan2(d.Y, d.X);
            elevation[i] = MathF.Asin(Math.Clamp(d.Z, -1f, 1f));
            lo = MathF.Min(lo, azimuth[i]); hi = MathF.Max(hi, azimuth[i]);
        }
        float sweep = (hi - lo) / Rad;
        if (!(spanDegrees > 0f) || sweep < StretchMinSweep || sweep >= spanDegrees) return path;
        float factor = MathF.Min(spanDegrees / sweep, StretchMost);
        float midAzimuth = (lo + hi) / 2f, midElevation = (elevation.Min() + elevation.Max()) / 2f, half = spanDegrees / 2f * Rad;
        var result = new Path { Directions = new Vector3[path.Directions.Length], TravelDegrees = path.TravelDegrees * factor };
        for (int i = 0; i < azimuth.Length; i++)
        {
            float a = Math.Clamp(midAzimuth + (azimuth[i] - midAzimuth) * factor, -half, half);
            float e = Math.Clamp(midElevation + (elevation[i] - midElevation) * factor, ElevationMin * Rad, ElevationMax * Rad);
            float c = MathF.Cos(e);
            result.Directions[i] = new Vector3(c * MathF.Cos(a), c * MathF.Sin(a), MathF.Sin(e));
        }
        Summarise(result);
        return result;
    }

    /// <summary>What a fitted path depends on, so two swings share one only when they would fit the same: the clip, the weapon, the cleave's half angle (degrees, rounded) and the body (its rig's own tip and chest).</summary>
    public readonly record struct Key(string Clip, string Wargear, int HalfAngle, int Body)
    {
        public static Key Of(string clip, string? wargear, float halfAngleDegrees, int body) =>
            new(clip.ToLowerInvariant(), (wargear ?? "").ToLowerInvariant(), (int)MathF.Round(halfAngleDegrees), body);
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
    public static Vector3[] Scale(Path path, float radius, float maxDrop = float.PositiveInfinity)
    {
        if (path.Arc is { } arc) return Points(arc, radius, path.Directions.Length - 1, maxDrop);   // the limited arc: 2 m long wherever the radius puts it
        var points = new Vector3[path.Directions.Length];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = path.Directions[i] * radius;
            if (points[i].Z < -maxDrop) points[i].Z = -maxDrop;   // never below the floor (the line has no depth test)
        }
        return points;
    }

    /// <summary>How far below the line's centre (the chest of the enemies struck, <paramref name="centreHeight"/> above the swinger's feet) the line may go: down to <see cref="FloorMargin"/> above the swinger's feet level.</summary>
    public static float MaxDrop(float centreHeight) => MathF.Max(0f, centreHeight - FloorMargin);

    /// <summary>The line stays at least this far (metres) above the swinger's feet level.</summary>
    public const float FloorMargin = 0.1f;

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

    // ---- The limited arc (owner, 2026-11-15) ----

    /// <summary>
    /// Seconds past the strike's hit time the arc still follows the tip (a short follow-through; never the recovery), the arc's fixed length (metres, along the curve), and the least and most the
    /// stroke turns through (degrees, end to end: a gentle even bow). Owner 2026-11-15: release plus short follow-through, about 2 m, a gentle even bow; the numbers are ours.
    /// </summary>
    public const float FollowSeconds = 0.06f, ArcLength = 2f, MinTurnDegrees = 8f, MaxTurnDegrees = 40f, DefaultTurnDegrees = 20f, BowRatioFull = 0.5f;

    /// <summary>The fewest usable samples and the least travel (degrees) the limited fit needs; below it the caller draws <see cref="Level"/>.</summary>
    public const int MinLimitedSamples = 3;
    public const float MinLimitedTravelDegrees = 4f;

    /// <summary>
    /// The samples <paramref name="tip"/> (evenly spaced in time from <paramref name="from"/> to <paramref name="to"/> seconds of the clip) that belong to the swing's released strike: none after
    /// <paramref name="hitTime"/> + <see cref="FollowSeconds"/> (the recovery), and of those only the fast part (<see cref="ActiveSpan"/>: from the release, not the wind-up).
    /// </summary>
    public static IReadOnlyList<Vector3> ReleaseSpan(IReadOnlyList<Vector3> tip, float from, float to, float hitTime)
    {
        if (tip.Count < 3 || !(to > from) || !float.IsFinite(hitTime)) return tip;
        float cutoff = hitTime + FollowSeconds;
        var kept = new List<Vector3>(tip.Count);
        for (int i = 0; i < tip.Count; i++)
        {
            float t = from + (to - from) * i / (tip.Count - 1);
            kept.Add(tip[i]);
            if (t >= cutoff) break;   // the first sample at or past the cutoff closes the strike
        }
        return ActiveSpan(kept);
    }

    /// <summary>
    /// Fits one smooth stroke to the swing's released strike: the samples <paramref name="tip"/> (see <see cref="ReleaseSpan"/>) seen from <paramref name="chest"/> give the stroke's middle, its
    /// direction of travel (first to last sample, so the swing's tilt and direction stay) and which side it bows to (the side the path strays farthest to; a hook or an S-bend collapses to its
    /// larger lobe, so the curvature never changes sign), clamped to <see cref="MinTurnDegrees"/> .. <see cref="MaxTurnDegrees"/>. Null when too little is usable (the caller draws <see cref="Level"/>).
    /// </summary>
    public static Path? FitLimited(IReadOnlyList<Vector3> tip, Vector3 chest, int segments, float from, float to, float hitTime, float halfAngleDegrees = 180f)
    {
        if (tip is null || segments < 1 || !IsFinite(chest)) return null;
        var kept = new List<Vector3>(tip.Count);
        foreach (var p in ReleaseSpan(tip, from, to, hitTime))
        {
            if (!IsFinite(p)) continue;
            var d = p - chest;
            float length = d.Length();
            if (length < MinReach) continue;
            d = Clamp(d / length);
            if (kept.Count > 0 && AngleBetween(kept[^1], d) < 0.5f * Rad) continue;
            kept.Add(d);
        }
        if (halfAngleDegrees < 180f) kept = LongestRunInside(kept, halfAngleDegrees);
        if (kept.Count < MinLimitedSamples) return null;
        float travel = 0f;
        for (int i = 1; i < kept.Count; i++) travel += AngleBetween(kept[i - 1], kept[i]);
        if (!(travel >= MinLimitedTravelDegrees * Rad)) return null;
        var chord = kept[^1] - kept[0];
        float chordLength = chord.Length();
        if (!(chordLength > 1e-4f)) return null;   // a swing that comes back to where it started has no direction
        var u = chord / chordLength;
        var midpoint = (kept[0] + kept[^1]) * 0.5f;
        // The bow is measured against the great circle through the swing's first and last direction, not against the straight chord: seen from the chest every swing's directions lie on a
        // sphere, so the chord of a plain level sweep already sags (0.5 of the sphere's radius for 120 degrees), which read as a 98 degree bow and hit the clamp on every swing (found 2026-11-15).
        // What bows the line is the path's sideways deviation from that great circle.
        var plane = Vector3.Cross(kept[0], kept[^1]);
        float alpha = AngleBetween(kept[0], kept[^1]);
        float beta = 0f;
        Vector3 bow;
        float turn;
        var mid = Vector3.Normalize(midpoint);
        if (plane.LengthSquared() > 1e-8f)
        {
            var n = Vector3.Normalize(plane);
            float signed = 0f;
            foreach (var d in kept) { float s = MathF.Asin(Math.Clamp(Vector3.Dot(d, n), -1f, 1f)); if (MathF.Abs(s) > MathF.Abs(signed)) signed = s; }
            beta = MathF.Abs(signed);
            bow = signed >= 0f ? n : -n;
            mid = Clamp(Vector3.Normalize(mid + bow * MathF.Sin(beta)));
        }
        else bow = Vector3.UnitZ;
        if (beta < 2f * Rad)
        {
            // A path that stays on its great circle bows away from the chest, like a cleave's circle round the swinger, by the default turn.
            var outward = mid - u * Vector3.Dot(mid, u);
            bow = outward.LengthSquared() > 1e-8f ? Vector3.Normalize(outward) : Vector3.UnitZ;
            turn = DefaultTurnDegrees;
        }
        else turn = MinTurnDegrees + (MaxTurnDegrees - MinTurnDegrees) * Math.Clamp(beta / MathF.Max(alpha, 1e-3f) / BowRatioFull, 0f, 1f);   // the real strikes sweep 120 to 165 degrees and stray 8 to 80 (logged): a straight tan() map hit the cap on 4 of 5
        var fitted = Build(mid, u, bow, turn, segments);
        fitted.BowDegrees = beta / Rad;
        fitted.SweepDegrees = alpha / Rad;
        return fitted;
    }

    /// <summary>The level default (no usable tip path): a stroke straight ahead at the line's height, bowed outward, travelling <paramref name="direction"/> (+1 right to left, -1 left to right).</summary>
    public static Path Level(int direction, int segments)
    {
        var tangent = new Vector3(0f, direction >= 0 ? 1f : -1f, 0f);
        return Build(Vector3.UnitX, tangent, Vector3.UnitX, DefaultTurnDegrees, segments);
    }

    static Path Build(Vector3 mid, Vector3 tangent, Vector3 bow, float turnDegrees, int segments)
    {
        tangent = Vector3.Normalize(tangent);
        bow -= tangent * Vector3.Dot(bow, tangent);   // perpendicular to the tangent
        bow = bow.LengthSquared() > 1e-8f ? Vector3.Normalize(bow) : Vector3.Normalize(Vector3.Cross(tangent, Vector3.UnitZ));
        var limited = new Limited(mid, tangent, bow, turnDegrees);
        var path = new Path { Arc = limited, TravelDegrees = turnDegrees };
        var points = Points(limited, 2.5f, segments);
        path.Directions = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++) path.Directions[i] = Vector3.Normalize(points[i]);
        Summarise(path);
        return path;
    }

    /// <summary>
    /// The limited arc's <paramref name="segments"/> + 1 points (offsets from the chest, X forward, Y left, Z up): a circular arc of <see cref="ArcLength"/> metres whose middle is at
    /// <paramref name="radius"/> metres from the chest along <see cref="Limited.Mid"/>, bulging toward <see cref="Limited.Bow"/>, first point where the swing starts. Never lower than
    /// <paramref name="maxDrop"/> below the chest: the whole stroke is lifted, so its shape is kept.
    /// </summary>
    public static Vector3[] Points(Limited arc, float radius, int segments, float maxDrop = float.PositiveInfinity)
    {
        float turn = Math.Clamp(arc.TurnDegrees, MinTurnDegrees, MaxTurnDegrees) * Rad;
        float rho = ArcLength / turn;
        var apex = arc.Mid * radius;
        var centre = apex - arc.Bow * rho;
        var points = new Vector3[segments + 1];
        float lowest = float.MaxValue;
        for (int k = 0; k <= segments; k++)
        {
            float phi = ((float)k / segments - 0.5f) * turn;
            points[k] = centre + (arc.Bow * MathF.Cos(phi) + arc.Tangent * MathF.Sin(phi)) * rho;
            lowest = MathF.Min(lowest, points[k].Z);
        }
        if (lowest < -maxDrop) { float lift = -maxDrop - lowest; for (int k = 0; k <= segments; k++) points[k].Z += lift; }
        return points;
    }

    /// <summary>The length (metres) of a polyline.</summary>
    public static float LengthOf(IReadOnlyList<Vector3> points)
    {
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
        return length;
    }

    static float AngleBetween(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f));

    static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
