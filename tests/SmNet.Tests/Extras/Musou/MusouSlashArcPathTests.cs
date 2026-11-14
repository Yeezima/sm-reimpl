using System.Numerics;
using SmNet.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The swing arc's path fit (owner, 2026-11-14: the arc matches the weapon tip's trajectory in orientation and shape, drawn at the cleave's distance; <see cref="MusouSlashArcPath"/>). Pure math:
/// tip samples in the swinger's frame (X forward, Y left, Z up) go in, a path of unit directions comes out, and <see cref="MusouSlashArcPath.Scale"/> puts it at the cleave's radius.
/// </summary>
public class MusouSlashArcPathTests
{
    const float Deg = MathF.PI / 180f;
    static readonly Vector3 Chest = new(0f, 0f, 1.3f);

    /// <summary>Tip samples on a circle of <paramref name="reach"/> metres round the chest: azimuth from <paramref name="a0"/> to <paramref name="a1"/> degrees, elevation from <paramref name="e0"/> to <paramref name="e1"/>.</summary>
    static List<Vector3> Swing(float a0, float a1, float e0, float e1, float reach = 1.2f, int n = 14)
    {
        var list = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)(n - 1);
            float a = (a0 + (a1 - a0) * t) * Deg, e = (e0 + (e1 - e0) * t) * Deg;
            list.Add(Chest + new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e)) * reach);
        }
        return list;
    }

    [Fact]
    public void A_level_swing_right_to_left_fits_a_level_path_that_turns_left()
    {
        var path = MusouSlashArcPath.Fit(Swing(-60, 60, 0, 0), Chest, 24)!;
        Assert.NotNull(path);
        Assert.Equal(25, path.Directions.Length);
        Assert.InRange(path.TiltDegrees, -1f, 1f);
        Assert.InRange(path.TravelDegrees, 115f, 121f);
        Assert.InRange(path.BowDegrees, 0f, 1f);
        Assert.All(path.Directions, d => Assert.InRange(d.Z, -0.02f, 0.02f));
        Assert.True(path.Directions[0].Y < -0.8f && path.Directions[24].Y > 0.8f, "starts on the right, ends on the left");
    }

    [Fact]
    public void A_left_to_right_swing_keeps_its_direction()
    {
        var path = MusouSlashArcPath.Fit(Swing(60, -60, 0, 0), Chest, 24)!;
        Assert.True(path.Directions[0].Y > 0.8f && path.Directions[24].Y < -0.8f);
    }

    [Fact]
    public void A_diagonal_swing_keeps_its_tilt_and_a_falling_one_tilts_down()
    {
        var rising = MusouSlashArcPath.Fit(Swing(-50, 50, -35, 35), Chest, 24)!;
        Assert.InRange(rising.TiltDegrees, 38f, 46f);   // the chord's slope: 1.15 m up over 1.26 m along
        Assert.True(rising.Directions[24].Z > rising.Directions[0].Z + 0.6f);
        var falling = MusouSlashArcPath.Fit(Swing(-50, 50, 35, -35), Chest, 24)!;
        Assert.InRange(falling.TiltDegrees, -46f, -38f);
    }

    [Fact]
    public void An_overhead_chop_is_mostly_vertical()
    {
        var path = MusouSlashArcPath.Fit(Swing(-5, 5, 70, -40), Chest, 24)!;
        Assert.True(path.TiltDegrees < -70f, $"tilt {path.TiltDegrees}");
        Assert.True(path.Directions[0].Z > 0.9f && path.Directions[24].Z < -0.5f);
    }

    [Fact]
    public void A_bowed_swing_reports_its_curve()
    {
        // A level swing that dips in the middle: its line bows away from the chord.
        var tip = Swing(-60, 60, 0, 0);
        for (int i = 0; i < tip.Count; i++) tip[i] += new Vector3(0f, 0f, -0.5f * MathF.Sin(MathF.PI * i / (tip.Count - 1)));
        var path = MusouSlashArcPath.Fit(tip, Chest, 24)!;
        Assert.True(path.BowDegrees > 3f, $"bow {path.BowDegrees}");   // a level swing's is under 1 degree
    }

    [Fact]
    public void Points_are_evenly_spaced_along_the_swing()
    {
        // Samples bunched at the start (a slow wind-up) still give an even line.
        var tip = Swing(-60, -40, 0, 0, n: 8);
        tip.AddRange(Swing(-38, 60, 0, 0, n: 6));
        var path = MusouSlashArcPath.Fit(tip, Chest, 20)!;
        var steps = new List<float>();
        for (int k = 1; k < path.Directions.Length; k++) steps.Add(MathF.Acos(Vector3.Dot(path.Directions[k - 1], path.Directions[k])) / Deg);
        Assert.InRange(steps.Max() - steps.Min(), 0f, 1.5f);
    }

    [Fact]
    public void The_scale_puts_every_point_at_the_cleave_radius_whatever_the_reach_of_the_tip()
    {
        // The same swing at two reaches (a short knife, a long hammer) fits the same directions; the line sits at the radius it is given.
        var near = MusouSlashArcPath.Fit(Swing(-50, 50, -20, 30, reach: 0.7f), Chest, 16)!;
        var far = MusouSlashArcPath.Fit(Swing(-50, 50, -20, 30, reach: 2.1f), Chest, 16)!;
        for (int k = 0; k < near.Directions.Length; k++) Assert.True((near.Directions[k] - far.Directions[k]).Length() < 0.02f);
        foreach (float radius in new[] { 1.5f, 2.8f, 3.4f })
        {
            var points = MusouSlashArcPath.Scale(near, radius);
            Assert.Equal(near.Directions.Length, points.Length);
            Assert.All(points, p => Assert.Equal(radius, p.Length(), 1e-3f));
        }
    }

    [Fact]
    public void The_scale_keeps_the_swing_s_orientation()
    {
        var path = MusouSlashArcPath.Fit(Swing(-50, 50, -35, 35), Chest, 16)!;
        var points = MusouSlashArcPath.Scale(path, 3f);
        // Its first point is low on the right, its last high on the left, in the same proportions as the directions.
        Assert.True(points[0].Z < 0f && points[0].Y < 0f);
        Assert.True(points[16].Z > 0f && points[16].Y > 0f);
        Assert.Equal(path.Directions[8].Z * 3f, points[8].Z, 1e-4f);
    }

    [Fact]
    public void A_path_with_too_few_samples_too_little_travel_or_bad_numbers_has_no_fit()
    {
        Assert.Null(MusouSlashArcPath.Fit(Swing(-60, 60, 0, 0, n: 3), Chest, 24));                 // too few samples
        Assert.Null(MusouSlashArcPath.Fit(Swing(0, 10, 0, 0), Chest, 24));                          // a twitch, not a swing
        Assert.Null(MusouSlashArcPath.Fit(Enumerable.Repeat(Chest, 14).ToList(), Chest, 24));       // the tip on the chest
        Assert.Null(MusouSlashArcPath.Fit(Swing(-60, 60, 0, 0), new Vector3(float.NaN, 0f, 0f), 24));
        var bad = Swing(-60, 60, 0, 0);
        for (int i = 0; i < bad.Count; i++) bad[i] = new Vector3(float.NaN, 0f, 0f);
        Assert.Null(MusouSlashArcPath.Fit(bad, Chest, 24));
        Assert.Null(MusouSlashArcPath.Fit(Swing(-60, 60, 0, 0), Chest, 0));
    }

    [Fact]
    public void A_swing_that_dips_below_the_floor_or_over_the_head_is_held_to_the_elevation_limits()
    {
        var low = MusouSlashArcPath.Fit(Swing(-60, 60, -85, -85), Chest, 24)!;
        Assert.All(low.Directions, d => Assert.True(d.Z >= MathF.Sin((MusouSlashArcPath.ElevationMin - 3f) * Deg)));
        Assert.All(low.Directions, d => Assert.Equal(1f, d.Length(), 1e-3f));
        var high = MusouSlashArcPath.Fit(Swing(-60, 60, 89, 89), Chest, 24)!;
        Assert.All(high.Directions, d => Assert.True(d.Z <= MathF.Sin((MusouSlashArcPath.ElevationMax + 3f) * Deg)));
    }

    [Fact]
    public void A_spin_that_passes_behind_the_swinger_keeps_its_direction()
    {
        var path = MusouSlashArcPath.Fit(Swing(-180, 90, 0, 0, n: 28), Chest, 24)!;   // from behind, round the right, to the left
        Assert.InRange(path.TravelDegrees, 265f, 275f);
        Assert.True(path.Directions[0].X < -0.9f && path.Directions[24].Y > 0.9f);
    }

    [Fact]
    public void Only_the_part_inside_the_cleave_s_cone_is_kept()
    {
        // A spin from behind the swinger round the left to the front: with a cone of 120 degrees to a side the line starts where the tip enters it.
        var path = MusouSlashArcPath.Fit(Swing(-170, 20, 0, 0, n: 28), Chest, 24, 120f)!;
        Assert.InRange(path.TravelDegrees, 130f, 142f);                                          // about -120 to 20 (the samples are 7 degrees apart)
        Assert.All(path.Directions, d => Assert.True(MathF.Abs(MathF.Atan2(d.Y, d.X)) <= 121f * Deg));
        // A swing wholly outside the cone has no line.
        Assert.Null(MusouSlashArcPath.Fit(Swing(130, 175, 0, 0), Chest, 24, 120f));
        Assert.NotNull(MusouSlashArcPath.Fit(Swing(-100, 100, 0, 0), Chest, 24, 120f));   // wholly inside: all of it
    }

    [Fact]
    public void The_active_span_keeps_the_fast_swing_and_drops_the_wind_up_and_the_recovery()
    {
        // 6 slow samples (a wind-up, 2 degrees a step), 8 fast ones (12 degrees a step), 6 slow ones again.
        var tip = Swing(-100, -90, 0, 0, n: 6);
        tip.AddRange(Swing(-78, 6, 0, 0, n: 8));
        tip.AddRange(Swing(8, 18, 0, 0, n: 6));
        var span = MusouSlashArcPath.ActiveSpan(tip);
        Assert.InRange(span.Count, 8, 10);
        var path = MusouSlashArcPath.Fit(tip, Chest, 24)!;
        Assert.InRange(path.TravelDegrees, 80f, 100f);   // the fast part alone (84 degrees, plus a step on each side)
        Assert.Equal(2, MusouSlashArcPath.ActiveSpan(tip.Take(2).ToList()).Count);   // too short to judge: all of it comes back
    }

    [Fact]
    public void The_window_is_the_swing_around_the_hit_and_stays_inside_the_clip()
    {
        Assert.True(MusouSlashArcPath.Window(0.5f, 1.2f, out float from, out float to));
        Assert.Equal(0.5f - MusouSlashArcPath.WindowBefore, from, 1e-4f);
        Assert.Equal(0.5f + MusouSlashArcPath.WindowAfter, to, 1e-4f);
        Assert.True(MusouSlashArcPath.Window(0.05f, 0.4f, out from, out to));
        Assert.Equal(0f, from);
        Assert.True(MusouSlashArcPath.Window(0.39f, 0.4f, out from, out to));
        Assert.Equal(0.4f, to);
        Assert.False(MusouSlashArcPath.Window(-1f, 1f, out _, out _));
        Assert.False(MusouSlashArcPath.Window(0.5f, 0f, out _, out _));
        Assert.False(MusouSlashArcPath.Window(float.NaN, 1f, out _, out _));
    }

    [Fact]
    public void The_cache_key_tells_two_bodies_apart_but_not_the_case_of_a_name()
    {
        var a = MusouSlashArcPath.Key.Of("SM_MEL_CS_ATK_F_01_D_1m", "sm_mp_melee_powersword", 120f, 3);
        Assert.Equal(a, MusouSlashArcPath.Key.Of("sm_mel_cs_atk_f_01_d_1m", "SM_MP_MELEE_POWERSWORD", 120.2f, 3));
        Assert.NotEqual(a, MusouSlashArcPath.Key.Of("SM_MEL_CS_ATK_F_01_D_1m", "sm_mp_melee_powersword", 120f, 4));   // another body
        Assert.NotEqual(a, MusouSlashArcPath.Key.Of("SM_MEL_CS_ATK_F_01_D_1m", "sm_mp_melee_powersword", 75f, 3));    // another cleave
        Assert.NotEqual(a, MusouSlashArcPath.Key.Of("SM_MEL_CS_ATK_F_01_D_1m", "csm_mp_melee_powersword", 120f, 3));  // another weapon
        Assert.NotEqual(a, MusouSlashArcPath.Key.Of("SM_MEL_CS_XATK_Combo3_1m", "sm_mp_melee_powersword", 120f, 3));  // another clip
        var cache = new Dictionary<MusouSlashArcPath.Key, int> { [a] = 1 };
        Assert.False(cache.ContainsKey(MusouSlashArcPath.Key.Of("SM_MEL_CS_ATK_F_01_D_1m", "sm_mp_melee_powersword", 120f, 9)));
    }

    [Fact]
    public void The_scale_never_takes_the_line_below_the_floor_under_the_swinger()
    {
        var path = MusouSlashArcPath.Fit(Swing(-5, 5, 20, -60), Chest, 24)!;                  // a steep chop down to -60 degrees
        var free = MusouSlashArcPath.Scale(path, 3f);
        Assert.True(free.Min(p => p.Z) < -2.4f, "unclamped, it drops about 2.6 m at a 3 m radius");
        float centreHeight = 1.1f;
        var floored = MusouSlashArcPath.Scale(path, 3f, MusouSlashArcPath.MaxDrop(centreHeight));
        Assert.True(floored.Min(p => p.Z) >= -(centreHeight - MusouSlashArcPath.FloorMargin) - 1e-4f);
        Assert.Equal(free[0], floored[0]);                                                     // the points above the floor are untouched
        Assert.Equal(0f, MusouSlashArcPath.MaxDrop(0.05f));
    }

    [Fact]
    public void A_short_path_is_stretched_over_the_cleave_span_keeping_its_tilt_and_curve()
    {
        var path = MusouSlashArcPath.Fit(Swing(-25, 25, -10, 10), Chest, 24)!;                 // 50 degrees of sweep, rising
        var stretched = MusouSlashArcPath.Stretch(path, 240f);
        float Sweep(MusouSlashArcPath.Path p) => (p.Directions.Max(d => MathF.Atan2(d.Y, d.X)) - p.Directions.Min(d => MathF.Atan2(d.Y, d.X))) / Deg;
        Assert.InRange(Sweep(stretched), Sweep(path) * 2.4f, Sweep(path) * 2.51f);              // at most 2.5 times
        Assert.True(stretched.Directions[0].Y < path.Directions[0].Y && stretched.Directions[24].Y > path.Directions[24].Y);
        Assert.Equal(path.Directions[0].Y < 0f, stretched.Directions[0].Y < 0f);               // same direction
        Assert.InRange(stretched.TiltDegrees, path.TiltDegrees - 10f, path.TiltDegrees + 10f);   // about the same slope (the angles scale together; the chord on the sphere shifts a little)
        Assert.All(stretched.Directions, d => Assert.Equal(1f, d.Length(), 1e-3f));
        // A path that already sweeps the span, or an overhead chop with no sweep, comes back as it was.
        var wide = MusouSlashArcPath.Fit(Swing(-100, 100, 0, 0), Chest, 24)!;
        Assert.Same(wide, MusouSlashArcPath.Stretch(wide, 200f));
        var chop = MusouSlashArcPath.Fit(Swing(-3, 3, 70, -40), Chest, 24)!;
        Assert.Same(chop, MusouSlashArcPath.Stretch(chop, 240f));
        // It stays inside a cone a short path would otherwise leave.
        var cone = MusouSlashArcPath.Stretch(MusouSlashArcPath.Fit(Swing(30, 80, 0, 0), Chest, 24)!, 150f);
        Assert.All(cone.Directions, d => Assert.True(MathF.Abs(MathF.Atan2(d.Y, d.X)) <= 75.5f * Deg));
    }
}
