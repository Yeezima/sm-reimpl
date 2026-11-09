using System;
using System.Collections.Generic;
using System.Linq;
using SmContent.Extras.Musou;
using SmFormats.Fx;

namespace SmFormats.Tests;

/// <summary>musou/gpu-particles: the engine-free parts of the GPU spray path (the pools' allocator, the curve table, the seed's two floats).</summary>
public class FxGpuLayoutTests
{
    [Theory]
    [InlineData(false, false, true, 1200)]    // Musou on a normal GPU: the GPU sprays, cap 1,200
    [InlineData(true, false, false, 160)]     // --fx-cpu: the CPU path, cap 160
    [InlineData(false, true, false, 160)]     // safe mode (Compatibility renderer or --safe-mode): the CPU path, cap 160
    [InlineData(true, true, false, 160)]
    public void Safe_mode_and_fx_cpu_keep_the_cpu_path_and_its_cap(bool fxCpu, bool safeMode, bool gpu, int cap)
    {
        Assert.Equal(gpu, FxGpuLayout.UseGpu(fxCpu, safeMode));
        Assert.Equal(cap, FxGpuLayout.EffectCap(FxGpuLayout.UseGpu(fxCpu, safeMode), null));
        Assert.Equal(300, FxGpuLayout.EffectCap(FxGpuLayout.UseGpu(fxCpu, safeMode), 300));   // --fx-cap wins on either path
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0xFFFFu)]
    [InlineData(0x10000u)]
    [InlineData(0x9E3779B9u)]
    [InlineData(uint.MaxValue)]
    public void A_seed_survives_its_two_float_halves(uint seed)
    {
        var (lo, hi) = FxGpuLayout.SplitSeed(seed);
        Assert.True(lo <= 65535f && hi <= 65535f && lo == MathF.Floor(lo) && hi == MathF.Floor(hi));
        Assert.Equal(seed, FxGpuLayout.JoinSeed(lo, hi));
    }

    static FxCurve Curve(params (float T, float V)[] keys) => new(keys.Select(k => new FxKey(k.T, k.V)).ToList());

    public static IEnumerable<object[]> Curves() =>
    [
        [Curve((0f, 2f))],
        [Curve((0f, 0f), (1f, 1f))],
        [Curve((0.2f, 1f), (0.5f, 3f), (0.5f, -1f), (0.9f, 4f))],   // a step (two keys at one time) and keys inside 0..1
        [Curve((0f, 0.3f), (0.1f, 2f), (0.15f, 1.9f), (0.6f, 0f), (1f, 0f))],
    ];

    [Theory]
    [MemberData(nameof(Curves))]
    public void A_packed_curve_evaluates_as_the_curve(FxCurve curve)
    {
        var table = new List<float> { 9f, 9f, 9f, 9f };   // something before it: the start index is used
        int count = FxGpuLayout.PackCurve(curve, table);
        Assert.Equal(curve.Keys.Count, count);
        for (float t = -0.25f; t <= 1.25f; t += 0.01f)
            Assert.Equal(curve.Evaluate(t), FxGpuLayout.EvaluatePacked(table, 1, count, t, -7f), 5);
    }

    [Fact]
    public void An_empty_curve_gives_the_fallback() =>
        Assert.Equal(0.1f, FxGpuLayout.EvaluatePacked(new List<float>(), 0, FxGpuLayout.PackCurve(FxCurve.Empty, new List<float>()), 0.5f, 0.1f));

    [Fact]
    public void A_packed_colour_curve_evaluates_as_the_curve()
    {
        var curve = new FxColourCurve([new FxColourKey(0f, new FxColour(255, 0, 0, 255)), new FxColourKey(0.4f, new FxColour(10, 200, 30, 128)), new FxColourKey(1f, new FxColour(0, 0, 0, 0))]);
        var table = new List<float>();
        int count = FxGpuLayout.PackColours(curve, table);
        Assert.Equal(3, count);
        Assert.Equal(count * 2 * 4, table.Count);
        for (float t = -0.1f; t <= 1.1f; t += 0.01f)
        {
            var want = curve.Evaluate(t);
            var got = FxGpuLayout.EvaluatePackedColours(table, 0, count, t);
            Assert.Equal(want.X, got.X, 5); Assert.Equal(want.Y, got.Y, 5); Assert.Equal(want.Z, got.Z, 5); Assert.Equal(want.W, got.W, 5);
        }
        Assert.Equal(System.Numerics.Vector4.One, FxGpuLayout.EvaluatePackedColours(table, 0, 0, 0.5f));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(1024, 10)]
    [InlineData(1025, 11)]
    public void A_request_rounds_up_to_a_power_of_two(int length, int sizeClass) => Assert.Equal(sizeClass, FxGpuBlocks.ClassOf(length));

    [Fact]
    public void The_allocator_reuses_a_freed_block_of_the_same_size_and_starts_over_when_empty()
    {
        var b = new FxGpuBlocks(64);
        int a = b.Allocate(3), c = b.Allocate(8), d = b.Allocate(1);
        Assert.Equal((0, 4, 12), (a, c, d));       // 3 takes 4 entries
        Assert.Equal(13, b.HighWater);
        Assert.Equal(13, b.Used);
        b.Free(c, 8);
        Assert.Equal(4, b.Allocate(7));            // the freed block of 8 again, not the top
        Assert.Equal(13, b.HighWater);
        Assert.Equal(-1, b.Allocate(64));          // more than is left at the top
        b.Free(a, 3); b.Free(4, 7); b.Free(d, 1);
        Assert.Equal((0, 0, 0, 0), (b.Used, b.Live, b.HighWater, b.FreeBlocks));
        Assert.Equal(0, b.Allocate(64));           // empty: the whole pool again
        Assert.Equal(-1, b.Allocate(1));
    }

    [Fact]
    public void Live_blocks_never_overlap_and_stay_under_the_high_water()
    {
        var rng = new Random(7);
        var b = new FxGpuBlocks(1 << 14);
        var live = new List<(int S, int L)>();
        for (int step = 0; step < 6000; step++)
        {
            if (live.Count > 0 && rng.NextDouble() < 0.48)
            {
                int i = rng.Next(live.Count);
                b.Free(live[i].S, live[i].L);
                live.RemoveAt(i);
            }
            else
            {
                int l = rng.Next(1, 300), s = b.Allocate(l);
                if (s < 0) continue;
                int size = 1 << FxGpuBlocks.ClassOf(l);
                Assert.DoesNotContain(live, r => s < r.S + (1 << FxGpuBlocks.ClassOf(r.L)) && r.S < s + size);
                Assert.True(s + size <= b.HighWater && b.HighWater <= b.Capacity);
                live.Add((s, l));
            }
            Assert.Equal(live.Sum(r => 1 << FxGpuBlocks.ClassOf(r.L)), b.Used);
            Assert.Equal(live.Count, b.Live);
        }
        foreach (var (s, l) in live.OrderBy(_ => rng.Next())) b.Free(s, l);
        Assert.Equal((0, 0, 0), (b.Used, b.HighWater, b.FreeBlocks));
    }
}
