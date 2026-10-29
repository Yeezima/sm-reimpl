using System.Numerics;
using SmContent;

namespace SmNet.Tests;

/// <summary>
/// M40-T4: the locomotion blend space's engine-free maths (<see cref="ClipGait"/>, <see cref="GaitPhase"/>, <see cref="GaitSpace"/>, <see cref="GaitBlend"/>,
/// <see cref="VelocityDamper"/>). Synthetic numbers only: no original data.
/// </summary>
public class GaitBlendTests
{
    // ---- a synthetic cycle -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The toes and ankles of a body moving at <paramref name="velocity"/> (X right, Y forward) over one cycle of <paramref name="duration"/> s: each foot is flat
    /// on the ground for <paramref name="stance"/> of the cycle (toe 1 cm up, ankle 20 cm), moving backward against the body at the body's speed, and swings
    /// forward lifted for the rest; the right foot half a cycle after the left, the left foot's stance starting at <paramref name="offset"/> of the cycle.
    /// </summary>
    static (Vector3[] LeftToe, Vector3[] RightToe, Vector3[] LeftAnkle, Vector3[] RightAnkle) Cycle(float duration, Vector2 velocity, float stance = 0.4f, float offset = 0f, int n = 64)
    {
        Vector3[][] result = [new Vector3[n], new Vector3[n], new Vector3[n], new Vector3[n]];
        for (int foot = 0; foot < 2; foot++)
            for (int i = 0; i < n; i++)
            {
                float u = (float)i / n - offset - 0.5f * foot;
                u -= MathF.Floor(u);                              // 0 at the foot's touchdown
                float x = foot == 0 ? -0.2f : 0.2f;
                Vector2 slide, centre = new(x, 0f);
                float lift = 0f;
                if (u < stance) slide = -velocity * (u * duration);                                   // stance: moves back at the body's velocity
                else
                {
                    float s = (u - stance) / (1f - stance);                                           // swing: back to the touchdown point
                    slide = -velocity * (stance * duration) * (1f - s);
                    lift = 0.15f * MathF.Sin(MathF.PI * s);
                }
                var p = centre + slide;
                result[foot][i] = new Vector3(p.X, p.Y, 0.01f + lift);
                result[2 + foot][i] = new Vector3(p.X, p.Y, 0.20f + lift);
            }
        return (result[0], result[1], result[2], result[3]);
    }

    static ClipGait? Measure(float duration, Vector2 velocity, float stance = 0.4f, float offset = 0f)
    {
        var (lt, rt, la, ra) = Cycle(duration, velocity, stance, offset);
        return ClipGait.FromFeet(duration, lt, rt, la, ra);
    }

    [Fact]
    public void Stride_is_the_distance_the_body_moves_per_cycle()
    {
        var gait = Measure(0.9f, new Vector2(1.5f, 4.5f));
        Assert.NotNull(gait);
        // Sampled 64 times a cycle, the planted window's edges are a little off: within 5 %.
        Assert.InRange(gait!.Stride.X, 1.5f * 0.9f * 0.95f, 1.5f * 0.9f * 1.05f);
        Assert.InRange(gait.Stride.Y, 4.5f * 0.9f * 0.95f, 4.5f * 0.9f * 1.05f);
        Assert.InRange(gait.Speed, new Vector2(1.5f, 4.5f).Length() * 0.95f, new Vector2(1.5f, 4.5f).Length() * 1.05f);
        Assert.True(gait.Usable);
    }

    [Fact]
    public void Foot_plant_times_are_the_middles_of_the_stances()
    {
        // The left foot stands during 0.1 to 0.5 of a 0.8 s cycle (middle 0.3 = 0.24 s), the right half a cycle later (0.8 = 0.64 s).
        var gait = Measure(0.8f, new Vector2(0f, 3f), stance: 0.4f, offset: 0.1f);
        Assert.NotNull(gait);
        Assert.Equal(0.24f, gait!.SyncLeft, 1);
        Assert.Equal(0.64f, gait.SyncRight, 1);
        Assert.InRange(gait.PlantedFraction, 0.3f, 0.45f);
    }

