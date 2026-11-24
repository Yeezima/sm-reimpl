using System;
using System.Numerics;
using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Weather;

/// <summary>Rain motion (series 37): the world-anchored, camera-wrapped precipitation field keeps the same density round the camera at any speed and direction, and a drop is a fixed world point between frames.</summary>
public class WeatherFieldWrapTests
{
    static readonly Vector3 NearBox = new(24f, 24f, 24f);

    static Vector3[] Seeds(int n, int seed = 7)
    {
        var rng = new Random(seed);
        var s = new Vector3[n];
        for (int i = 0; i < n; i++) s[i] = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
        return s;
    }

    static int Within(Vector3[] seeds, Vector3 drift, Vector3 camera, Vector3 centre, float radius)
    {
        int n = 0;
        foreach (var s in seeds)
        {
            var at = WeatherFieldWrap.Place(s, NearBox, drift, centre);
            if (Vector3.Distance(at, camera) <= radius && WeatherFieldWrap.EdgeFade(at, centre, NearBox) > 0.99f) n++;
        }
        return n;
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]       // standing
    [InlineData(20f, 0f, 0f)]      // the wxslide sideways case
    [InlineData(16f, 26.8f, 0f)]   // a jump-pack lift-off
    [InlineData(0f, -15f, 0f)]     // dropping off a ledge
    [InlineData(0f, -33f, 0f)]     // the end of a jump-pack fall, faster than the rain
    [InlineData(0f, -50f, 30f)]    // a dive
    public void The_density_round_the_camera_does_not_depend_on_its_motion(float vx, float vy, float vz)
    {
        var seeds = Seeds(1560);   // the near layer at the High preset
        var fall = new Vector3(0.1f, -25f, 0.04f);
        var drift = Vector3.Zero;
        var camera = new Vector3(200f, 2.4f, 804f);
        var v = new Vector3(vx, vy, vz);
        double dt = 1.0 / 60;
        // expected: N times the 8 m sphere over the box (1560 x 2145 / 13824 = 242)
        float expected = seeds.Length * (4f / 3f * MathF.PI * 512f) / (NearBox.X * NearBox.Y * NearBox.Z);
        int min = int.MaxValue, max = 0;
        for (int f = 0; f < 180; f++)   // three seconds
        {
            drift = WeatherFieldWrap.Advance(drift, fall, dt, NearBox);
            camera += v * (float)dt;
            var centre = camera + new Vector3(0f, 1f, 0f);
            int n = Within(seeds, drift, camera, centre, 8f);
            min = Math.Min(min, n); max = Math.Max(max, n);
        }
        Assert.InRange(min, expected * 0.75f, expected * 1.25f);
        Assert.InRange(max, expected * 0.75f, expected * 1.25f);
    }

    [Fact]
    public void A_drop_is_a_fixed_world_point_that_only_falls_while_the_camera_moves()
    {
        var seed = new Vector3(0.3f, 0.6f, 0.45f);
        var fall = new Vector3(0f, -25f, 0f);
        var drift = new Vector3(1f, 2f, 3f);
        var camA = new Vector3(10f, 5f, -4f);
        var at0 = WeatherFieldWrap.Place(seed, NearBox, drift, camA);
        var drift1 = WeatherFieldWrap.Advance(drift, fall, 0.02, NearBox);
        var camB = camA + new Vector3(0.6f, -0.5f, 0.2f);   // 30 m/s sideways and 25 m/s down for 0.02 s
        var at1 = WeatherFieldWrap.Place(seed, NearBox, drift1, camB);
        Assert.True(Vector3.Distance(at1, at0 + fall * 0.02f) < 1e-3f, $"{at0} -> {at1}");
    }

