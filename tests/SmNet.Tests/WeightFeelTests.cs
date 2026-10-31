using SmNet;
using Xunit;

namespace SmNet.Tests;

/// <summary>feel/weight-impact: the weight pass's pure maths (<see cref="WeightFeel"/>, <see cref="CameraImpulse"/>).</summary>
public class WeightFeelTests
{
    [Fact]
    public void Settle_is_none_below_its_speed_and_rises_with_the_fall()
    {
        Assert.Equal(0f, WeightFeel.SettleWeight(7.9f));
        Assert.Equal(WeightFeel.SettleMinWeight, WeightFeel.SettleWeight(WeightFeel.SettleFromSpeed), 3);
        Assert.Equal(WeightFeel.SettleMaxWeight, WeightFeel.SettleWeight(30f), 3);
        Assert.True(WeightFeel.SettleWeight(14f) > WeightFeel.SettleWeight(10f));
        Assert.Equal(0f, WeightFeel.SettleWeight(float.NaN));
    }

    [Fact]
    public void Landing_dip_scales_with_the_fall_speed_and_is_capped()
    {
        Assert.Equal(0f, WeightFeel.LandingDipDegrees(5f));
        Assert.Equal(0.35f, WeightFeel.LandingDipDegrees(8f), 3);
        Assert.Equal(1.2f, WeightFeel.LandingDipDegrees(20f), 3);
        Assert.Equal(1.2f, WeightFeel.LandingDipDegrees(60f), 3);
    }

    [Fact]
    public void Speed_of_a_drop()
    {
        Assert.Equal(20f, WeightFeel.SpeedOfDrop(10f), 3);
        Assert.Equal(0f, WeightFeel.SpeedOfDrop(-1f));
    }

    [Fact]
    public void Blast_jolt_falls_with_distance()
    {
        Assert.Equal(WeightFeel.BlastPeakDegrees, WeightFeel.BlastJoltDegrees(0f), 3);
        Assert.True(WeightFeel.BlastJoltDegrees(5f) > WeightFeel.BlastJoltDegrees(15f));
        Assert.Equal(0f, WeightFeel.BlastJoltDegrees(25f));
    }

    [Theory]
    [InlineData("sm_mp_ranged_lascannon", 1.5f)]
    [InlineData("csm_mp_ranged_plasma_cannon", 1.5f)]
    [InlineData("sm_mp_ranged_heavy_bolter", 1.35f)]
    [InlineData("sm_mp_ranged_bolter", 1.15f)]
    [InlineData("sm_mp_ranged_bolt_pistol", 1f)]
    public void Recoil_is_stronger_on_heavier_guns(string weapon, float scale) => Assert.Equal(scale, WeightFeel.RecoilScale(weapon), 3);

    [Fact]
    public void Camera_recoil_is_for_heavy_guns_and_halved_when_aiming()
    {
        Assert.Equal(0f, WeightFeel.CameraRecoilDegrees("sm_mp_ranged_bolter", 0, false));
        Assert.Equal(0f, WeightFeel.CameraRecoilDegrees("sm_mp_ranged_plasma_gun", 0, false));
        Assert.True(WeightFeel.CameraRecoilDegrees("sm_mp_ranged_plasma_gun", 2, false) > 0f);
        float full = WeightFeel.CameraRecoilDegrees("sm_mp_ranged_lascannon", 0, false);
        Assert.Equal(full / 2f, WeightFeel.CameraRecoilDegrees("sm_mp_ranged_lascannon", 0, true), 3);
    }

    static float Peak(CameraImpulse i, float seconds, float dt)
    {
        float peak = 0f;
        for (float t = 0f; t < seconds; t += dt) peak = System.MathF.Max(peak, System.MathF.Abs(i.Step(dt)));
        return peak;
    }

    [Fact]
    public void Impulse_rises_over_more_than_one_frame_and_returns_to_rest()
    {
        var i = new CameraImpulse();
        i.Add(1f);
        float first = i.Step(1f / 60f);
        Assert.InRange(first, 0.01f, 0.8f);   // not the whole kick in a frame
        float peak = Peak(i, 0.2f, 1f / 60f);
        Assert.InRange(peak, first, 1f);
        Peak(i, 0.8f, 1f / 60f);
        Assert.Equal(0f, i.Now);
    }

    [Fact]
    public void Impulse_decay_barely_depends_on_the_frame_rate()
    {
        float At(float dt) { var i = new CameraImpulse(); i.Add(1f); float v = 0f; for (int n = (int)System.MathF.Round(0.1f / dt); n > 0; n--) v = i.Step(dt); return v; }
        Assert.Equal(At(1f / 240f), At(1f / 30f), 1);
    }

    [Fact]
    public void Impulse_total_is_capped_so_a_burst_cannot_walk_the_view()
    {
        var i = new CameraImpulse();
        for (int k = 0; k < 100; k++) { i.Add(-0.7f); i.Step(1f / 60f); }
        Assert.InRange(i.Now, -CameraImpulse.MaxDegrees, 0f);
    }

    [Fact]
    public void Impulse_ignores_bad_numbers()
    {
        var i = new CameraImpulse();
        i.Add(float.NaN);
        Assert.Equal(0f, i.Step(float.NaN));
        Assert.Equal(0f, i.Step(0.016f));
    }
}
