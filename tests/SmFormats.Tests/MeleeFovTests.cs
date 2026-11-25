using SmClient.Customizer.Model;
using Xunit;

namespace SmFormats.Tests;

/// <summary>camera/melee-fov-widen: the widening's target and blend. When it is on is the shared melee mode (camera/melee-orbit; its rules: SmNet.Tests MeleeModeTests).</summary>
public class MeleeFovTests
{
    const float Wide = 16f / 9f;
    const float Dt = 1f / 60f;

    static void Run(MeleeFov f, bool wide, float seconds) { for (float t = 0; t < seconds; t += Dt) f.Advance(wide, Dt); }

    static MeleeFov Widened()
    {
        var f = new MeleeFov();
        Run(f, true, 0.5f);
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
    public void AtOneTwentyTheScaleDoesNotChange() => Assert.Equal(FovMath.Scale(120f, Wide), Widened().Scale(120f, Wide), 4);

    [Fact]
    public void FullyWideShowsTheTarget() => Assert.Equal(MeleeFov.WideTarget(90f, Wide), Widened().Horizontal(90f, Wide), 2);

    [Fact]
    public void StaysWideWhileTheModeIsOn()
    {
        var f = Widened();
        Run(f, true, 60f);
        Assert.True(f.Wide);
        Assert.Equal(1f, f.Progress);
    }

    [Fact]
    public void BlendTakesPointThreeSecondsEitherWay()
    {
        var f = new MeleeFov();
        f.Advance(true, 0.15f);
        Assert.Equal(0.5f, f.Progress, 3);
        Assert.Equal(0.5f, f.Blend, 3);   // smoothstep midpoint
        f.Advance(true, 0.15f);
        Assert.Equal(1f, f.Progress, 3);
        f.Advance(false, 0.15f);
        Assert.Equal(0.5f, f.Progress, 3);
        f.Advance(false, 0.15f);
        Assert.Equal(0f, f.Progress, 3);
        Assert.False(f.Wide);
    }

    [Fact]
    public void NotWidenedScaleIsTheConfiguredOne() => Assert.Equal(FovMath.Scale(100f, Wide), new MeleeFov().Scale(100f, Wide), 5);

    [Theory]
    [InlineData(4f / 3f)]
    [InlineData(21f / 9f)]
    public void TheCapHoldsAtOtherAspects(float aspect)
    {
        Assert.True(MeleeFov.WideTarget(115f, aspect) <= 120f + 1e-3f);
        Assert.Equal(120f, MeleeFov.WideTarget(120f, aspect), 3);
        Assert.Equal(FovMath.Clamp(80f, aspect) + 10f, MeleeFov.WideTarget(80f, aspect), 3);
    }
}
