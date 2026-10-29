using System.Numerics;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// M36 client presentation rules (engine-free, ours): the own marine's drawn yaw / pitch, the position between predicted ticks, the sprint
/// hysteresis and the nearest movement direction. Synthetic numbers only.
/// </summary>
public class ViewSmoothingTests
{
    // ---- FollowAngle (DisplayYaw / pitch) ----

    [Fact]
    public void The_view_angle_is_drawn_while_the_state_follows_the_last_sampled_input()
    {
        Assert.Equal(1.234f, ViewSmoothing.FollowAngle(1.0f, 1.0f, 1.234f));
        // Within the tolerance the state still counts as following; just beyond it, it does not (a locked heading shows).
        Assert.Equal(2.0f, ViewSmoothing.FollowAngle(1.0f + ViewSmoothing.FollowTolerance * 0.99f, 1.0f, 2.0f));
        Assert.Equal(1.02f, ViewSmoothing.FollowAngle(1.02f, 1.0f, 2.0f));
        Assert.Equal(0.98f, ViewSmoothing.FollowAngle(0.98f, 1.0f, 2.0f));
    }

    [Fact]
    public void The_follow_check_wraps_at_plus_and_minus_pi()
    {
        // The state's yaw is kept inside one turn while the view's runs on: these two are a whole turn apart.
        float state = MathF.PI - 0.003f, sampled = -MathF.PI - 0.003f;
        Assert.Equal(7f, ViewSmoothing.FollowAngle(state, sampled, 7f));
        // 0.02 across the wrap is not following.
        Assert.Equal(MathF.PI - 0.01f, ViewSmoothing.FollowAngle(MathF.PI - 0.01f, -MathF.PI + 0.01f, 7f));
        Assert.Equal(9f, ViewSmoothing.FollowAngle(1.5f + MathF.Tau, 1.5f, 9f));   // a full turn apart follows
    }

    [Fact]
    public void Non_finite_angles_fall_back_to_the_state()
    {
        Assert.Equal(1f, ViewSmoothing.FollowAngle(1f, 1f, float.NaN));
        Assert.Equal(1f, ViewSmoothing.FollowAngle(1f, float.PositiveInfinity, 3f));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(3.5f, -2.783185f)]
    [InlineData(-3.5f, 2.783185f)]
    [InlineData(10f, -2.566371f)]
    public void WrapPi_maps_into_the_half_open_range(float angle, float expected) => Assert.Equal(expected, ViewSmoothing.WrapPi(angle), 4);

    // ---- tick fraction and interpolation ----

    [Fact]
    public void The_tick_fraction_runs_from_zero_to_one_across_a_tick_and_is_clamped()
    {
        const double tick = 1.0 / 30;
        Assert.Equal(0f, ViewSmoothing.TickFraction(10.0, 10.0 + tick, tick), 4);          // the tick just ran
        Assert.Equal(0.5f, ViewSmoothing.TickFraction(10.0 + tick / 2, 10.0 + tick, tick), 4);
        Assert.Equal(1f, ViewSmoothing.TickFraction(10.0 + tick, 10.0 + tick, tick), 4);   // the next is due
        Assert.Equal(1f, ViewSmoothing.TickFraction(11.0, 10.0 + tick, tick));             // a late client shows the newest tick
        Assert.Equal(0f, ViewSmoothing.TickFraction(5.0, 10.0 + tick, tick));              // the clock was set back
        Assert.Equal(1f, ViewSmoothing.TickFraction(1.0, 2.0, 0.0));
        Assert.Equal(1f, ViewSmoothing.TickFraction(double.NaN, 2.0, tick));
    }

    [Fact]
    public void The_position_is_drawn_between_the_last_two_ticks()
    {
        var a = new Vector3(0, 0, 0); var b = new Vector3(0.22f, 0, 0);
        Assert.Equal(a, ViewSmoothing.Interpolate(a, b, 0f));
        Assert.Equal(b, ViewSmoothing.Interpolate(a, b, 1f));
        Assert.Equal(0.11f, ViewSmoothing.Interpolate(a, b, 0.5f).X, 5);
        Assert.Equal(b, ViewSmoothing.Interpolate(a, b, 7f));    // clamped
        Assert.Equal(a, ViewSmoothing.Interpolate(a, b, -1f));
    }