    [Fact]
    public void A_gait_without_a_planted_foot_is_refused()
    {
        var flat = Enumerable.Range(0, 32).Select(i => new Vector3(0f, i * 0.1f, 0.5f + i * 0.01f)).ToArray();
        // The lowest sample is the planted one by definition, but a single sample has no neighbours to take a velocity from.
        Assert.Null(ClipGait.FromFeet(1f, flat, flat));
        Assert.Null(ClipGait.FromFeet(1f, flat.Take(4).ToArray(), flat.Take(4).ToArray()));
        Assert.Null(ClipGait.FromFeet(0f, flat, flat));
    }

    // ---- phase ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Phase_is_zero_at_the_left_plant_and_half_at_the_right_one()
    {
        var gait = new ClipGait(0.9f, 0.3f, 0.8f, new Vector2(0, 4), 0.2f);
        Assert.Equal(0f, GaitPhase.PhaseAt(gait, 0.3f), 4);
        Assert.Equal(0.5f, GaitPhase.PhaseAt(gait, 0.8f), 4);
        Assert.Equal(0.3f, GaitPhase.TimeAt(gait, 0f), 4);
        Assert.Equal(0.8f, GaitPhase.TimeAt(gait, 0.5f), 4);
    }

    [Fact]
    public void Phase_and_time_are_inverses_even_with_uneven_halves()
    {
        // The right plant 0.3 of the cycle after the left: the halves are 0.3 and 0.7 of the clip.
        var gait = new ClipGait(1.2f, 1.0f, 0.4f, new Vector2(0, 4), 0.2f);   // 1.0 + 0.36 wraps to 0.16: use the declared 0.4 (0.6 s after)
        for (int i = 0; i < 100; i++)
        {
            float phase = i / 100f;
            Assert.Equal(phase, GaitPhase.PhaseAt(gait, GaitPhase.TimeAt(gait, phase)), 3);
        }
        for (float t = 0f; t < 1.2f; t += 0.07f)
            Assert.Equal(t, GaitPhase.TimeAt(gait, GaitPhase.PhaseAt(gait, t)), 3);
    }

    [Fact]
    public void Blended_clips_of_different_length_and_start_have_their_left_foot_down_at_the_same_phase()
    {
        var run = new ClipGait(0.867f, 0.41f, 0.84f, new Vector2(0, 5.5f), 0.2f);
        var walk = new ClipGait(1.333f, 0.77f, 0.12f, new Vector2(0, 2.6f), 0.45f);
        Assert.Equal(GaitPhase.TimeAt(run, 0f), run.SyncLeft, 4);
        Assert.Equal(GaitPhase.TimeAt(walk, 0f), walk.SyncLeft, 4);
        // The right plant sits at 0.5 in both although it is 0.43 s into the run clip and 0.57 s (wrapped) into the walk clip after the left one.
        Assert.Equal(0.5f, GaitPhase.PhaseAt(run, 0.84f), 3);
        Assert.Equal(0.5f, GaitPhase.PhaseAt(walk, 0.12f), 3);
    }

    [Fact]
    public void Bad_sync_data_falls_back_to_an_even_phase()
    {
        // Both feet at the same time: no stride has that; the halves are taken as even.
        var gait = new ClipGait(1f, 0.3f, 0.3f, new Vector2(0, 4), 0.2f);
        Assert.Equal(0.8f, GaitPhase.TimeAt(gait, 0.5f), 3);
        Assert.Equal(0.5f, GaitPhase.PhaseAt(gait, 0.8f), 3);
    }

    [Theory]
    [InlineData(-0.2f, 1f, 0.8f)]
    [InlineData(3.3f, 1f, 0.3f)]
    [InlineData(0f, 0f, 0f)]
    [InlineData(float.NaN, 1f, 0f)]
    public void Wrap_stays_in_the_period(float value, float period, float expected) => Assert.Equal(expected, GaitPhase.Wrap(value, period), 3);

