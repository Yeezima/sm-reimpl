using System;
using System.Collections.Generic;
using System.Numerics;
using SmClient.ImpactRules;
using static SmClient.ImpactRules.ClassicBloodRules;

namespace SmNet.Tests;

/// <summary>classic-hit-effects, the Extended profile's fatal gunfire headshot wall decal: when it is placed and the splash it draws.</summary>
public class ClassicBloodRulesTests
{
    static readonly Vector3 Wall = new(-1, 0, 0);   // a wall facing back along a +X shot
    static TraceHit At(float d, Vector3 normal, bool player = false) => new(d, new Vector3(d, 0, 1.7f), normal, player);

    [Theory]
    [InlineData(1.0f)]
    [InlineData(4.99f)]
    [InlineData(5.0f)]
    public void An_aligned_wall_at_or_inside_five_metres_takes_the_decal_at_the_intersection(float d)
    {
        var (r, hit) = DecalTrace(fatal: true, head: true, gunfire: true, [At(d, Wall)]);
        Assert.Equal(DecalResult.Placed, r);
        Assert.Equal(new Vector3(d, 0, 1.7f), hit.Point);
    }

    [Fact]
    public void Past_the_cutoff_absent_out_of_line_blocked_or_not_a_wall_leaves_none()
    {
        Assert.Equal(DecalResult.TooFar, DecalTrace(true, true, true, [At(5.01f, Wall)]).Result);
        Assert.Equal(DecalResult.NoWall, DecalTrace(true, true, true, []).Result);   // nothing on the line: no wall, or a wall only to the side (the ray misses it)
        Assert.Equal(DecalResult.Blocked, DecalTrace(true, true, true, [At(3f, Wall), At(1.2f, -Vector3.UnitX, player: true)]).Result);
        Assert.Equal(DecalResult.NotAWall, DecalTrace(true, true, true, [At(2f, Vector3.UnitZ)]).Result);    // the floor
        Assert.Equal(DecalResult.NotAWall, DecalTrace(true, true, true, [At(2f, -Vector3.UnitZ)]).Result);   // a ceiling
        // the first surface decides: a crate top in front of the wall blocks it (no fallback to the wall behind)
        Assert.Equal(DecalResult.NotAWall, DecalTrace(true, true, true, [At(4f, Wall), At(1f, Vector3.Normalize(new Vector3(-0.3f, 0, 1)))]).Result);
        Assert.True(IsWall(Vector3.Normalize(new Vector3(-1, 0, 0.4f))));   // a slightly leaning wall
    }

    [Fact]
    public void Only_a_fatal_gunfire_headshot_qualifies()
    {
        Assert.Equal(DecalResult.NotFatal, DecalTrace(fatal: false, head: true, gunfire: true, [At(2f, Wall)]).Result);
        Assert.Equal(DecalResult.NotHead, DecalTrace(fatal: true, head: false, gunfire: true, [At(2f, Wall)]).Result);
        Assert.Equal(DecalResult.NotGunfire, DecalTrace(fatal: true, head: true, gunfire: false, [At(2f, Wall)]).Result);
    }

    [Fact]
    public void The_splash_is_the_same_in_every_view_and_varies_between_events()
    {
        Assert.Equal(VariantSeed(12, 4), VariantSeed(12, 4));
        Assert.NotEqual(VariantSeed(12, 4), VariantSeed(13, 4));
        var a = DecalShape(VariantSeed(12, 4), 96);
        var b = DecalShape(VariantSeed(12, 4), 96);
        var c = DecalShape(VariantSeed(13, 4), 96);
        Assert.Equal(a.Alpha, b.Alpha);
        Assert.NotEqual(a.Alpha, c.Alpha);
    }

    [Fact]
    public void The_splash_has_a_dense_dark_core_at_the_centre_clear_corners_and_drips_below()
    {
        const int n = 128;
        float below = 0f, above = 0f;
        for (int seed = 1; seed <= 20; seed++)
        {
            var (alpha, shade) = DecalShape(seed, n);
            int centre = n / 2 * n + n / 2;
            Assert.True(alpha[centre] > 0.9f, $"seed {seed}: core");
            Assert.True(shade[centre] < 0.5f, $"seed {seed}: the core is darker");
            Assert.True(alpha[0] < 0.01f && alpha[n - 1] < 0.01f && alpha[(n - 1) * n] < 0.01f, $"seed {seed}: corners clear");
            // thin vertical bands outside the lobes' main reach: the drips run down, nothing runs up
            for (int y = (int)(n * 0.84); y < (int)(n * 0.97); y++) for (int x = (int)(n * 0.3); x < (int)(n * 0.7); x++) below += alpha[y * n + x];
            for (int y = (int)(n * 0.03); y < (int)(n * 0.16); y++) for (int x = (int)(n * 0.3); x < (int)(n * 0.7); x++) above += alpha[y * n + x];
        }
        Assert.True(below > above, $"drips below {below:F0} vs above {above:F0}");
    }

    [Fact]
    public void The_core_is_dark_blood_red_never_near_black()
    {
        Assert.True(DecalValue(0f) >= 0.45f);   // review of 2b89de15: 12-28 % of the blood colour read black
        Assert.Equal(1f, DecalValue(1f), 3);
        var (alpha, shade) = DecalShape(VariantSeed(3, 9), 128);
        Assert.True(DecalValue(shade[64 * 128 + 64]) < 0.8f, "the core is darker than the edges");
    }