    [Fact]
    public void A_teleport_or_a_missing_previous_tick_is_not_interpolated_across()
    {
        var respawn = new Vector3(50, 0, 0);
        Assert.Equal(respawn, ViewSmoothing.Interpolate(Vector3.Zero, respawn, 0.3f));
        Assert.Equal(respawn, ViewSmoothing.Interpolate(new Vector3(float.NaN, 0, 0), respawn, 0.3f));
        // 1.67 m a tick (a 50 m/s dive) is still interpolated.
        Assert.Equal(0.835f, ViewSmoothing.Interpolate(Vector3.Zero, new Vector3(0, 1.67f, 0), 0.5f).Y, 4);
    }

    [Fact]
    public void Drawn_over_frames_the_running_position_moves_every_frame_where_the_ticks_step()
    {
        // 60 fps frames over a 30 Hz tick: the simulation position steps 0.217 m every second frame; the drawn one moves every frame.
        const double tick = 1.0 / 30, frame = 1.0 / 60;
        const float speed = 6.5f;
        float PositionAtTick(long n) => (float)(n * tick * speed);
        int simStill = 0, drawnStill = 0; float previousSim = 0, previousDrawn = 0;
        for (int i = 1; i <= 120; i++)
        {
            double now = i * frame + 1e-9;
            long n = (long)Math.Floor(now / tick);                  // the newest tick that has run
            float fraction = ViewSmoothing.TickFraction(now, (n + 1) * tick, tick);
            float sim = PositionAtTick(n);
            float drawn = ViewSmoothing.Interpolate(new Vector3(PositionAtTick(n - 1), 0, 0), new Vector3(sim, 0, 0), fraction).X;
            if (i > 3)
            {
                if (MathF.Abs(sim - previousSim) < 1e-6f) simStill++;
                if (MathF.Abs(drawn - previousDrawn) < 1e-6f) drawnStill++;
                Assert.InRange(drawn - previousDrawn, 0f, 0.22f * 1.01f);
            }
            previousSim = sim; previousDrawn = drawn;
        }
        Assert.True(simStill > 40, $"the simulation's position stands still in about half of the frames ({simStill})");
        Assert.Equal(0, drawnStill);
    }

    // ---- reconciliation correction ----

    [Fact]
    public void A_correction_fades_out_of_the_drawn_position_instead_of_snapping()
    {
        var c = new CorrectionOffset();
        c.Add(new Vector3(0.4f, 0, 0));                         // the predicted position moved 0.4 m: draw from where it was
        Assert.Equal(-0.4f, c.Offset.X, 5);
        float last = c.Offset.X;
        for (int i = 0; i < 30; i++)
        {
            float now = c.Step(1f / 60f).X;
            Assert.InRange(now, last, 0f);                      // monotonically toward zero, never overshooting
            last = now;
        }
        Assert.True(MathF.Abs(last) < 0.4f * 0.01f, "gone after half a second");
        c.Add(new Vector3(ViewSmoothing.TeleportDistance + 1f, 0, 0));   // a teleport is shown at once
        Assert.Equal(Vector3.Zero, c.Offset);
        c.Add(new Vector3(float.NaN, 0, 0));
        Assert.Equal(Vector3.Zero, c.Offset);
    }

    // ---- sprint ----

    const float Run = 6.5f, Sprint = 10f, Enter = 0.25f, Leave = 0.1f;   // the install's speeds; the client's thresholds

    static bool Sprinting(bool was, float speed, float forward = 1f, bool allowed = true) =>
        ViewSmoothing.Sprinting(was, allowed, speed, forward, Run, Sprint, Enter, Leave);

    [Fact]
    public void The_sprint_starts_above_the_enter_speed_and_lasts_down_to_the_leave_speed()
    {
        float enter = Run + Enter * (Sprint - Run), leave = Run + Leave * (Sprint - Run);   // 7.375 and 6.85
        Assert.False(Sprinting(false, Run));                       // running forward at full speed is not a sprint
        Assert.False(Sprinting(false, enter));                     // the threshold itself is not above it
        Assert.True(Sprinting(false, enter + 0.01f));
        Assert.True(Sprinting(true, enter - 0.2f));                // hysteresis: held below the enter speed
        Assert.True(Sprinting(true, leave + 0.01f));
        Assert.False(Sprinting(true, leave));
        Assert.False(Sprinting(false, enter - 0.2f));
    }