    // ---- the blend space ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Eight directions, walk and run each, strides from the speed profile given (forward fastest, back slowest), like the marine's relaxed run.</summary>
    static GaitSpace RelaxedSpace(Func<float, float>? runSpeed = null)
    {
        runSpeed ??= angle => 4.5f + 1.8f * MathF.Cos(angle * MathF.PI / 180f);   // 6.3 forward, 4.5 sideways, 2.7 back
        var directions = new List<(float, IReadOnlyList<ClipGait>)>();
        // Listed from the back like the graph: -180, -135, ... 135 (the back direction at -180 only).
        foreach (float angle in new[] { -180f, -135f, -90f, -45f, 0f, 45f, 90f, 135f })
        {
            var dir = new Vector2(MathF.Sin(angle * MathF.PI / 180f), MathF.Cos(angle * MathF.PI / 180f));
            var walk = new ClipGait(1.333f, 0f, 0.667f, dir * 1.9f * 1.333f, 0.45f);
            var run = new ClipGait(0.867f, 0f, 0.433f, dir * runSpeed(angle) * 0.867f, 0.2f);
            directions.Add((angle, [walk, run]));
        }
        return new GaitSpace(directions, cyclic: true);
    }

    static Vector2 Velocity(float headingDegrees, float speed) => new Vector2(MathF.Sin(headingDegrees * MathF.PI / 180f), MathF.Cos(headingDegrees * MathF.PI / 180f)) * speed;

    /// <summary>The body velocity the solution's planted feet make: cadence x sum of weight x stride.</summary>
    static Vector2 Produced(GaitSpace space, GaitWeight[] weights, int count, float cadence)
    {
        var sum = Vector2.Zero;
        for (int i = 0; i < count; i++) sum += space.Clip(weights[i].Direction, weights[i].Band).Stride * weights[i].Weight;
        return sum * cadence;
    }

    [Fact]
    public void The_blends_planted_feet_cancel_the_velocity_at_every_heading_and_speed()
    {
        var space = RelaxedSpace();
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        int checkedCount = 0;
        for (float heading = -180f; heading < 180f; heading += 7.5f)
            for (float speed = 1.5f; speed <= 6.0f; speed += 0.5f)
            {
                var v = Velocity(heading, speed);
                int n = space.Solve(v, buffer, out float cadence, out float ratio, out bool exact);
                Assert.InRange(n, 1, GaitSpace.MaxWeights);
                Assert.Equal(1f, buffer.Take(n).Sum(w => w.Weight), 3);
                Assert.InRange(ratio, GaitSpace.MinRatio, GaitSpace.MaxRatio);
                if (!exact) continue;
                var made = Produced(space, buffer, n, cadence);
                Assert.True((made - v).Length() < 0.02f * speed + 0.02f, $"heading {heading} speed {speed}: made {made} wanted {v}");
                checkedCount++;
            }
        Assert.True(checkedCount > 150, "most of the grid is inside the clips' pace limits");
    }

    [Fact]
    public void A_velocity_along_a_clips_direction_uses_that_direction_only()
    {
        var space = RelaxedSpace();
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        int n = space.Solve(Velocity(90f, 4.5f), buffer, out float cadence, out _, out bool exact);
        Assert.True(exact);
        // Direction 6 is +90 (right): the run band at its own pace: one clip, cadence one cycle per clip length.
        Assert.Equal(1, n);
        Assert.Equal(new GaitWeight(6, 1, 1f), buffer[0]);
        Assert.Equal(1f / 0.867f, cadence, 2);
    }

    [Fact]
    public void Between_two_directions_the_weights_follow_the_angle_and_sum_to_the_stride_direction()
    {
        var space = RelaxedSpace();
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        int n = space.Solve(Velocity(22.5f, 6f), buffer, out _, out _, out _);
        Assert.Equal(2, n);
        var forward = buffer.Take(n).Single(w => space.AngleOf(w.Direction) == 0f);
        var diagonal = buffer.Take(n).Single(w => space.AngleOf(w.Direction) == 45f);
        Assert.InRange(forward.Weight, 0.3f, 0.7f);
        Assert.Equal(1f, forward.Weight + diagonal.Weight, 3);
        // Nearer to 45 degrees, more of the diagonal.
        space.Solve(Velocity(40f, 6f), buffer, out _, out _, out _);
        Assert.True(buffer.Take(2).Single(w => space.AngleOf(w.Direction) == 45f).Weight > diagonal.Weight);
    }

