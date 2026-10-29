using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// sim/player-collision (client only, **ours**): where the remote players are drawn, moved apart so their capsules do not visibly overlap each other or
/// the own player. Remote players are drawn between snapshots in the past (interpolation) and the own player in the present (prediction), so two
/// bodies the simulation keeps apart can still be drawn inside each other (a remote runner beside the own player, two remotes crossing). Each pair of
/// living bodies whose heights overlap (<see cref="PlayerCollision.Relate"/>'s Side) and whose axes are closer than two capsule radii is pushed apart
/// along the line between the axes (a tie: <see cref="PlayerCollision.TieDirection"/>): half the depth each, all of it on the remote when the other is
/// the own player (never moved: it is the prediction), <see cref="Passes"/> passes in id order, each body's shift at most <see cref="MaxShift"/>.
/// Positions only (drawing; the simulation and hit tests are untouched).
/// </summary>
public static class DrawnBodies
{
    /// <summary>Passes over the pairs (ours).</summary>
    public const int Passes = 3;
    /// <summary>Most a drawn body is moved from its interpolated place (metres; ours: a capsule radius).</summary>
    public const float MaxShift = 0.45f;

    /// <summary>
    /// <paramref name="remotes"/> with the living ones moved apart from each other and from <paramref name="own"/> (its feet; null: no own body, e.g. dead).
    /// Returns the same list when nothing overlaps.
    /// </summary>
    public static IReadOnlyList<PlayerState> Separate(IReadOnlyList<PlayerState> remotes, Vector3? own, int ownId, SimConfig c)
    {
        if (!c.PlayerBodies || remotes.Count == 0) return remotes;
        float r = MathF.Max(c.CapsuleRadius, 0.05f), reach = 2f * r, height = MathF.Max(c.CapsuleHeight, 2f * r);
        int n = remotes.Count;
        var order = Enumerable.Range(0, n).Where(i => remotes[i].Alive).OrderBy(i => remotes[i].PlayerId).ToArray();
        if (order.Length == 0 || (order.Length == 1 && own is null)) return remotes;
        var origin = new Vector2[n];
        var at = new Vector2[n];
        for (int i = 0; i < n; i++) at[i] = origin[i] = new Vector2(remotes[i].Position.X, remotes[i].Position.Y);
        bool moved = false;
        for (int pass = 0; pass < Passes; pass++)
        {
            for (int a = 0; a < order.Length; a++)
            {
                int i = order[a];
                var zi = remotes[i].Position.Z;
                if (own is { } o && PlayerCollision.Relate(zi, o.Z, height) == PlayerCollision.Relation.Side)
                    moved |= Apart(ref at[i], origin[i], new Vector2(o.X, o.Y), remotes[i].PlayerId, ownId, reach, whole: true);
                for (int b = a + 1; b < order.Length; b++)
                {
                    int j = order[b];
                    if (PlayerCollision.Relate(zi, remotes[j].Position.Z, height) != PlayerCollision.Relation.Side) continue;
                    var offset = at[i] - at[j];
                    float distance = offset.Length(), depth = reach - distance;
                    if (depth <= PlayerCollision.Tolerance) continue;
                    var dir = distance > 1e-4f ? offset / distance : PlayerCollision.TieDirection(remotes[i].PlayerId, remotes[j].PlayerId);
                    at[i] = Clamp(at[i] + dir * (depth / 2f), origin[i]);
                    at[j] = Clamp(at[j] - dir * (depth / 2f), origin[j]);
                    moved = true;
                }
            }
        }
        if (!moved) return remotes;
        var result = new PlayerState[n];
        for (int i = 0; i < n; i++)
            result[i] = at[i] == origin[i] ? remotes[i] : remotes[i] with { Position = new Vector3(at[i].X, at[i].Y, remotes[i].Position.Z) };
        return result;
    }

    /// <summary>Moves <paramref name="p"/> out of the circle around <paramref name="other"/> (all of the depth), within <see cref="MaxShift"/> of its origin.</summary>
    static bool Apart(ref Vector2 p, Vector2 origin, Vector2 other, int id, int otherId, float reach, bool whole)
    {
        var offset = p - other;
        float distance = offset.Length(), depth = reach - distance;
        if (depth <= PlayerCollision.Tolerance) return false;
        var dir = distance > 1e-4f ? offset / distance : PlayerCollision.TieDirection(id, otherId);
        p = Clamp(p + dir * (whole ? depth : depth / 2f), origin);
        return true;
    }

    static Vector2 Clamp(Vector2 p, Vector2 origin)
    {
        var shift = p - origin;
        float length = shift.Length();
        return length > MaxShift ? origin + shift * (MaxShift / length) : p;
    }
}
