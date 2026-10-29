using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// sim/player-collision (client only, **ours**, visual only): where the remote players are drawn, moved apart so their capsules do not visibly overlap
/// each other or the own player. Remote players are drawn between snapshots in the past (interpolation) and the own player in the present
/// (prediction), so two bodies the simulation keeps apart can still be drawn inside each other (a remote runner beside the own player, two remotes
/// crossing). Each pair of living bodies whose heights overlap (<see cref="PlayerCollision.Relate"/>'s Side) and whose axes are closer than two capsule
/// radii is pushed apart along the line between the axes (a tie: <see cref="PlayerCollision.TieDirection"/>): half the depth each, all of it on the
/// remote when the other is the own player (never moved: it is the prediction), <see cref="Passes"/> passes in id order, each body's shift at most
/// <see cref="MaxShift"/>. Positions of the drawing only: the simulation, the server's hit tests and the shots are untouched.
/// One instance per view: its buffers are reused from frame to frame (no allocation once they have grown), and the list it returns is valid until the
/// next call.
/// </summary>
public sealed class DrawnBodies
{
    /// <summary>Passes over the pairs (ours).</summary>
    public const int Passes = 3;
    /// <summary>Most a drawn body is moved from its interpolated place (metres; ours: a capsule radius).</summary>
    public const float MaxShift = 0.45f;

    int[] _order = new int[16];
    Vector2[] _origin = new Vector2[16], _at = new Vector2[16];
    readonly List<PlayerState> _result = new(16);

    /// <summary>
    /// <paramref name="remotes"/> with the living ones moved apart from each other and from <paramref name="own"/> (its feet; null: no own body, e.g. dead).
    /// Returns <paramref name="remotes"/> itself when nothing overlaps, else a list this instance reuses on the next call.
    /// </summary>
    public IReadOnlyList<PlayerState> Separate(IReadOnlyList<PlayerState> remotes, Vector3? own, int ownId, SimConfig c)
    {
        if (!c.PlayerBodies || remotes.Count == 0) return remotes;
        float r = MathF.Max(c.CapsuleRadius, 0.05f), reach = 2f * r, height = MathF.Max(c.CapsuleHeight, 2f * r);
        int n = remotes.Count;
        if (_order.Length < n) { _order = new int[n]; _origin = new Vector2[n]; _at = new Vector2[n]; }
        int living = 0;
        for (int i = 0; i < n; i++)
        {
            _at[i] = _origin[i] = new Vector2(remotes[i].Position.X, remotes[i].Position.Y);
            if (!remotes[i].Alive) continue;
            // Insertion into id order (a handful of players: no sort allocation).
            int k = living++;
            while (k > 0 && remotes[_order[k - 1]].PlayerId > remotes[i].PlayerId) { _order[k] = _order[k - 1]; k--; }
            _order[k] = i;
        }
        if (living == 0 || (living == 1 && own is null)) return remotes;
        bool moved = false;
        for (int pass = 0; pass < Passes; pass++)
            for (int a = 0; a < living; a++)
            {
                int i = _order[a];
                float zi = remotes[i].Position.Z;
                if (own is { } o && PlayerCollision.Relate(zi, o.Z, height) == PlayerCollision.Relation.Side)
                    moved |= Apart(ref _at[i], _origin[i], new Vector2(o.X, o.Y), remotes[i].PlayerId, ownId, reach);
                for (int b = a + 1; b < living; b++)
                {
                    int j = _order[b];
                    if (PlayerCollision.Relate(zi, remotes[j].Position.Z, height) != PlayerCollision.Relation.Side) continue;
                    var offset = _at[i] - _at[j];
                    float distance = offset.Length(), depth = reach - distance;
                    if (depth <= PlayerCollision.Tolerance) continue;
                    var dir = distance > 1e-4f ? offset / distance : PlayerCollision.TieDirection(remotes[i].PlayerId, remotes[j].PlayerId);
                    _at[i] = Clamp(_at[i] + dir * (depth / 2f), _origin[i]);
                    _at[j] = Clamp(_at[j] - dir * (depth / 2f), _origin[j]);
                    moved = true;
                }
            }
        if (!moved) return remotes;
        _result.Clear();
        for (int i = 0; i < n; i++)
            _result.Add(_at[i] == _origin[i] ? remotes[i] : remotes[i] with { Position = new Vector3(_at[i].X, _at[i].Y, remotes[i].Position.Z) });
        return _result;
    }

    /// <summary>Moves <paramref name="p"/> all the way out of the circle around <paramref name="other"/> (the own player), within <see cref="MaxShift"/> of its origin.</summary>
    static bool Apart(ref Vector2 p, Vector2 origin, Vector2 other, int id, int otherId, float reach)
    {
        var offset = p - other;
        float distance = offset.Length(), depth = reach - distance;
        if (depth <= PlayerCollision.Tolerance) return false;
        var dir = distance > 1e-4f ? offset / distance : PlayerCollision.TieDirection(id, otherId);
        p = Clamp(p + dir * depth, origin);
        return true;
    }

    static Vector2 Clamp(Vector2 p, Vector2 origin)
    {
        var shift = p - origin;
        float length = shift.Length();
        return length > MaxShift ? origin + shift * (MaxShift / length) : p;
    }
}
