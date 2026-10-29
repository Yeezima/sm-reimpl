using SmNet;

namespace SmNet.Tests;

/// <summary>feel/camera-hitstop: when the follow camera hides the whole own character (<see cref="CameraHideRules"/>).</summary>
public class CameraHideTests
{
    [Theory]
    [InlineData(3.53f)]   // the exploration camera
    [InlineData(1.97f)]   // the aim camera
    [InlineData(1.7f)]    // the pistol's aim camera looking down
    public void NormalCamerasShowTheCharacter(float distance) => Assert.False(CameraHideRules.Hidden(false, distance));

    [Theory]
    [InlineData(1.29f)]   // the owner's wall at an angle: the old 1.0 m limit left the pauldron in the lens
    [InlineData(0.62f)]
    [InlineData(0.05f)]
    public void ACameraPulledInHidesIt(float distance) => Assert.True(CameraHideRules.Hidden(false, distance));

    [Fact]
    public void HysteresisKeepsItHiddenUntilTheCameraIsWellOut()
    {
        float limit = CameraHideRules.HideDistance;
        Assert.True(CameraHideRules.Hidden(true, limit + 0.05f));
        Assert.True(CameraHideRules.Hidden(true, limit * CameraHideRules.ShowFactor - 0.01f));
        Assert.False(CameraHideRules.Hidden(true, limit * CameraHideRules.ShowFactor + 0.01f));
        Assert.False(CameraHideRules.Hidden(false, limit + 0.05f));
    }

    [Fact]
    public void TheHideLimitIsInsideTheAimCameraAndTheComeBackDistanceBelowIt()
    {
        Assert.True(CameraHideRules.HideDistance * CameraHideRules.ShowFactor < 1.7f);
        Assert.True(CameraHideRules.HideDistance > 1.0f);
    }

    [Fact]
    public void ANanDistanceKeepsTheState()
    {
        Assert.True(CameraHideRules.Hidden(true, float.NaN));
        Assert.False(CameraHideRules.Hidden(false, float.NaN));
    }
}