    [Fact]
    public void Between_the_walk_and_the_run_the_bands_blend_with_the_speed()
    {
        var space = RelaxedSpace();
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        int slow = space.Solve(Velocity(0f, 2.0f), buffer, out float slowCadence, out _, out _);
        Assert.True(buffer.Take(slow).Where(w => w.Band == 0).Sum(w => w.Weight) > 0.9f, "about the walk");
        int mid = space.Solve(Velocity(0f, 4.1f), buffer, out float midCadence, out _, out _);
        float walkShare = buffer.Take(mid).Where(w => w.Band == 0).Sum(w => w.Weight);
        Assert.InRange(walkShare, 0.3f, 0.7f);
        int fast = space.Solve(Velocity(0f, 6.2f), buffer, out float fastCadence, out _, out _);
        Assert.True(buffer.Take(fast).Where(w => w.Band == 1).Sum(w => w.Weight) > 0.9f, "about the run");
        Assert.True(slowCadence < midCadence && midCadence < fastCadence, "the legs go faster as the body does");
    }

    /// <summary>The relaxed space with the walk-on-the-spot clip as the speed 0 band of every direction (animation pass 1): a clip whose planted feet do not move.</summary>
    static GaitSpace RelaxedSpaceWithSpot()
    {
        var directions = new List<(float, IReadOnlyList<ClipGait>)>();
        foreach (float angle in new[] { -180f, -135f, -90f, -45f, 0f, 45f, 90f, 135f })
        {
            var dir = new Vector2(MathF.Sin(angle * MathF.PI / 180f), MathF.Cos(angle * MathF.PI / 180f));
            var spot = new ClipGait(1.333f, 0.75f, 0.146f, new Vector2(0f, 0.01f), 0.47f);   // measured on SM_NAV_WOS_F_01: 0.01 m a cycle
            var walk = new ClipGait(1.333f, 0f, 0.667f, dir * 1.9f * 1.333f, 0.45f);
            var run = new ClipGait(0.867f, 0f, 0.433f, dir * 5f * 0.867f, 0.2f);
            directions.Add((angle, [spot, walk, run]));
        }
        return new GaitSpace(directions, cyclic: true);
    }

    [Fact]
    public void A_walk_on_the_spot_clip_is_the_zero_speed_band_and_the_blend_keeps_its_cadence_below_the_walk()
    {
        var space = RelaxedSpaceWithSpot();
        Assert.Equal(3, space.BandCount);
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        // Half the walk's speed: half the weight on the spot clip, half on the walk; the legs go at the walk's own pace, so the planted foot still cancels the velocity.
        var v = Velocity(0f, 0.95f);
        int n = space.Solve(v, buffer, out float cadence, out float ratio, out bool exact);
        Assert.InRange(n, 1, GaitSpace.MaxWeights);
        Assert.Equal(0.5f, buffer.Take(n).Where(w => w.Band == 0).Sum(w => w.Weight), 2);
        Assert.Equal(0.5f, buffer.Take(n).Where(w => w.Band == 1).Sum(w => w.Weight), 2);
        Assert.True(exact);
        Assert.Equal(1f, ratio, 2);
        Assert.Equal(1f / 1.333f, cadence, 2);
        Assert.Equal(v.Y, Produced(space, buffer, n, cadence).Y, 2);
        // At the walk's speed the spot clip has no weight left; just above a fraction of a metre a second it dominates.
        n = space.Solve(Velocity(0f, 1.9f), buffer, out _, out _, out _);
        Assert.True(buffer.Take(n).Where(w => w.Band == 0).Sum(w => w.Weight) < 0.02f);
        n = space.Solve(Velocity(0f, 0.2f), buffer, out _, out _, out _);
        Assert.True(buffer.Take(n).Where(w => w.Band == 0).Sum(w => w.Weight) > 0.85f);
        // The spot clip's own gait never counts as a stride, so a blend with a stationary body has nothing to solve.
        Assert.Equal(0, space.Solve(Vector2.Zero, buffer, out _, out _, out _));
    }