    [Fact]
    public void The_sprint_needs_forward_movement_and_a_grounded_free_state()
    {
        Assert.False(Sprinting(true, 9f, forward: 0f));
        Assert.False(Sprinting(true, 9f, forward: -3f));           // backwards at speed (a knockback) is not a sprint
        Assert.False(Sprinting(true, 9f, allowed: false));         // in the air, rolling, charging or reacting
        Assert.False(ViewSmoothing.Sprinting(true, true, 9f, 1f, 10f, 10f, Enter, Leave));   // no sprint speed above run: never
        Assert.False(ViewSmoothing.Sprinting(true, true, 9f, float.NaN, Run, Sprint, Enter, Leave));
    }

    // ---- nearest direction ----

    static readonly float[] Eight = [-180f, -135f, -90f, -45f, 0f, 45f, 90f, 135f];   // the graph's list: back once, at -180

    [Theory]
    [InlineData(0f, 4)]
    [InlineData(40f, 5)]
    [InlineData(100f, 6)]
    [InlineData(-100f, 2)]
    [InlineData(180f, 0)]        // +180 is the back clip listed at -180
    [InlineData(179f, 0)]
    [InlineData(-179f, 0)]
    [InlineData(170f, 0)]        // 10 from back, 25 from back-right
    [InlineData(150f, 7)]
    [InlineData(540f, 0)]        // angles beyond a turn wrap
    public void The_nearest_direction_wraps_around_the_back(float angle, int expected) => Assert.Equal(expected, ViewSmoothing.NearestDirection(Eight, angle, -1, 8f));

    [Fact]
    public void The_direction_last_shown_is_kept_inside_the_hold_band_only()
    {
        // At 25 degrees forward and forward-right (0 and 45) are 25 and 20 away: forward-right is nearest, but forward is kept within 8 degrees.
        Assert.Equal(5, ViewSmoothing.NearestDirection(Eight, 25f, -1, 8f));
        Assert.Equal(4, ViewSmoothing.NearestDirection(Eight, 25f, 4, 8f));
        // At 30 degrees: 30 against 15, a difference of 15 > 8: the nearest wins.
        Assert.Equal(5, ViewSmoothing.NearestDirection(Eight, 30f, 4, 8f));
        // The hold works across the wrap too: 178 is nearest back (index 0); previous = back-right (index 7, 135) is 43 away: not kept.
        Assert.Equal(0, ViewSmoothing.NearestDirection(Eight, 178f, 7, 8f));
        Assert.Equal(0, ViewSmoothing.NearestDirection(Eight, -170f, 1, 8f));   // 10 from back, 35 from back-left: back
        Assert.Equal(-1, ViewSmoothing.NearestDirection([], 10f, -1, 8f));
        Assert.Equal(4, ViewSmoothing.NearestDirection(Eight, 0f, 99, 8f));   // a stale index is ignored
    }

    // ---- the client's tick state ----

    [Fact]
    public void The_client_exposes_the_previous_predicted_tick_and_a_fraction_that_runs_through_each_tick()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0), seed: 5, clients: 1);
        var c = h.Clients[0];
        h.Run(TimeSpan.FromSeconds(2), (_, t) => new PlayerInput(t, 0, 1, 0, InputButtons.None));
        Assert.Equal(SmNet.ClientStatus.Connected, c.Status);
        var fractions = new List<float>();
        float biggestStep = 0f;
        for (int i = 0; i < 200; i++)
        {
            h.Step((_, t) => new PlayerInput(t, 0, 1, 0, InputButtons.None));
            fractions.Add(c.TickFraction);
            Assert.InRange(c.TickFraction, 0f, 1f);
            // Previous is the state one tick before: one tick's travel behind.
            float step = Vector3.Distance(c.Predicted.Position, c.PreviousPredicted.Position);
            Assert.InRange(step, 0f, 0.4f);
            biggestStep = MathF.Max(biggestStep, step);
        }
        Assert.InRange(biggestStep, 0.1f, 0.4f);   // about 6.5 m/s x 1/30 s = 0.22 m
        Assert.Contains(fractions, f => f < 0.1f);
        Assert.Contains(fractions, f => f > 0.9f);
        Assert.True(fractions.Zip(fractions.Skip(1)).Count(p => p.Second < p.First) >= 4, "the fraction wraps once per tick");
        Assert.True(c.CorrectionSerial > 0, "snapshots reconcile the prediction");
    }
}