    [Fact]
    public void Every_drop_is_drawn_inside_the_box_and_wraps_only_where_it_is_faded_out()
    {
        var seeds = Seeds(400, 3);
        var fall = new Vector3(0.5f, -25f, 0.2f);
        var drift = Vector3.Zero;
        var camera = Vector3.Zero;
        var prev = new Vector3[seeds.Length];
        for (int i = 0; i < seeds.Length; i++) prev[i] = WeatherFieldWrap.Place(seeds[i], NearBox, drift, camera);
        for (int f = 0; f < 240; f++)
        {
            drift = WeatherFieldWrap.Advance(drift, fall, 1.0 / 60, NearBox);
            camera += new Vector3(0.1f, -0.1f, 0.05f);   // 6, 6 and 3 m/s at 60 frames a second, besides the 25 m/s fall
            for (int i = 0; i < seeds.Length; i++)
            {
                var at = WeatherFieldWrap.Place(seeds[i], NearBox, drift, camera);
                var d = at - camera;
                Assert.True(Math.Abs(d.X) <= 12.001f && Math.Abs(d.Y) <= 12.001f && Math.Abs(d.Z) <= 12.001f, $"{d}");
                bool jumped = Vector3.Distance(at, prev[i] + fall / 60f) > 1f;
                if (jumped)
                {
                    Assert.True(WeatherFieldWrap.EdgeFade(at, camera, NearBox) < 0.1f, $"a wrapped drop shows at {d}");
                    Assert.True(WeatherFieldWrap.EdgeFade(prev[i], camera - new Vector3(0.1f, -0.1f, 0.05f), NearBox) < 0.1f, "it left from a faded face");
                }
                prev[i] = at;
            }
        }
    }

    [Fact]
    public void The_drift_stays_inside_one_box_and_a_stall_moves_it_at_most_a_quarter_second()
    {
        var d = WeatherFieldWrap.Advance(Vector3.Zero, new Vector3(0f, -25f, 0f), 100.0, NearBox);
        Assert.Equal(Mod(-25f * 0.25f, 24f), d.Y, 3);
        var e = Vector3.Zero;
        for (int i = 0; i < 100_000; i++) e = WeatherFieldWrap.Advance(e, new Vector3(3f, -25f, -7f), 1.0 / 60, NearBox);
        Assert.InRange(e.X, 0f, 24f); Assert.InRange(e.Y, 0f, 24f); Assert.InRange(e.Z, 0f, 24f);
        Assert.Equal(e, WeatherFieldWrap.Advance(e, Vector3.One, double.NaN, NearBox));
        static float Mod(float v, float s) => v - MathF.Floor(v / s) * s;
    }

    [Fact]
    public void A_switch_or_a_ring_change_eases_over_half_a_second_never_at_once()
    {
        float level = 1f;
        level = WeatherFieldWrap.Ease(level, 0f, 1f, 0.1);
        Assert.Equal(0.8f, level, 4);
        for (int i = 0; i < 4; i++) level = WeatherFieldWrap.Ease(level, 0f, 1f, 0.1);
        Assert.Equal(0f, level);
        float hole = WeatherFieldWrap.Ease(0f, 14f, 14f, 1.0 / 60);
        Assert.InRange(hole, 0.4f, 0.5f);   // 28 m/s
        Assert.Equal(14f, WeatherFieldWrap.Ease(13.9f, 14f, 14f, 0.1));   // never past the target
        Assert.Equal(3f, WeatherFieldWrap.Ease(3f, 14f, 14f, double.NaN));
        Assert.Equal(1f - 0.25f / 0.5f, WeatherFieldWrap.Ease(1f, 0f, 1f, 10.0), 4);   // a stall moves it at most a quarter second's worth
    }

    [Fact]
    public void Drift_groups_differ_average_the_layers_speed_and_keep_the_density_round_a_moving_camera()
    {
        var v = new Vector3(10f, -0.5f, 0f);
        var sum = Vector3.Zero;
        for (int g = 0; g < WeatherFieldWrap.Groups; g++)
        {
            var gv = WeatherFieldWrap.GroupVelocity(v, g);
            Assert.NotEqual(v, gv);
            sum += gv;
        }
        Assert.InRange((sum / WeatherFieldWrap.Groups - v).Length(), 0f, 0.3f);   // the mean motion is the layer's within 3 % (the yaw spread of at most 9 degrees)

        var box = new Vector3(24f, 6f, 24f);
        var seeds = Seeds(1200, 11);
        var drifts = new Vector3[WeatherFieldWrap.Groups];
        var camera = Vector3.Zero;
        float expected = seeds.Length * (4f / 3f * MathF.PI * 27f) / (box.X * box.Y * box.Z);   // a 3 m sphere
        for (int f = 0; f < 120; f++)
        {
            for (int g = 0; g < drifts.Length; g++) drifts[g] = WeatherFieldWrap.Advance(drifts[g], WeatherFieldWrap.GroupVelocity(v, g), 1.0 / 60, box);
            camera += new Vector3(0.3f, -0.5f, 0.1f);   // 18 m/s sideways, 30 m/s down
            int n = 0;
            for (int i = 0; i < seeds.Length; i++)
            {
                var at = WeatherFieldWrap.Place(seeds[i], box, drifts[i % WeatherFieldWrap.Groups], camera);
                if (Vector3.Distance(at, camera) <= 3f) n++;
            }
            Assert.InRange(n, expected * 0.5f, expected * 1.6f);
        }
    }

