using System.Numerics;
using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// M45 (the owner's request: the marine should look like he falls after the thrust is cut): the flight clips posed by the vertical angle of motion
/// (<see cref="JumpFlightPose"/>, the graph's user-controlled time) and when the thrust ends for the sound (<see cref="ThrustEndRules"/>). Engine-free.
/// </summary>
public class JumpFlightPoseTests
{
    [Theory]
    [InlineData(10f, 0f, 0f)]       // straight up: the clip's start (rising)
    [InlineData(-10f, 0f, 1f)]      // straight down: its end (falling)
    [InlineData(0f, 10f, 0.5f)]     // level: the middle (the apex tuck)
    [InlineData(10f, 10f, 0.25f)]   // 45 degrees up
    [InlineData(-10f, 10f, 0.75f)]  // 45 degrees down
    [InlineData(0f, 0f, 0.5f)]      // no motion: level
    [InlineData(5f, -5f, 0.25f)]    // the horizontal speed's sign does not matter
    public void The_target_fraction_is_the_vertical_angle_from_straight_up_to_straight_down(float vz, float h, float expected) =>
        Assert.Equal(expected, JumpFlightPose.Target(vz, h), 4);

    [Fact]
    public void The_target_falls_monotonically_as_the_vertical_speed_drops_and_bad_input_is_level()
    {
        float previous = -1f;
        for (float vz = 30f; vz >= -30f; vz -= 0.5f)
        {
            float f = JumpFlightPose.Target(vz, 4f);
            Assert.InRange(f, 0f, 1f);
            Assert.True(f > previous);
            previous = f;
        }
        Assert.Equal(0.5f, JumpFlightPose.Target(float.NaN, 1f));
        Assert.Equal(0.5f, JumpFlightPose.Target(1f, float.PositiveInfinity));
    }

    [Fact]
    public void Damping_starts_at_the_target_approaches_it_without_overshoot_and_holds_still_without_time()
    {
        Assert.Equal(0.8f, JumpFlightPose.Damp(float.NaN, 0.8f, 0.016f));
        float f = 0f;
        for (int k = 0; k < 60; k++)
        {
            float next = JumpFlightPose.Damp(f, 1f, 1f / 60f);
            Assert.True(next > f && next <= 1f);
            f = next;
        }
        Assert.True(f > 0.99f);   // one second is more than 6 time constants
        // One time constant closes 1 − 1/e of the gap.
        Assert.Equal(1f - MathF.Exp(-1f), JumpFlightPose.Damp(0f, 1f, JumpFlightPose.DampSeconds), 4);
        Assert.Equal(0.3f, JumpFlightPose.Damp(0.3f, 1f, 0f));
        Assert.Equal(1f, JumpFlightPose.Damp(0.3f, 1f, 0.1f, tau: 0f));
        Assert.Equal(0.5f, JumpFlightPose.Damp(0.5f, float.NaN, 10f), 3);
    }

    [Fact]
    public void The_low_set_is_shown_near_the_ground_with_hysteresis_around_the_graphs_midpoint()
    {
        Assert.Equal(13.5f, JumpFlightPose.LowHighSwitch);
        Assert.True(JumpFlightPose.Low(2f, false));
        Assert.False(JumpFlightPose.Low(20f, true));
        Assert.True(JumpFlightPose.Low(13.5f, true));     // inside the band: unchanged
        Assert.False(JumpFlightPose.Low(13.5f, false));
        Assert.True(JumpFlightPose.Low(12.4f, false));
        Assert.False(JumpFlightPose.Low(14.6f, true));
        Assert.False(JumpFlightPose.Low(float.PositiveInfinity, true));   // nothing below: high
    }

    [Fact]
    public void The_clip_time_is_the_fraction_of_its_length()
    {
        Assert.Equal(0f, JumpFlightPose.Time(0f, 3.333f));
        Assert.Equal(3.333f, JumpFlightPose.Time(1f, 3.333f), 4);
        Assert.Equal(3.333f, JumpFlightPose.Time(7f, 3.333f), 4);
        Assert.Equal(3.333f / 2f, JumpFlightPose.Time(float.NaN, 3.333f), 4);
        Assert.Equal(0f, JumpFlightPose.Time(0.5f, 0f));
    }

    [Fact]
    public void Over_a_real_jump_the_pose_rises_tucks_at_the_apex_and_falls_and_a_cut_turns_to_falling_soon_after_the_release()
    {
        // The jump of JumpHeightTests (synthetic install-shaped pack, the cut on), let go on tick 20; the pose stepped at 30 Hz.
        var c = JumpPackDashTests.Config with { JumpPacks = [JumpPackDashTests.Config.JumpPacks[0] with { ReleaseCut = true }] };
        var s = Combat.Respawned(new PlayerState(1, default, Vector3.Zero, 0f, true) { Class = 0 }, c);
        float f = float.NaN;
        var poses = new List<(PlayerState S, float F)>();
        for (int k = 0; k < 400 && s.Jump != JumpPhase.Landed; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, k < 20 ? InputButtons.Jump : InputButtons.None, 0f), c, out _);
            if (JumpPack.InFlight(s.Jump))
            {
                f = JumpFlightPose.Damp(f, JumpFlightPose.Target(s.Velocity.Z, new Vector2(s.Velocity.X, s.Velocity.Y).Length()), 1f / 30f);
                poses.Add((s, f));
            }
        }
        Assert.True(poses[0].F < 0.25f, $"lift-off pose {poses[0].F:F2}");          // rising: the clip's first quarter
        int release = poses.FindIndex(p => p.S.JumpCut);
        int apex = poses.FindIndex(p => p.S.Jump == JumpPhase.Hang);
        Assert.InRange(apex - release, 4, 10);
        Assert.True(poses[apex + 20].F > 0.6f, $"falling pose {poses[apex + 20].F:F2}");   // two thirds of a second after the apex (16 m/s forward): past the tuck, falling
        Assert.True(poses[^1].F > 0.7f, $"landing pose {poses[^1].F:F2}");
    }

    [Fact]
    public void The_thrust_ends_at_the_apex_without_a_cut_and_at_the_release_with_one_but_not_twice()
    {
        Assert.True(ThrustEndRules.EndsNow(true, 0.4f, -0.2f, false, false));     // the apex
        Assert.False(ThrustEndRules.EndsNow(true, 5f, 4f, false, false));         // still rising
        Assert.False(ThrustEndRules.EndsNow(false, 0.4f, -0.2f, false, false));   // not jumping
        Assert.True(ThrustEndRules.EndsNow(true, 12f, 11f, false, true));          // let go while rising
        Assert.False(ThrustEndRules.EndsNow(true, 11f, 9f, true, true));           // the cut goes on
        Assert.False(ThrustEndRules.EndsNow(true, 0.3f, -0.1f, true, false));      // the apex after a cut: ended already
        Assert.False(ThrustEndRules.EndsNow(true, -0.1f, -0.5f, false, false));    // falling
    }
}
