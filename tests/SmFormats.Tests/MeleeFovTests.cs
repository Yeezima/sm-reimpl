using SmClient.Customizer.Model;
using SmSim;
using Xunit;

namespace SmFormats.Tests;

public class MeleeFovTests
{
    const float Wide = 16f / 9f;
    const float Dt = 1f / 60f;

    static MeleeFov.Inputs In(bool melee = false, bool fire = false, bool aim = false, bool throwing = false, bool alive = true, bool excluded = false) =>
        new(alive, melee, fire, aim, throwing, excluded);

    static void Run(MeleeFov f, MeleeFov.Inputs i, float seconds) { for (float t = 0; t < seconds; t += Dt) f.Step(i, Dt); }

    static MeleeFov Widened()
    {
        var f = new MeleeFov();
        f.Step(In(), Dt);
        Run(f, In(melee: true), 0.5f);
        Run(f, In(), 0.5f);
        Assert.True(f.Wide);
        return f;
    }

    [Theory]
    [InlineData(66f, 76f)]
    [InlineData(100f, 110f)]
    [InlineData(110f, 120f)]
    [InlineData(115f, 120f)]
    [InlineData(120f, 120f)]
    public void TargetIsTenMoreCappedAtOneTwenty(float configured, float expected) => Assert.Equal(expected, MeleeFov.WideTarget(configured, Wide), 3);

    [Fact]
    public void UnsetConfiguredWidensFromTheDataLook() => Assert.Equal(FovMath.MinimumHorizontal(Wide) + 10f, MeleeFov.WideTarget(0f, Wide), 3);

    [Fact]
    public void AtOneTwentyTheScaleDoesNotChange()
    {
        var f = Widened();
        Assert.Equal(FovMath.Scale(120f, Wide), f.Scale(120f, Wide), 4);
    }

    [Fact]
    public void FullyWideShowsTheTarget() => Assert.Equal(MeleeFov.WideTarget(90f, Wide), Widened().Horizontal(90f, Wide), 2);

    [Fact]
    public void MeleeWidensAndStaysWideIndefinitely()
    {
        var f = Widened();
        Run(f, In(), 60f);
        Assert.True(f.Wide);
        Assert.Equal(1f, f.Progress);
    }

    [Fact]
    public void BlendTakesPointThreeSecondsEitherWay()
    {
        var f = new MeleeFov();
        f.Step(In(), Dt);
        f.Step(In(melee: true), 0.15f);
        Assert.Equal(0.5f, f.Progress, 3);
        Assert.Equal(0.5f, f.Blend, 3);   // smoothstep midpoint
        f.Step(In(melee: true), 0.15f);
        Assert.Equal(1f, f.Progress, 3);
        f.Step(In(fire: true), 0.15f);
        Assert.Equal(0.5f, f.Progress, 3);
        f.Step(In(), 0.15f);
        Assert.Equal(0f, f.Progress, 3);
    }

    [Fact]
    public void ARangedShotEndsIt()
    {
        var f = Widened();
        Assert.Equal(MeleeFov.Change.Shot, f.Step(In(fire: true), Dt));
        Assert.False(f.Wide);
    }

    [Fact]
    public void FireHeldBeforeAMeleeDoesNotEndItWhileTheMeleeRuns()
    {
        var f = new MeleeFov();
        f.Step(In(fire: true), Dt);
        f.Step(In(fire: true, melee: true), Dt);
        f.Step(In(fire: true, melee: true), Dt);
        Assert.True(f.Wide);
        f.Step(In(fire: true), Dt);   // the melee is over and the gun still fires
        Assert.False(f.Wide);
    }

    [Fact] public void AimingEndsIt() { var f = Widened(); Assert.Equal(MeleeFov.Change.Aimed, f.Step(In(aim: true), Dt)); Assert.False(f.Wide); }
    [Fact] public void AGrenadeThrowEndsIt() { var f = Widened(); Assert.Equal(MeleeFov.Change.Threw, f.Step(In(throwing: true), Dt)); Assert.False(f.Wide); }
    [Fact] public void DeathEndsItAndRespawnStartsClean()
    {
        var f = Widened();
        Assert.Equal(MeleeFov.Change.Died, f.Step(In(alive: false), Dt));
        Run(f, In(alive: false), 0.5f);
        Assert.Equal(0f, f.Progress);
        f.Step(In(), Dt);
        Assert.False(f.Wide);
    }

    [Fact]
    public void NoWideningWhileAimingOrThrowing()
    {
        var f = new MeleeFov();
        f.Step(In(aim: true, melee: true), Dt);
        f.Step(In(throwing: true, melee: true), Dt);
        Assert.False(f.Wide);
    }

    [Fact]
    public void ExcludedBodyNeverWidens()
    {
        var f = new MeleeFov();
        f.Step(In(excluded: true), Dt);
        Run(f, In(melee: true, excluded: true), 1f);
        Assert.False(f.Wide);
        Assert.Equal(0f, f.Progress);
        Assert.Equal(FovMath.Scale(90f, Wide), f.Scale(90f, Wide), 4);
    }

    [Fact]
    public void NotWidenedScaleIsTheConfiguredOne() => Assert.Equal(FovMath.Scale(100f, Wide), new MeleeFov().Scale(100f, Wide), 5);

    [Fact]
    public void ANewSwingAfterAShotWidensAgain()
    {
        var f = Widened();
        f.Step(In(fire: true), Dt);
        f.Step(In(), Dt);
        Assert.Equal(MeleeFov.Change.Widened, f.Step(In(melee: true), Dt));
    }
    [Fact]
    public void AMeleeStartOnTheSameFrameAsAFirePressStillWidens()
    {
        var f = new MeleeFov();
        f.Step(In(), Dt);
        Assert.Equal(MeleeFov.Change.Widened, f.Step(In(melee: true, fire: true), Dt));
        Run(f, In(melee: true, fire: true), 0.2f);
        Assert.True(f.Wide);
    }

    [Theory]
    [InlineData(4f / 3f)]
    [InlineData(21f / 9f)]
    public void TheCapHoldsAtOtherAspects(float aspect)
    {
        Assert.True(MeleeFov.WideTarget(115f, aspect) <= 120f + 1e-3f);
        Assert.Equal(120f, MeleeFov.WideTarget(120f, aspect), 3);
        Assert.Equal(FovMath.Clamp(80f, aspect) + 10f, MeleeFov.WideTarget(80f, aspect), 3);
    }

    [Fact]
    public void ExclusionAfterWideningEndsIt()
    {
        var f = Widened();
        Assert.Equal(MeleeFov.Change.Excluded, f.Step(In(excluded: true), Dt));
        Assert.False(f.Wide);
    }

    // camera/melee-orbit (owner 2026-11-25: "keep the old widen rules"): every move that widened before still widens, independent of the camera's melee mode.
    [Theory]
    [InlineData(1, RushPhase.None, JumpPhase.None)]         // a swing: knife, kick or melee weapon alike
    [InlineData(0, RushPhase.Rush, JumpPhase.None)]         // the charge
    [InlineData(0, RushPhase.DashAttack, JumpPhase.None)]   // the dash attack's lunge
    [InlineData(0, RushPhase.None, JumpPhase.Dive)]         // the ground pound
    [InlineData(0, RushPhase.None, JumpPhase.Recover)]
    public void TheOldMovesStillWiden(int step, RushPhase rush, JumpPhase jump) => Assert.True(MeleeFov.Widens(step, rush, jump));

    [Fact]
    public void NoMoveNoWidening() => Assert.False(MeleeFov.Widens(0, RushPhase.None, JumpPhase.None));
}