    [Fact]
    public void A_clip_that_steps_in_place_is_a_gait_only_for_the_clips_named_as_such()
    {
        var spot = new ClipGait(1.333f, 0.75f, 0.146f, new Vector2(0f, 0.01f), 0.47f);
        Assert.False(spot.Usable);
        Assert.True(spot.UsableInPlace);
        Assert.False(new ClipGait(1.333f, 0.75f, 0.146f, new Vector2(0f, 2.5f), 0.47f).UsableInPlace);   // a walk strides
        Assert.False(new ClipGait(1.333f, float.NaN, 0.146f, Vector2.Zero, 0.47f).UsableInPlace);
        var unnamed = GaitLibrary.Build(["wos"], _ => true, _ => spot);
        Assert.Empty(unnamed.Clips);
        Assert.Contains(unnamed.Notes, n => n.Contains("wos") && n.Contains("no usable"));
        var named = GaitLibrary.Build(["wos"], _ => true, _ => spot, inPlace: new HashSet<string> { "wos" });
        Assert.Same(spot, named.Get("wos"));
        // A stride-less clip named in place but with a real stride is not accepted in place, but it is a normal gait.
        var walk = new ClipGait(1.333f, 0f, 0.667f, new Vector2(0f, 2.5f), 0.45f);
        Assert.Same(walk, GaitLibrary.Build(["w"], _ => true, _ => walk, inPlace: new HashSet<string> { "w" }).Get("w"));
    }

    [Fact]
    public void The_seam_at_back_is_crossed_without_a_jump()
    {
        var space = RelaxedSpace();
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        // Sweep through 180 degrees: the weights must change a little at a time (no flip from the clip at -180 to the one at +135 beyond the angle step).
        float previousBack = -1f;
        for (float heading = 150f; heading <= 210f; heading += 1f)
        {
            int n = space.Solve(Velocity(heading, 4.5f), buffer, out _, out _, out _);
            float back = buffer.Take(n).Where(w => space.AngleOf(w.Direction) < -170f).Sum(w => w.Weight);
            if (previousBack >= 0f) Assert.True(MathF.Abs(back - previousBack) < 0.1f, $"heading {heading}: back weight {previousBack} to {back}");
            previousBack = back;
        }
    }

    [Fact]
    public void The_directions_are_placed_by_their_stride_not_their_label()
    {
        // The strafe-right clip actually travels at 80 degrees: a velocity at 80 degrees takes it alone.
        var directions = new List<(float, IReadOnlyList<ClipGait>)>();
        foreach (float angle in new[] { -90f, 0f, 90f })
        {
            float real = angle == 90f ? 80f : angle;
            var dir = Velocity(real, 1f);
            directions.Add((angle, [new ClipGait(0.9f, 0f, 0.45f, dir * 5f * 0.9f, 0.2f)]));
        }
        var space = new GaitSpace(directions, cyclic: false);
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        int n = space.Solve(Velocity(80f, 5f), buffer, out _, out _, out bool exact);
        Assert.True(exact);
        Assert.Equal(1, n);
        Assert.Equal(2, buffer[0].Direction);
    }

    [Fact]
    public void A_non_cyclic_space_stops_at_its_ends()
    {
        // The sprint: -45, 0, +45 only; a velocity at 80 degrees takes the +45 clip, at -170 the -45 clip.
        var directions = new List<(float, IReadOnlyList<ClipGait>)>();
        foreach (float angle in new[] { -45f, 0f, 45f })
            directions.Add((angle, [new ClipGait(0.633f, 0.3f, 0.6f, Velocity(angle, 10f) * 0.633f, 0.15f)]));
        var space = new GaitSpace(directions, cyclic: false);
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        int n = space.Solve(Velocity(80f, 10f), buffer, out float cadence, out _, out _);
        Assert.Equal(1, n);
        Assert.Equal(2, buffer[0].Direction);
        Assert.Equal(1f / 0.633f * 10f / 10f, cadence, 2);
        n = space.Solve(Velocity(-170f, 10f), buffer, out _, out _, out _);
        Assert.Equal(1, n);
        Assert.Equal(0, buffer[0].Direction);
    }

