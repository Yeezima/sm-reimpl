using System.Numerics;
using SmContent;

namespace SmNet.Tests;

/// <summary>
/// anim/strafe-blend: the blend weights' smoothing over time (<see cref="GaitWeightSmoother"/>), alone and behind the blend space on a strafe reversal (the owner's report of
/// 2026-11-06: the pose snapped when the strafe or run direction changed). Synthetic numbers only: no original data.
/// </summary>
public class GaitWeightSmootherTests
{
    const float Frame = 1f / 60f;

    [Fact]
    public void The_first_step_takes_the_target_as_it_is()
    {
        var s = new GaitWeightSmoother();
        Span<float> o = stackalloc float[3];
        s.Step([0.2f, 0.8f, 0f], Frame, o);
        Assert.Equal(0.2f, o[0], 5); Assert.Equal(0.8f, o[1], 5); Assert.Equal(0f, o[2], 5);
    }

    [Fact]
    public void A_flip_from_one_weight_to_another_eases_in_and_out_keeping_the_sum()
    {
        var s = new GaitWeightSmoother();
        Span<float> o = stackalloc float[2];
        s.Step([1f, 0f], Frame, o);
        float previous = 1f, previousStep = 0f, worstStep = 0f, worstBend = 0f;
        float at95 = -1f;
        for (int i = 1; i <= 60; i++)
        {
            s.Step([0f, 1f], Frame, o);
            Assert.True(o[0] >= 0f && o[1] >= 0f);
            Assert.Equal(1f, o[0] + o[1], 4);
            Assert.True(o[1] >= 1f - previous - 1e-5f, "no overshoot from rest");
            float step = o[1] - (1f - previous);
            worstStep = MathF.Max(worstStep, step);
            worstBend = MathF.Max(worstBend, MathF.Abs(step - previousStep));
            previousStep = step;
            previous = o[0];
            if (at95 < 0f && o[1] >= 0.95f) at95 = i * Frame;
        }
        Assert.True(worstStep < 0.15f, $"at most 15 % of the weight moves in one frame ({worstStep:F3})");
        Assert.True(worstBend < 0.06f, $"the weight's rate changes gradually: no kink at the start ({worstBend:F3})");
        Assert.InRange(at95, 0.15f, 0.3f);
    }

    [Fact]
    public void A_gap_or_a_new_size_snaps_to_the_target()
    {
        var s = new GaitWeightSmoother();
        Span<float> o = stackalloc float[2];
        s.Step([1f, 0f], Frame, o);
        s.Step([0f, 1f], GaitWeightSmoother.GapSeconds + 0.01f, o);
        Assert.Equal(1f, o[1], 5);
        Span<float> o3 = stackalloc float[3];
        s.Step([0f, 0f, 1f], Frame, o3);
        Assert.Equal(1f, o3[2], 5);
    }

    [Fact]
    public void Bad_input_does_not_poison_the_state()
    {
        var s = new GaitWeightSmoother();
        Span<float> o = stackalloc float[2];
        s.Step([0.5f, 0.5f], Frame, o);
        s.Step([float.NaN, 1f], Frame, o);
        Assert.True(float.IsFinite(o[0]) && float.IsFinite(o[1]));
        s.Step([0.5f, 0.5f], float.NaN, o);
        Assert.Equal(0.5f, o[0], 5);
    }

    /// <summary>Eight directions, walk and run each (the shape of the marine's relaxed run; as GaitBlendTests).</summary>
    static GaitSpace Space()
    {
        var directions = new List<(float, IReadOnlyList<ClipGait>)>();
        foreach (float angle in new[] { -180f, -135f, -90f, -45f, 0f, 45f, 90f, 135f })
        {
            var dir = new Vector2(MathF.Sin(angle * MathF.PI / 180f), MathF.Cos(angle * MathF.PI / 180f));
            float run = 4.5f + 1.8f * MathF.Cos(angle * MathF.PI / 180f);
            directions.Add((angle, [new ClipGait(1.333f, 0f, 0.667f, dir * 1.9f * 1.333f, 0.45f), new ClipGait(0.867f, 0f, 0.433f, dir * run * 0.867f, 0.2f)]));
        }
        return new GaitSpace(directions, cyclic: true);
    }

    /// <summary>
    /// A strafe reversal as the simulation makes it (5.85 m/s right to left at 36 m/s squared, sampled at 60 frames a second) through the damper and the blend space, with the
    /// idle share below 1 m/s: the largest frame-to-frame change of the weight vector (sum of the absolute changes) with and without the smoothing.
    /// </summary>
    static float WorstReversalChange(bool smooth)
    {
        var space = Space();
        var damper = new VelocityDamper();
        var smoother = new GaitWeightSmoother();
        int cells = space.DirectionCount * space.BandCount;
        var previous = new float[cells + 1];
        var solution = new GaitWeight[GaitSpace.MaxWeights];
        float vx = 5.85f, worst = 0f;
        for (int i = 0; i < 60; i++)
        {
            if (i >= 10) vx = MathF.Max(-5.85f, vx - 36f * Frame);
            var v = damper.Step(new Vector2(vx, 0f), Frame);
            var w = new float[cells + 1];
            int n = space.Solve(v, solution, out _, out _, out _);
            float moving = GaitBlend.MovingWeight(v.Length(), 1f);
            for (int k = 0; k < n; k++) w[solution[k].Direction * space.BandCount + solution[k].Band] += solution[k].Weight * moving;
            w[cells] = 1f - (n > 0 ? moving : 0f);
            if (smooth) smoother.Step(w, Frame, w);
            if (i > 0)
            {
                float change = 0f;
                for (int k = 0; k <= cells; k++) change += MathF.Abs(w[k] - previous[k]);
                worst = MathF.Max(worst, change);
            }
            previous = w;
        }
        return worst;
    }

    [Fact]
    public void A_strafe_reversal_no_longer_flips_the_clips_in_one_frame()
    {
        float before = WorstReversalChange(smooth: false), after = WorstReversalChange(smooth: true);
        Assert.True(before > 0.5f, $"without the smoothing a reversal moves at least half the weight in one frame ({before:F2})");
        Assert.True(after < 0.25f, $"with it, under a quarter ({after:F2}; before {before:F2})");
    }
}
