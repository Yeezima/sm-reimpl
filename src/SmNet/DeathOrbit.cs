using System;

namespace SmNet;

/// <summary>
/// late-g/death-orbit-cam (the owner, 2026-11-06; all modes), client presentation only: while the own player is dead and waits to respawn, the mouse and the
/// right stick orbit the death camera round the own body. The look deltas are the alive view's (radians, a positive yaw turns the view left, a positive pitch
/// looks up), so the player's sensitivity, invert and pad curves carry over: turning the view right swings the camera round to the left of the body, looking up
/// lowers the camera. The orbit is an offset on the death camera's own path (its azimuth and its rising elevation), reset at each death so the view still starts
/// from the last camera. Collision is the death camera's (the cast from the body, the push out of living players, the depenetration). All numbers are ours
/// (estimates): the original's death camera is not decoded. Tested in <c>DeathOrbitTests</c>; <c>--no-death-orbit</c> restores the fixed view.
/// </summary>
public sealed class DeathOrbit
{
    /// <summary>Ours (estimate): radians; the lowest elevation the orbit reaches (about 3 degrees above the body's level: lower only drives the camera into the floor the corpse lies on).</summary>
    public const float MinElevation = 0.05f;
    /// <summary>Ours (estimate): radians; the highest (about 74 degrees: short of straight down, where the look basis has no stable up).</summary>
    public const float MaxElevation = 1.3f;

    /// <summary>Radians added to the death camera's azimuth (Godot space: a positive value swings the camera clockwise seen from above).</summary>
    public float Yaw { get; private set; }
    /// <summary>Radians added to the death camera's elevation.</summary>
    public float Pitch { get; private set; }
    /// <summary>The player has moved the orbit since this death began.</summary>
    public bool Moved { get; private set; }

    public void Reset() { Yaw = 0f; Pitch = 0f; Moved = false; }

    /// <summary>
    /// Applies one look delta in the alive view's terms (<paramref name="viewYaw"/>: positive turns left; <paramref name="viewPitch"/>: positive looks up)
    /// against the camera's own elevation now (<paramref name="baseElevation"/>), so the result stays within the limits.
    /// </summary>
    public void Look(float viewYaw, float viewPitch, float baseElevation)
    {
        if (!float.IsFinite(viewYaw) || !float.IsFinite(viewPitch) || !float.IsFinite(baseElevation)) return;
        if (viewYaw == 0f && viewPitch == 0f) return;
        Yaw = Wrap(Yaw - viewYaw);
        Pitch = Math.Clamp(baseElevation + Pitch - viewPitch, MinElevation, MaxElevation) - baseElevation;
        Moved = true;
    }

    /// <summary>The camera's azimuth with the orbit applied.</summary>
    public float Azimuth(float baseAzimuth) => baseAzimuth + Yaw;

    /// <summary>The camera's elevation with the orbit applied, within the limits once the player has moved it (the death camera's own path is left alone until then).</summary>
    public float Elevation(float baseElevation) =>
        Moved ? Math.Clamp(baseElevation + Pitch, MinElevation, MaxElevation) : baseElevation;

    static float Wrap(float a)
    {
        a %= MathF.Tau;
        if (a > MathF.PI) a -= MathF.Tau;
        else if (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}
