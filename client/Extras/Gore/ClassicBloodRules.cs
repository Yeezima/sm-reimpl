using System;
using System.Collections.Generic;
using System.Numerics;

namespace SmClient.ImpactRules;

/// <summary>
/// classic-hit-effects, gore part (Extended Classic PvP only; owner 2026-11-23, question card "Extended only"): the engine-free rules of the fatal gunfire headshot's wall decal.
/// <list type="bullet">
/// <item><see cref="DecalTrace"/>: extend the shot's real trajectory from the head contact; the first blocking surface along it, if it is an eligible wall (<see cref="IsWall"/>) within
/// <see cref="Reach"/> m, takes the decal at the exact intersection; otherwise none (no fallback surface). Only a fatal head shot by gunfire qualifies. Cosmetic: it does not touch the shot.</item>
/// <item><see cref="DecalShape"/>: the splash's alpha and shade, made in code from a seed (never from a texture): a dense irregular core, uneven splash lobes, satellite droplets and a few
/// downward drips; the same seed gives the same splash in every view.</item>
/// </list>
/// Every number is ours (kind: ours; spec/behavior.md "Classic hit effects").
/// </summary>
public static class ClassicBloodRules
{
    /// <summary>How far behind the head the wall may be (m; the brief's cutoff).</summary>
    public const float Reach = 5f;
    /// <summary>The steepest a surface may face up or down and still be a wall: |normal.z| at most this (about 30 degrees off vertical; ours).</summary>
    public const float WallUpLimit = 0.5f;
    /// <summary>The decal's side (m), its depth (m) and its life (s; under the shared alpha-decal cap and eviction fade).</summary>
    public const float DecalSize = 1.1f, DecalDepth = 0.5f, DecalSeconds = 30f;

    /// <summary>Why a fatal headshot left a decal or not.</summary>
    public enum DecalResult { Placed, NotFatal, NotHead, NotGunfire, NoWall, TooFar, Blocked, NotAWall }

    /// <summary>A surface the trace met (game space, Z up): distance along the ray, point, normal; <paramref name="Player"/> for another body in the way.</summary>
    public readonly record struct TraceHit(float Distance, Vector3 Point, Vector3 Normal, bool Player = false);

    /// <summary>Whether a surface facing <paramref name="normal"/> is a wall for the decal (game space, Z up).</summary>
    public static bool IsWall(Vector3 normal) => normal.LengthSquared() > 1e-8f && MathF.Abs(Vector3.Normalize(normal).Z) <= WallUpLimit;

    /// <summary>
    /// The decal decision for one hit: only a <paramref name="fatal"/> <paramref name="head"/> hit by <paramref name="gunfire"/>; then the nearest of <paramref name="hits"/> (the level
    /// along the ray and any other body, all from <paramref name="head"/> contact along the shot, unsorted) decides: none within the search: <see cref="DecalResult.NoWall"/>; a body:
    /// blocked; a floor, ceiling or ledge: not a wall; past <see cref="Reach"/>: too far; else placed at that point.
    /// </summary>
    public static (DecalResult Result, TraceHit Hit) DecalTrace(bool fatal, bool head, bool gunfire, IEnumerable<TraceHit> hits)
    {
        if (!gunfire) return (DecalResult.NotGunfire, default);
        if (!head) return (DecalResult.NotHead, default);
        if (!fatal) return (DecalResult.NotFatal, default);
        TraceHit? first = null;
        foreach (var h in hits) if (h.Distance >= 0f && (first is null || h.Distance < first.Value.Distance)) first = h;
        if (first is not { } f) return (DecalResult.NoWall, default);
        if (f.Distance > Reach) return (DecalResult.TooFar, f);
        if (f.Player) return (DecalResult.Blocked, f);
        if (!IsWall(f.Normal)) return (DecalResult.NotAWall, f);
        return (DecalResult.Placed, f);
    }

    /// <summary>How far the caller traces (a little past the cutoff, so a wall just beyond it reads as too far rather than absent).</summary>
    public const float SearchDistance = Reach + 1.5f;

    /// <summary>The decal's variant seed: the kill's sequence number and the victim, the same in every view.</summary>
    public static int VariantSeed(int killSequence, int victim) => unchecked((int)((uint)killSequence * 2654435761u ^ (uint)victim * 40503u ^ 0x5bd1e995u));

