using SmNet;

namespace SmNet.Tests;

/// <summary>
/// M40-T5: the rules of the standing body heading (<see cref="BodyYaw"/>): planted until the aim is more than the threshold off, then the clip's root rotation
/// turns it, never more than the twist cap, at most the follow rate when it follows. Synthetic numbers (the rifle's turn: 135 degrees over 0.833 s, linear, ending at
/// 0.92 of the clip), not the install's.
/// </summary>
public class TurnInPlaceTests
{
    const float Deg = MathF.PI / 180f;
    const float Enter = 90f * Deg, MaxTwist = 135f * Deg, FollowRate = 720f * Deg, Blend = 0.1f, Dt = 1f / 60f;

    static TurnProfile Clip(float sign, float endFraction = 0.92f, float total = 0.833f)
    {
        var samples = new float[26];
        for (int i = 0; i < samples.Length; i++) samples[i] = sign * 135f * Deg * i / (samples.Length - 1);
        return new TurnProfile(endFraction * total, total, samples, total);
    }

    static readonly TurnProfile Left = Clip(1f), Right = Clip(-1f);

    static BodyYaw Standing(float yaw = 0f) { var b = new BodyYaw(); b.Snap(yaw); return b; }

    static void Step(BodyYaw b, float aim, bool canTwist = true, float dt = Dt) => b.Step(aim, dt, canTwist, Enter, Left, Right, Blend, MaxTwist, FollowRate);

    static float Degrees(float radians) => radians / Deg;

    [Fact]
    public void A_standing_marine_keeps_its_body_while_the_aim_stays_within_the_threshold()
    {
        var b = Standing();
        foreach (float aim in new[] { 30f, 60f, 89f, -89f, -30f, 0f })
            for (int i = 0; i < 30; i++) { Step(b, aim * Deg); Assert.Equal(0f, b.Yaw, 1e-6f); Assert.Equal(TurnSide.None, b.Side); }
        Assert.Equal(0, b.TurnsStarted);
        Assert.Equal(0f, b.LayerWeight);
    }

    [Fact]
    public void An_aim_beyond_the_threshold_to_the_left_starts_a_left_turn_that_follows_the_clips_root_motion_and_ends_at_the_branch()
    {
        var b = Standing();
        float aim = 100f * Deg;
        Step(b, aim);
        Assert.Equal(TurnSide.Left, b.Side);
        Assert.Equal(1, b.TurnsStarted);
        float last = b.Yaw, largest = 0f;
        int frames = 1;
        while (b.Side != TurnSide.None && frames < 200)
        {
            Step(b, aim); frames++;
            largest = MathF.Max(largest, MathF.Abs(b.Yaw - last)); last = b.Yaw;
        }
        // 0.92 of 0.833 s at 60 fps: 46 frames; the body turned the clip's root rotation at the end of the turn (124.2 degrees), left (positive).
        Assert.InRange(frames, 45, 48);
        Assert.Equal(Left.TurnRadians, b.Yaw, 0.05f);
        Assert.InRange(Degrees(b.Yaw), 123f, 126f);
        Assert.InRange(Degrees(largest), 2f, 3.2f);   // 135 degrees per 0.833 s = 2.7 degrees a frame: far under the audit's 15
        // The aim was 100 degrees; the body overshot to 124: the twist is -24 degrees, inside the threshold, so no second turn.
        Assert.Equal(1, b.TurnsStarted);
        Assert.InRange(Degrees(b.Twist(aim)), -26f, -22f);
    }

    [Fact]
    public void A_right_turn_is_the_mirror()
    {
        var b = Standing();
        float aim = -100f * Deg;
        for (int i = 0; i < 80; i++) Step(b, aim);
        Assert.Equal(TurnSide.None, b.Side);
        Assert.InRange(Degrees(b.Yaw), -126f, -123f);
        Assert.Equal(1, b.TurnsStarted);
    }

    [Fact]
    public void An_aim_still_beyond_the_threshold_when_a_turn_ends_starts_the_next_turn_at_once()
    {
        var b = Standing();
        // The view keeps turning left at 200 degrees a second, faster than the turn clip catches up: the twist is beyond the threshold again when the turn ends.
        float aim = 100f * Deg, last = 0f, largest = 0f;
        int restartsSeenAtOnce = 0, previousStarted = 0;
        for (int i = 0; i < 240; i++)
        {
            aim = ViewSmoothing.WrapPi(aim + 200f * Deg * Dt);
            bool wasTurning = b.Side != TurnSide.None;
            Step(b, aim);
            largest = MathF.Max(largest, MathF.Abs(ViewSmoothing.WrapPi(b.Yaw - last))); last = b.Yaw;
            if (b.TurnsStarted > previousStarted && wasTurning && b.Side != TurnSide.None) restartsSeenAtOnce++;
            previousStarted = b.TurnsStarted;
        }
        Assert.True(b.TurnsStarted >= 3, $"turns started {b.TurnsStarted}");
        Assert.True(restartsSeenAtOnce >= 1, "the next turn did not follow the end of the last at once");
        Assert.True(Degrees(largest) <= 12.01f);
        Assert.True(MathF.Abs(b.Twist(aim)) <= MaxTwist + 1e-3f);
    }

