using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// Orks face the one they engage (Musou Mode, owner 2026-11-08: "orks should try to face the player more ... shooter orks running from the player or moving around in a different direction still manage to
/// shoot the player, while not facing their target"). Every value ours (spec "Musou Mode: Ork facing", refine register). Before, a shooter's facing snapped to its target the tick a burst began (a 180 degree
/// spin between two snapshots) and in between it faced its walking direction, so a backing-off or flow-routed gunner looked away while its tracers flew at the player.
/// </summary>
public static class MusouFacingRules
{
    /// <summary>A ranged Ork's round leaves only with its facing within this many degrees (each side) of its target; otherwise it turns first (ours).</summary>
    public const float FireConeDegrees = 30f;
    public const float FireConeRadians = FireConeDegrees * MathF.PI / 180f;
    /// <summary>How fast a gunner turns to aim (degrees a second, ours): a half turn in a quarter second.</summary>
    public const float ShooterTurnDegrees = 720f;
    /// <summary>How fast an engaged Ork turns its body toward its target while it moves (degrees a second, ours).</summary>
    public const float EngageTurnDegrees = 480f;
    /// <summary>A ranged Ork within this multiple of its range faces its target as it moves (backing off, strafing, closing in; ours).</summary>
    public const float ShooterEngageScale = 1.5f;
    /// <summary>A melee Ork this near its target (m) faces it as it closes in (ours).</summary>
    public const float MeleeEngageRange = 10f;
}

public sealed partial class MusouSim
{
    readonly Vector2[] _faceWant = new Vector2[Capacity];   // the direction the Ork faces while it moves this tick (zero: its walking direction)

    /// <summary>Turns Ork <paramref name="i"/> toward <paramref name="direction"/> by at most <paramref name="degreesPerSecond"/> this tick; the angle left (radians, 0 to pi).</summary>
    float TurnTo(int i, Vector2 direction, float degreesPerSecond)
    {
        if (direction.LengthSquared() < 1e-8f) return 0f;
        float want = MathF.Atan2(-direction.X, direction.Y);
        float diff = MathF.IEEERemainder(want - _yaw[i], MathF.Tau);
        float max = degreesPerSecond * MathF.PI / 180f * Dt;
        _yaw[i] = MathF.IEEERemainder(_yaw[i] + Math.Clamp(diff, -max, max), MathF.Tau);
        return MathF.Abs(MathF.IEEERemainder(want - _yaw[i], MathF.Tau));
    }

    /// <summary>Test aid: enemy <paramref name="id"/>'s yaw.</summary>
    public float YawOf(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] ? _yaw[id - 1] : 0f;
}
