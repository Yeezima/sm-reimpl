using SmNet;

namespace SmNet.Tests;

/// <summary>
/// Install transitions batch 3: the braced heavy bolter's turn rules in <see cref="BodyYaw"/> with a threshold per side (the brace graph: a left turn when the aim is 60 degrees or more
/// left of the body, a right turn at 77 or more right; <c>SM_RNG_TB_Brace_TurnLeft</c> / <c>_Right</c>, 1.467 s, the root turning 112.6 degrees, ended at <c>BRANCH_TurnComplete</c>
/// 0.9773, where the same tests pick the next turn or the idle). Synthetic numbers shaped like the install's.
/// </summary>
public class BraceTurnTests
{
    const float Deg = MathF.PI / 180f;
    const float EnterLeft = 60f * Deg, EnterRight = 77f * Deg, MaxTwist = 135f * Deg, FollowRate = 720f * Deg, Blend = 0.1f, Dt = 1f / 60f;
    const float Length = 1.467f, EndFraction = 0.9773f;

    static TurnProfile Clip(float sign)
    {
        var samples = new float[45];
        for (int i = 0; i < samples.Length; i++) samples[i] = sign * 112.6f * Deg * i / (samples.Length - 1);
        return new TurnProfile(EndFraction * Length, Length, samples, Length);
    }

    static readonly TurnProfile Left = Clip(1f), Right = Clip(-1f);

    static BodyYaw Standing() { var b = new BodyYaw(); b.Snap(0f); return b; }

    static void Step(BodyYaw b, float aim) => b.Step(aim, Dt, true, EnterLeft, EnterRight, Left, Right, Blend, MaxTwist, FollowRate);

    [Theory]
    [InlineData(59f, null)]
    [InlineData(61f, TurnSide.Left)]
    [InlineData(-61f, null)]      // 61 degrees to the right is inside the right side's 77
    [InlineData(-76f, null)]
    [InlineData(-78f, TurnSide.Right)]
    [InlineData(179f, TurnSide.Left)]
    [InlineData(-179f, TurnSide.Right)]
    public void Each_side_has_its_own_threshold(float twistDegrees, TurnSide? expected) =>
        Assert.Equal(expected, BodyYaw.Beyond(twistDegrees * Deg, EnterLeft, EnterRight));

    [Fact]
    public void An_aim_held_between_the_two_thresholds_keeps_the_body_planted()
    {
        var b = Standing();
        foreach (float aim in new[] { 55f, -70f, 30f, -76f })
            for (int i = 0; i < 60; i++) { Step(b, aim * Deg); Assert.Equal(0f, b.Yaw, 1e-6f); }
        Assert.Equal(0, b.TurnsStarted);
    }

    [Fact]
    public void A_left_turn_follows_the_clip_and_ends_at_the_branch_with_110_degrees()
    {
        var b = Standing();
        float aim = 100f * Deg;
        Step(b, aim);
        Assert.Equal(TurnSide.Left, b.Side);
        int frames = 1;
        while (b.Side != TurnSide.None && frames < 300) { Step(b, aim); frames++; }
        // 0.9773 x 1.467 s = 1.434 s: 86 frames at 60 fps; the body turned the clip's root rotation at the branch (110 degrees) and the aim (100) is now 10 degrees right: idle.
        Assert.InRange(frames, 85, 88);
        Assert.InRange(b.Yaw / Deg, 109f, 111f);
        Assert.Equal(1, b.TurnsStarted);
    }

    [Fact]
    public void A_right_turn_starts_only_past_77_degrees()
    {
        var b = Standing();
        for (int i = 0; i < 30; i++) Step(b, -70f * Deg);
        Assert.Equal(0, b.TurnsStarted);
        Step(b, -80f * Deg);
        Assert.Equal(TurnSide.Right, b.Side);
        Assert.True(b.Yaw < 0f);
    }

    [Fact]
    public void An_aim_still_out_at_the_branch_repeats_the_turn()
    {
        var b = Standing();
        // The aim kept 70 degrees left of the turning body (past the left side's 60, under the twist cap): at the branch the turn plays again.
        float aim = 0f;
        int frames = 0;
        while (b.TurnsStarted < 2 && frames < 400) { aim = b.Yaw + 70f * Deg; Step(b, aim); frames++; }
        Assert.Equal(2, b.TurnsStarted);
        Assert.InRange(frames, 85, 89);   // the second turn starts in the frame the first reaches its branch
        Assert.Equal(TurnSide.Left, b.Side);
        // Then the aim holds: the second turn's 110 degrees overshoot it by about 40, inside the right side's 77: the idle.
        while (b.Side != TurnSide.None && frames < 400) { Step(b, aim); frames++; }
        Assert.Equal(2, b.TurnsStarted);
        float twist = b.Twist(aim) / Deg;
        Assert.InRange(twist, -77f, 60f);   // settled inside both thresholds
    }

    [Fact]
    public void A_turn_that_ends_with_the_aim_past_the_other_side_turns_back()
    {
        var b = Standing();
        float aim = 70f * Deg;
        Step(b, aim);   // a left turn
        Assert.Equal(TurnSide.Left, b.Side);
        // The aim swings back to the start while the turn runs: at the branch it is 110 degrees right of the turned body, past the right side's 77.
        aim = 0f;
        for (int i = 0; i < 90 && b.TurnsStarted < 2; i++) Step(b, aim);
        Assert.Equal(2, b.TurnsStarted);
        Assert.Equal(TurnSide.Right, b.Side);
    }

    [Fact]
    public void DropTurn_ends_a_turn_where_the_body_is()
    {
        var b = Standing();
        Step(b, 100f * Deg);
        for (int i = 0; i < 20; i++) Step(b, 100f * Deg);
        float yaw = b.Yaw;
        b.DropTurn();
        Assert.Equal(TurnSide.None, b.Side);
        Assert.Equal(yaw, b.Yaw);
    }

    [Theory]
    [InlineData(50f, 0f)]       // inside the grid's 60 to the left: the clips make it
    [InlineData(70f, 10f)]      // 10 past the left column
    [InlineData(-70f, 0f)]      // the right column reaches 77
    [InlineData(-90f, -13f)]
    public void The_spine_twists_only_past_the_brace_grids_reach(float twistDegrees, float expected) =>
        Assert.Equal(expected, FootPinRules.SpineTwist(twistDegrees * Deg, false, 60f * Deg, 77f * Deg, MaxTwist) / Deg, 0.01f);
}
