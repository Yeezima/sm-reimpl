using SmNet;
using Xunit;

namespace SmNet.Tests;

/// <summary>M44a: the test bots' aim error (a test aid, ours): a perfect aim adds nothing, and the standard deviation is set so that the intended fraction of shots lands on a body at the medium range.</summary>
public class BotAimModelTests
{
    const float Dt = 1f / 30f;

    [Fact]
    public void A_perfect_accuracy_adds_no_error()
    {
        var model = new BotAimModel(1f, 7);
        Assert.True(model.IsPerfect);
        for (int i = 0; i < 100; i++) Assert.Equal((0f, 0f), model.Step(Dt, firing: i % 2 == 0));
        Assert.Equal(0f, BotAimModel.SigmaFor(1f));
        Assert.Equal(0f, BotAimModel.SigmaFor(0.9995f));
    }

    [Fact]
    public void Sigma_falls_as_accuracy_rises_and_its_hit_chance_at_medium_range_is_the_accuracy()
    {
        float previous = float.MaxValue;
        foreach (float accuracy in new[] { 0.05f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 0.99f })
        {
            float sigma = BotAimModel.SigmaFor(accuracy);
            Assert.InRange(sigma, 1e-5f, previous - 1e-9f);
            previous = sigma;
            Assert.Equal(accuracy, BotAimModel.HitChance(sigma, BotAimModel.ReferenceDistance), 3);
        }
        // The error is an angle: the chance falls with distance.
        float s = BotAimModel.SigmaFor(0.25f);
        Assert.True(BotAimModel.HitChance(s, 5f) > BotAimModel.HitChance(s, 15f));
        Assert.True(BotAimModel.HitChance(s, 15f) > BotAimModel.HitChance(s, 60f));
        Assert.True(BotAimModel.HitChance(s, 100f) < 0.05f);
    }

    [Fact]
    public void The_default_error_puts_about_the_asked_fraction_of_firing_ticks_on_the_body_at_medium_range()
    {
        foreach (float accuracy in new[] { 0.25f, 0.6f })
        {
            var model = new BotAimModel(accuracy, 12345);
            float cone = BotAimModel.BodyRadius / BotAimModel.ReferenceDistance;
            int hits = 0, n = 600_000;
            for (int i = 0; i < n; i++)
            {
                var (yaw, pitch) = model.Step(Dt, firing: true);
                if (MathF.Sqrt(yaw * yaw + pitch * pitch) <= cone) hits++;
            }
            Assert.InRange(hits / (float)n, accuracy - 0.03f, accuracy + 0.03f);
        }
    }

    [Fact]
    public void The_wander_is_slow_and_the_scatter_is_per_shot()
    {
        var model = new BotAimModel(0.25f, 99);
        // Not firing: the offset only drifts, a small step per tick.
        var last = model.Step(Dt, false);
        float biggestStep = 0f, squares = 0f;
        for (int i = 0; i < 3000; i++)
        {
            var now = model.Step(Dt, false);
            biggestStep = MathF.Max(biggestStep, MathF.Abs(now.Yaw - last.Yaw)); squares += (now.Yaw - last.Yaw) * (now.Yaw - last.Yaw);
            last = now;
        }
        Assert.True(MathF.Sqrt(squares / 3000f) < model.Sigma * 0.2f, "the wander moves a little each tick");
        Assert.True(biggestStep < model.Sigma, $"the wander jumped {biggestStep}");
        // Firing: every tick has its own scatter on top, so successive firing ticks differ by about the scatter.
        float sum = 0f;
        var prev = model.Step(Dt, true);
        for (int i = 0; i < 2000; i++)
        {
            var now = model.Step(Dt, true);
            sum += MathF.Abs(now.Yaw - prev.Yaw);
            prev = now;
        }
        Assert.True(sum / 2000f > model.Sigma * BotAimModel.ShotShare * 0.8f);
    }

    [Fact]
    public void The_scatter_is_only_for_firing_ticks_and_the_parts_add_up_to_the_whole()
    {
        var parts = new BotAimModel(0.25f, 42);
        var whole = new BotAimModel(0.25f, 42);
        for (int i = 0; i < 200; i++)
        {
            bool firing = i % 3 != 0;
            var (wy, wp, sy, sp) = parts.StepParts(Dt, firing);
            var (y, p) = whole.Step(Dt, firing);
            if (!firing) Assert.Equal((0f, 0f), (sy, sp));
            else Assert.NotEqual(0f, sy);
            Assert.Equal(wy + sy, y, 6);
            Assert.Equal(wp + sp, p, 6);
        }
    }

    [Fact]
    public void The_same_seed_gives_the_same_error_and_another_seed_another()
    {
        var a = new BotAimModel(0.25f, 5); var b = new BotAimModel(0.25f, 5); var c = new BotAimModel(0.25f, 6);
        bool differs = false;
        for (int i = 0; i < 50; i++)
        {
            var x = a.Step(Dt, true); var y = b.Step(Dt, true); var z = c.Step(Dt, true);
            Assert.Equal(x, y);
            differs |= x != z;
        }
        Assert.True(differs);
    }

    [Fact]
    public void A_non_finite_accuracy_is_taken_as_perfect_and_the_range_is_clamped()
    {
        Assert.True(new BotAimModel(float.NaN, 1).IsPerfect);
        Assert.True(new BotAimModel(5f, 1).IsPerfect);
        Assert.False(new BotAimModel(-1f, 1).IsPerfect);   // clamped to 0: the widest error
        Assert.True(BotAimModel.SigmaFor(0f) > BotAimModel.SigmaFor(0.25f));
    }
}