    [Fact]
    public void Too_slow_or_too_fast_a_body_gets_a_clamped_cadence_and_is_not_exact()
    {
        var space = RelaxedSpace();
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        int n = space.Solve(Velocity(0f, 12f), buffer, out _, out float ratio, out bool exact);
        Assert.Equal(1, n);
        Assert.Equal(GaitSpace.MaxRatio, ratio, 3);
        Assert.False(exact);
        space.Solve(Velocity(0f, 0.3f), buffer, out _, out ratio, out exact);
        Assert.Equal(GaitSpace.MinRatio, ratio, 3);
        Assert.False(exact);
    }

    [Fact]
    public void Standing_still_has_no_solution_and_bad_input_is_refused()
    {
        var space = RelaxedSpace();
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        Assert.Equal(0, space.Solve(Vector2.Zero, buffer, out float cadence, out _, out _));
        Assert.Equal(0f, cadence);
        Assert.Equal(0, space.Solve(new Vector2(float.NaN, 1f), buffer, out _, out _, out _));
        Assert.Equal(0, space.Solve(new Vector2(0f, 4f), new GaitWeight[2], out _, out _, out _));
    }

    [Fact]
    public void A_space_needs_directions_and_the_same_bands_in_each()
    {
        Assert.Throws<ArgumentException>(() => new GaitSpace([], true));
        var a = new ClipGait(1f, 0f, 0.5f, new Vector2(0, 4), 0.2f);
        Assert.Throws<ArgumentException>(() => new GaitSpace([(0f, [a, a]), (90f, [a])], true));
    }

    [Fact]
    public void Planted_feet_cancel_the_velocity_through_the_whole_pipeline()
    {
        // Measure two synthetic gaits from toe motion, build a space from them and check the planted feet of the blend against the wanted velocity.
        var forward = Measure(0.8f, new Vector2(0f, 5f))!;
        var diagonal = Measure(0.8f, new Vector2(3.5f, 3.5f), offset: 0.25f)!;
        var space = new GaitSpace([(0f, [forward]), (45f, [diagonal])], cyclic: false);
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        var v = Velocity(20f, 5.2f);
        int n = space.Solve(v, buffer, out float cadence, out _, out bool exact);
        Assert.True(exact);
        Assert.True((Produced(space, buffer, n, cadence) - v).Length() < 0.05f);
    }

    // ---- idle share and damping --------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_moving_weight_rises_linearly_with_the_speed()
    {
        Assert.Equal(0f, GaitBlend.MovingWeight(0f, 1f));
        Assert.Equal(0.25f, GaitBlend.MovingWeight(0.25f, 1f), 3);
        Assert.Equal(1f, GaitBlend.MovingWeight(3f, 1f));
        Assert.Equal(0f, GaitBlend.MovingWeight(float.NaN, 1f));
        Assert.Equal(1f, GaitBlend.MovingWeight(2f, 0f));
    }

    [Fact]
    public void The_damper_follows_a_step_without_overshoot_and_settles()
    {
        var damper = new VelocityDamper();
        Assert.Equal(new Vector2(0, 5), damper.Step(new Vector2(0, 5), 0.016f));      // the first value is taken as it is
        float last = 5f;
        for (int i = 0; i < 20; i++)
        {
            float y = damper.Step(new Vector2(0, 0), 1f / 60f).Y;
            Assert.True(y <= last && y >= 0f);
            last = y;
        }
        Assert.True(last < 0.1f, "settled after about a third of a second");
    }

    [Fact]
    public void A_reversal_goes_through_zero_without_a_jump()
    {
        var damper = new VelocityDamper();
        damper.Step(new Vector2(-6, 0), 0.016f);
        float previous = -6f;
        var worstStep = 0f;
        for (int i = 0; i < 30; i++)
        {
            float x = damper.Step(new Vector2(6, 0), 1f / 60f).X;
            worstStep = MathF.Max(worstStep, MathF.Abs(x - previous));
            Assert.True(x >= previous, "monotonic");
            previous = x;
        }
        Assert.True(worstStep < 3f, "no frame covers the whole reversal");
        Assert.True(previous > 4f);
    }

