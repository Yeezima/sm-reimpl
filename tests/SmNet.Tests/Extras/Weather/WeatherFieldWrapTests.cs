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
    public void The_far_ring_under_cover_keeps_the_middle_dry()
    {
        var c = new Vector3(5f, 0f, 5f);
        Assert.Equal(1f, WeatherFieldWrap.HoleFade(c, c, 0f, 44f));
        Assert.Equal(0f, WeatherFieldWrap.HoleFade(c + new Vector3(10f, 3f, 0f), c, 14f, 44f));
        Assert.Equal(1f, WeatherFieldWrap.HoleFade(c + new Vector3(20f, 0f, 10f), c, 14f, 44f));
        Assert.Equal(0f, WeatherFieldWrap.HoleFade(c + new Vector3(40f, 0f, 30f), c, 14f, 44f));
    }
}
