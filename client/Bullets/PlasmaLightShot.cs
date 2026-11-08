using System;
using SmSim;

namespace SmClient.Bullets;

/// <summary>
/// fix/plasma-light-tracer (owner, 2026-11-08: "all modes - plasma pistol has no tracers - all plasma weapons (not plasma cannon) should have a larger tracer than they currently do. model the
/// light shot tracer after the charged shot tracer shot exactly, but scale it down to .75% of the charged shot size"; engine-free, tested; client presentation only).
/// <para>
/// Read as 75 % (0.75 x): the owner's other words ("larger than they currently do") only fit that reading. The light (uncharged) shot of the plasma gun and the plasma pistol, either side, is no
/// longer the thin blue streak of fx/tracers-impact (<see cref="PlasmaBolt"/>, whose data tracer the pistol never had): it is drawn as the weapon's own charged shot, ball and trail, flying
/// from the muzzle to the shot's end point, at <see cref="Scale"/> of its size: the same solid roiling ball (<c>PlasmaBallMaterial</c>), the same data trail effects of the charged projectile
/// (glow, streamers, light), the same colours, each scaled by <see cref="Scale"/>. The charged shot itself and the plasma cannon are untouched. The flight speed (twice the charged ball's)
/// is **ours**; the light shot stays a ray in the simulation, its impact effect waits for the ball.
/// </para>
/// </summary>
public static class PlasmaLightShot
{
    /// <summary>The light shot's size as a share of the charged shot's (the owner's ".75%", read as 75 %).</summary>
    public const float Scale = 0.75f;

    /// <summary>How many times the charged ball's speed the light ball flies (owner, 2026-11-09: "real speed (faster than 90 if possible)"; ours: 2x, 180 m/s for the 90 m/s plasma gun and pistol balls). No drop; the hit and damage stay instant, so the impact effect waits for the ball.</summary>
    public const float SpeedFactor = 2f;

    /// <summary>Whether the wargear's light shot is drawn as a small charged shot: the plasma gun and plasma pistol, either side (the plasma cannon's shots are projectiles of their own).</summary>
    public static bool Applies(string wargear) => PlasmaBolt.IsBoltWeapon(wargear);

    /// <summary>The stage (1-based, as a shot's stage) whose projectile the light shot is modelled on: the highest stage of the weapon that fires a projectile, 0 when there is none.</summary>
    public static int ChargedStage(WeaponSpec weapon)
    {
        if (weapon.Charge is not { Stages.Count: > 0 } charge) return 0;
        for (int stage = charge.Stages.Count; stage >= 1; stage--)
            if (Shots.ProjectileOf(weapon, stage) is not null) return stage;
        return 0;
    }

    /// <summary>A size, trail scale or radius of the charged shot, as the light shot's.</summary>
    public static float Scaled(float charged) => charged * Scale;

    /// <summary>The light ball's speed, m/s: <see cref="SpeedFactor"/> times the charged projectile's own (90 when the data names none).</summary>
    public static float FlightSpeed(float chargedSpeed) => SpeedFactor * (chargedSpeed > 1f ? chargedSpeed : 90f);
}
