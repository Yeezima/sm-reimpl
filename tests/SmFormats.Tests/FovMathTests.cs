using System.Numerics;
using SmClient.Customizer.Model;
using SmSim;
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

    // camera/right-shoulder-fov: the pinned shift per slider step at 16:9 (metres; spec "Field-of-view camera shift").
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(66f, 0f)]
    [InlineData(75f, 0.037f)]
    [InlineData(90f, 0.109f)]
    [InlineData(105f, 0.201f)]
    [InlineData(120f, 0.335f)]
    public void TheCameraShiftAtEachSliderStep(float slider, float metres) =>
        Assert.Equal(metres, FovMath.CameraShift(FovMath.Scale(slider, Wide)), 0.0025f);

    [Fact]
    public void TheShiftGrowsWithEverySliderStepAndBadValuesAreNoShift()
    {
        float previous = 0f;
        for (float h = FovMath.MinimumHorizontal(Wide) + 1f; h <= FovMath.Maximum; h++)
        {
            float shift = FovMath.CameraShift(FovMath.Scale(h, Wide));
            Assert.True(shift > previous, $"{h} degrees: {shift} after {previous}");
            previous = shift;
        }
        Assert.Equal(0f, FovMath.CameraShift(float.NaN));
        Assert.Equal(0f, FovMath.CameraShift(0.5f));
    }

    [Fact]
    public void ANarrowWindowKeepsTheShiftModest() => Assert.Equal(FovMath.MaxShift, FovMath.CameraShift(FovMath.Scale(120f, 1f)));

    [Theory]
    [InlineData(0.3f, 0.64f, 0.78f, 0.3f)]     // well inside the limit: the whole shift
    [InlineData(0.3f, 0.64f, 1.2f, 0.081f)]    // cut to reach 1.4 m
    [InlineData(0.3f, 0.64f, 1.39f, 0f)]       // no room: none
    [InlineData(0.3f, 1.5f, 0.2f, 0f)]         // already beyond it: never to the left
    [InlineData(0f, 0.64f, 0.78f, 0f)]
    public void TheShiftGivesWayAtTheShotOriginLimit(float shift, float side, float across, float expected) =>
        Assert.Equal(expected, FovMath.LimitShift(shift, side, across, 1.4f), 0.002f);

    /// <summary>
    /// The crosshair at every slider step and at any window shape: a camera 0.64 m right and 0.78 m to 1.3 m across the view from the eye (the data rig's place at level is 0.78 m; the rig reaches
    /// more at other pitches), moved right by the limited shift, looks along the view, and the client's converged shot is that ray from its point nearest the eye. The server must fire that same
    /// ray, so the shot lands under the crosshair at any distance. Guards the limit and the wire; the real rig is measured with <c>--aim-test</c> (spec "Field-of-view camera shift").
    /// </summary>
    [Fact]
    public void AtEverySliderStepTheServersShotIsTheCrosshairsRay()
    {
        var c = new SimConfig();
        var me = new PlayerState { Position = new Vector3(3f, -2f, 1f) };
        foreach (float yaw in new[] { 0f, 0.9f, -2.4f })
            foreach (float pitch in new[] { 0f, 0.5f, -0.6f })
            foreach (float across in new[] { 0.78f, 1.2f, 1.3f })
            foreach (float aspect in new[] { Wide, 1f })
                for (float h = FovMath.MinimumHorizontal(aspect); h <= FovMath.Maximum; h++)
                {
                    var view = Combat.AimDirection(yaw, pitch);
                    var eye = Combat.EyePosition(me, c);
                    var right = Vector3.Normalize(Vector3.Cross(view, Vector3.UnitZ));
                    float shift = FovMath.LimitShift(FovMath.CameraShift(FovMath.Scale(h, aspect)), 0.64f, across, c.MaxShotOriginOffset - FovMath.OriginMargin);
                    var camera = eye + right * (0.64f + shift) + Vector3.Cross(right, view) * across - view * (3.38f * 1.3f);
                    var start = camera + view * Vector3.Dot(eye - camera, view);
                    var input = new PlayerInput(1, 0f, 0f, yaw, InputButtons.None, pitch).WithShot(start, view, eye);
                    var origin = Combat.ShotOrigin(me, input, c);
                    Assert.True(Vector3.Distance(origin, start) < 0.02f, $"{h} degrees: origin {origin}, ray {start}");
                    var (shotYaw, shotPitch) = Combat.ShotAim(input, c);
                    Assert.True(Vector3.Distance(Combat.AimDirection(shotYaw, shotPitch), view) < 1e-3f, $"{h} degrees: shot off the view");
                }
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
