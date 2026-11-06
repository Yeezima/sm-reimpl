using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// Stream B (the owner, 2026-11-06 late: "the plasma gun/cannon/pistol charged shot release is still delayed - let the projectile fire out of the barrel instantly when letting go of
/// left mouse button"): the own plasma ball's flight as the client predicts it, so the ball leaves the barrel on the frame the prediction fires (the release tick) instead of when the
/// server's first snapshot that holds it arrives (half a round trip and a tick or two later). Engine-free so it can be tested; client presentation only (the server's projectile
/// decides the damage and the detonation, the protocol is unchanged). The flight is the server's (<c>World.Fly</c>): the launch along the shot's aim raised by the projectile's
/// <see cref="ProjectileSpec.ExtraAimAngle"/>, gravity by the spec's distance table, a straight segment per step against the level's weapon collision. Players are not tested (the
/// ball is stopped where the own shot's trace met a player instead: <see cref="Step"/>'s <c>maxTravel</c>).
/// </summary>
public static class OwnBallFlight
{
    /// <summary>The launch velocity of a ball shot along <paramref name="aim"/> (unit, game space): raised by the spec's extra aim angle (never past straight up), at its speed.</summary>
    public static Vector3 LaunchVelocity(Vector3 aim, ProjectileSpec spec)
    {
        float length = aim.Length();
        if (!(length > 1e-6f) || !float.IsFinite(length)) return Vector3.Zero;
        aim /= length;
        float yaw = MathF.Atan2(-aim.X, aim.Y), pitch = MathF.Asin(Math.Clamp(aim.Z, -1f, 1f));   // the inverse of Combat.AimDirection
        var direction = Combat.AimDirection(yaw, MathF.Min(pitch + spec.ExtraAimAngle * (MathF.PI / 180f), PlayerInput.MaxPitch));
        return direction * spec.Speed;
    }

    /// <summary>
    /// One step of <paramref name="dt"/> seconds: gravity, then the segment against <paramref name="raycast"/> (origin, unit direction, length → hit or null) and the
    /// <paramref name="maxTravel"/> along the path. Returns the new position, velocity and distance flown, and whether the ball stopped (it then rests at the stop point).
    /// </summary>
    public static (Vector3 Position, Vector3 Velocity, float Travelled, bool Stopped) Step(Vector3 position, Vector3 velocity, float travelled, ProjectileSpec spec, float gravity,
        float dt, float maxTravel, Func<Vector3, Vector3, float, RayHit?>? raycast)
    {
        if (!(dt > 0f) || !float.IsFinite(dt)) return (position, velocity, travelled, false);
        velocity -= new Vector3(0f, 0f, gravity * Combat.GravityFactor(spec.Gravity, travelled) * dt);
        var segment = velocity * dt;
        float length = segment.Length();
        if (!(length > 1e-7f)) return (position, velocity, travelled, false);
        var direction = segment / length;
        float allowed = MathF.Max(0f, maxTravel - travelled);
        float reach = MathF.Min(length, allowed);
        if (raycast?.Invoke(position, direction, reach) is { } hit && hit.Distance <= reach)
            return (position + direction * hit.Distance, velocity, travelled + hit.Distance, true);
        if (reach < length) return (position + direction * reach, velocity, travelled + reach, true);
        return (position + segment, velocity, travelled + length, false);
    }
}
