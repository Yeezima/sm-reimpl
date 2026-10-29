using SmNet;

namespace SmNet.Tests;

/// <summary>
/// M40-T5 review: the engine-free decisions of the foot pins and the run start (<see cref="FootPinRules"/>, <see cref="RunStartRules"/>): reach and teleport guards,
/// the spine twist past the aim-offset idle's range, and the start's arm / hold / reset / play verdicts. Synthetic numbers.
/// </summary>
public class FootPinRulesTests
{
    const float Deg = MathF.PI / 180f;
    const float RunThreshold = 1f, SprintAbove = 7.375f, EnterSpeed = 3f;

    [Theory]
    [InlineData(0.80f, 0.90f, false)]   // inside the leg
    [InlineData(0.90f, 0.90f, false)]   // at full stretch
    [InlineData(0.92f, 0.90f, false)]   // within the margin
    [InlineData(0.94f, 0.90f, true)]    // beyond it
    [InlineData(3.0f, 0.90f, true)]     // a teleport
    [InlineData(float.NaN, 0.90f, true)]
    public void ReachGuard(float hipToAnkle, float legLength, bool expected) =>
        Assert.Equal(expected, FootPinRules.OutOfReach(hipToAnkle, legLength));

    [Theory]
    [InlineData(0.1f, 0f, 0f, false)]
    [InlineData(0.9f, 0f, 0f, false)]
    [InlineData(0.8f, 0.8f, 0f, true)]    // 1.13 m
    [InlineData(0f, 0f, 5f, true)]
    [InlineData(float.NaN, 0f, 0f, true)]
    public void TeleportGuard(float dx, float dy, float dz, bool expected) =>
        Assert.Equal(expected, FootPinRules.Teleported(dx, dy, dz));

    [Fact]
    public void SpineTwistWithinTheGridRangeAddsNothingForClipFamilies()
    {
        Assert.Equal(0f, FootPinRules.SpineTwist(60f * Deg, false, 90f * Deg, 135f * Deg));
        Assert.Equal(0f, FootPinRules.SpineTwist(-90f * Deg, false, 90f * Deg, 135f * Deg));
    }

    [Fact]
    public void SpineTwistContinuesPastTheRangeToTheCap()
    {
        Assert.Equal(20f * Deg, FootPinRules.SpineTwist(110f * Deg, false, 90f * Deg, 135f * Deg), 4);
        Assert.Equal(-45f * Deg, FootPinRules.SpineTwist(-135f * Deg, false, 90f * Deg, 135f * Deg), 4);
        Assert.Equal(45f * Deg, FootPinRules.SpineTwist(170f * Deg, false, 90f * Deg, 135f * Deg), 4);   // the cap
    }

    [Fact]
    public void ProceduralTwistFamiliesTwistTheWholeAngleUpToTheCap()
    {
        Assert.Equal(60f * Deg, FootPinRules.SpineTwist(60f * Deg, true, 90f * Deg, 135f * Deg), 4);
        Assert.Equal(-135f * Deg, FootPinRules.SpineTwist(-170f * Deg, true, 90f * Deg, 135f * Deg), 4);
    }

    [Fact]
    public void SpineTwistIgnoresNonFiniteInput() => Assert.Equal(0f, FootPinRules.SpineTwist(float.NaN, true, 90f * Deg, 135f * Deg));

    [Fact]
    public void StartArmsOnlyWhenTheMarineLeavesStandingFast()
    {
        Assert.True(RunStartRules.ShouldArm(true, false, 3f, RunThreshold));
        Assert.False(RunStartRules.ShouldArm(false, false, 3f, RunThreshold));   // already moving
        Assert.False(RunStartRules.ShouldArm(true, true, 3f, RunThreshold));     // still standing
        Assert.False(RunStartRules.ShouldArm(true, false, 0.5f, RunThreshold));  // a nudge
    }

    [Fact]
    public void ArmedStartHoldsWhileARunAccelerates() =>
        Assert.Equal(RunStartVerdict.Hold, RunStartRules.Armed(2f, 0.05, 40f, false, SprintAbove, 0f, EnterSpeed, RunThreshold));

    [Fact]
    public void ArmedStartPlaysAtTheClipsSpeed() =>
        Assert.Equal(RunStartVerdict.Play, RunStartRules.Armed(6.5f, 0.15, 43f, false, SprintAbove, 10f, EnterSpeed, RunThreshold));

    [Fact]
    public void ArmedStartDropsAWalkThatLevelsOff() =>
        Assert.Equal(RunStartVerdict.Reset, RunStartRules.Armed(2.6f, 0.2, 5f, false, SprintAbove, 0f, EnterSpeed, RunThreshold));

    [Fact]
    public void ArmedStartGivesAWalkNoVerdictBeforeTheMinimumWait() =>
        Assert.Equal(RunStartVerdict.Hold, RunStartRules.Armed(2.6f, 0.03, 5f, false, SprintAbove, 0f, EnterSpeed, RunThreshold));

    [Theory]
    [InlineData(6.5f, 0.35, false, 0f)]    // waited too long
    [InlineData(0.5f, 0.1, false, 0f)]     // stopped
    [InlineData(6.5f, 0.1, true, 0f)]      // weapon went up or down
    [InlineData(9f, 0.1, false, 0f)]       // sprint
    [InlineData(6.5f, 0.1, false, 60f)]    // strafing
    public void ArmedStartResets(float speed, double elapsed, bool postureChanged, float heading) =>
        Assert.Equal(RunStartVerdict.Reset, RunStartRules.Armed(speed, elapsed, 40f, postureChanged, SprintAbove, heading, EnterSpeed, RunThreshold));

    [Fact]
    public void PlayingStartAbortsOnStopSprintTurnOrPostureChange()
    {
        Assert.False(RunStartRules.Aborted(6.5f, 20f, SprintAbove, false, RunThreshold));
        Assert.False(RunStartRules.Aborted(6.5f, 80f, SprintAbove, false, RunThreshold));   // up to twice the cone
        Assert.True(RunStartRules.Aborted(6.5f, 100f, SprintAbove, false, RunThreshold));
        Assert.True(RunStartRules.Aborted(0.4f, 0f, SprintAbove, false, RunThreshold));
        Assert.True(RunStartRules.Aborted(9f, 0f, SprintAbove, false, RunThreshold));
        Assert.True(RunStartRules.Aborted(6.5f, 0f, SprintAbove, true, RunThreshold));
    }
}