    [Fact]
    public void A_view_that_outruns_the_turn_drags_the_body_so_the_twist_never_exceeds_the_cap_and_no_frame_turns_more_than_the_follow_rate()
    {
        var b = Standing();
        float aim = 0f, last = 0f, largest = 0f, worstTwist = 0f;
        // A mouse flick: 720 degrees per second for half a second (360 degrees), then still.
        for (int i = 0; i < 120; i++)
        {
            if (i < 30) aim = ViewSmoothing.WrapPi(aim + 720f * Deg * Dt);
            Step(b, aim);
            largest = MathF.Max(largest, MathF.Abs(ViewSmoothing.WrapPi(b.Yaw - last))); last = b.Yaw;
            worstTwist = MathF.Max(worstTwist, MathF.Abs(b.Twist(aim)));
        }
        Assert.True(Degrees(largest) <= 12.01f, $"body turned {Degrees(largest)} degrees in a frame");
        Assert.True(Degrees(worstTwist) <= 135.5f, $"twist {Degrees(worstTwist)}");
    }

    [Fact]
    public void An_instant_jump_of_the_view_by_180_degrees_turns_the_body_over_several_frames_not_in_one()
    {
        var b = Standing();
        float aim = MathF.PI * 0.999f, last = 0f, largest = 0f;
        for (int i = 0; i < 120; i++)
        {
            Step(b, aim);
            largest = MathF.Max(largest, MathF.Abs(ViewSmoothing.WrapPi(b.Yaw - last))); last = b.Yaw;
        }
        Assert.True(Degrees(largest) <= 12.01f, $"largest step {Degrees(largest)}");
        Assert.True(MathF.Abs(b.Twist(aim)) <= Enter + 1e-3f, "the turn left the aim inside the threshold");
    }

    [Fact]
    public void A_marine_that_cannot_twist_follows_the_aim_at_the_follow_rate_and_drops_its_turn()
    {
        var b = Standing();
        float aim = 100f * Deg;
        for (int i = 0; i < 10; i++) Step(b, aim);
        Assert.Equal(TurnSide.Left, b.Side);
        // It starts to move: the body goes to the aim at 720 degrees a second at most.
        float last = b.Yaw;
        for (int i = 0; i < 20; i++)
        {
            Step(b, aim, canTwist: false);
            Assert.True(MathF.Abs(b.Yaw - last) <= FollowRate * Dt + 1e-5f);
            last = b.Yaw;
        }
        Assert.Equal(TurnSide.None, b.Side);
        Assert.Equal(aim, b.Yaw, 1e-4f);
        // The turn layer fades out over the blend (0.1 s = 6 frames), not at once.
        for (int i = 0; i < 12; i++) Step(b, aim, canTwist: false);
        Assert.Equal(0f, b.LayerWeight);
        Assert.Equal(TurnSide.None, b.LayerSide);
    }

    [Fact]
    public void The_turn_layer_fades_in_over_the_blend_and_out_after_the_turn()
    {
        var b = Standing();
        float aim = 100f * Deg;
        Step(b, aim);
        Assert.InRange(b.LayerWeight, 0.1f, 0.2f);
        for (int i = 0; i < 8; i++) Step(b, aim);
        Assert.Equal(1f, b.LayerWeight);
        while (b.Side != TurnSide.None) Step(b, aim);
        Assert.Equal(TurnSide.Left, b.LayerSide);   // still showing while it fades
        Assert.True(b.LayerWeight > 0.5f);
        for (int i = 0; i < 8; i++) Step(b, aim);
        Assert.Equal(0f, b.LayerWeight);
        Assert.Equal(TurnSide.None, b.LayerSide);
    }

    [Fact]
    public void The_twist_takes_the_short_way_round_the_wrap()
    {
        var b = Standing(175f * Deg);
        for (int i = 0; i < 30; i++) Step(b, -175f * Deg);   // 10 degrees apart across the wrap, not 350
        Assert.Equal(0, b.TurnsStarted);
        Assert.Equal(175f * Deg, b.Yaw, 1e-5f);
        Assert.InRange(Degrees(b.Twist(-175f * Deg)), 9.9f, 10.1f);
    }

    [Fact]
    public void The_first_step_a_long_frame_and_non_finite_aims_do_not_turn_the_body_through_them()
    {
        var b = new BodyYaw();
        Assert.False(b.Valid);
        Step(b, 2f);   // the first step takes the aim
        Assert.True(b.Valid);
        Assert.Equal(2f, b.Yaw, 1e-6f);
        Step(b, -1f, dt: 2f);   // a stall of two seconds: snaps, no turn
        Assert.Equal(-1f, b.Yaw, 1e-6f);
        Assert.Equal(TurnSide.None, b.Side);
        Step(b, float.NaN);
        Assert.Equal(-1f, b.Yaw, 1e-6f);
        Step(b, 0.5f, dt: 0f);
        Assert.Equal(-1f, b.Yaw, 1e-6f);
    }

