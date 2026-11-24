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
        // review of fcd76a6f (about 70 % against the reference image): a larger, irregular, near-black core; long, thick, tapered lobes reaching up to 0.45; noisier edges;
        // droplets of mixed sizes stretched along their radius; and a silhouette that differs per seed (stretch, lobe count and lengths), not only its dots
        var rng = new Random(seed);
        var alpha = new float[size * size];
        var shade = new float[size * size];
        for (int i = 0; i < shade.Length; i++) shade[i] = 1f;
        float S(double lo, double hi) => (float)(lo + rng.NextDouble() * (hi - lo));

        // the variant's silhouette: an overall stretch along a random angle and an off-centre lean of the core
        float stretch = S(0.78, 1.3), stretchAng = S(0, Math.Tau);
        float sc = MathF.Cos(stretchAng), ss = MathF.Sin(stretchAng);
        (float X, float Y) P(float dx, float dy)
        {
            // stretch the offset along the variant's axis, keep it inside the square's disc
            float a = dx * sc + dy * ss, b = -dx * ss + dy * sc;
            a *= stretch; b /= stretch;
            float x = a * sc - b * ss, y = a * ss + b * sc;
            float l = MathF.Sqrt(x * x + y * y);
            if (l > 0.46f) { x *= 0.46f / l; y *= 0.46f / l; }
            return (0.5f + x, 0.5f + y);
        }
        var blobs = new List<(float X, float Y, float R, float[]? Wave, float Dark, float Elong, float Ang)>();

        // the core: several overlapping blobs with ragged outlines, near black
        int coreBlobs = rng.Next(4, 8);
        for (int b = 0; b < coreBlobs; b++)
        {
            var (x, y) = P(S(-0.07, 0.07), S(-0.07, 0.07));
            blobs.Add((x, y, S(0.12, 0.2), Waves(rng, 0.38f), S(0.08, 0.22), 1f, 0f));
        }
        // the lobes: 3 to 9, long, thick at the root and tapering, each bending a little
        int lobes = rng.Next(3, 10);
        var lobeAngles = new float[lobes];
        for (int l = 0; l < lobes; l++)
        {
            float ang = (float)(l * Math.Tau / lobes + rng.NextDouble() * 0.9);
            lobeAngles[l] = ang;
            float reach = S(0.2, 0.45), bend = S(-0.5, 0.5), root = S(0.05, 0.085);
            int steps = 7;
            for (int k = 1; k <= steps; k++)
            {
                float t = k / (float)steps;
                float a = ang + bend * t * t;
                var (x, y) = P(MathF.Cos(a) * reach * t, MathF.Sin(a) * reach * t);
                float r = root * (1.1f - 0.85f * t) * S(0.85, 1.15);
                blobs.Add((x, y, r, Waves(rng, 0.45f), S(0.2, 0.55) + 0.3f * t, 1f, 0f));
            }
        }
        // satellite droplets: mixed sizes (many small, a few large), stretched along their radius, mostly beyond the lobes
        int drops = rng.Next(20, 46);
        for (int d = 0; d < drops; d++)
        {
            float ang = rng.NextDouble() < 0.7 ? lobeAngles[rng.Next(lobes)] + S(-0.4, 0.4) : S(0, Math.Tau);
            float dist = S(0.24, 0.46);
            var (x, y) = P(MathF.Cos(ang) * dist, MathF.Sin(ang) * dist);
            float r = 0.004f + 0.03f * (float)Math.Pow(rng.NextDouble(), 2.2);
            blobs.Add((x, y, r, Waves(rng, 0.2f), S(0.35, 0.8), 1f + 2f * (dist - 0.2f) / 0.26f * S(0.5, 1.0), MathF.Atan2(y - 0.5f, x - 0.5f)));
        }
        foreach (var (x, y, r, wave, dark, elong, ang) in blobs) Stamp(alpha, shade, size, x, y, r, wave, dark, elong, ang);

        // a few downward drips from the core's and the lower lobes' edge: a run that tapers, with a bulb at its end
        int drips = rng.Next(2, 6);
        for (int d = 0; d < drips; d++)
        {
            float x = 0.5f + S(-0.18, 0.18);
            float y0 = 0.5f + S(0.08, 0.17);
            float len = S(0.1, 0.32);
            float w = S(0.007, 0.014);
            float y1 = MathF.Min(0.97f, y0 + len);
            for (float y = y0; y <= y1; y += 0.5f / size)
            {
                float taper = 1f - 0.45f * (y - y0) / MathF.Max(1e-3f, y1 - y0);
                Stamp(alpha, shade, size, x + 0.004f * MathF.Sin(y * 40f + d), y, w * taper, null, 0.3f, 1f, 0f);
            }
            Stamp(alpha, shade, size, x, y1, w * 1.8f, null, 0.3f, 1f, 0f);
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
    static void Stamp(float[] alpha, float[] shade, int size, float cx, float cy, float r, float[]? wave, float dark, float elong = 1f, float elongAng = 0f)
    {
        float reachR = r * 1.6f * MathF.Max(1f, elong);
        float ec = MathF.Cos(elongAng), es = MathF.Sin(elongAng);
        int x0 = Math.Max(0, (int)((cx - reachR) * size)), x1 = Math.Min(size - 1, (int)((cx + reachR) * size) + 1);
        int y0 = Math.Max(0, (int)((cy - reachR) * size)), y1 = Math.Min(size - 1, (int)((cy + reachR) * size) + 1);
        float edge = 1.5f / size;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f) / size - cx, dy = (y + 0.5f) / size - cy;
                if (elong > 1.001f)
                {
                    // an elongated droplet: squeezed along its radial axis
                    float u = (dx * ec + dy * es) / elong, v = -dx * es + dy * ec;
                    dx = u * ec - v * es; dy = u * es + v * ec;
                }
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
