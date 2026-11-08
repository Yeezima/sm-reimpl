using SmClient.Bullets;
using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// fix/plasma-light-tracer (owner, 2026-11-08): the plasma gun's and pistol's light shot is the charged shot's ball and trail at 0.75 of its size
/// (client/Bullets/PlasmaLightShot.cs, engine-free). Synthetic data only.
/// </summary>
public class PlasmaLightShotTests
{
    [Fact]
    public void TheLightShotIsThreeQuartersOfTheChargedShot()
    {
        Assert.Equal(0.75f, PlasmaLightShot.Scale);
        // The charged ball is 0.35 m in radius (ProjectileLook, stage > 0); its trail effects keep their scale: both at 0.75.
        Assert.Equal(0.2625f, PlasmaLightShot.Scaled(0.35f), 5);
        Assert.Equal(0.75f, PlasmaLightShot.Scaled(1f), 5);
        Assert.Equal(1.5f, PlasmaLightShot.Scaled(2f), 5);
        Assert.True(PlasmaLightShot.Scaled(0.35f) > 0.2f, "larger than the old light ball's 0.2 m radius and far thicker than the 0.09 m bolt");
    }

    [Fact]
    public void EveryPlasmaGunAndPistolButNotTheCannonDrawsTheLightBall()
    {
        foreach (var wargear in new[] { "sm_mp_ranged_plasma_gun", "sm_mp_pistol_plasma_pistol", "csm_mp_ranged_plasma_gun", "csm_mp_pistol_plasma_pistol" })
            Assert.True(PlasmaLightShot.Applies(wargear), wargear);
        Assert.False(PlasmaLightShot.Applies("sm_mp_exotic_plasma_cannon"));
        Assert.False(PlasmaLightShot.Applies("sm_mp_ranged_bolter"));
    }

    [Fact]
    public void TheChargedStageIsTheHighestOneThatFiresAProjectile()
    {
        Assert.True(PlasmaLightShot.ChargedStage(ChargeTests.PlasmaGun) > 0);
        Assert.NotNull(Shots.ProjectileOf(ChargeTests.PlasmaGun, PlasmaLightShot.ChargedStage(ChargeTests.PlasmaGun)));
        Assert.Equal(0, PlasmaLightShot.ChargedStage(ChargeTests.PlasmaGun with { Charge = null }));
        Assert.Equal(0, PlasmaLightShot.ChargedStage(ChargeTests.Las));   // a charged ray has no projectile to model on
    }

    [Fact]
    public void TheLightBallFliesAtTwiceTheChargedBallsSpeedWithNoCap()
    {
        Assert.Equal(180f, PlasmaLightShot.FlightSpeed(90f));
        Assert.Equal(180f, PlasmaLightShot.FlightSpeed(0f));   // no speed in the data: the 90 m/s plasma ball's
        Assert.True(PlasmaLightShot.FlightSpeed(90f) > 90f);
    }
}
