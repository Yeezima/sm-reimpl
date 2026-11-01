using SmContent;
using SmSim;
using Xunit;

namespace SmNet.Tests;

/// <summary>feel/melee-reliability: the sooner step, the slow strikes' tracking to the hit, the stalker crater.</summary>
public class MeleeReliabilityTests
{
    static readonly MeleeStep Swing = new(2f, 0.6f, 0f, 0f, 0f, 1f, 100f, 0f, 0f)
    {
        Reach = new MeleeReach(0f, 0.3f, 0f, 0f, 0f, 0f, [new MeleeVariant(0f, [0f, 1f, 2f, 3f, 4f])]),
    };

    static SimConfig Early => new() { MeleeLock = MeleeLockSpec.Default with { StepEarly = 1.5f } };

    [Fact]
    public void Step_reaches_its_hit_point_sooner_and_the_whole_motion_is_unchanged()
    {
        var c = Early;
        Assert.Equal(0.6f, MeleeLock.MotionTime(Swing, 0.4f, c), 3);
        Assert.Equal(2f, MeleeLock.MotionTime(Swing, 2f, c), 3);
        float last = 0f;
        for (float t = 0f; t <= 2f; t += 0.05f)
        {
            float clip = MeleeLock.MotionTime(Swing, t, c);
            Assert.True(clip >= last - 1e-5f && clip >= t - 1e-5f);
            last = clip;
        }
    }

    [Fact]
    public void Step_keeps_the_clips_timing_at_one()
    {
        var c = new SimConfig();
        Assert.Equal(1f, c.MeleeLock.StepEarly);
        Assert.Equal(0.4f, MeleeLock.MotionTime(Swing, 0.4f, c));
    }

    [Fact]
    public void Tracking_strike_keeps_turning_until_the_hit()
    {
        var c = new SimConfig();
        Assert.Equal(0.3f, MeleeLock.AlignWindow(Swing, c).Close, 3);
        Assert.Equal(0.6f, MeleeLock.AlignWindow(Swing with { TrackToHit = true }, c).Close, 3);
    }

    [Theory]
    [InlineData("sm_mp_ranged_sniper_rifle", true)]
    [InlineData("csm_mp_ranged_sniper_rifle", true)]
    [InlineData("sm_mp_ranged_bolter", false)]
    public void Stalker_is_found_by_its_file(string wargear, bool stalker) => Assert.Equal(stalker, StalkerCrater.Is(wargear));

    [Fact]
    public void Crater_follows_the_surface_and_beats_the_bolters()
    {
        Assert.Equal("bolter_dmg_metal", StalkerCrater.Effect("metal"));
        Assert.Equal("bolter_dmg_stone", StalkerCrater.Effect("stone"));
        Assert.True(StalkerCrater.Scale > 1f);
    }
}