    [Fact]
    public void The_outline_is_ragged_and_the_splatter_fine()
    {
        const int n = 192;
        for (int seed = 1; seed <= 8; seed++)
        {
            var (alpha, _) = DecalShape(VariantSeed(seed, 2), n);
            // ragged: the painted radius along 360 rays varies a lot between neighbouring rays (a round blob's does not)
            var radius = new float[360];
            for (int k = 0; k < 360; k++)
            {
                float a = k * MathF.PI / 180f;
                for (float r = 0f; r < 0.5f; r += 1f / n)
                {
                    int x = (int)((0.5f + MathF.Cos(a) * r) * n), y = (int)((0.5f + MathF.Sin(a) * r) * n);
                    if (x < 0 || y < 0 || x >= n || y >= n || alpha[y * n + x] < 0.5f) break;
                    radius[k] = r;
                }
            }
            float jump = 0f, min = float.MaxValue, max = 0f;
            for (int k = 0; k < 360; k++) { jump += MathF.Abs(radius[k] - radius[(k + 1) % 360]); min = MathF.Min(min, radius[k]); max = MathF.Max(max, radius[k]); }
            Assert.True(max > 1.8f * min, $"seed {seed}: uneven lobes ({min:F2} to {max:F2})");
            Assert.True(jump > 1.2f, $"seed {seed}: ragged outline ({jump:F2})");
            // fine splatter: many small separate specks outside the main shape
            int specks = 0;
            var seen = new bool[n * n];
            for (int i = 0; i < n * n; i++)
            {
                if (seen[i] || alpha[i] < 0.4f) continue;
                int size = 0; var stack = new Stack<int>(); stack.Push(i); seen[i] = true;
                while (stack.Count > 0)
                {
                    int j = stack.Pop(); size++;
                    int jx = j % n, jy = j / n;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int qx = jx + dx, qy = jy + dy;
                        if (qx < 0 || qy < 0 || qx >= n || qy >= n) continue;
                        int q = qy * n + qx;
                        if (!seen[q] && alpha[q] >= 0.4f) { seen[q] = true; stack.Push(q); }
                    }
                }
                if (size <= 40) specks++;
            }
            Assert.True(specks >= 20, $"seed {seed}: fine splatter ({specks} specks)");
        }
    }

    // ---- the spray's ribbons (third design, owner answer 4) ----

    static Vector3[] Curve(ReadOnlySpan<(double T, Vector3 P)> path, Vector3 dir, float back = 0.9f, float fwd = 1.8f)
    {
        var into = new Vector3[2 * RibbonHalf + 1];
        RibbonPath(path, Vector3.Zero, dir, back, fwd, into);
        return into;
    }

    [Fact]
    public void Without_a_path_the_ribbons_lie_straight_along_the_shot_on_both_sides_of_the_contact()
    {
        var c = Curve(ReadOnlySpan<(double, Vector3)>.Empty, new Vector3(0, 0, 2), back: 0.3f, fwd: 1.5f);
        Assert.Equal(Vector3.Zero, c[RibbonHalf]);
        Assert.Equal(1.5f, c[^1].Z, 3); Assert.Equal(0f, c[^1].X, 3);
        Assert.Equal(-0.3f, c[0].Z, 3);
        for (int k = 1; k < c.Length; k++) Assert.True(c[k].Z > c[k - 1].Z);
    }

    static (double, Vector3)[] Arc(float radius, float speed, int sign)
    {
        // a swing round an attacker standing at (0, -radius): through the contact at the origin, moving along +X (sign 1) or -X (sign -1)
        var pts = new (double, Vector3)[17];
        for (int k = 0; k < pts.Length; k++)
        {
            double t = (k - 8) * 0.016;
            float a = sign * speed * (float)t / radius;
            pts[k] = (t, new Vector3(radius * MathF.Sin(a), radius * MathF.Cos(a) - radius, 1.3f));
        }
        return pts;
    }

    [Fact]
    public void A_sweep_s_ribbons_follow_its_arc_on_both_sides_and_never_curl_round_the_attacker()
    {
        var c = Curve(Arc(1.6f, 12f, 1), Vector3.UnitX);
        Assert.True(c[^1].X > 1.2f, "the far part runs on along the swing");
        Assert.True(c[0].X < -0.5f, "the near part lies back along where it came from");
        Assert.True(c[^1].Y < -0.2f, "it bends with the arc, toward the attacker's side");
        Assert.True(c[0].Y < -0.05f, "both sides bend the same way (an arc, not an S)");
        // bounded: no part points back past the side (at most RibbonMaxTurn per step)
        for (int k = RibbonHalf + 1; k < c.Length; k++) Assert.True(c[k].X > c[k - 1].X);
        // the reverse swing mirrors it
        var r = Curve(Arc(1.6f, 12f, -1), -Vector3.UnitX);
        Assert.Equal(-c[^1].X, r[^1].X, 2); Assert.Equal(c[^1].Y, r[^1].Y, 2);
    }

    [Fact]
    public void A_vertical_stroke_lays_its_ribbons_vertically_and_a_pose_pop_is_skipped()
    {
        var pts = new (double, Vector3)[9];
        for (int k = 0; k < pts.Length; k++) { double t = (k - 4) * 0.02; pts[k] = (t, new Vector3(0, 0, -10f * (float)t)); }
        pts[6] = (pts[6].Item1, new Vector3(5, 5, 5));   // a pop
        var c = Curve(pts, -Vector3.UnitZ);
        Assert.True(c[^1].Z < -1.5f && MathF.Abs(c[^1].X) < 0.2f);
        Assert.True(c[0].Z > 0.6f);
    }

    [Fact]
    public void The_ribbons_are_laid_at_once_and_follow_the_reference_curve()
    {
        // review 3 of 569ad7f0, the reference's red excess (% of peak): at least 85 % from about 0.10 to 0.33 s, 50 / 25 / 10 % at about 0.45 / 0.57 / 0.73 s, last red about 0.95 s
        Assert.Equal(0f, RibbonEnvelope(-0.1f));
        Assert.True(RibbonReveal(1f / 30f) > 0.75f, "most of the reach after one frame");
        Assert.Equal(1f, RibbonReveal(0.05f), 3);
        Assert.InRange(RibbonEnvelope(1f / 30f), 0.5f, 0.85f);   // the reference: 75 % a frame after contact
        for (float t = 0.10f; t <= 0.33f; t += 0.01f) Assert.True(RibbonEnvelope(t) >= 0.85f, $"held at {t:F2}");
        Assert.InRange(RibbonEnvelope(0.45f), 0.4f, 0.6f);
        Assert.InRange(RibbonEnvelope(0.57f), 0.18f, 0.32f);
        Assert.InRange(RibbonEnvelope(0.73f), 0.08f, 0.2f);   // the shader's soft patches and ends take the measured curve to about 10 % here
        Assert.InRange(RibbonEnvelope(0.95f), 0.02f, 0.12f);   // faint wisps to about 0.95 s
        Assert.True(RibbonEnvelope(RibbonSeconds) < 0.001f);
        for (float t = 0.07f; t < RibbonSeconds; t += 1f / 30f) Assert.True(RibbonEnvelope(t) - RibbonEnvelope(t + 1f / 30f) < 0.2f);   // smooth: no pop
    }

    [Fact]
    public void The_visibility_pass_lays_broad_solid_bands_that_draw_over_the_power_flash_without_brightening()
    {
        // owner answer 6 (2026-11-24, "Visibility pass"): the release build read as faint red. Broader and more opaque than before (0.075 m half width, 0.8 opacity, 0.35 mottle floor)...
        Assert.True(RibbonHalfWidth >= 0.1f && RibbonHalfWidth <= 0.16f, "a band about a quarter of a metre wide at the body, not a thin streak and not a slab");
        Assert.InRange(RibbonOpacity, 0.9f, 1f);   // the cap is the mix blend (never brighter than the tint), so a single band may be nearly opaque
        // repair 3 (re-review of 50ae0bd7, finding 1): a mottle floor low enough that the band's middle erodes with the envelope, not after it (0.6 kept the core solid to 0.45 s)...
        Assert.InRange(RibbonMottleFloor, 0.15f, 0.35f);
        // ...and a held threshold high enough that the soft rim and the thin mottle are clear from the start (0.04 showed all of it)
        Assert.InRange(RibbonErodeFloor, 0.3f, 0.4f);
        Assert.InRange(RibbonShadeFloor, 0.2f, 0.9f);   // darker patches of the same red...
        Assert.InRange(RibbonShadeTop, RibbonShadeFloor, 1f);   // ...never brighter than the tint
        Assert.InRange(RibbonDensityFloor, 0.5f, 1f);
        Assert.True(RibbonBundle > 0.3f && RibbonBundle <= 0.5f);
        // ...and drawn after the additive power-weapon flash (FxBatch's materials keep the default priority 0)
        Assert.True(RibbonRenderPriority > 0 && RibbonRenderPriority <= 127);
        // heavier and lethal hits still show more by coverage (bands x width x reach), not brightness
        float Footprint(float amount) => RibbonCount(amount) * RibbonWidthScale(amount) * RibbonReach(RibbonKind.Sweep, amount).Forward;
        // since repair 5 (finding 3) at most 3 bands: heavy and lethal hits are wider (x1.15, x1.33) and longer instead of a fourth band
        Assert.True(Footprint(1.5f) > 1.15f * Footprint(1f));
        Assert.True(Footprint(2.1f) > 1.4f * Footprint(1f) && Footprint(2.1f) > Footprint(1.5f));
    }

    [Fact]
    public void The_covered_area_follows_the_envelope_and_the_middle_breaks_up_with_it()
    {
        // repair 3 (re-review of 50ae0bd7, finding 1): the core stayed fully covered to about 0.45 s, then 45 / 25 / 15 % at 0.6 / 0.8 / 0.95 s, against the reference's envelope
        // 0.47 / 0.21 / 0.115 at 0.45 / 0.6 / 0.8 s. The model samples the shader's own fragment math (its noise, profile, taper, score and threshold).
        var (sweepBack, sweepFwd) = RibbonReach(RibbonKind.Sweep, 1f);
        foreach (int count in new[] { 1, NormalRibbons, RibbonMaxBands })
        {
            float grow = RibbonGrow(count, RibbonBundleFor(count, ClassicHitDirRules.SweepSpread, 1f), 1f, sweepFwd, sweepBack);
            var held = RibbonCoverage(0.2f, count, grow);
            Assert.True(held.Core >= 0.7f, $"{count} bands: the middle is blood at the hold ({held.Core:F2})");   // 0.8 before repair 5: the grit opens small holes in it
            Assert.InRange(held.Area / ((1f + grow * 0.2f) * (1f + 0.27f * 0.2f)), 0.25f, 0.6f);   // the hang's growth to 0.2 s taken out: the soft rim and the thinnest mottle are clear: a ragged band, not a slab
            // the drawn area grows while it hangs (wider, longer) to its most at about 0.3 s: the envelope is a share of that (the reference's red excess, % of peak)
            var peak = (Area: MathF.Max(held.Area, RibbonCoverage(0.3f, count, grow).Area), held.Core);
            Assert.True(RibbonCoverage(0.1f, count, grow).Area >= 0.7f * peak.Area, "held from 0.1 s");
            foreach (float t in new[] { 0.35f, 0.45f, 0.6f, 0.8f, 0.95f })
            {
                var c = RibbonCoverage(t, count, grow);
                float want = RibbonEnvelope(t);
                float area = c.Area / peak.Area, core = c.Core / peak.Core;
                Assert.True(MathF.Abs(area - want) <= 0.07f + 0.25f * want, $"{count} bands at {t:F2} s: area {area:F3} against the envelope {want:F3}");
                Assert.True(MathF.Abs(core - want) <= 0.07f + 0.25f * want, $"{count} bands at {t:F2} s: middle {core:F3} against the envelope {want:F3}");
            }
            // the middle is broken up by 0.45 s (the finding: fully covered there), and nearly gone by 0.8 s
            Assert.True(RibbonCoverage(0.45f, count, grow).Core <= 0.65f);
            Assert.True(RibbonCoverage(0.8f, count, grow).Core <= 0.2f);
            Assert.True(RibbonCoverage(RibbonSeconds, count, grow).Area < 0.005f);
        }
        // the shader uses the same rule (its threshold is built from these constants)
        var src = RepoFile("client", "Extras", "Gore", "ClassicBloodRibbons.cs");
        Assert.Contains("float thr = 1.0 - {{F(1f - ClassicBloodRules.RibbonErodeFloor)}} * pow(max(env, 0.0), {{F(ClassicBloodRules.RibbonErodeCurve)}});", src);
        Assert.Contains("float score = prof * taper * mix(mfloor, 1.0, smoothstep(0.15, 0.85, 0.75 * n + 0.25 * grain));", src);
        Assert.Contains("score *= 1.0 - smoothstep(hthr, hthr + 0.06, fine);", src);
        Assert.Contains("float fine = vnoise(vec2(v_s * {{F(ClassicBloodRules.RibbonGritScale)}} + v_ri * 4.1 + seed * 19.0, x * 7.0 + seed * 5.0));", src);
        Assert.Contains("s.Set(PFloor, ClassicBloodRules.RibbonMottleFloorFor(count));", src);
        Assert.Equal(1f, RibbonThreshold(-0.05f), 4);
        Assert.Equal(RibbonErodeFloor, RibbonThreshold(0.2f), 3);
    }

    [Fact]
    public void Neighbouring_bands_stand_at_least_a_band_s_width_apart_so_the_stroke_s_direction_reads()
    {
        // repair 3, finding 1: a lethal hit's 6 bands about 0.1 m apart, each 0.28 to 0.46 m wide, merged into one opaque slab. Repair 4, finding N1: the stack under a body's height
        // over the WHOLE hang (it grows about 60 % while it hangs, and fans out 10 %/m toward the far end), not only at the contact.
        foreach (var kind in Enum.GetValues<RibbonKind>())
            foreach (float amount in new[] { 1f, ClassicHitDirRules.HeavyAmount, ClassicHitDirRules.LethalAmount })
                foreach (float spread in new[] { ClassicHitDirRules.ThrustSpread, 0.2f, 0.3f, 0.32f, ClassicHitDirRules.SweepSpread, 0.6f })
                {
                    int count = RibbonBands(kind, spread, amount);
                    float size = RibbonWidthScale(amount);
                    float bundle = RibbonBundleFor(count, spread, size);
                    var (back, fwd) = RibbonReach(kind, amount);
                    float grow = RibbonGrow(count, bundle, size, fwd, back);
                    Assert.InRange(grow, 0f, 1f);
                    Assert.True(bundle / count >= RibbonMeanWidth * size - 1e-5f, $"{kind} amount {amount}, spread {spread}: {count} bands {bundle / count:F3} m apart, each {RibbonMeanWidth * size:F3} m wide");
                    float far = fwd * (1f + RibbonStaggerOuter + 0.5f * RibbonStaggerEchelon + 0.5f * RibbonStaggerJitter);   // the farthest any band reaches (RibbonBandLayout: |slot| at most 0.5)
                    for (float age = 0f; age <= RibbonSeconds; age += 0.05f)
                        for (float s = -back; s <= far; s += 0.05f)
                        {
                            float h = RibbonStackHeight(count, bundle, size, grow, MathF.Min(age, RibbonHangSeconds), s, fwd, back);
                            Assert.True(h <= RibbonStackMax + 1e-3f, $"{kind} amount {amount}, spread {spread}: the stack {h:F2} m tall at {age:F2} s, {s:F2} m along");
                        }
                }
        Assert.True(RibbonStackMax <= 1.25f);
        Assert.Equal(3, RibbonCount(1f));
        Assert.Equal(RibbonMaxBands, RibbonCount(ClassicHitDirRules.LethalAmount));
        Assert.True(RibbonMaxBands <= 3);
        // a normal sweep still grows the full 60 % while it hangs (review 3's +63 %) unless that would pass the cap; a lethal one gives up most of it (it starts tall)
        var (sb, sf) = RibbonReach(RibbonKind.Sweep, ClassicHitDirRules.LethalAmount);
        int lethalBands = RibbonBands(RibbonKind.Sweep, ClassicHitDirRules.SweepSpread, ClassicHitDirRules.LethalAmount);
        float ls = RibbonWidthScale(ClassicHitDirRules.LethalAmount);
        // repair 5 (finding 3): with the bands parting by angle and 3 wider bands, a lethal sweep grows again while it hangs (it gave up nearly all of it with 4 bars)
        Assert.True(RibbonGrow(lethalBands, RibbonBundleFor(lethalBands, ClassicHitDirRules.SweepSpread, ls), ls, sf, sb) >= 0.5f);
        // the shader takes the stack's height, its band count and its growth share from these rules
        var src = RepoFile("client", "Extras", "Gore", "ClassicBloodRibbons.cs");
        Assert.Contains("uniform float bundle", src);
        Assert.Contains("uniform float grow", src);
        Assert.Contains("int count = ClassicBloodRules.RibbonBands(kind, spread, amount);", src);
        Assert.Contains("float bundle = ClassicBloodRules.RibbonBundleFor(count, spread, size);", src);
        Assert.Contains("s.Set(PGrow, ClassicBloodRules.RibbonGrow(count, bundle, size, fwd, back));", src);
        Assert.Contains("float off = (slot * bundle * div + (h4 - 0.5) * 0.06) * (1.0 + grow * hold);", src);
        Assert.Contains("float div = s <= 0.0 ? {{F(ClassicBloodRules.RibbonDivergeFrom)}} : mix({{F(ClassicBloodRules.RibbonDivergeFrom)}}, 1.0, clamp(s / max(len_fwd, 1e-3), 0.0, 1.0));", src);
        Assert.Contains("* mix(1.0, 0.6, min(dn, 1.0)) * (1.0 + grow * hold);", src);
        Assert.Contains("0.2 * hold * (1.0 + 0.8 * grow * (h1 - 0.5))", src);   // the lifts RibbonStackHeight allows for (RibbonLiftSpread 0.16 = 0.2 x 0.8, by the growth share)
        Assert.DoesNotContain("{{F(ClassicBloodRules.RibbonBundle)}}", src);
    }

    [Fact]
    public void The_spread_drives_the_stack_of_bands_for_every_kind()
    {
        // repair 4, re-review of 06ef6bc2 finding N1: the pitch rule had made every stack 0.756 m tall (a thrust's 0.147 m spread included) and a bolter headshot three bars high
        float Stack(RibbonKind kind, float spread, float amount)
        {
            int n = RibbonBands(kind, spread, amount);
            return RibbonBundleFor(n, spread, RibbonWidthScale(amount));
        }
        foreach (var kind in Enum.GetValues<RibbonKind>())
            foreach (float amount in new[] { 1f, ClassicHitDirRules.LethalAmount })
            {
                Assert.True(RibbonBands(kind, ClassicHitDirRules.ThrustSpread, amount) < RibbonBands(kind, ClassicHitDirRules.SweepSpread, amount), $"{kind} x{amount}: the narrow spread lays fewer bands");
                Assert.True(Stack(kind, ClassicHitDirRules.ThrustSpread, amount) < 0.8f * Stack(kind, ClassicHitDirRules.SweepSpread, amount), $"{kind} x{amount}: the narrow spread's stack is lower");
                Assert.True(RibbonBands(kind, 0.3f, amount) <= RibbonBands(kind, ClassicHitDirRules.SweepSpread, amount));
            }
        // each kind at its own spread: thrusts stay narrow (ClassicHitDirRules: "thrusts stay narrow, sweeps bloom wider")
        Assert.Equal(1, RibbonBands(RibbonKind.Thrust, ClassicHitDirRules.ThrustSpread, 1f));
        Assert.Equal(2, RibbonBands(RibbonKind.Thrust, ClassicHitDirRules.ThrustSpread, ClassicHitDirRules.LethalAmount));
        Assert.Equal(2, RibbonBands(RibbonKind.Gunfire, 0.3f, 1f));
        Assert.Equal(2, RibbonBands(RibbonKind.Gunfire, 0.32f, ClassicHitDirRules.LethalAmount));   // a lethal bolter headshot: two bands, not three
        Assert.Equal(2, RibbonBands(RibbonKind.Charge, ClassicHitDirRules.ChargeSpread, 1f));
        Assert.Equal(2, RibbonBands(RibbonKind.Charge, ClassicHitDirRules.ChargeSpread, ClassicHitDirRules.LethalAmount));
        Assert.Equal(3, RibbonBands(RibbonKind.Sweep, ClassicHitDirRules.SweepSpread, 1f));
        Assert.Equal(3, RibbonBands(RibbonKind.Sweep, ClassicHitDirRules.SweepSpread, ClassicHitDirRules.HeavyAmount));   // 4 before repair 5: wider, not more
        Assert.Equal(3, RibbonBands(RibbonKind.Sweep, ClassicHitDirRules.SweepSpread, ClassicHitDirRules.LethalAmount));
        // a normal thrust's stack is one band (0.25 m), a shot's two (0.5 m), a sweep's three (0.76 m)
        Assert.InRange(Stack(RibbonKind.Thrust, ClassicHitDirRules.ThrustSpread, 1f), 0.2f, 0.3f);
        Assert.InRange(Stack(RibbonKind.Gunfire, 0.3f, 1f), 0.45f, 0.55f);
        Assert.InRange(Stack(RibbonKind.Sweep, ClassicHitDirRules.SweepSpread, 1f), 0.7f, 0.8f);
        // and the fit itself grows with the spread, one band per half a band's width of the spread's height
        for (float sp = 0.05f; sp < 0.7f; sp += 0.01f) Assert.True(RibbonBandsFit(sp + 0.01f) >= RibbonBandsFit(sp));
    }

    [Fact]
    public void A_multi_band_hit_is_one_staggered_stroke_not_a_ladder_of_equal_stripes()
    {
        // repair 4, re-review of 06ef6bc2 finding N2: the bands started, ended and stood alike, about a band's width apart (jitter 0.03 m against a 0.25 m pitch): four equal stripes
        var (back, fwd) = RibbonReach(RibbonKind.Sweep, 1f);
        foreach (int count in new[] { 3, 4 })
            foreach (float lead in new[] { 1f, -1f })
            {
                var bands = new (float Shift, float Fwd, float Back, float Width)[count];
                for (int ri = 0; ri < count; ri++) bands[ri] = RibbonBandLayout(RibbonSlot(ri, count), 0.5f, 0.5f, 0.5f, lead, fwd, back);
                // the outer bands start past the body and are shorter and narrower than the middle ones: the blood fans out past the body from the middle
                // repair 5: the bands leave the body together (their middles at most 0.1 of the reach apart along it) and part by angle; one side reaches farther (slanted far ends)
                Assert.True(MathF.Abs(bands[0].Shift - bands[count / 2].Shift) <= 0.1f * fwd);
                Assert.True(bands[0].Fwd + bands[0].Back < bands[count / 2].Fwd + bands[count / 2].Back);
                Assert.True(bands[0].Width < bands[count / 2].Width && bands[^1].Width < bands[count / 2].Width);
                // one side leads (seeded): the two outer bands are offset along the stroke, the stack reads slanted
                Assert.True(MathF.Abs((bands[0].Shift + bands[0].Fwd) - (bands[^1].Shift + bands[^1].Fwd)) >= 0.2f * fwd);
                Assert.Equal(lead > 0f, bands[^1].Shift + bands[^1].Fwd > bands[0].Shift + bands[0].Fwd);
                // every band still crosses the body or starts at it (the blood is laid through the contact, not detached from it)
                foreach (var b in bands) Assert.True(b.Shift - b.Back <= 0.05f * fwd, $"a band starts {b.Shift - b.Back:F2} m past the contact");
            }
        // a single band is the full stroke through the body
        var one = RibbonBandLayout(RibbonSlot(0, 1), 0.5f, 0.5f, 0.5f, 1f, fwd, back);
        Assert.Equal(0f, one.Shift, 3);
        Assert.Equal(1f, one.Width / (0.8f + 0.5f * 0.5f), 3);
        // the shader lays the bands out the same way
        var src = RepoFile("client", "Extras", "Gore", "ClassicBloodRibbons.cs");
        Assert.Contains("float shift = len_fwd * ({{F(ClassicBloodRules.RibbonStaggerOuter)}} * c2 + {{F(ClassicBloodRules.RibbonStaggerJitter)}} * (h2 - 0.5));", src);
        Assert.Contains("float fwd = len_fwd * (0.6 + 0.4 * h1) * (1.0 - {{F(ClassicBloodRules.RibbonStaggerReach)}} * c2) * (1.0 + {{F(ClassicBloodRules.RibbonStaggerEchelon)}} * slot * lead);", src);
        // the across-stroke offsets grow from 0.3 of the bundle at the body to all of it at the far reach
        Assert.Equal(RibbonDivergeFrom, RibbonDiverge(0f, 2f));
        Assert.Equal(RibbonDivergeFrom, RibbonDiverge(-0.5f, 2f));
        Assert.Equal(1f, RibbonDiverge(2f, 2f));
        Assert.Equal(1f, RibbonDiverge(3f, 2f));
        Assert.InRange(RibbonDivergeFrom, 0.2f, 0.4f);
        Assert.Contains("float back = len_back * (0.45 + 0.55 * h2) * (1.0 - {{F(2f * ClassicBloodRules.RibbonStaggerReach)}} * c2);", src);
        Assert.Contains("float s = shift + sl;", src);
        Assert.Contains("(0.8 + 0.5 * h3) * (1.0 - {{F(ClassicBloodRules.RibbonStaggerWidth)}} * c2)", src);
    }

    [Fact]
    public void The_blood_is_red_or_darker_only_never_white_or_pink()
    {
        // owner 2026-11-24, series 37 answer 4 "Blood only": the ribbons and the headshot splatter use only red and darker colours
        Assert.True(IsBloodOnly(BloodRgb, BloodRgb.R));
        for (float n = 0f; n <= 1f; n += 0.05f)
        {
            var c = RibbonColour(n);
            Assert.True(IsBloodOnly(c, BloodRgb.R), $"ribbon colour at noise {n:F2}: {c}");
        }
        // the decal's blood is CharacterViewer.BloodColour (a Godot Color, read from its source so the test follows any change to it)
        var m = System.Text.RegularExpressions.Regex.Match(RepoFile("client", "CharacterViewer.Flash.cs"), @"BloodColour = new\(([0-9.]+)f, ([0-9.]+)f, ([0-9.]+)f\)");
        Assert.True(m.Success, "CharacterViewer.BloodColour not found");
        float P(int g) => float.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
        var decalBlood = (P(1), P(2), P(3));
        Assert.True(IsBloodOnly(decalBlood, decalBlood.Item1));
        for (int seed = 1; seed <= 4; seed++)
        {
            var (_, shade) = DecalShape(seed, 64);
            foreach (var s in shade) Assert.True(IsBloodOnly(DecalColour(decalBlood, s), decalBlood.Item1), $"decal shade {s}");
        }
        // the checker itself: white, pink and a brightened red are refused
        Assert.False(IsBloodOnly((1f, 1f, 1f), 1f));
        Assert.False(IsBloodOnly((1f, 0.5f, 0.55f), 1f));
        Assert.False(IsBloodOnly((1.2f, 0.1f, 0.1f), 1f));
    }

    static string RepoFile(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "SmReimpl.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return System.IO.File.ReadAllText(System.IO.Path.Combine([dir!.FullName, .. parts]));
    }

    [Fact]
    public void The_ribbon_shader_mixes_and_never_adds_and_its_tint_is_the_health_hit_red()
    {
        // the final on-screen colour: mixed over the scene, the colour can never exceed the tint (no additive brightening toward white, however bands stack)
        var src = RepoFile("client", "Extras", "Gore", "ClassicBloodRibbons.cs");
        Assert.Contains("render_mode blend_mix, unshaded", src);
        Assert.DoesNotContain("blend_add", src);
        Assert.Contains("ALBEDO = tint.rgb * mix(", src);   // the tint times a share at most 1
        // the strip pass multiplies what lies under the band by (1, 0, 0): red kept, green and blue taken out, nothing brightened
        Assert.Contains("\"render_mode blend_mix,\", \"render_mode blend_mul,\"", src);
        Assert.Contains("ALBEDO = mix(vec3(1.0), vec3({{F(ClassicBloodRules.RibbonStripRed)}}, 0.0, 0.0), strip);", src);
        Assert.Contains("float strip = cov * cov * cov;", src);   // full wherever the patch covers the pixel, feathered with its anti-aliased rim (RibbonStrip)
        Assert.Contains("float a = opacity * cov * mix({{F(ClassicBloodRules.RibbonDensityFloor)}}, 1.0, smoothstep(0.1, 0.9, 0.5 * n + 0.5 * grain));", src);   // RibbonAlpha
        Assert.Contains("BloodTint = new(1f, 0.12f, 0.08f)", RepoFile("client", "ClassicHitSpray.cs"));   // BloodRgb mirrors it
        Assert.Equal((1f, 0.12f, 0.08f), BloodRgb);
    }

    [Fact]
    public void No_opacity_over_any_background_adds_green_or_blue_relative_to_red()
    {
        // repair of review 5, finding 1 (owner's "only red or darker"): inside a patch (covered, every density of the mottle), through the strip and blood passes, over white and over
        // coloured and dark grounds, the pixel is blood only
        (float R, float G, float B)[] grounds = [(1f, 1f, 1f), (0.9f, 0.95f, 1f), (0.2f, 0.4f, 0.9f), (0f, 1f, 0f), (0.6f, 0.6f, 0.55f), (0.05f, 0.05f, 0.05f), (1f, 0.12f, 0.08f), (0f, 0f, 0f)];
        foreach (var g in grounds)
            for (int i = 0; i <= 200; i++)
            {
                float density = i / 200f;
                foreach (float n in new[] { 0f, 0.5f, 1f })
                {
                    var c = RibbonComposite(g, 1f, density, n);
                    Assert.True(c.R <= MathF.Max(g.R, BloodRgb.R) + 1e-5f && c.G <= MathF.Max(g.G, BloodRgb.G) + 1e-5f && c.B <= MathF.Max(g.B, BloodRgb.B) + 1e-5f, $"brighter at density {density} over {g}: {c}");
                    Assert.True(IsBloodOnly(c, 1f), $"pink at density {density:F3} over {g}: {c}");
                }
            }
    }

    [Fact]
    public void The_patch_s_anti_aliased_rim_blends_the_ground_and_the_blood_without_a_dark_contour()
    {
        // repair 4, re-review of 06ef6bc2 finding N3: the strip was full from 1/255 of opacity, so under the rim's faint blood it took the ground to 0.4 of its red and nothing else: a
        // near-black outline round every patch. Feathered with the coverage, every rim pixel lies between the ground and the patch's inside, channel by channel (no darker contour, no
        // colour that is neither), and it is never brighter than the ground or the blood
        (float R, float G, float B)[] grounds = [(1f, 1f, 1f), (0.9f, 0.95f, 1f), (0.2f, 0.4f, 0.9f), (0.6f, 0.6f, 0.55f), (0.3f, 0.3f, 0.3f), (0.05f, 0.05f, 0.05f), (1f, 0.12f, 0.08f), (3f, 1.5f, 0.4f)];
        static float Lum((float R, float G, float B) c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
        foreach (var g in grounds)
            foreach (float density in new[] { 0f, 0.5f, 1f })
                foreach (float n in new[] { 0f, 0.5f, 1f })
                {
                    var inside = RibbonComposite(g, 1f, density, n);
                    float lo = MathF.Min(Lum(g), Lum(inside));
                    for (int i = 0; i <= 200; i++)
                    {
                        float cov = i / 200f;
                        var c = RibbonComposite(g, cov, density, n);
                        Assert.True(Lum(c) >= lo - 0.01f, $"a dark contour at coverage {cov:F3} over {g}: {c} (ground {Lum(g):F3}, inside {Lum(inside):F3})");
                        var blood = RibbonColour(n);
                        Assert.True(c.R <= MathF.Max(g.R, MathF.Max(inside.R, blood.R)) + 1e-5f && c.G <= MathF.Max(g.G, MathF.Max(inside.G, blood.G)) + 1e-5f && c.B <= MathF.Max(g.B, MathF.Max(inside.B, blood.B)) + 1e-5f, $"brighter at coverage {cov:F3} over {g}: {c}");
                        Assert.True(c.G >= MathF.Min(g.G, inside.G) - 0.02f && c.B >= MathF.Min(g.B, inside.B) - 0.02f, $"a colour neither the ground's nor the blood's at coverage {cov:F3} over {g}: {c}");
                    }
                }
        Assert.Equal(0f, RibbonStrip(0f));
        Assert.Equal(1f, RibbonStrip(1f));
        Assert.Equal(0.125f, RibbonStrip(0.5f), 5);
    }

    [Fact]
    public void No_opacity_over_a_bright_red_or_orange_surface_shows_an_orange_rim()
    {
        // repair 3 (re-review of 50ae0bd7, finding 3): the strip kept all the red underneath, so the thin patches and the anti-aliased rim over a bright surface were brighter than the
        // blood's darker shade and went orange through the tonemapper. Now, wherever there is blood, the pixel is no brighter than the blood's brightest shade and its green and blue are
        // at most the blood's own share of red.
        // Repair 4, finding N4: also over HDR surfaces above 1 (a power weapon's glow, a lit emissive) up to RibbonStripHdr in red.
        Assert.InRange(RibbonStripRed, 0.05f, 0.2f);
        Assert.True(RibbonStripHdr >= 4f);
        float bloodGr = BloodRgb.G / BloodRgb.R, bloodBr = BloodRgb.B / BloodRgb.R;
        float brightest = BloodRgb.R * RibbonShadeTop;
        (float R, float G, float B)[] grounds = [(1f, 0f, 0f), (1f, 0.12f, 0.08f), (1f, 0.45f, 0.1f), (1f, 0.7f, 0.2f), (1f, 1f, 1f), (0.9f, 0.3f, 0.3f),
            (2f, 0.5f, 0.1f), (2.5f, 2.5f, 2.5f), (4f, 0f, 0f), (4f, 2f, 0.4f), (RibbonStripHdr, RibbonStripHdr, RibbonStripHdr), (1.5f, 1.8f, 3f)];
        foreach (var g in grounds)
            for (int i = 0; i <= 200; i++)
            {
                float density = i / 200f;
                foreach (float n in new[] { 0f, 0.5f, 1f })
                {
                    var c = RibbonComposite(g, 1f, density, n);
                    Assert.True(c.R <= brightest + 1e-5f, $"brighter red than the blood at density {density:F3} over {g}: {c}");
                    Assert.True(c.G <= bloodGr * c.R + 1e-5f && c.B <= bloodBr * c.R + 1e-5f, $"orange at density {density:F3} over {g}: {c}");
                }
            }
    }

    [Fact]
    public void The_bands_are_gritty_at_the_hold_and_break_into_specks_late_and_every_early_seed_lasts()
    {
        // repair 5, re-review of 272299f4 finding 2: solid slabs, no grit; a fine octave opens 5 to 15 % small holes at the hold and more as the envelope falls
        static double H(double px, double py) { double v = Math.Sin(px * 127.1 + py * 311.7) * 43758.5453; return v - Math.Floor(v); }
        static float VN(float x, float y)
        {
            double ix = Math.Floor(x), iy = Math.Floor(y), fx = x - ix, fy = y - iy;
            double ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
            double a = H(ix, iy), b = H(ix + 1, iy), c = H(ix, iy + 1), d = H(ix + 1, iy + 1);
            return (float)((a + (b - a) * ux) + ((c + (d - c) * ux) - (a + (b - a) * ux)) * uy);
        }
        float Holes(float env) { int n = 0, h = 0; for (int i = 0; i < 400; i++) for (int j = 0; j < 100; j++) { n++; if (RibbonHole(VN(i * 0.37f, j * 0.53f), env) < 0.5f) h++; } return h / (float)n; }
        Assert.InRange(Holes(1f), 0.05f, 0.15f);
        Assert.True(Holes(0.2f) > 1.5f * Holes(1f), "late patches break into specks");
        Assert.InRange(RibbonDensityFloor, 0.4f, 0.6f);   // a wider density range (0.7 before)
        Assert.True(RibbonShadeTop - RibbonShadeFloor >= 0.3f);   // a wider shade range (0.25 before)
        // finding 4: a one- or two-band spray's seeds each still show blood at 0.6 s (the first seed of the old sequence, 0, had eroded before 0.5 s); the first spray takes seed 1
        for (int count = 1; count <= 2; count++)
            for (int sd = 1; sd <= 12; sd++)
            {
                float peak = MathF.Max(RibbonCoverage(0.2f, count, 1f, 1, firstSeed: sd).Area, RibbonCoverage(0.3f, count, 1f, 1, firstSeed: sd).Area);
                float at = RibbonCoverage(0.6f, count, 1f, 1, firstSeed: sd).Area / peak;
                Assert.True(at >= 0.05f, $"{count} band(s), seed {sd}: {at:F3} of its peak left at 0.6 s");
            }
        Assert.Contains("int _made, _next, _events = 1;", RepoFile("client", "Extras", "Gore", "ClassicBloodRibbons.cs"));
        Assert.True(RibbonMottleFloorFor(1) > RibbonMottleFloorFor(3));
    }

    [Fact]
    public void A_headshot_decal_pairs_only_with_the_killers_own_shot_within_the_window_whichever_comes_first()
    {
        Assert.True(HeadshotPairs(shooter: 3, killer: 3, apart: 0.0, window: 1.0));
        Assert.True(HeadshotPairs(3, 3, 1.0, 1.0));
        Assert.False(HeadshotPairs(4, 3, 0.2, 1.0));   // review 4, finding 8: another shooter's head hit on the same victim (the kill arrived first) takes no decal
        Assert.False(HeadshotPairs(3, 3, 1.01, 1.0));
        Assert.False(HeadshotPairs(3, 3, -0.1, 1.0));
    }

    [Fact]
    public void Thrusts_and_shots_reach_mostly_past_the_body_sweeps_both_sides_and_heavier_hits_lay_more()
    {
        var sweep = RibbonReach(RibbonKind.Sweep, 1f); var thrust = RibbonReach(RibbonKind.Thrust, 1f); var gun = RibbonReach(RibbonKind.Gunfire, 1f);
        Assert.InRange(sweep.Forward, 2.2f, 2.6f);   // repair 5: a long streak several body widths out along the swing (the owner's reference sequence, 2026-11-25; 1.8 m before)
        Assert.True(sweep.Back > 2f * thrust.Back && sweep.Back > 2f * gun.Back);
        Assert.True(RibbonReach(RibbonKind.Sweep, 2.1f).Forward > sweep.Forward);
        Assert.Equal(3, RibbonCount(1f));   // the most a normal hit lays: a sweep's 3 broad bands (review 3's reference showed 2 to 3); narrower strokes lay fewer (RibbonBands)
        Assert.True(RibbonCount(1f) <= RibbonCount(1.5f) && RibbonCount(1.5f) <= RibbonCount(2.1f) && RibbonCount(2.1f) <= RibbonMaxBands && RibbonCount(100f) == RibbonMaxBands && RibbonMaxBands <= MaxRibbons);
        Assert.True(RibbonWidthScale(2.1f) > RibbonWidthScale(1f));
    }
}