    [Fact]
    public void Without_a_turn_clip_the_body_follows()
    {
        var b = Standing();
        for (int i = 0; i < 40; i++) b.Step(100f * Deg, Dt, true, Enter, null, null, Blend, MaxTwist, FollowRate);
        Assert.Equal(100f * Deg, b.Yaw, 1e-4f);
        Assert.Equal(0, b.TurnsStarted);
    }

    [Fact]
    public void A_profile_holds_its_last_rotation_and_interpolates_between_samples()
    {
        Assert.Equal(0f, Left.YawAt(-1f));
        Assert.Equal(135f * Deg, Left.YawAt(5f), 1e-5f);
        Assert.Equal(67.5f * Deg, Left.YawAt(0.4165f), 2e-3f);
        var made = BodyYaw.Profile([0f, 45f, 90f], 1f, 1f, 0.5f);
        Assert.Equal(0.5f, made.EndSeconds);
        Assert.Equal(45f * Deg, made.TurnRadians, 1e-5f);
    }
}

/// <summary>M40-T5: the aim-offset idle's grid weights (<see cref="AimOffsetGrid"/>), synthetic angles in the shape of the rifle's graph.</summary>
public class AimOffsetTests
{
    static AimOffsetGrid Rifle() => new([-90f, 0f, 90f], [[-80f, 0f, 40f, 80f], [-80f, 0f, 80f], [-80f, 0f, 40f, 80f]]);

    static float[] W(AimOffsetGrid g, float h, float p) { var w = new float[g.Count]; g.Weights(h, p, w); return w; }

    [Fact]
    public void The_grid_has_a_clip_per_column_and_pitch_key_laid_out_column_by_column()
    {
        var g = Rifle();
        Assert.Equal(11, g.Count);
        Assert.Equal([0, 4, 7], [g.ColumnStart(0), g.ColumnStart(1), g.ColumnStart(2)]);
    }

    [Theory]
    [InlineData(0f, 0f, 5)]       // straight ahead, level: the ahead column's level clip
    [InlineData(0f, -80f, 4)]
    [InlineData(0f, 80f, 6)]
    [InlineData(-90f, 0f, 1)]     // full left, level: the left column's mid clip
    [InlineData(90f, 80f, 10)]
    [InlineData(-120f, -80f, 0)]  // beyond the grid reads as its edge
    public void A_grid_point_gives_its_clip_the_whole_weight(float horizontal, float pitch, int clip)
    {
        var w = W(Rifle(), horizontal, pitch);
        Assert.Equal(1f, w[clip], 1e-5f);
        Assert.Equal(1f, w.Sum(), 1e-5f);
    }

    [Fact]
    public void Between_columns_and_pitch_keys_the_four_neighbours_share_by_the_angles()
    {
        var w = W(Rifle(), 45f, 20f);
        // Half way to the right column; between the level and the +40 clip of the right column, and the level and +80 clip of the ahead column.
        Assert.Equal(1f, w.Sum(), 1e-5f);
        Assert.Equal(0.5f * 0.5f, w[8], 1e-5f);                       // right column (starts at 7): level clip
        Assert.Equal(0.5f * 0.5f, w[9], 1e-5f);                       // right column: +40 clip
        Assert.Equal(0.5f * (1f - 20f / 80f), w[5], 1e-5f);           // ahead column: level
        Assert.Equal(0.5f * (20f / 80f), w[6], 1e-5f);                // ahead column: +80
        Assert.Equal(4, w.Count(x => x > 0f));
    }

    [Fact]
    public void Weights_are_continuous_across_a_sweep_and_always_sum_to_one()
    {
        var g = Rifle();
        var previous = W(g, -100f, -90f);
        for (float h = -100f; h <= 100f; h += 0.5f)
            for (float p = -90f; p <= 90f; p += 5f)
            {
                var w = W(g, h, p);
                Assert.Equal(1f, w.Sum(), 1e-4f);
                Assert.All(w, x => Assert.InRange(x, 0f, 1f));
            }
        // A step of half a degree moves any weight by little.
        for (float p = -80f; p <= 80f; p += 20f)
        {
            previous = W(g, -90f, p);
            for (float h = -89.5f; h <= 90f; h += 0.5f)
            {
                var w = W(g, h, p);
                for (int i = 0; i < w.Length; i++) Assert.True(MathF.Abs(w[i] - previous[i]) < 0.02f);
                previous = w;
            }
        }
    }

    [Fact]
    public void A_one_column_grid_blends_the_pitch_only_and_non_finite_angles_read_as_zero()
    {
        var g = new AimOffsetGrid([0f], [[-80f, 0f, 80f]]);
        Assert.Equal(1f, W(g, 50f, 0f)[1], 1e-5f);
        Assert.Equal(0.25f, W(g, 0f, 20f)[2], 1e-5f);
        Assert.Equal(1f, W(g, float.NaN, float.NaN)[1], 1e-5f);
        Assert.Throws<ArgumentException>(() => new AimOffsetGrid([0f], []));
    }
}