    [Fact]
    public void The_damper_snaps_after_a_gap_or_a_teleport_and_ignores_garbage()
    {
        var damper = new VelocityDamper();
        damper.Step(new Vector2(0, 5), 0.016f);
        Assert.Equal(new Vector2(0, 0), damper.Step(new Vector2(0, 0), 1f));                       // not shown for a second
        Assert.Equal(new Vector2(0, 5), damper.Step(new Vector2(0, 5), 1f));                       // and again
        Assert.Equal(new Vector2(0, 5), damper.Step(new Vector2(float.NaN, 0), 0.016f));            // unchanged
        Assert.Equal(new Vector2(40, 0), damper.Step(new Vector2(40, 0), 0.016f));                  // a jump beyond SnapSpeed
        damper.Reset();
        Assert.Equal(new Vector2(1, 1), damper.Step(new Vector2(1, 1), 0.016f));
    }

    // ---- review additions (M40-T4) ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_damper_is_frame_rate_independent_for_a_constant_target()
    {
        var one = new VelocityDamper(); var two = new VelocityDamper();
        one.Step(new Vector2(0, 0), 0.016f); two.Step(new Vector2(0, 0), 0.016f);
        var whole = one.Step(new Vector2(3, 6), 1f / 30f);
        two.Step(new Vector2(3, 6), 1f / 60f);
        var halves = two.Step(new Vector2(3, 6), 1f / 60f);
        Assert.Equal(whole.X, halves.X, 4);
        Assert.Equal(whole.Y, halves.Y, 4);
    }

    /// <summary>A four-direction cyclic space like the heavy bolter's ready run (back, left, forward, right; walk and run bands).</summary>
    static GaitSpace HeavySpace()
    {
        var directions = new List<(float, IReadOnlyList<ClipGait>)>();
        foreach (var (angle, runSpeed) in new[] { (-180f, 4.6f), (-90f, 5.9f), (0f, 6.5f), (90f, 5.9f) })
        {
            var walk = new ClipGait(1.333f, 0f, 0.667f, Velocity(angle, 1.95f) * 1.333f, 0.45f);
            var run = new ClipGait(0.867f, 0f, 0.433f, Velocity(angle, runSpeed) * 0.867f, 0.2f);
            directions.Add((angle, [walk, run]));
        }
        return new GaitSpace(directions, cyclic: true);
    }

    [Fact]
    public void A_four_direction_space_blends_across_ninety_degrees_and_round_the_back()
    {
        var space = HeavySpace();
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        for (float heading = -180f; heading < 180f; heading += 5f)
        {
            var v = Velocity(heading, 5f);
            int n = space.Solve(v, buffer, out float cadence, out _, out bool exact);
            Assert.InRange(n, 1, GaitSpace.MaxWeights);
            Assert.Equal(1f, buffer.Take(n).Sum(w => w.Weight), 3);
            Assert.True(float.IsFinite(cadence) && cadence > 0f);
            if (exact) Assert.True((Produced(space, buffer, n, cadence) - v).Length() < 0.1f, $"heading {heading}");
        }
        // 45 degrees: forward and right, about half each; 150: back and right.
        space.Solve(Velocity(45f, 5f), buffer, out _, out _, out _);
        var weights = buffer.Take(2).ToDictionary(w => space.AngleOf(w.Direction), w => w.Weight);
        Assert.Contains(0f, weights.Keys); Assert.Contains(90f, weights.Keys);
        Assert.InRange(weights[0f], 0.3f, 0.7f);
        int back = space.Solve(Velocity(150f, 4.5f), buffer, out _, out _, out _);
        Assert.Contains(buffer.Take(back), w => space.AngleOf(w.Direction) == 90f);
        Assert.Contains(buffer.Take(back), w => Math.Abs(space.AngleOf(w.Direction)) > 170f);
    }

