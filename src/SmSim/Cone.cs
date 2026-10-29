using System.Numerics;

namespace SmSim;

/// <summary>
/// Cone shots (the melta's shapecast; rules **interpretation**, spec/behavior.md).
/// <para>
/// Geometry: from the eye point along the aim (as hit-scan rays), a cone whose apex is <see cref="ConeSpec.StartLength"/>
/// behind the eye, with half angle <see cref="ConeSpec.HalfAngle"/> (half the file's angle), cut to the part from the eye to
/// <see cref="ConeSpec.Length"/> ahead of it.
/// </para>
/// <para>
/// Targets: every other living player, as the shooter saw them (the same rewind as rays), whose hit shapes the cone covers:
/// coverage is the fraction of <see cref="CoverageSamples"/> fixed sample points of the head sphere and body capsule inside
/// the cone (ours: the original's coverage measure is not known); 0 is no hit. With a level, the line from the eye to the
/// target's chest must be clear of weapon collision. Each target takes the cone's damage (health and shield parts) on the
/// body, scaled by the distance fall-off (eye to chest; linear between points, as blasts) times the coverage multiplier
/// (linear between points, clamped to the end values).
/// </para>
/// </summary>
public static class Cones
{
    /// <summary>Unit sample points of the hit shapes: the head centre, then rings on the body capsule axis (ours).</summary>
    public const int CoverageSamples = 1 + 5 * 5;

    /// <summary>
    /// The sample points of a player's hit shapes standing at <paramref name="feet"/>, facing <paramref name="yaw"/>. With
    /// <see cref="SimConfig.HitVolumes"/> (M20): each volume's centre and each capsule's ends (<see cref="HitVolumes.SamplePoints"/>;
    /// ours); with the placeholders, the <see cref="CoverageSamples"/> points below.
    /// </summary>
    public static IEnumerable<Vector3> Samples(Vector3 feet, SimConfig c, float yaw = 0f)
    {
        if (c.HitVolumes is { } volumes)
        {
            foreach (var p in HitVolumes.SamplePoints(volumes)) yield return HitVolumes.ToWorld(p, feet, yaw);
            yield break;
        }
        yield return feet + new Vector3(0, 0, c.HeadHeight);
        float low = c.BodyBottom + c.BodyRadius, high = MathF.Max(low + 1e-3f, c.BodyTop - c.BodyRadius);
        float r = c.BodyRadius * 0.7f;
        for (int k = 0; k < 5; k++)
        {
            var axis = feet + new Vector3(0, 0, low + (high - low) * k / 4f);
            yield return axis;
            yield return axis + new Vector3(r, 0, 0);
            yield return axis + new Vector3(-r, 0, 0);
            yield return axis + new Vector3(0, r, 0);
            yield return axis + new Vector3(0, -r, 0);
        }
    }

    /// <summary>Whether a point is inside the cone fired from <paramref name="origin"/> along unit <paramref name="direction"/>.</summary>
    public static bool Inside(ConeSpec cone, Vector3 origin, Vector3 direction, Vector3 point)
    {
        var offset = point - origin;
        float along = Vector3.Dot(offset, direction);
        if (along < 0f || along > cone.Length) return false;
        float radial = (offset - direction * along).Length();
        return radial <= (along + cone.StartLength) * MathF.Tan(cone.HalfAngle);
    }

    /// <summary>The fraction of a player's sample points inside the cone (0 to 1).</summary>
    public static float Coverage(ConeSpec cone, Vector3 origin, Vector3 direction, Vector3 feet, SimConfig c, float yaw = 0f)
    {
        int inside = 0, total = 0;
        foreach (var p in Samples(feet, c, yaw)) { total++; if (Inside(cone, origin, direction, p)) inside++; }
        return total == 0 ? 0f : (float)inside / total;
    }

    /// <summary>The damage multiplier for a target at <paramref name="distance"/> with <paramref name="coverage"/> (0 when not covered).</summary>
    public static float Multiplier(ConeSpec cone, float distance, float coverage) =>
        coverage <= 0f ? 0f
        : Combat.SplashIntensity(cone.FallOff, distance) * (cone.CoverageFallOff.Count == 0 ? 1f : Combat.PointValue(cone.CoverageFallOff, coverage));
}

public sealed partial class World
{
    /// <summary>A cone shot's hits (see <see cref="Cones"/>), in player order.</summary>
    /// <remarks>
    /// Returns where the shot went (M25a, for <see cref="ShotEvent"/>): the end is the first weapon-collision surface along the
    /// shot direction within the cone's length, else the point at its length (interpretation: the melta's fire effect is drawn
    /// from the muzzle to the hit point); the hit is the nearest player it damaged (Body), else that surface (World), else none.
    /// </remarks>
    ShotOutcome ResolveCone(int shooter, PlayerInput input, PlayerState state, ConeSpec cone)
    {
        var (seen, flight) = SeenAt(input);   // fix/hitboxes: between ticks, as the client drew them, and the jump poses shown
        var origin = Combat.ShotOrigin(state, input, Config, Collision);
        var direction = Combat.ShotDirection(input, Config, state);   // weapons/spread: the scatter turns the cone's axis
        var outcome = WorldOutcome(origin, direction, cone.Length, Collision?.Weapons.Raycast(origin, direction, cone.Length));
        float nearest = float.MaxValue;
        foreach (var (id, then) in seen.OrderBy(kv => kv.Key).ToList())
        {
            if (id == shooter || !then.Alive || !_players.TryGetValue(id, out var now) || !now.Alive) continue;
            float coverage = Cones.Coverage(cone, origin, direction, then.Position, BodyOf(id, then, flight), then.Yaw);   // Ork O4: the target's own volumes; fix/hitboxes: as drawn in a jump
            if (coverage <= 0f) continue;
            var centre = then.Position + new Vector3(0, 0, ChestOf(then));
            if (Blocked(origin, centre)) continue;
            float distance = Vector3.Distance(origin, centre);
            float multiplier = Cones.Multiplier(cone, distance, coverage);
            if (multiplier <= 0f) continue;
            bool armoured = now.Shield > 0f;   // before this hit (hit feedback, protocol 22)
            ApplyHit(shooter, id, cone.Damage, HitZone.Body, distance, multiplier, 0, false, cone: true, weapon: Loadouts.WeaponIndex(Config, state, state.Slot),
                direction: centre - origin);
            if (distance < nearest && Harms(shooter, id)) { nearest = distance; outcome = outcome with { Hit = ShotHit.Body, Surface = -1, Target = id, Armoured = armoured }; }
        }
        return outcome;
    }
}