    [Fact]
    public void The_flakes_keep_the_old_emitters_share_at_high_wind()
    {
        Assert.Equal(1f, WeatherFieldWrap.EmitterShare(24f, 7.4f, 3f));            // a lull's wind: the old flakes stayed in the box
        Assert.Equal(24f / (12f * 0.925f * 3f), WeatherFieldWrap.EmitterShare(24f, 12f * 0.925f, 3f), 4);   // full wind: about 0.72
        Assert.Equal(1f, WeatherFieldWrap.EmitterShare(24f, 0f, 3f));
    }

    [Fact]
    public void The_keep_cut_is_continuous_through_share_one_and_full_at_one()
    {
        const int amount = 576; float soft = Math.Max(8f, 0.1f * amount);
        static float Shown(float cut, float soft) { float s = 0; for (int i = 0; i < amount; i++) s += WeatherFieldWrap.KeepAlpha(i, cut, soft); return s; }
        for (int i = 0; i < amount; i++) Assert.Equal(1f, WeatherFieldWrap.KeepAlpha(i, WeatherFieldWrap.KeepCut(1f, amount, soft), soft));   // at 1 every drop is full
        float prevCut = WeatherFieldWrap.KeepCut(0.5f, amount, soft);
        var prevAlpha = new float[amount];
        for (int i = 0; i < amount; i++) prevAlpha[i] = WeatherFieldWrap.KeepAlpha(i, prevCut, soft);
        for (float share = 0.5f; share <= 1.00001f; share += 0.001f)   // a gust's crossing, in small steps
        {
            float cut = WeatherFieldWrap.KeepCut(Math.Min(share, 1f), amount, soft);
            for (int i = 0; i < amount; i++)
            {
                float a = WeatherFieldWrap.KeepAlpha(i, cut, soft);
                Assert.True(Math.Abs(a - prevAlpha[i]) < 0.12f, $"drop {i} jumped {prevAlpha[i]} -> {a} at share {share}");
                prevAlpha[i] = a;
            }
        }
        Assert.Equal(0.72f * amount, Shown(WeatherFieldWrap.KeepCut(0.72f, amount, soft), soft), 1.0);   // the centred edge draws the share
    }

    [Fact]
    public void A_fade_or_a_hole_reversed_half_way_turns_back_from_where_it_is()
    {
        float level = 1f;
        for (int i = 0; i < 3; i++) level = WeatherFieldWrap.Ease(level, 0f, 1f, 0.05);   // switched off: 0.3 of the way
        Assert.Equal(0.7f, level, 4);
        level = WeatherFieldWrap.Ease(level, 1f, 1f, 0.05);   // switched back on mid-fade: no jump, it climbs from 0.7
        Assert.Equal(0.8f, level, 4);
        float hole = 0f;
        for (int i = 0; i < 6; i++) hole = WeatherFieldWrap.Ease(hole, 16f, 16f, 0.05);   // under cover: opening
        Assert.Equal(9.6f, hole, 3);
        hole = WeatherFieldWrap.Ease(hole, 0f, 16f, 0.05);   // back in the open half way: closes from 9.6
        Assert.Equal(8f, hole, 3);
        var c = Vector3.Zero;
        Assert.Equal(1f, WeatherFieldWrap.HoleFade(new Vector3(9f, 0f, 0f), c, 0f, 60f));   // a closed hole keeps the drops round the player
    }

    [Fact]
    public void The_far_ring_under_cover_keeps_the_middle_dry()
    {
        var c = new Vector3(5f, 0f, 5f);
        Assert.Equal(1f, WeatherFieldWrap.HoleFade(c, c, 0f, 44f));
        Assert.Equal(0f, WeatherFieldWrap.HoleFade(c + new Vector3(10f, 3f, 0f), c, 14f, 44f));
        Assert.Equal(1f, WeatherFieldWrap.HoleFade(c + new Vector3(20f, 0f, 10f), c, 14f, 44f));
        Assert.Equal(0f, WeatherFieldWrap.HoleFade(c + new Vector3(40f, 0f, 30f), c, 14f, 44f));
    }
}
