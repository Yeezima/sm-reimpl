using System.Numerics;

namespace SmNet.Extras.Musou;

/// <summary>
/// The flight of a corpse a blast throws in Musou Mode, engine-free (the owner, 2026-11-08: "completely remove the corkscrewing corpses flying in the air in musou mode. corpses should never fly in
/// a way that is not a natural looking straight line parabolic arc away from the explosion"). The body leaves along <see cref="MusouGoreRules.BlastThrow"/> (away from the blast plus up), falls
/// under <see cref="Gravity"/> and turns about ONE fixed horizontal axis only, square to its flight (it falls over backwards, away from the blast), by at most <see cref="TumbleMaxAngle"/>: no turn
/// about its own long axis, no helix, no spin that grows. Both the crowd's thrown bodies and the ragdolls' flight use it. Every number is ours (spec "Musou: corpse arcs").
/// </summary>
public static class MusouFling
{
    /// <summary>Gravity (m/s2), the fastest a thrown body tips over (rad/s), and the most it tips over in the whole flight (rad: a quarter turn, landing on its back).</summary>
    public const float Gravity = 9.8f, TumbleMaxRate = 2.5f, TumbleMaxAngle = MathF.PI / 2f;

    /// <summary>The seconds a body thrown at <paramref name="velocity"/> flies before it is back at the height it left (at least 0.2 s).</summary>
    public static float FlightSeconds(Vector3 velocity) => MathF.Max(0.2f, 2f * MathF.Max(0f, velocity.Z) / Gravity);

    /// <summary>
    /// The body's one tumble (rad/s, game space, z up): about the horizontal axis up x flight direction, so the head leads away from the blast, at the rate that tips it a quarter turn over its
    /// flight, never above <see cref="TumbleMaxRate"/>. A throw straight up (no flat direction) does not tumble.
    /// </summary>
    public static Vector3 Tumble(Vector3 velocity)
    {
        var flat = new Vector3(velocity.X, velocity.Y, 0f);
        if (flat.LengthSquared() < 1e-4f) return Vector3.Zero;
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, flat));
        return axis * MathF.Min(TumbleMaxRate, TumbleMaxAngle / FlightSeconds(velocity));
    }

    /// <summary>How far the body has tipped (rad) <paramref name="seconds"/> into its flight: the tumble's rate times the time, held at <see cref="TumbleMaxAngle"/>.</summary>
    public static float TumbleAngle(Vector3 tumble, float seconds) => MathF.Min(TumbleMaxAngle, tumble.Length() * MathF.Max(0f, seconds));

    /// <summary>One step of the arc in the air: gravity, then the move. The flat velocity is never changed in the air, so the path's ground track is one straight line.</summary>
    public static void Fly(ref Vector3 position, ref Vector3 velocity, float dt)
    {
        velocity.Z -= Gravity * dt;
        position += velocity * dt;
    }
}
