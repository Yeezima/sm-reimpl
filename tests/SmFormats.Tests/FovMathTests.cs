using SmClient.Customizer.Model;
using Xunit;

namespace SmFormats.Tests;

public class FovMathTests
{
    const float Wide = 16f / 9f;

    [Fact]
    public void DataVerticalAtSixteenNineIsAboutSixtySixHorizontal() => Assert.InRange(FovMath.ToHorizontal(40f, Wide), 65f, 66f);

    [Fact]
    public void MinimumAndUnsetScaleNothing()
    {
        Assert.Equal(1f, FovMath.Scale(0f, Wide));
        Assert.Equal(1f, FovMath.Scale(FovMath.MinimumHorizontal(Wide), Wide));
        Assert.Equal(40f, FovMath.Apply(40f, FovMath.Scale(0f, Wide)));
    }

    [Fact]
    public void MaximumGivesAHundredTwentyHorizontal()
    {
        float scale = FovMath.Scale(120f, Wide);
        float vertical = FovMath.Apply(FovMath.DataVertical, scale);
        Assert.InRange(FovMath.ToHorizontal(vertical, Wide), 119.9f, 120.1f);
    }

    [Fact]
    public void ScaleGrowsWithTheSliderAndOutOfRangeIsClamped()
    {
        Assert.True(FovMath.Scale(90f, Wide) > FovMath.Scale(80f, Wide));
        Assert.Equal(FovMath.Scale(120f, Wide), FovMath.Scale(500f, Wide));
        Assert.Equal(1f, FovMath.Scale(float.NaN, Wide));
        Assert.Equal(FovMath.MinimumHorizontal(Wide), FovMath.Clamp(10f, Wide));
    }

    [Fact]
    public void ARoundTripThroughTheSettingsFileKeepsTheChoiceAndBadValuesFallBack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fov-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(FrontEndSettings.Default.With(f: 97.5f).Save(dir));
            Assert.Equal(97.5f, FrontEndSettings.Load(dir).FovDegrees);
            File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"fovDegrees\": 900}");
            Assert.Equal(0f, FrontEndSettings.Load(dir).FovDegrees);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

static class FovTestExtensions
{
    public static FrontEndSettings With(this FrontEndSettings s, float f) => s with { FovDegrees = f };
}
