using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the players' attacks on the enemies (MU1's simple pass; MU3 takes this file over with the cones, the projectiles, the blasts, the
/// cleave and the tracers that end on the enemy hit). MU1: after every player's shots of the tick resolved against the players and the level, each hit-scan
/// ray (<see cref="ShotEvent"/> with an origin, no projectile, not a cone) is tested from its origin to where it ended against the enemies
/// (<see cref="MusouSim.Raycast"/>); the nearest takes the shot's damage at its distance (<see cref="Combat.DamageAt(float, WeaponSpec)"/>, or a charged
/// stage's damage) times the weapon's head or body multiplier. No lag compensation (local play; ours). The tracer still ends where the core put it.
/// </summary>
public sealed partial class World
{
    void MusouPlayerShots()
    {
        var musou = _musou!;
        foreach (var shot in _shots)
        {
            if (shot.Projectile != 0 || shot.Weapon < 0 || shot.Weapon >= Config.Weapons.Count) continue;
            var weapon = Config.Weapons[shot.Weapon];
            if (shot.Stage == 0 && weapon.Delivery == DeliveryKind.Cone) continue;   // MU3: cones
            var ray = shot.End - shot.Origin;
            float length = ray.Length();
            if (!(length > 1e-3f)) continue;
            var direction = ray / length;
            ushort enemy = musou.Raycast(shot.Origin, direction, length, out float distance, out bool head);
            if (enemy == 0) continue;
            var spec = Shots.ChargeShotOf(weapon, shot.Stage)?.Damage ?? weapon.DirectDamage with { Damage = Combat.DamageAt(distance, weapon) };
            float damage = spec.Damage * (head ? spec.HeadMultiplier : spec.BodyMultiplier);
            musou.Damage(enemy, shot.Shooter, damage, direction, out _, marker: head ? (byte)2 : (byte)1, cause: MusouDeathKind.Shot);
        }
    }
}
