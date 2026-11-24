using System;
using System.Collections.Generic;
using System.Numerics;

namespace SmClient.ImpactRules;

/// <summary>
/// classic-hit-effects, gore part (Extended Classic PvP only; owner 2026-11-23, question card "Extended only"): the engine-free rules of the blood spray's ribbons and the fatal gunfire
/// headshot's wall decal.
/// <list type="bullet">
/// <item><see cref="RibbonPath"/>: the curve the blood is laid along, from the weapon's recorded samples on both sides of the contact (owner answer 4, 2026-11-24: "Redesign it"), with
/// <see cref="RibbonReach"/>, <see cref="RibbonCount"/> and <see cref="RibbonEnvelope"/> (the look's timing, mirrored by the shader of <c>ClassicBloodRibbons</c>); the bands' opacity, width
/// and draw order (<see cref="RibbonOpacity"/>, <see cref="RibbonHalfWidth"/>, <see cref="RibbonRenderPriority"/>; the visibility pass of 2026-11-24, owner answer 6).</item>
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
    public const int NormalRibbons = 3;
    /// <summary>The spray's life (s) and the end of its brightest hold (s): brightest from about 0.04 to 0.35 s after contact, strong to about 0.6 s, faint to about 1 s (the reference clip, measured in the review of 2b89de15).</summary>
    public const float RibbonSeconds = 1.1f, RibbonHoldSeconds = 0.31f;
    /// <summary>The fade after the hold (1/s): half at about 0.45 s, a quarter at about 0.57 s, a tenth at about 0.73 s (review 3, the reference's curve).</summary>
    public const float RibbonDecay = 5.4f;
    /// <summary>The slower fade of the last faint wisps after <see cref="RibbonTailFrom"/> s (1/s; the reference's last red about 0.95 s).</summary>
    public const float RibbonTailDecay = 3f, RibbonTailFrom = 0.6f;
    /// <summary>
    /// A band's opacity at its middle while held (ours; was 0.8 before the visibility pass of 2026-11-24, owner answer 6). The bands are MIXED over the scene (<c>blend_mix</c>), never added:
    /// the colour can never be brighter than the blood tint, however many bands, sprays or lights stack, which is the brightness cap of review 3 (finding 5) and owner answer 5. Stacked bands
    /// only grow more opaque (toward solid blood red), never white.
    /// </summary>
    public const float RibbonOpacity = 0.95f;
    /// <summary>
    /// How the band's mottled score is weighted (ours; was the weakest patch's opacity, 0.35 then 0.6; 0.25 since repair 3, the re-review of 50ae0bd7, finding 1: at 0.6 every interior
    /// texel scored above the held threshold, so the core stayed solid to about 0.45 s): the score of a texel is its shape (1 in the middle to 0 at the ragged edge and the staggered ends)
    /// times this to 1 by the mottle noise; it decides which texels stay blood as the band erodes (<see cref="RibbonScore"/>).
    /// </summary>
    public const float RibbonMottleFloor = 0.25f;
    /// <summary>
    /// The darkest and the brightest a band's colour gets by its mottle, as shares of the blood tint (ours; 0.8 to 1 at 3570b719, 0.3 to 0.55 since the repair of review 5): the same hue,
    /// a deeper blood red. Near-full red unshaded goes through the filmic/AgX tonemapper toward orange (measured p95 G/R 0.25 to 0.30 on screen at 0.55 to 0.92); at 0.3 to 0.55 it
    /// measured 0.06 to 0.12 at the hold (the owner's "red or darker").
    /// </summary>
    public const float RibbonShadeFloor = 0.3f, RibbonShadeTop = 0.55f;
    /// <summary>The thinnest the blood gets inside a patch, as a share of <see cref="RibbonOpacity"/> (ours; repair of review 5): mottled in density as well as shade; the strip pass keeps it red.</summary>
    public const float RibbonDensityFloor = 0.7f;
    /// <summary>A band's half width at the body for a normal hit (m; ours; was 0.075): before the per-band x0.8 to x1.3, the narrowing to 0.6 at the ends and the growth while it hangs.</summary>
    public const float RibbonHalfWidth = 0.12f;
    /// <summary>How tall the stack of bands is across the stroke for a sweep at least (m; ours; was 0.3), scaled by the spread; more bands stand farther apart (<see cref="RibbonBundleFor"/>).</summary>
    public const float RibbonBundle = 0.42f;
    /// <summary>
    /// The least distance between neighbouring bands' middles, as a share of a band's mean width at the body (ours; repair 3, finding 1: a lethal hit's 6 bands stood about 0.1 m apart
    /// while each was 0.28 to 0.46 m wide, one opaque slab that hid the stroke's direction). At 1 the bands' ragged, eroding edges leave gaps between them.
    /// </summary>
    public const float RibbonPitchShare = 1f;
    /// <summary>The most bands in one spray (ours; repair 3, finding 1: 3 for a normal hit, 4 for heavy and lethal ones, which also grow wider and longer).</summary>
    public const int RibbonMaxBands = 4;
    /// <summary>A band's mean full width at the body before the hang's growth (m): twice <see cref="RibbonHalfWidth"/> times the shader's mean per-band factor 0.8 + 0.5 x 0.5.</summary>
    public const float RibbonMeanWidth = 2f * RibbonHalfWidth * 1.05f;
    /// <summary>
    /// The height of the stack of bands across the stroke (m; the shader's <c>bundle</c>): <see cref="RibbonBundle"/> by the spread (0.35 to 1.25 of 0.42 m), and never less than
    /// <paramref name="count"/> pitches (<see cref="RibbonPitchShare"/> of <see cref="RibbonMeanWidth"/>), so neighbouring bands' middles are at least a band's width apart; times the
    /// hit's width scale <paramref name="size"/>. The bands' offsets and widths grow together while they hang, so this ratio holds throughout.
    /// </summary>
    public static float RibbonBundleFor(int count, float spread, float size) =>
        MathF.Max(RibbonBundle * Math.Clamp(spread / 0.42f, 0.35f, 1.25f), Math.Max(1, count) * RibbonPitchShare * RibbonMeanWidth) * size;
    /// <summary>
    /// The erosion threshold at the hold (ours; 0.04 at the repair of review 5, 0.35 since repair 3, finding 1): a texel is blood while its score is above
    /// <c>1 - (1 - this) * envelope^<see cref="RibbonErodeCurve"/></c> (<see cref="RibbonThreshold"/>), so at the hold the band's middle and its denser mottle show, its soft rim not, and
    /// as the envelope falls the band breaks into shrinking patches and specks from the edges inward. The band never fades through translucency (a translucent band over a light floor read
    /// as pink); its covered area follows <see cref="RibbonEnvelope"/> (tested by <see cref="RibbonCoverage"/>).
    /// </summary>
    public const float RibbonErodeFloor = 0.35f;
    /// <summary>
    /// The power the envelope is raised to for the erosion threshold (ours; repair 3, finding 1; 1 before): the score's spread is narrower than its range, so a threshold straight on the
    /// envelope cleared the band too late in the middle and too early in the tail; 0.7 makes the drawn area (the hang's growth included) follow the envelope (0.47 / 0.21 / 0.115 at 0.45 / 0.6 / 0.8 s; 0.55 left the measured screen red about 0.1 above it).
    /// The early break-up term of the repair of review 5 (+0.1 x envelope from 0.22 to 0.45 s) is gone: the held band is already mottled.
    /// </summary>
    public const float RibbonErodeCurve = 0.7f;

    /// <summary>The erosion threshold at <paramref name="age"/> s (the shader's <c>thr</c>): 1 before contact and after the life (nothing shows), <see cref="RibbonErodeFloor"/> at the hold.</summary>
    public static float RibbonThreshold(float age) => 1f - (1f - RibbonErodeFloor) * MathF.Pow(RibbonEnvelope(age), RibbonErodeCurve);

    /// <summary>A band texel's score (the shader's <c>score</c>) from its shape <paramref name="prof"/> across, <paramref name="taper"/> along, and its mottle and grain noise (0..1).</summary>
    public static float RibbonScore(float prof, float taper, float n, float grain) =>
        prof * taper * (RibbonMottleFloor + (1f - RibbonMottleFloor) * Smooth(0.15f, 0.85f, 0.75f * n + 0.25f * grain));
    /// <summary>The half width is kept under this share of the curve's local radius (ours; repair of review 5, finding 2: a wide camera-facing strip on a bend folded into stepped edges).</summary>
    public const float RibbonFoldShare = 0.7f;
    /// <summary>The share of its width a band keeps when seen along its length (its tangent toward the camera; ours; repair of review 5, finding 2), smoothly up to all of it side-on.</summary>
    public const float RibbonEndOnWidth = 0.35f;
    /// <summary>
    /// The ribbons' render priority (ours; 0 before the visibility pass, 10 at 3570b719, 1 since the repair of review 5): the strip pass at this, the blood at one more, so they draw just
    /// after the effects batch (<c>FxBatch</c>: every effect sprite, the power weapons' hit flash and cloud among them, is one additive and one alpha draw at the default priority 0 with a
    /// view-dependent sort depth), and the flash at the same contact lies under the blood. Anything at priority 3 or more still draws over the blood (2 shares the blood's priority and sorts
    /// with it by depth). Depth-tested against the level and bodies.
    /// </summary>
    public const int RibbonRenderPriority = 1;
    /// <summary>
    /// The strip pass (drawn before the blood, <c>blend_mul</c>) takes the green and blue out of what lies under a band wherever the band's opacity is above this (ours; owner
    /// 2026-11-24, series 37 answer 4 "Blood only"): full, not in proportion, so no opacity over any background shows pink (repair of review 5, finding 1).
    /// </summary>
    public const float RibbonStripFrom = 1f / 255f;

    /// <summary>The strip pass's strength for a band texel of opacity <paramref name="a"/> (the shader's <c>strip</c>): 1 wherever the band covers the pixel, else 0.</summary>
    public static float RibbonStrip(float a) => a > RibbonStripFrom ? 1f : 0f;

    /// <summary>
    /// The share of the red under a band that the strip pass keeps (ours; 1 before repair 3, finding 3: the full red of a bright surface under a band's thinner patches and anti-aliased
    /// rim was brighter than the blood's darker shade and went orange through the tonemapper). At 0.4 the red underneath is no brighter than the blood's own shade
    /// (<see cref="RibbonShadeFloor"/> to <see cref="RibbonShadeTop"/>) over any surface up to full red.
    /// </summary>
    public const float RibbonStripRed = 0.4f;

    /// <summary>
    /// The final colour of a pixel of colour <paramref name="under"/> under a band texel of opacity <paramref name="a"/> and mottle <paramref name="n"/>, through both passes (the shader's math):
    /// the strip multiplies it by (<see cref="RibbonStripRed"/>, 0, 0) at <see cref="RibbonStrip"/> (part of its red kept, its green and blue taken out), then the blood colour is mixed
    /// over it at <paramref name="a"/>, so the only green and blue left are the blood's own share of the blood's red.
    /// </summary>
    public static (float R, float G, float B) RibbonComposite((float R, float G, float B) under, float a, float n)
    {
        float s = RibbonStrip(a);
        var u = (under.R * (1f - s + s * RibbonStripRed), under.G * (1f - s), under.B * (1f - s));
        var c = RibbonColour(n);
        return (a * c.R + (1 - a) * u.Item1, a * c.G + (1 - a) * u.Item2, a * c.B + (1 - a) * u.Item3);
    }

    /// <summary>How far each step of the curve turns toward the weapon's recorded path (share) and the most it turns per step (rad): it follows the swing without curling round the attacker (ours).</summary>
    public const float RibbonFollow = 0.75f, RibbonMaxTurn = 0.3f;

    /// <summary>The ribbons' blood red (0..1 linear RGB): today's Classic health-hit red, the same value as <c>ClassicHitSpray.BloodTint</c> (the colour the brief keeps).</summary>
    public static readonly (float R, float G, float B) BloodRgb = (1f, 0.12f, 0.08f);

    /// <summary>The most green or blue a blood colour may carry, as a share of its red (owner, 2026-11-24, series 37 answer 4 "Blood only": red or darker, never white or pink).</summary>
    public const float BloodOnlyGreenBlue = 0.15f;

    /// <summary>
    /// Whether a colour is blood only: no channel above <paramref name="maxRed"/> (the blood's own red: nothing brighter than it), and green and blue at most
    /// <see cref="BloodOnlyGreenBlue"/> of its red (no pink, no white). Owner 2026-11-24, series 37 answer 4.
    /// </summary>
    public static bool IsBloodOnly((float R, float G, float B) c, float maxRed) =>
        c.R <= maxRed + 1e-5f && c.G <= BloodOnlyGreenBlue * c.R + 1e-5f && c.B <= BloodOnlyGreenBlue * c.R + 1e-5f && c.R >= 0f && c.G >= 0f && c.B >= 0f;

    /// <summary>A ribbon texel's colour (the shader's <c>ALBEDO</c>) for its mottle noise <paramref name="n"/> (0..1): the blood red, darker in the dense patches, never brighter.</summary>
    public static (float R, float G, float B) RibbonColour(float n)
    {
        float k = RibbonShadeFloor + (RibbonShadeTop - RibbonShadeFloor) * Smooth(0.15f, 0.85f, n);
        return (BloodRgb.R * k, BloodRgb.G * k, BloodRgb.B * k);
    }

    /// <summary>A decal texel's colour from the blood colour <paramref name="blood"/> and its shade (<see cref="DecalValue"/>): the same red, darker in the core, never brighter.</summary>
    public static (float R, float G, float B) DecalColour((float R, float G, float B) blood, float shade)
    {
        float s = DecalValue(shade);
        return (blood.R * s, blood.G * s, blood.B * s);
    }

    /// <summary>Bands for a hit of <paramref name="amount"/>: 3 for a normal hit, more for heavier ones up to <see cref="RibbonMaxBands"/> (repair 3: was up to 6 for a lethal hit).</summary>
    public static int RibbonCount(float amount) => Math.Clamp((int)MathF.Round(NormalRibbons * amount, MidpointRounding.AwayFromZero), 1, Math.Min(RibbonMaxBands, MaxRibbons));

    /// <summary>
    /// The engine-free model of a spray's covered area (repair 3, finding 1; the shader's fragment math with its value noise, sampled over a grid of every band's length and width,
    /// weighted by the band's width there): the bands' blood area at <paramref name="age"/> s as a share of their geometric area at contact (so including the hang's growth: x(1 + hold) wide, x(1 + 0.27 hold) long, hold = min(age, 0.6)), and the share of their middles (the inner 35 % across, the
    /// inner 35 % of each band's reach) that is. <paramref name="seeds"/> sprays of <paramref name="count"/> sweep bands are averaged. Used by the tests to hold the covered area to the
    /// envelope; it ignores the anti-aliased rim (a pixel wide) and the view.
    /// </summary>
    public static (float Area, float Core) RibbonCoverage(float age, int count = NormalRibbons, int seeds = 6, int along = 60, int across = 24)
    {
        float thr = RibbonThreshold(age);
        double tot = 0, cov = 0, ctot = 0, ccov = 0;
        var (lenBack, lenFwd) = RibbonReach(RibbonKind.Sweep, 1f);
        for (int sd = 0; sd < seeds; sd++)
        {
            float seed = (float)((sd * 0.137) % 1.0);
            var rng = new Random(5413 + sd);
            for (int ri = 0; ri < count; ri++)
            {
                float h1 = (float)rng.NextDouble(), h2 = (float)rng.NextDouble();
                float fwd = lenFwd * (0.6f + 0.4f * h1), back = lenBack * (0.45f + 0.55f * h2);
                for (int i = 0; i < along; i++)
                {
                    float s = -back + (fwd + back) * (i + 0.5f) / along;
                    float dn = s >= 0f ? s / lenFwd : -s / lenBack;
                    float end = s >= 0f ? s / fwd : -s / back;
                    float w = 1f - 0.4f * MathF.Min(dn, 1f);
                    for (int j = 0; j < across; j++)
                    {
                        float x = -1f + 2f * (j + 0.5f) / across;
                        float edge = 0.62f + 0.38f * VNoise(s * 5f + ri * 7.3f + seed * 13f, ri * 3.1f + MathF.Sign(x) * 4.7f);
                        float prof = 1f - Smooth(edge - 0.45f, edge, MathF.Abs(x));
                        float taper = 1f - Smooth(0.4f, 1f, end);
                        float n = 0.6f * VNoise(s * 2.4f + seed * 17f + ri * 5.3f, x * 1.1f + ri * 9.1f + age * 0.4f) + 0.4f * VNoise(s * 7.5f - seed * 7f + ri * 2.7f, x * 2.4f - age * 0.7f);
                        float grain = VNoise(s * 16f + ri * 3.7f, x * 5f + seed * 11f);
                        bool blood = RibbonScore(prof, taper, n, grain) > thr;
                        tot += w; if (blood) cov += w;
                        if (MathF.Abs(x) < 0.35f && end < 0.35f) { ctot++; if (blood) ccov++; }
                    }
                }
            }
        }
        float hold = Math.Clamp(age, 0f, 0.6f);
        return ((float)(cov / Math.Max(tot, 1e-9) * (1f + hold) * (1f + 0.27f * hold)), (float)(ccov / Math.Max(ctot, 1e-9)));
    }

    /// <summary>The shader's <c>vnoise</c> (its sine hash, in double precision: the same field up to rounding).</summary>
    static float VNoise(float x, float y)
    {
        static double H(double px, double py) { double v = Math.Sin(px * 127.1 + py * 311.7) * 43758.5453; return v - Math.Floor(v); }
        double ix = Math.Floor(x), iy = Math.Floor(y), fx = x - ix, fy = y - iy;
        double ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
        double a = H(ix, iy), b = H(ix + 1, iy), c = H(ix, iy + 1), d = H(ix + 1, iy + 1);
        return (float)((a + (b - a) * ux) + ((c + (d - c) * ux) - (a + (b - a) * ux)) * uy);
    }
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
    public static float RibbonEnvelope(float age) => age < 0f ? 0f : Smooth(-0.01f, 0.06f, age)
        * MathF.Exp(-RibbonDecay * Math.Clamp(age - RibbonHoldSeconds, 0f, RibbonTailFrom - RibbonHoldSeconds) - RibbonTailDecay * MathF.Max(0f, age - RibbonTailFrom))
        * (1f - Smooth(RibbonSeconds - 0.15f, RibbonSeconds, age));

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

    /// <summary>
    /// Whether a fatal head shot and the server's headshot kill belong together, whichever arrived first: the shot is the killer's own (<paramref name="shooter"/> is
    /// <paramref name="killer"/>) and they are at most <paramref name="window"/> s apart (<paramref name="apart"/>). Every decal path asks this (review 4, finding 8: the kill-first path took any
    /// shooter's head shot).
    /// </summary>
    public static bool HeadshotPairs(int shooter, int killer, double apart, double window) => shooter == killer && apart >= 0.0 && apart <= window;

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
            lobeLen[l] = l < longOnes ? S(0.2, 0.32) : S(0.04, 0.15);
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
                float edge = Radius(ang) * (0.86f + 0.28f * coarse) * (1f + 0.36f * (fine - 0.5f));
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
            for (int y = size / 2; y < size && alpha[y * size + col] > 0.5f; y++) start = y;   // the core's lower edge in this column (not a speck below it; review 3)
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
