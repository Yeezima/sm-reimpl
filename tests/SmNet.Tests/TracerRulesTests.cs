using System.Numerics;
using SmClient.Bullets;
using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// fx/tracers-impact (owner, 2026-11-07): the tracer is one straight line from the barrel to where the impact's decal is painted, spread included; a plasma
/// gun's or pistol's uncharged shot plays the weapon's data tracer as a bolt; a shot that launches a projectile paints no impact when it is fired
/// (client/Bullets/TracerRules.cs, engine-free). Synthetic data only.
/// </summary>
public class TracerRulesTests
{
    static readonly Vector3 Origin = new(0, 0, 1.6f);
    static readonly Vector3 Muzzle = new(0.5f, 0.7f, 1.3f);   // beside and below the camera ray, a little ahead, as a held bolter's

    [Fact]
    public void TheTracerIsOneStraightLineFromTheMuzzleToTheDecalPointEvenWithAScatteredRay()
    {
        var rng = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            // A scattered shot: the ray turned by up to 4 degrees, ending 2 to 80 m out (a wall or a player); the decal is projected onto that end point.
            float yaw = (float)(rng.NextDouble() * 8 - 4) * MathF.PI / 180f, pitch = (float)(rng.NextDouble() * 8 - 4) * MathF.PI / 180f;
            var dir = Combat.AimDirection(yaw, pitch);
            var end = Origin + dir * (2f + (float)rng.NextDouble() * 78f);
            var path = TracerRules.Path(Muzzle, end, Origin, dir, bent: false);
            Assert.Null(path.Via);
            Assert.Equal(Muzzle, path.From);
            Assert.Equal(0f, TracerRules.EndGap(path, end), 5);
            Assert.True(TracerRules.MaxOffLine(path, Muzzle, end) < 1e-4f, $"shot {i}");
        }
    }

    [Fact]
    public void TheOldBentPathLeftTheBarrelToImpactLine()
    {
        var end = Origin + Vector3.UnitY * 30f;
        var bent = TracerRules.Path(Muzzle, end, Origin, Vector3.UnitY, bent: true);
        Assert.NotNull(bent.Via);
        Assert.Equal(0f, TracerRules.EndGap(bent, end), 5);   // it ended on the decal too, but on the way it followed the crosshair's line
        Assert.True(TracerRules.MaxOffLine(bent, Muzzle, end) > 0.4f);
        // Without a ray (or with the bend off) it is the straight line.
        Assert.Null(TracerRules.Path(Muzzle, end, null, null, bent: true).Via);
    }

    [Fact]
    public void ProjectileShotsAreNotTracedOrImpactedWhenFired()
    {
        Assert.True(TracerRules.DrawnAtFire(ChargeTests.PlasmaGun, 0));    // the tap: a ray
        Assert.False(TracerRules.DrawnAtFire(ChargeTests.PlasmaGun, 2));   // the charged bolt: a projectile, impact at its detonation
        Assert.True(TracerRules.DrawnAtFire(ChargeTests.PlasmaGun, 1));    // a stage without a shot fires the basic ray
        Assert.True(TracerRules.DrawnAtFire(ChargeTests.Las, 2));           // a charged ray is still a ray
        var launcher = ChargeTests.PlasmaGun with { Name = "test_launcher", Delivery = DeliveryKind.Projectile, Projectile = ChargeTests.Bolt, Charge = null };
        Assert.False(TracerRules.DrawnAtFire(launcher, 0));
    }

    static FxPlay Play(string name, bool target) => new("master_fx", name, 1f, 1f, false, "", "fx_muzzle", target ? "hitpoint" : "", "", "", "", 0f, "", false, false);

    static FxBinding Bind(string ability, string evt, FxPlay play) =>
        new($"ability_data/ability_definitions/{ability}/delivery/{evt}/commands/play_fx", evt, ability, [], play, null);

    [Fact]
    public void ThePlasmaBoltIsTheWeaponsOwnDataTracer()
    {
        // The gun: its basic shot binds the tracer.
        var gun = new WeaponEffects("sm_mp_ranged_plasma_gun", "ability_ranged_primary", "fx_muzzle", 250f,
            [Bind("ability_ranged_primary", "fire_fx", Play("plasma_gun_muzzle", false)), Bind("ability_ranged_primary", "hit_fx", Play("plasma_gun_tracer", true))], null);
        Assert.Equal("plasma_gun_tracer", PlasmaBolt.Tracer(gun)?.Name);
        // A wargear whose basic shot binds none: the first delivery tracer the wargear binds elsewhere is used.
        var pistol = new WeaponEffects("sm_mp_pistol_plasma_pistol", "ability_ranged_primary", "fx_muzzle", 100f,
            [Bind("ability_ranged_primary", "fire_fx", Play("plasma_gun_muzzle", false)), Bind("ability_ranged_secondary", "hit_fx", Play("plasma_gun_tracer", true))], null);
        Assert.Null(pistol.Basic.Tracer);
        Assert.Equal("plasma_gun_tracer", PlasmaBolt.Tracer(pistol)?.Name);
        Assert.True(PlasmaBolt.IsBoltWeapon(gun.Wargear) && PlasmaBolt.IsBoltWeapon(pistol.Wargear) && PlasmaBolt.IsBoltWeapon("csm_mp_pistol_plasma_pistol"));
        Assert.False(PlasmaBolt.IsBoltWeapon("sm_mp_exotic_plasma_cannon") || PlasmaBolt.IsBoltWeapon("sm_mp_ranged_bolter"));
    }

    [Fact]
    public void BothPlasmaBoltsGetThePooledStreakInSafeModeOnlyAndItMatchesTheBoltsSize()
    {
        foreach (var wargear in new[] { "sm_mp_ranged_plasma_gun", "sm_mp_pistol_plasma_pistol", "csm_mp_ranged_plasma_gun", "csm_mp_pistol_plasma_pistol" })
            Assert.True(PlasmaBolt.IsBoltWeapon(wargear));
        Assert.True(PlasmaBolt.UsesPooledStreak(true));
        Assert.False(PlasmaBolt.UsesPooledStreak(false));
        Assert.Equal(1.8f, PlasmaBolt.SafeTail, 3);
        Assert.Equal(0.09f, PlasmaBolt.SafeWidthScale * 0.16f, 4);
    }

    [Fact]
    public void ThePlasmaBoltIsBlueForTheLoyalistsPurpleForChaosAndThinnerThanTheChargedBall()
    {
        var (r, g, b) = PlasmaBolt.Colour("sm_mp_ranged_plasma_gun");
        Assert.True(b >= g && g > r, "loyalist blue");
        Assert.Equal(PlasmaColour.ChaosPurple, PlasmaBolt.Colour("csm_mp_ranged_plasma_gun"));
        // The data's streak: 0.3 m wide, 60 m per U. The bolt: thinner than the charged ball (0.7 m across) and longer than it is wide.
        float width = 0.3f * PlasmaBolt.WidthScale, length = 60f * PlasmaBolt.TrailScale;
        Assert.True(width < 0.7f && length > 0.7f && length / width > 10f);
    }
}
