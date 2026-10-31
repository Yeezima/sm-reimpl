using System.IO;
using SmClient.Customizer.Model;
using Xunit;

namespace SmFormats.Tests;

public class MouseSensitivityTests
{
    [Fact]
    public void DefaultIsOneAndLeavesTheLookScaleAlone()
    {
        MouseSensitivity.Selected = MouseSensitivity.Default;
        Assert.Equal(1f, MouseSensitivity.Default);
        Assert.Equal(0.75f, MouseSensitivity.Look(0.75f));
    }

    [Fact]
    public void SelectedMultipliesTheLookScale()
    {
        try
        {
            MouseSensitivity.Selected = 2f;
            Assert.Equal(3f, MouseSensitivity.Look(1.5f));
            MouseSensitivity.Selected = 0.5f;
            Assert.Equal(0.75f, MouseSensitivity.Look(1.5f));
        }
        finally { MouseSensitivity.Selected = MouseSensitivity.Default; }
    }

    [Theory]
    [InlineData(0f, 0.1f)]
    [InlineData(9f, 3f)]
    [InlineData(1.02f, 1f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    public void ClampLimitsAndSnaps(float value, float expected) => Assert.Equal(expected, MouseSensitivity.Clamp(value), 3);

    [Fact]
    public void SettingsRoundTripAndDefaultWhenMissingOrWrong()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-mouse-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(1f, FrontEndSettings.Load(dir).MouseSensitivity);
            Assert.Null((FrontEndSettings.Default with { MouseSensitivity = 1.65f }).Save(dir));
            Assert.Equal(1.65f, FrontEndSettings.Load(dir).MouseSensitivity, 3);
            File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"mouseSensitivity\": 50}");
            Assert.Equal(1f, FrontEndSettings.Load(dir).MouseSensitivity);
            File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"mouseSensitivity\": \"fast\"}");
            Assert.Equal(1f, FrontEndSettings.Load(dir).MouseSensitivity);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
