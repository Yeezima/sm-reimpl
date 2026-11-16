using SmClient.Customizer.Model;
using Xunit;

namespace SmFormats.Tests;

public class ReticleFovScaleTests
{
    const float Wide = 16f / 9f;

    [Fact]
    public void DefaultSettingScalesNothing()
    {
        Assert.Equal(1f, ReticleFovScale.ForSetting(0f, Wide));
        Assert.Equal(1f, ReticleFovScale.ForSetting(FovMath.MinimumHorizontal(Wide), Wide));
        Assert.Equal(1f, ReticleFovScale.Between(40f, 40f));
    }

    [Theory]
    [InlineData(90f)]
    [InlineData(105f)]
    [InlineData(120f)]
    public void WiderSettingShrinksTheCrosshair(float horizontal) => Assert.InRange(ReticleFovScale.ForSetting(horizontal, Wide), ReticleFovScale.Min, 0.999f);

    [Fact]
    public void ScaleIsTheTanRatioOfTheRealVerticalAngle()
    {
        foreach (float h in new[] { 80f, 100f, 120f })
        {
            float v = FovMath.ToVertical(h, Wide);
            float expected = MathF.Tan(40f * MathF.PI / 360f) / MathF.Tan(v * MathF.PI / 360f);
            Assert.Equal(expected, ReticleFovScale.ForSetting(h, Wide), 3);
        }
        Assert.Equal(0.5f, ReticleFovScale.Between(2f * MathF.Atan(0.5f) * 180f / MathF.PI, 2f * MathF.Atan(1f) * 180f / MathF.PI), 3);
    }

    [Fact]
    public void NarrowerLiveViewEnlargesAndBadInputIsOne()
    {
        Assert.True(ReticleFovScale.Between(40f, 30f) > 1f);
        Assert.Equal(1f, ReticleFovScale.Between(float.NaN, 40f));
        Assert.Equal(1f, ReticleFovScale.Between(40f, 0f));
    }
}
