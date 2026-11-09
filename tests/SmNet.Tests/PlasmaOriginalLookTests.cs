using SmClient.Bullets;

namespace SmNet.Tests;

/// <summary>
/// plasma-original-fx (owner, 2026-11-09): the plasma gun's and pistol's projectiles in the original game files' look (client/Bullets/PlasmaOriginalLook.cs, engine-free). Synthetic curves only.
/// </summary>
public class PlasmaOriginalLookTests
{
    // The shape of the charged trail's "blue trail" width keys (0: 1 m, 1: 6 m), synthetic.
    static float Widening(float x) => 1f + 5f * x;

    [Fact]
    public void TheOriginalLookIsTheDefaultAndTheFlagRestoresTheOldOne()
    {
        Assert.True(PlasmaOriginalLook.Original(oldLookFlag: false));
        Assert.False(PlasmaOriginalLook.Original(oldLookFlag: true));
    }

    [Fact]
    public void AlongTheTrailTheWidthKeysRunFromTheHeadToTheTail()
    {
        // Head = the first key, tail = the last, whatever the loop time: the trail never becomes a 6 m slab.
        foreach (float loop in new[] { 0f, 0.5f, 0.99f })
        {
            Assert.Equal(1f, PlasmaOriginalLook.StreamerWidth(Widening, 0f, loop, alongTrail: true), 4);
            Assert.Equal(3.5f, PlasmaOriginalLook.StreamerWidth(Widening, 0.5f, loop, alongTrail: true), 4);
            Assert.Equal(6f, PlasmaOriginalLook.StreamerWidth(Widening, 1f, loop, alongTrail: true), 4);
        }
        Assert.Equal(0f, PlasmaOriginalLook.StreamerWidth(_ => -1f, 0.3f, 0f, alongTrail: true));   // never negative
    }

    [Fact]
    public void TheOldReadingKeysTheWidthOverLoopTimeWithTheTaper()
    {
        Assert.Equal(6f, PlasmaOriginalLook.StreamerWidth(Widening, 0f, 1f, alongTrail: false), 4);    // a 6 m wide head once a second: the slab
        Assert.Equal(1.8f, PlasmaOriginalLook.StreamerWidth(Widening, 1f, 1f, alongTrail: false), 4);  // 0.3 of it at the tail
        Assert.Equal(1f, PlasmaOriginalLook.StreamerWidth(Widening, 0f, 0f, alongTrail: false), 4);
    }

    [Fact]
    public void OnlyThePlasmaGunAndPistolTrailsReadTheWidthAlongTheTrailInTheOriginalLook()
    {
        foreach (var wargear in new[] { "sm_mp_ranged_plasma_gun", "sm_mp_pistol_plasma_pistol", "csm_mp_ranged_plasma_gun", "csm_mp_pistol_plasma_pistol" })
        {
            Assert.True(PlasmaOriginalLook.WidthAlongTrail(wargear, original: true), wargear);
            Assert.False(PlasmaOriginalLook.WidthAlongTrail(wargear, original: false), wargear);
        }
        Assert.False(PlasmaOriginalLook.WidthAlongTrail("sm_mp_exotic_plasma_cannon", original: true));
        Assert.False(PlasmaOriginalLook.WidthAlongTrail("sm_mp_ranged_grenade_launcher", original: true));
    }

    [Fact]
    public void TheSafeModeStreakTakesTheDataTracersSmokeColour()
    {
        var loyal = PlasmaOriginalLook.SafeStreakColour(chaos: false);
        Assert.Equal((113f / 255f, 1f, 1f), loyal);
        var chaos = PlasmaOriginalLook.SafeStreakColour(chaos: true);
        Assert.Equal((224f / 255f, 101f / 255f, 1f), chaos);
    }
}
