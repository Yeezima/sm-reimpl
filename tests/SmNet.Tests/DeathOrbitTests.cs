using SmNet;

namespace SmNet.Tests;

/// <summary>late-g/death-orbit-cam: the death camera's orbit offset (look deltas in the alive view's terms, elevation limits, reset per death).</summary>
public class DeathOrbitTests
{
    [Fact]
    public void UntouchedOrbitLeavesTheDeathCameraPathAlone()
    {
        var orbit = new DeathOrbit();
        Assert.False(orbit.Moved);
        Assert.Equal(1.2f, orbit.Azimuth(1.2f));
        Assert.Equal(0.02f, orbit.Elevation(0.02f));   // below the limit: the death camera's own start is kept until the player moves the view
        orbit.Look(0f, 0f, 0.3f);
        Assert.False(orbit.Moved);
    }

    [Fact]
    public void TurningTheViewRightSwingsTheCameraTheOtherWay()
    {
        var orbit = new DeathOrbit();
        orbit.Look(-0.5f, 0f, 0.3f);   // the alive view's right turn is a negative yaw
        Assert.True(orbit.Moved);
        Assert.Equal(0.5f, orbit.Yaw, 5);
        Assert.Equal(1.5f, orbit.Azimuth(1f), 5);
    }

    [Fact]
    public void LookingUpLowersTheCameraWithinTheLimits()
    {
        var orbit = new DeathOrbit();
        orbit.Look(0f, 0.2f, 0.4f);
        Assert.Equal(0.2f, orbit.Elevation(0.4f), 5);
        orbit.Look(0f, 5f, 0.4f);
        Assert.Equal(DeathOrbit.MinElevation, orbit.Elevation(0.4f), 5);
        orbit.Look(0f, -10f, 0.4f);
        Assert.Equal(DeathOrbit.MaxElevation, orbit.Elevation(0.4f), 5);
        Assert.Equal(DeathOrbit.MaxElevation, orbit.Elevation(0.9f), 5);   // the death camera's rising path cannot push it past the limit either
    }

    [Fact]
    public void ALoweredCameraStaysDownWhileTheDeathCameraRises()
    {
        // Review fix: the death camera's own elevation rises to 0.4 rad over its first 1.5 s; a camera the player lowered to the floor limit must not ride up with it.
        var orbit = new DeathOrbit();
        orbit.Look(0f, 1f, 0.1f);
        Assert.Equal(DeathOrbit.MinElevation, orbit.Elevation(0.1f), 5);
        Assert.Equal(DeathOrbit.MinElevation, orbit.Elevation(0.4f), 5);
        orbit.Look(0f, -0.1f, 0.4f);
        Assert.Equal(DeathOrbit.MinElevation + 0.1f, orbit.Elevation(0.4f), 5);
    }

    [Fact]
    public void YawWrapsAndBadInputIsIgnored()
    {
        var orbit = new DeathOrbit();
        for (int i = 0; i < 100; i++) orbit.Look(-0.7f, 0f, 0.4f);
        Assert.InRange(orbit.Yaw, -MathF.PI, MathF.PI);
        float before = orbit.Yaw;
        orbit.Look(float.NaN, 0f, 0.4f);
        orbit.Look(0f, float.PositiveInfinity, 0.4f);
        Assert.Equal(before, orbit.Yaw);
    }

    [Fact]
    public void ResetStartsTheNextDeathFromTheLastCamera()
    {
        var orbit = new DeathOrbit();
        orbit.Look(1f, 0.1f, 0.4f);
        orbit.Reset();
        Assert.False(orbit.Moved);
        Assert.Equal(0f, orbit.Yaw);
        Assert.Equal(0.7f, orbit.Elevation(0.7f));
    }
}
