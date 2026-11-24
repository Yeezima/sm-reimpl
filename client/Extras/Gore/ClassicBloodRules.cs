using System;
using System.Collections.Generic;
using System.Numerics;

namespace SmClient.ImpactRules;

/// <summary>
/// classic-hit-effects, gore part (Extended Classic PvP only; owner 2026-11-23, question card "Extended only"): the engine-free rules of the blood spray's ribbons and the fatal gunfire
/// headshot's wall decal.
/// <list type="bullet">
/// <item><see cref="RibbonPath"/>: the curve the blood is laid along, from the weapon's recorded samples on both sides of the contact (owner answer 4, 2026-11-24: "Redesign it"), with
/// <see cref="RibbonReach"/>, <see cref="RibbonCount"/> and <see cref="RibbonEnvelope"/> (the look's timing, mirrored by the shader of <c>ClassicBloodRibbons</c>).</item>
/// <item><see cref="DecalTrace"/>: extend the shot's real trajectory from the head contact; the first blocking surface along it, if it is an eligible wall (<see cref="IsWall"/>) within
/// <see cref="Reach"/> m, takes the decal at the exact intersection; otherwise none (no fallback surface). Only a fatal head shot by gunfire qualifies. Cosmetic: it does not touch the shot.</item>
/// <item><see cref="DecalShape"/>: the splash's alpha and shade, made in code from a seed (never from a texture): a dense core with a ragged, noisy outline, pronounced uneven lobes, fine
/// satellite splatter and a few downward drips; the same seed gives the same splash in every view.</item>
/// </list>
/// Every number is ours (kind: ours; spec/behavior.md "Classic hit effects").
/// </summary>
public static class ClassicBloodRules
{
    // ---- the spray's ribbons (third design, owner answer 4) ----

    /// <summary>What a spray's ribbons follow: a melee sweep's path, a thrust (from the exit side), a shot (from the exit side) or a bull-rush charge.</summary>
    public enum RibbonKind { Sweep, Thrust, Gunfire, Charge }

    /// <summary>Control points on each side of the contact, segments along one ribbon, and the most ribbons in one spray (the shared mesh).</summary>
    public const int RibbonHalf = 4, RibbonSegments = 20, MaxRibbons = 16;
    /// <summary>Ribbons for a normal hit (heavier and lethal hits scale it by their amount).</summary>
    public const int NormalRibbons = 7;
    /// <summary>The spray's life (s) and the end of its brightest hold (s): brightest from about 0.04 to 0.35 s after contact, strong to about 0.6 s, faint to about 1 s (the reference clip, measured in the review of 2b89de15).</summary>
    public const float RibbonSeconds = 1.0f, RibbonHoldSeconds = 0.32f;
    /// <summary>The additive brightness of the ribbons (ours).</summary>
    public const float RibbonGlow = 1.15f;
    /// <summary>How far each step of the curve turns toward the weapon's recorded path (share) and the most it turns per step (rad): it follows the swing without curling round the attacker (ours).</summary>
    public const float RibbonFollow = 0.75f, RibbonMaxTurn = 0.3f;

    public static int RibbonCount(float amount) => Math.Clamp((int)MathF.Round(NormalRibbons * amount, MidpointRounding.AwayFromZero), 1, MaxRibbons);
    public static float RibbonWidthScale(float amount) => 1f + 0.12f * MathF.Max(0f, amount - 1f);

