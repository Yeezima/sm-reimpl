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
        Assert.True(RibbonMaxAlpha < 1f, "mixed and capped: stacked bands never white out");
    }

    [Fact]
    public void Thrusts_and_shots_reach_mostly_past_the_body_sweeps_both_sides_and_heavier_hits_lay_more()
    {
        var sweep = RibbonReach(RibbonKind.Sweep, 1f); var thrust = RibbonReach(RibbonKind.Thrust, 1f); var gun = RibbonReach(RibbonKind.Gunfire, 1f);
        Assert.InRange(sweep.Forward, 1.6f, 2.0f);   // about 0.9 body heights past the body (the reference)
        Assert.True(sweep.Back > 2f * thrust.Back && sweep.Back > 2f * gun.Back);
        Assert.True(RibbonReach(RibbonKind.Sweep, 2.1f).Forward > sweep.Forward);
        Assert.Equal(3, RibbonCount(1f));   // 2 to 3 broad bands (review 3)
        Assert.True(RibbonCount(1f) < RibbonCount(1.5f) && RibbonCount(1.5f) < RibbonCount(2.1f) && RibbonCount(2.1f) <= MaxRibbons && RibbonCount(100f) == MaxRibbons);
        Assert.True(RibbonWidthScale(2.1f) > RibbonWidthScale(1f));
    }
}
