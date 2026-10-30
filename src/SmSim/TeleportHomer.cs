using System.Numerics;

namespace SmSim;

/// <summary>
/// Teleport Homer (perk <c>mp_perk_friendly_spawn_target</c>; owner's decision of 2026-10-30, from his knowledge of the original): the carrier is a spawn
/// point. While a carrier is alive, a respawning ally of its side appears next to it, instead of in the spawn room or at a map spawn. Server only; no wire change
/// of its own. Rules (the choices are ours, tagged estimates in spec/behavior.md "Teleport Homer"):
/// several carriers: the one nearest to where the respawning player died; the place: the first free spot of a ring around the carrier, starting behind it and
/// moving outward (1.5 m, 2.0 m, 2.5 m, 3.0 m; 12 directions), which must hold a capsule, be on the carrier's floor and in its sight; none free: the normal spawn.
/// Only death respawns use it (a join and a round start use the side's spawn as ever).
/// </summary>
public sealed partial class World
{
    /// <summary>Radii of the spawn ring around a Teleport Homer carrier (metres; ours).</summary>
    static readonly float[] HomerRadii = [1.5f, 2.0f, 2.5f, 3.0f];
    const int HomerDirections = 12;

    /// <summary>
    /// A spawn next to a living carrier of Teleport Homer on <paramref name="dead"/>'s side (see the class summary), or false when there is none (no carrier alive,
    /// no teams, or no free spot around any carrier).
    /// </summary>
    bool TryHomerSpawn(PlayerState dead, out Vector3 position, out float yaw)
    {
        position = default; yaw = 0f;
        if (Rules is null || dead.Team is not (Team.SpaceMarines or Team.Chaos)) return false;
        var carriers = _players.Values
            .Where(p => p.Alive && p.PlayerId != dead.PlayerId && p.Team == dead.Team && Loadouts.HasPerk(Config, p, Loadouts.TeleportHomerPerk))
            .OrderBy(p => Vector3.DistanceSquared(p.Position, dead.Position)).ThenBy(p => p.PlayerId);
        foreach (var carrier in carriers)
            if (HomerSpotNear(carrier, dead.PlayerId) is { } spot) { position = spot; yaw = carrier.Yaw; return true; }
        return false;
    }

    /// <summary>The first free spot of the carrier's ring (see the class summary), or null.</summary>
    Vector3? HomerSpotNear(PlayerState carrier, int self)
    {
        float gap = 2f * MathF.Max(Config.CapsuleRadius, 0.05f) + 0.1f;
        // Direction 0 is straight behind the carrier (yaw 0 faces +Y, as in movement: forward is (-sin, cos)).
        float behind = carrier.Yaw + MathF.PI;
        foreach (float radius in HomerRadii)
            for (int k = 0; k < HomerDirections; k++)
            {
                // Alternate sides of the back direction: 0, +30, -30, +60, -60, ...
                int step = (k + 1) / 2 * (k % 2 == 1 ? 1 : -1);
                float angle = behind + step * (MathF.Tau / HomerDirections);
                var at = carrier.Position + new Vector3(-MathF.Sin(angle) * radius, MathF.Cos(angle) * radius, 0f);
                if (Collision is not null)
                {
                    if (LowestStandable(at.X, at.Y, carrier.Position.Z + 0.6f, carrier.Position.Z - 0.6f) is not { } floor) continue;
                    at = floor;
                    var chest = new Vector3(0f, 0f, Config.CapsuleHeight * 0.5f);
                    var from = carrier.Position + chest;
                    var to = at + chest;
                    float distance = Vector3.Distance(from, to);
                    if (distance > 1e-3f && Collision.Character.Raycast(from, (to - from) / distance, distance) is not null) continue;   // behind a wall or a prop
                    if (Collision.KillsPlayer(at, at, Config.CapsuleHeight)) continue;
                    // The capsule must fit: nothing solid across its axis from the feet to the head.
                    if (Collision.Character.Raycast(at + new Vector3(0f, 0f, 0.05f), Vector3.UnitZ, Config.CapsuleHeight - 0.1f) is not null) continue;
                }
                bool crowded = false;
                foreach (var (id, p) in _players)
                    if (id != self && p.Alive && Vector3.DistanceSquared(new Vector3(p.Position.X, p.Position.Y, 0f), new Vector3(at.X, at.Y, 0f)) < gap * gap
                        && MathF.Abs(p.Position.Z - at.Z) < Config.CapsuleHeight) { crowded = true; break; }
                if (!crowded) return at;
            }
        return null;
    }
}
