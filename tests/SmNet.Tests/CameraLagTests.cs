using SmNet;

namespace SmNet.Tests;

/// <summary>ui/hud-fidelity camera lag (temporary, owner: likely to be removed): the rig's spring converges, snaps on big jumps, and resets after any frame it did not run (death).</summary>
public class CameraLagTests
{
    const float Dt = 1f / 60f, Smooth = 0.05f;

    [Fact]
    public void Follows_a_turn_late_and_converges()
    {
        var lag = new CameraLag();
        long frame = 0;
        Assert.Equal((0f, 0f), lag.Step(0f, 0f, Dt, Smooth, true, frame++));   // the first step snaps
        Assert.False(lag.Lagging);
        // A turn of 0.5 rad at once: the lens trails it, then arrives within about 0.3 s.
        var (y1, _) = lag.Step(0.5f, 0f, Dt, Smooth, true, frame++);
        Assert.True(lag.Lagging);
        Assert.InRange(y1, 0.001f, 0.3f);
        float y = y1;
        for (int i = 0; i < 18; i++) y = lag.Step(0.5f, 0f, Dt, Smooth, true, frame++).Yaw;
        Assert.InRange(y, 0.49f, 0.51f);
        // Critically damped: no overshoot.
        for (int i = 0; i < 60; i++) Assert.True(lag.Step(0.5f, 0f, Dt, Smooth, true, frame++).Yaw <= 0.5f + 1e-4f);
    }

    [Fact]
    public void Wraps_across_pi_the_short_way()
    {
        var lag = new CameraLag();
        long frame = 0;
        lag.Step(3.1f, 0f, Dt, Smooth, true, frame++);
        var (y, _) = lag.Step(-3.1f, 0f, Dt, Smooth, true, frame++);   // 0.083 rad the short way, not 6.2 back
        Assert.True(MathF.Abs(ViewSmoothing.WrapPi(y - 3.1f)) < 0.09f);
    }

    [Fact]
    public void Snaps_on_a_big_jump_a_teleport_and_when_off()
    {
        var lag = new CameraLag();
        long frame = 0;
        lag.Step(0f, 0f, Dt, Smooth, true, frame++);
        Assert.Equal(2f, lag.Step(2f, 0f, Dt, Smooth, true, frame++).Yaw);            // more than 1.2 rad
        Assert.False(lag.Lagging);
        Assert.Equal(2.3f, lag.Step(2.3f, 0f, Dt, Smooth, true, frame++, jumped: true).Yaw);
        Assert.Equal(2.6f, lag.Step(2.6f, 0.1f, Dt, 0f, true, frame++).Yaw);           // --camera-lag 0
        Assert.Equal(2.9f, lag.Step(2.9f, 0f, Dt, Smooth, false, frame++).Yaw);        // disabled (scoped, dead)
    }

    [Fact]
    public void A_frame_without_the_rig_resets_it_so_a_respawn_does_not_swing()
    {
        var lag = new CameraLag();
        long frame = 0;
        lag.Step(0f, 0f, Dt, Smooth, true, frame++);
        lag.Step(0.4f, 0f, Dt, Smooth, true, frame++);
        Assert.True(lag.Lagging);
        // Dead for 300 frames (the death camera ran instead), then respawned facing 0.8 rad (45 degrees): the first step is the view itself.
        frame += 300;
        Assert.Equal((0.8f, 0f), lag.Step(0.8f, 0f, Dt, Smooth, true, frame++));
        Assert.False(lag.Lagging);
        Assert.True(lag.Step(1.0f, 0f, Dt, Smooth, true, frame++).Yaw < 1.0f);   // lags again from there
        lag.Reset();
        Assert.Equal(1.2f, lag.Step(1.2f, 0f, Dt, Smooth, true, frame++).Yaw);
    }
}