    /// <summary>
    /// The reach (m) behind the contact (the side the weapon or shot came from) and past it, per kind: a sweep lays blood about 0.9 body heights past the body and part of that on the near
    /// side (the reference); a thrust, a shot and a charge mostly past it, from the exit side. Heavier and lethal hits reach a little farther.
    /// </summary>
    public static (float Back, float Forward) RibbonReach(RibbonKind kind, float amount)
    {
        var (b, f) = kind switch
        {
            RibbonKind.Thrust => (0.35f, 1.5f),
            RibbonKind.Gunfire => (0.3f, 1.5f),
            RibbonKind.Charge => (0.3f, 1.4f),
            _ => (0.9f, 1.8f),
        };
        float k = 1f + 0.1f * MathF.Max(0f, amount - 1f);
        return (b * k, f * k);
    }

    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }

    /// <summary>The spray's brightness at <paramref name="age"/> s after contact, 0..1 (the shader's envelope): up within about a frame, held to <see cref="RibbonHoldSeconds"/>, a smooth fade to <see cref="RibbonSeconds"/>.</summary>
    public static float RibbonEnvelope(float age) => age < 0f ? 0f : Smooth(0f, 0.035f, age) * (1f - Smooth(RibbonHoldSeconds, RibbonSeconds, age));

    /// <summary>How far along their reach the ribbons are laid at <paramref name="age"/> (the shader's reveal): about 80 % after one frame at 30 fps, all after 0.05 s.</summary>
    public static float RibbonReveal(float age) => 0.3f + 0.7f * Smooth(0f, 0.05f, age);

    /// <summary>
    /// The ribbons' curve, <see cref="RibbonHalf"/> * 2 + 1 points into <paramref name="into"/> relative to <paramref name="contact"/>: index <see cref="RibbonHalf"/> is the contact, below it
    /// the back part (<paramref name="back"/> m, the way the weapon came), above it the far part (<paramref name="forward"/> m, the way it went). Each part starts along
    /// <paramref name="dir"/> (the weapon's motion at the contact) and turns step by step toward the weapon's recorded path on that side of the contact (<paramref name="path"/>: points with
    /// their times from the contact, before it negative, in any space as long as all match; pose pops faster than <see cref="ClassicHitDirRules.MaxWeaponSpeed"/> skipped), at most
    /// <see cref="RibbonMaxTurn"/> per step. No path: a straight line along <paramref name="dir"/> (a shot, a charge).
    /// </summary>
    public static void RibbonPath(ReadOnlySpan<(double T, Vector3 P)> path, Vector3 contact, Vector3 dir, float back, float forward, Span<Vector3> into)
    {
        var d = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : Vector3.UnitX;
        into[RibbonHalf] = Vector3.Zero;
        var w0 = WeaponAt(path, contact);
        Walk(path, w0, +1, d, forward, into);
        Walk(path, w0, -1, -d, back, into);
    }

    /// <summary>The weapon point at the contact time (interpolated between the samples around time 0); no samples: <paramref name="fallback"/>.</summary>
    static Vector3 WeaponAt(ReadOnlySpan<(double T, Vector3 P)> path, Vector3 fallback)
    {
        if (path.Length == 0) return fallback;
        if (path[0].T >= 0.0) return path[0].P;
        for (int i = 1; i < path.Length; i++)
            if (path[i].T >= 0.0)
            {
                double dt = path[i].T - path[i - 1].T;
                float u = dt > 1e-9 ? (float)(-path[i - 1].T / dt) : 1f;
                return Vector3.Lerp(path[i - 1].P, path[i].P, Math.Clamp(u, 0f, 1f));
            }
        return path[^1].P;
    }

    static void Walk(ReadOnlySpan<(double T, Vector3 P)> path, Vector3 w0, int sign, Vector3 d, float length, Span<Vector3> into)
    {
        float step = MathF.Max(0f, length) / RibbonHalf;
        var cur = Vector3.Zero;
        for (int k = 1; k <= RibbonHalf; k++)
        {
            if (TangentAt(path, w0, sign, (k - 0.5f) * step) is { } t)
            {
                var want = Vector3.Lerp(d, t, RibbonFollow);
                if (want.LengthSquared() > 1e-8f) d = Turn(d, Vector3.Normalize(want), RibbonMaxTurn);
            }
            cur += d * step;
            into[RibbonHalf + sign * k] = cur;
        }
    }

    /// <summary>The direction of the weapon's path <paramref name="arc"/> m from the contact, walking forward (<paramref name="sign"/> 1) or back (-1) in time; null without a usable segment.</summary>
    static Vector3? TangentAt(ReadOnlySpan<(double T, Vector3 P)> path, Vector3 w0, int sign, float arc)
    {
        Vector3? last = null;
        float walked = 0f;
        var prev = w0; double prevT = 0.0;
        int n = path.Length;
        for (int j = 0; j < n; j++)
        {
            int i = sign > 0 ? j : n - 1 - j;
            double t = path[i].T;
            if (sign > 0 ? t <= 0.0 : t >= 0.0) continue;
            var seg = path[i].P - prev;
            float len = seg.Length();
            double dt = Math.Abs(t - prevT);
            if (len > 1e-4f && dt > 1e-6 && len / dt <= ClassicHitDirRules.MaxWeaponSpeed)
            {
                last = seg / len;
                walked += len;
                if (walked >= arc) return last;
                prev = path[i].P; prevT = t;
            }
            else if (len <= 1e-4f) { prevT = t; }
            // a pose pop is skipped: the next segment starts from the point before it
        }
        return last;
    }

    /// <summary><paramref name="from"/> turned toward <paramref name="to"/> (both unit) by at most <paramref name="max"/> radians.</summary>
    public static Vector3 Turn(Vector3 from, Vector3 to, float max)
    {
        float c = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (MathF.Acos(c) <= max) return to;
        var perp = to - from * c;
        if (perp.LengthSquared() < 1e-10f) return from;   // straight back: keep going
        perp = Vector3.Normalize(perp);
        return Vector3.Normalize(from * MathF.Cos(max) + perp * MathF.Sin(max));
    }

    // ---- the fatal headshot's wall decal ----

    /// <summary>How far behind the head the wall may be (m; the brief's cutoff).</summary>
    public const float Reach = 5f;
    /// <summary>The steepest a surface may face up or down and still be a wall: |normal.z| at most this (about 30 degrees off vertical; ours).</summary>
    public const float WallUpLimit = 0.5f;
    /// <summary>The decal's side (m), its depth (m) and its life (s; under the shared alpha-decal cap and eviction fade).</summary>
    public const float DecalSize = 1.1f, DecalDepth = 0.5f, DecalSeconds = 30f;
    /// <summary>The splash texture's side (texels).</summary>
    public const int DecalTexels = 256;

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
    /// The colour value of a texel of the splash from its shade (0 darkest .. 1 lightest): 0.5 to 1 of the blood colour, so the dense core reads as dark blood red on a lit wall, never
    /// near black (review of 2b89de15: 12-28 % read black).
    /// </summary>
    public static float DecalValue(float shade) => 0.5f + 0.5f * Math.Clamp(shade, 0f, 1f);

    /// <summary>
    /// The splash, <paramref name="size"/> square: alpha (0..1) and shade (0 darkest .. 1 lightest; the core is darkest) per texel, row 0 at the top (rows grow downward, the drips' way).
    /// The splash is centred: its core sits on the middle texel. Third design (review of 2b89de15): the outline is a noise field, not round blobs: a core whose radius follows 4 to 9
    /// uneven lobes (one or two long), broken up by two scales of value noise into a ragged, spiky edge with gaps in the lobes; then fine satellite splatter (many specks, a few larger
    /// drops, some in short lines along the lobes) and 2 to 5 wavering downward drips from the lower edge.
    /// </summary>
    public static (float[] Alpha, float[] Shade) DecalShape(int seed, int size = DecalTexels)
    {
        var rng = new Random(seed);
        var alpha = new float[size * size];
        var shade = new float[size * size];
        for (int i = 0; i < shade.Length; i++) shade[i] = 1f;
        float S(double lo, double hi) => (float)(lo + rng.NextDouble() * (hi - lo));
        int noiseSeed = rng.Next();

        // the silhouette: core radius, overall stretch, lobes (angle, length, width), one or two long
        float r0 = S(0.14, 0.18);
        float stretch = S(0.85, 1.2), stretchAng = S(0, Math.Tau);
        float sc = MathF.Cos(stretchAng), ss = MathF.Sin(stretchAng);
        int lobes = rng.Next(4, 10);
        var lobeAng = new float[lobes]; var lobeLen = new float[lobes]; var lobeW = new float[lobes];
        int longOnes = rng.Next(1, 3);
        for (int l = 0; l < lobes; l++)
        {
            lobeAng[l] = (float)(l * Math.Tau / lobes + rng.NextDouble() * 0.8);
            lobeLen[l] = l < longOnes ? S(0.14, 0.26) : S(0.03, 0.12);
            lobeW[l] = l < longOnes ? S(0.18, 0.32) : S(0.25, 0.55);
        }

        float Radius(float ang)
        {
            float r = r0;
            for (int l = 0; l < lobes; l++)
            {
                float da = MathF.Abs(MathF.IEEERemainder(ang - lobeAng[l], MathF.Tau));
                float g = da / lobeW[l];
                r += lobeLen[l] * MathF.Exp(-g * g);
            }
            return r;
        }

        float h = 1f / size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float px = (x + 0.5f) * h - 0.5f, py = (y + 0.5f) * h - 0.5f;
                // undo the variant's stretch
                float a = px * sc + py * ss, b = -px * ss + py * sc;
                a /= stretch; b *= stretch;
                float d = MathF.Sqrt(a * a + b * b);
                if (d > 0.49f) continue;
                float ang = MathF.Atan2(b, a);
                float coarse = Fbm(noiseSeed, px * 7f, py * 7f, 3);
                float fine = Fbm(noiseSeed + 977, px * 26f, py * 26f, 2);
                float edge = Radius(ang) * (0.78f + 0.44f * coarse) * (1f + 0.42f * (fine - 0.5f));
                float lip = edge - d;
                if (lip <= -2f * h) continue;
                float al = Math.Clamp(lip / (1.6f * h) + 0.5f, 0f, 1f);
                // thin gaps where the lobes run out (the paint breaks up toward their tips)
                float inner = Math.Clamp(lip / MathF.Max(edge, 1e-3f), 0f, 1f);
                if (d > r0) al *= Smooth(0.22f, 0.4f, coarse * 0.7f + fine * 0.3f + inner * 0.9f);
                if (al <= 0f) continue;
                int i = y * size + x;
                alpha[i] = MathF.Max(alpha[i], al * (0.86f + 0.1f * inner));
                // darkest in the dense middle, mottled, lighter toward the thin edges and the lobes (not by angle: no radial streaks)
                float sh = 0.12f + 0.6f * Smooth(0.2f * r0, 1.7f * r0, d) + 0.35f * (coarse - 0.5f) + 0.12f * (fine - 0.5f) + 0.25f * (1f - Smooth(0f, 2.5f * h, lip));
                shade[i] = MathF.Min(shade[i], Math.Clamp(sh, 0f, 1f));
            }

        // fine satellite splatter: many specks near the edge and along the lobes, a few larger drops, some in short lines
        int specks = rng.Next(90, 170);
        for (int k = 0; k < specks; k++)
        {
            float ang = rng.NextDouble() < 0.65 ? lobeAng[rng.Next(lobes)] + S(-0.35, 0.35) : S(0, Math.Tau);
            float dist = MathF.Min(0.47f, Radius(ang) * S(0.85, 1.6));
            float r = 0.0055f + 0.012f * (float)Math.Pow(rng.NextDouble(), 3.0);
            Dot(alpha, shade, size, 0.5f + MathF.Cos(ang) * dist, 0.5f + MathF.Sin(ang) * dist, r, S(0.35, 0.75), rng);
        }
        int drops = rng.Next(4, 10);
        for (int k = 0; k < drops; k++)
        {
            float ang = S(0, Math.Tau);
            float dist = MathF.Min(0.44f, Radius(ang) * S(1.05, 1.5));
            Dot(alpha, shade, size, 0.5f + MathF.Cos(ang) * dist, 0.5f + MathF.Sin(ang) * dist, S(0.015, 0.03), S(0.3, 0.6), rng);
        }
        int lines = rng.Next(2, 5);
        for (int k = 0; k < lines; k++)
        {
            float ang = lobeAng[rng.Next(lobes)] + S(-0.2, 0.2);
            float from = Radius(ang) * 0.9f;
            int n = rng.Next(4, 9);
            for (int j = 0; j < n; j++)
            {
                float dist = MathF.Min(0.47f, from + j * S(0.012, 0.03));
                Dot(alpha, shade, size, 0.5f + MathF.Cos(ang) * dist, 0.5f + MathF.Sin(ang) * dist, 0.006f + 0.006f * (1f - j / (float)n), S(0.4, 0.7), rng);
            }
        }

        // a few downward drips from the lower edge: a wavering run that thins, ending in a small bead
        int drips = rng.Next(2, 6);
        for (int k = 0; k < drips; k++)
        {
            float x = 0.5f + S(-0.2, 0.2);
            int col = Math.Clamp((int)(x * size), 0, size - 1);
            int start = size / 2;
            for (int y = size / 2; y < size; y++) if (alpha[y * size + col] > 0.5f) start = y;   // the lowest painted texel in this column
            float y0 = (start + 0.5f) * h - 0.01f;
            float len = S(0.07, 0.3);
            float w = S(0.006, 0.012);
            float y1 = MathF.Min(0.97f, y0 + len);
            float phase = S(0, 10);
            for (float y = y0; y <= y1; y += 0.4f * h)
            {
                float t = (y - y0) / MathF.Max(1e-3f, y1 - y0);
                Disc(alpha, shade, size, x + 0.006f * MathF.Sin(y * 55f + phase), y, w * (1f - 0.5f * t), 0.4f, 0.9f);
            }
            Disc(alpha, shade, size, x, y1, w * 1.25f, 0.35f, 0.9f);
        }
        return (alpha, shade);
    }

    /// <summary>A speck or drop: a small disc with a ragged edge (four random harmonics).</summary>
    static void Dot(float[] alpha, float[] shade, int size, float cx, float cy, float r, float dark, Random rng)
    {
        float w1 = (float)rng.NextDouble() * 0.35f, p1 = (float)(rng.NextDouble() * Math.Tau), w2 = (float)rng.NextDouble() * 0.2f, p2 = (float)(rng.NextDouble() * Math.Tau);
        float reach = r * 1.6f;
        int x0 = Math.Max(0, (int)((cx - reach) * size)), x1 = Math.Min(size - 1, (int)((cx + reach) * size) + 1);
        int y0 = Math.Max(0, (int)((cy - reach) * size)), y1 = Math.Min(size - 1, (int)((cy + reach) * size) + 1);
        float edge = 1.2f / size;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f) / size - cx, dy = (y + 0.5f) / size - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float ang = MathF.Atan2(dy, dx);
                float rr = r * (1f + w1 * MathF.Sin(3f * ang + p1) + w2 * MathF.Sin(5f * ang + p2));
                float a = Math.Clamp((rr - d) / edge + 0.5f, 0f, 1f) * 0.9f;
                if (a <= 0f) continue;
                int i = y * size + x;
                if (a > alpha[i]) alpha[i] = a;
                float s = 1f - (1f - dark) * a;
                if (s < shade[i]) shade[i] = s;
            }
    }

    static void Disc(float[] alpha, float[] shade, int size, float cx, float cy, float r, float dark, float opacity)
    {
        float reach = r + 2f / size;
        int x0 = Math.Max(0, (int)((cx - reach) * size)), x1 = Math.Min(size - 1, (int)((cx + reach) * size) + 1);
        int y0 = Math.Max(0, (int)((cy - reach) * size)), y1 = Math.Min(size - 1, (int)((cy + reach) * size) + 1);
        float edge = 1.2f / size;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f) / size - cx, dy = (y + 0.5f) / size - cy;
                float a = Math.Clamp((r - MathF.Sqrt(dx * dx + dy * dy)) / edge + 0.5f, 0f, 1f) * opacity;
                if (a <= 0f) continue;
                int i = y * size + x;
                if (a > alpha[i]) alpha[i] = a;
                float s = 1f - (1f - dark) * a;
                if (s < shade[i]) shade[i] = s;
            }
    }

    /// <summary>Value noise (0..1) on an integer lattice hashed with <paramref name="seed"/>, smooth between lattice points; <paramref name="octaves"/> summed at doubling frequency.</summary>
    static float Fbm(int seed, float x, float y, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * ValueNoise(seed + o * 1013, x, y);
            norm += amp;
            x *= 2.03f; y *= 2.03f; amp *= 0.5f;
        }
        return sum / norm;
    }

    static float ValueNoise(int seed, float x, float y)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
        float a = Lattice(seed, ix, iy), b = Lattice(seed, ix + 1, iy), c = Lattice(seed, ix, iy + 1), d = Lattice(seed, ix + 1, iy + 1);
        return (a + (b - a) * ux) + ((c + (d - c) * ux) - (a + (b - a) * ux)) * uy;
    }

    static float Lattice(int seed, int x, int y)
    {
        unchecked
        {
            uint hsh = (uint)seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)y * 0xC2B2AE3Du;
            hsh ^= hsh >> 15; hsh *= 0x2C1B3C6Du; hsh ^= hsh >> 12; hsh *= 0x297A2D39u; hsh ^= hsh >> 15;
            return (hsh & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