    [Fact]
    public void A_single_band_cyclic_space_works_like_the_sprint_all_round()
    {
        var directions = new List<(float, IReadOnlyList<ClipGait>)>();
        foreach (float angle in new[] { -90f, 0f, 90f, 180f })
            directions.Add((angle, [new ClipGait(0.7f, 0.3f, 0.65f, Velocity(angle, 5f) * 0.7f, 0.2f)]));
        var space = new GaitSpace(directions, cyclic: true);
        Assert.Equal(1, space.BandCount);
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        int n = space.Solve(Velocity(45f, 5f), buffer, out float cadence, out _, out bool exact);
        Assert.True(exact);
        Assert.Equal(2, n);
        Assert.All(buffer.Take(n), w => Assert.Equal(0, w.Band));
        Assert.True((Produced(space, buffer, n, cadence) - Velocity(45f, 5f)).Length() < 0.05f);
    }

    [Fact]
    public void Opposite_strides_never_give_a_nan_or_a_weight_outside_zero_to_one()
    {
        // Two directions with exactly opposite strides, and velocities at right angles to both: the cross-product solve has nothing to divide by.
        var forward = new ClipGait(0.9f, 0f, 0.45f, new Vector2(0, 4.5f), 0.2f);
        var back = new ClipGait(0.9f, 0f, 0.45f, new Vector2(0, -4.5f), 0.2f);
        var space = new GaitSpace([(0f, [forward]), (180f, [back])], cyclic: true);
        var buffer = new GaitWeight[GaitSpace.MaxWeights];
        foreach (float heading in new[] { 0f, 10f, 89f, 90f, 91f, 170f, 180f, 270f, -90f })
        {
            int n = space.Solve(Velocity(heading, 4f), buffer, out float cadence, out float ratio, out _);
            Assert.True(float.IsFinite(cadence) && float.IsFinite(ratio), $"heading {heading}");
            for (int i = 0; i < n; i++) Assert.InRange(buffer[i].Weight, 0f, 1.0001f);
            if (n > 0) Assert.Equal(1f, buffer.Take(n).Sum(w => w.Weight), 3);
        }
    }

    // ---- GaitLibrary: the gait part is optional ------------------------------------------------------------------------------------------

    [Fact]
    public void One_clip_that_throws_costs_only_that_clip()
    {
        var good = new ClipGait(0.9f, 0.1f, 0.55f, new Vector2(0, 5f), 0.2f);
        var library = GaitLibrary.Build(["a", "bad", "c", "missing", "flat"], name => name != "missing", name => name switch
        {
            "bad" => throw new InvalidOperationException("no decode"),
            "flat" => new ClipGait(0.9f, 0f, 0.45f, Vector2.Zero, 0.2f),
            _ => good,
        });
        Assert.Equal(["a", "c"], library.Clips.Keys.OrderBy(k => k).ToArray());
        Assert.Contains(library.Notes, n => n.Contains("bad") && n.Contains("no decode"));
        Assert.Contains(library.Notes, n => n.Contains("missing") && n.Contains("not in the rig"));
        Assert.Contains(library.Notes, n => n.Contains("flat") && n.Contains("no usable"));
    }

    [Fact]
    public void A_failing_gait_build_gives_an_empty_library_with_a_note_not_an_exception()
    {
        var library = GaitLibrary.Guard(() => throw new System.IO.InvalidDataException("graph unreadable"));
        Assert.Empty(library.Clips);
        Assert.Empty(library.Animations);
        Assert.Single(library.Notes);
        Assert.Contains("graph unreadable", library.Notes[0]);
        // Out of memory is not hidden.
        Assert.Throws<OutOfMemoryException>(() => GaitLibrary.Guard(() => throw new OutOfMemoryException()));
        // A build that works is passed through.
        var fine = GaitLibrary.Build(["a"], _ => true, _ => new ClipGait(0.9f, 0.1f, 0.55f, new Vector2(0, 5f), 0.2f));
        Assert.Same(fine, GaitLibrary.Guard(() => fine));
    }

    [Fact]
    public void The_library_belongs_to_the_marine_rig()
    {
        // Preloaded clips are taken only by a rig with this key (another faction's rig has other animations under the same names).
        Assert.Equal(CharacterModel.MarineRigEntry, GaitLibrary.Build([], _ => true, _ => null).RigKey);
    }
}