    /// <summary>
    /// The splash, <paramref name="size"/> square: alpha (0..1) and shade (0 darkest .. 1 lightest; the core is darkest) per texel, row 0 at the top (rows grow downward, the drips' way).
    /// The splash is centred: its core sits on the middle texel.
    /// </summary>
    public static (float[] Alpha, float[] Shade) DecalShape(int seed, int size = 192)
    {
        var rng = new Random(seed);
        var alpha = new float[size * size];
        var shade = new float[size * size];
        for (int i = 0; i < shade.Length; i++) shade[i] = 1f;
        float S(double lo, double hi) => (float)(lo + rng.NextDouble() * (hi - lo));

        // The core: a few overlapping blobs with a wavy outline (radius in texture units, 0..1 across the square).
        int coreBlobs = rng.Next(3, 6);
        var blobs = new List<(float X, float Y, float R, float[] Wave, float Dark)>();
        for (int b = 0; b < coreBlobs; b++)
            blobs.Add((0.5f + S(-0.05, 0.05), 0.5f + S(-0.05, 0.05), S(0.11, 0.17), Waves(rng, 0.22f), S(0.35, 0.55)));
        // The lobes: uneven, elongated, radiating at random angles.
        int lobes = rng.Next(5, 9);
        var lobeAngles = new float[lobes];
        for (int l = 0; l < lobes; l++)
        {
            float ang = (float)(l * Math.Tau / lobes + rng.NextDouble() * 0.7);
            lobeAngles[l] = ang;
            float reach = S(0.17, 0.33);
            int steps = 4;
            for (int k = 1; k <= steps; k++)
            {
                float t = k / (float)steps;
                float r = S(0.035, 0.06) * (1.15f - 0.75f * t);
                blobs.Add((0.5f + MathF.Cos(ang) * reach * t, 0.5f + MathF.Sin(ang) * reach * t, r, Waves(rng, 0.35f), S(0.6, 0.85)));
            }
        }
        // Satellite droplets: mostly along the lobes, a few anywhere.
        int drops = rng.Next(22, 40);
        for (int d = 0; d < drops; d++)
        {
            float ang = rng.NextDouble() < 0.7 ? lobeAngles[rng.Next(lobes)] + S(-0.35, 0.35) : S(0, Math.Tau);
            float dist = S(0.26, 0.46);
            blobs.Add((0.5f + MathF.Cos(ang) * dist, 0.5f + MathF.Sin(ang) * dist, S(0.006, 0.02), Waves(rng, 0.15f), S(0.7, 0.95)));
        }
        foreach (var (x, y, r, wave, dark) in blobs) Stamp(alpha, shade, size, x, y, r, wave, dark);

        // A few downward drips from the core's and the lower lobes' edge: a thin run with a bulb at its end.
        int drips = rng.Next(2, 5);
        for (int d = 0; d < drips; d++)
        {
            float x = 0.5f + S(-0.16, 0.16);
            float y0 = 0.5f + S(0.08, 0.16);
            float len = S(0.08, 0.3);
            float w = S(0.006, 0.012);
            float y1 = MathF.Min(0.97f, y0 + len);
            for (float y = y0; y <= y1; y += 0.5f / size)
            {
                float taper = 1f - 0.4f * (y - y0) / MathF.Max(1e-3f, y1 - y0);
                Stamp(alpha, shade, size, x + 0.004f * MathF.Sin(y * 40f + d), y, w * taper, null, 0.7f);
            }
            Stamp(alpha, shade, size, x, y1, w * 1.7f, null, 0.65f);
        }
        return (alpha, shade);
    }

    static float[] Waves(Random rng, float amount)
    {
        var w = new float[6];
        for (int i = 0; i < 3; i++) { w[i * 2] = (float)(rng.NextDouble() * amount / (i + 1)); w[i * 2 + 1] = (float)(rng.NextDouble() * Math.Tau); }
        return w;
    }

    /// <summary>One blob: a soft-edged disc whose radius wobbles with the angle (three harmonics), darkening the shade toward its centre.</summary>
    static void Stamp(float[] alpha, float[] shade, int size, float cx, float cy, float r, float[]? wave, float dark)
    {
        float reachR = r * 1.6f;
        int x0 = Math.Max(0, (int)((cx - reachR) * size)), x1 = Math.Min(size - 1, (int)((cx + reachR) * size) + 1);
        int y0 = Math.Max(0, (int)((cy - reachR) * size)), y1 = Math.Min(size - 1, (int)((cy + reachR) * size) + 1);
        float edge = 1.5f / size;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f) / size - cx, dy = (y + 0.5f) / size - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float rr = r;
                if (wave is not null && d > 1e-5f)
                {
                    float ang = MathF.Atan2(dy, dx);
                    rr *= 1f + wave[0] * MathF.Sin(2f * ang + wave[1]) + wave[2] * MathF.Sin(3f * ang + wave[3]) + wave[4] * MathF.Sin(5f * ang + wave[5]);
                }
                float a = Math.Clamp((rr - d) / edge + 0.5f, 0f, 1f);
                if (a <= 0f) continue;
                int i = y * size + x;
                if (a > alpha[i]) alpha[i] = a;
                float inner = Math.Clamp(1f - d / MathF.Max(rr, 1e-4f), 0f, 1f);
                float s = 1f - (1f - dark) * a * (0.5f + 0.5f * inner);
                if (s < shade[i]) shade[i] = s;
            }
    }
}
