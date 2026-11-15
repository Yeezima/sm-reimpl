using System.Numerics;
using SmNet.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The limited swing arc (owner, 2026-11-15: a gentle even bow of about 2 m, from the swing's release through a short follow-through, never the wind-up or the recovery;
/// <see cref="MusouSlashArcPath.ReleaseSpan"/>, <see cref="MusouSlashArcPath.FitLimited"/>, <see cref="MusouSlashArcPath.Points"/>). Pure math, no engine.
/// </summary>
public class MusouSlashArcLimitTests
{
    const float Deg = MathF.PI / 180f;
    static readonly Vector3 Chest = new(0f, 0f, 1.3f);
    const float From = 0.10f, To = 0.52f, Hit = 0.40f;   // a 0.42 s window ending 0.12 s after a hit at 0.40 s
    const int N = 28;

    static float TimeOf(int i) => From + (To - From) * i / (N - 1);

    /// <summary>Tip samples evenly in time: a slow wind-up (right side, rising) until 0.20 s, the fast strike right to left to 0.46 s with a bend in elevation, then a slow recovery that drops away.</summary>
    static List<Vector3> Samples()
    {
        var list = new List<Vector3>();
        for (int i = 0; i < N; i++)
        {
            float t = TimeOf(i);
            float az, el;
            if (t < 0.20f) { float k = (t - From) / 0.10f; az = -75f + 15f * k; el = 10f + 8f * k; }
            else if (t < 0.46f) { float k = (t - 0.20f) / 0.26f; az = -60f + 120f * k; el = 18f - 25f * MathF.Sin(k * MathF.PI); }
            else { float k = (t - 0.46f) / 0.06f; az = 60f + 8f * k; el = 18f - 10f * k; }
            float a = az * Deg, e = el * Deg;
            list.Add(Chest + new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e)) * 1.2f);
        }
        return list;
    }

    static Vector3 Dir(Vector3 tip) => Vector3.Normalize(tip - Chest);

    [Fact]
    public void The_span_ends_just_after_the_hit_and_skips_the_wind_up()
    {
        var all = Samples();
        var span = MusouSlashArcPath.ReleaseSpan(all, From, To, Hit);
        Assert.InRange(span.Count, 3, 20);
        // No sample later than hit + follow-through (plus one sample step) and none of the wind-up's (azimuth behind the swinger).
        float lastTime = TimeOf(all.IndexOf(span[^1]));
        Assert.True(lastTime <= Hit + MusouSlashArcPath.FollowSeconds + (To - From) / (N - 1) + 1e-4f, $"ends at {lastTime}");
        Assert.All(span, p => Assert.True(MathF.Atan2(Dir(p).Y, Dir(p).X) > -62f * Deg, "none of the wind-up (azimuth before the release)"));
        Assert.True(all.IndexOf(span[0]) > 0);
    }

    [Fact]
    public void The_limited_arc_is_two_metres_long_at_any_radius()
    {
        var path = MusouSlashArcPath.FitLimited(Samples(), Chest, 36, From, To, Hit)!;
        Assert.NotNull(path);
        Assert.NotNull(path.Arc);
        foreach (float radius in new[] { 1.5f, 3f, 8.8f, 15f })
        {
            var points = MusouSlashArcPath.Scale(path, radius);
            Assert.Equal(37, points.Length);
            Assert.InRange(MusouSlashArcPath.LengthOf(points), 1.97f, 2.0f);   // the polyline of a 2 m arc (a hair under: chords)
        }
    }

    [Fact]
    public void The_arc_never_changes_the_sign_of_its_curvature_and_has_no_hooks()
    {
        var path = MusouSlashArcPath.FitLimited(Samples(), Chest, 36, From, To, Hit)!;
        var points = MusouSlashArcPath.Scale(path, 3f);
        var normal = Vector3.Zero;
        int sign = 0;
        for (int i = 1; i < points.Length - 1; i++)
        {
            var a = points[i] - points[i - 1];
            var b = points[i + 1] - points[i];
            var turn = Vector3.Cross(a, b);
            if (normal == Vector3.Zero) normal = turn;
            float s = Vector3.Dot(turn, normal);
            if (MathF.Abs(s) < 1e-9f) continue;
            if (sign == 0) sign = Math.Sign(s);
            Assert.Equal(sign, Math.Sign(s));
        }
        Assert.InRange(path.Arc!.TurnDegrees, MusouSlashArcPath.MinTurnDegrees, MusouSlashArcPath.MaxTurnDegrees);
        // Even bow: each step turns by the same angle.
        float first = Angle(points[1] - points[0], points[2] - points[1]);
        for (int i = 1; i < points.Length - 1; i++) Assert.InRange(Angle(points[i] - points[i - 1], points[i + 1] - points[i]), first - 0.001f, first + 0.001f);
    }

    static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f));

    [Fact]
    public void An_s_bend_in_the_swing_collapses_to_one_bow()
    {
        // Strike with a wiggle: elevation oscillates a full wave while the azimuth sweeps.
        var list = new List<Vector3>();
        for (int i = 0; i < N; i++)
        {
            float k = i / (float)(N - 1);
            float a = (-50f + 100f * k) * Deg, e = 25f * MathF.Sin(k * 2f * MathF.PI) * Deg;
            list.Add(Chest + new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e)) * 1.2f);
        }
        var path = MusouSlashArcPath.FitLimited(list, Chest, 36, 0f, 0.42f, 0.42f - 0.06f)!;
        Assert.NotNull(path);
        Assert.InRange(path.Arc!.TurnDegrees, 8f, 40f);
        var points = MusouSlashArcPath.Scale(path, 2f);
        Assert.InRange(MusouSlashArcPath.LengthOf(points), 1.97f, 2.0f);
    }

    [Fact]
    public void A_plain_sweep_is_not_read_as_a_sharp_bow_just_because_it_lies_on_a_sphere()
    {
        // A level 120 degree sweep and a diagonal one lie on the chest's sphere: no sideways deviation, so the default turn, not the clamp.
        var level = new List<Vector3>();
        var diagonal = new List<Vector3>();
        for (int i = 0; i < N; i++)
        {
            float k = i / (float)(N - 1);
            float a = (-60f + 120f * k) * Deg;
            level.Add(Chest + new Vector3(MathF.Cos(a), MathF.Sin(a), 0f) * 1.2f);
            float b = (-50f + 100f * k) * Deg, e = (-30f + 60f * k) * Deg;
            diagonal.Add(Chest + new Vector3(MathF.Cos(e) * MathF.Cos(b), MathF.Cos(e) * MathF.Sin(b), MathF.Sin(e)) * 1.2f);
        }
        Assert.Equal(MusouSlashArcPath.DefaultTurnDegrees, MusouSlashArcPath.FitLimited(level, Chest, 24, 0f, 0.42f, 0.42f)!.Arc!.TurnDegrees, 3);
        Assert.InRange(MusouSlashArcPath.FitLimited(diagonal, Chest, 24, 0f, 0.42f, 0.42f)!.Arc!.TurnDegrees, 8f, 39f);
    }

    [Fact]
    public void A_swing_that_bulges_sideways_bows_by_how_much_it_bulges()
    {
        float Turn(float bulge)
        {
            var list = new List<Vector3>();
            for (int i = 0; i < N; i++)
            {
                float k = i / (float)(N - 1);
                float a = (-50f + 100f * k) * Deg, e = bulge * MathF.Sin(k * MathF.PI) * Deg;
                list.Add(Chest + new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e)) * 1.2f);
            }
            return MusouSlashArcPath.FitLimited(list, Chest, 24, 0f, 0.42f, 0.42f)!.Arc!.TurnDegrees;
        }
        float small = Turn(6f), large = Turn(14f);
        Assert.InRange(small, 8f, 40f);
        Assert.True(large > small, $"{large} vs {small}");
        Assert.True(small < 40f, "a mild bulge is under the clamp");
    }

    [Fact]
    public void The_swing_keeps_its_direction_and_tilt()
    {
        // Rising diagonal, right to left.
        var rising = new List<Vector3>();
        for (int i = 0; i < N; i++)
        {
            float k = i / (float)(N - 1);
            float a = (-50f + 100f * k) * Deg, e = (-30f + 60f * k) * Deg;
            rising.Add(Chest + new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e)) * 1.2f);
        }
        var path = MusouSlashArcPath.FitLimited(rising, Chest, 24, 0f, 0.42f, 0.42f)!;
        var p = MusouSlashArcPath.Scale(path, 2f);
        Assert.True(p[^1].Y > p[0].Y + 0.5f, "travels right to left");
        Assert.True(p[^1].Z > p[0].Z + 0.3f, "rises");
        var falling = MusouSlashArcPath.FitLimited(Mirror(rising), Chest, 24, 0f, 0.42f, 0.42f)!;
        var q = MusouSlashArcPath.Scale(falling, 2f);
        Assert.True(q[^1].Z < q[0].Z - 0.3f, "falls");
    }

    static List<Vector3> Mirror(List<Vector3> tips) => tips.Select(t => new Vector3(t.X, t.Y, 2f * Chest.Z - t.Z)).ToList();

    [Fact]
    public void The_arc_is_lifted_off_the_floor_without_changing_its_shape()
    {
        var path = MusouSlashArcPath.FitLimited(Samples(), Chest, 36, From, To, Hit)!;
        var free = MusouSlashArcPath.Scale(path, 3f);
        var held = MusouSlashArcPath.Scale(path, 3f, maxDrop: 0.2f);
        Assert.All(held, p => Assert.True(p.Z >= -0.2f - 1e-4f));
        Assert.InRange(MusouSlashArcPath.LengthOf(held), MusouSlashArcPath.LengthOf(free) - 1e-3f, MusouSlashArcPath.LengthOf(free) + 1e-3f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void The_level_default_is_two_metres_and_travels_the_swings_way(int direction)
    {
        var path = MusouSlashArcPath.Level(direction, 36);
        var points = MusouSlashArcPath.Scale(path, 2f);
        Assert.InRange(MusouSlashArcPath.LengthOf(points), 1.97f, 2.0f);
        Assert.Equal(direction, Math.Sign(points[^1].Y - points[0].Y));
        Assert.All(points, p => Assert.True(p.X > 0f, "in front of the swinger"));
    }

    [Fact]
    public void Too_little_travel_or_no_samples_gives_no_path()
    {
        var held = Enumerable.Repeat(Chest + new Vector3(1.2f, 0f, 0f), N).ToList();
        Assert.Null(MusouSlashArcPath.FitLimited(held, Chest, 24, From, To, Hit));
        Assert.Null(MusouSlashArcPath.FitLimited(new List<Vector3>(), Chest, 24, From, To, Hit));
        Assert.Null(MusouSlashArcPath.FitLimited(Samples(), Chest, 0, From, To, Hit));
    }

    [Fact]
    public void The_older_full_swing_path_is_untouched_by_the_limit()
    {
        // The restore switch (--old-musou-arc) draws Fit + Stretch: no limited arc, directions at any radius reach as far as the swing swept.
        var swing = new List<Vector3>();
        for (int i = 0; i < 14; i++)
        {
            float a = (-60f + 120f * i / 13f) * Deg;
            swing.Add(Chest + new Vector3(MathF.Cos(a), MathF.Sin(a), 0f) * 1.2f);
        }
        var old = MusouSlashArcPath.Fit(swing, Chest, 24)!;
        Assert.Null(old.Arc);
        var points = MusouSlashArcPath.Scale(old, 8f);
        Assert.True(MusouSlashArcPath.LengthOf(points) > 10f, "the old line spans the whole 120 degree swing at 8 m");
    }
}
