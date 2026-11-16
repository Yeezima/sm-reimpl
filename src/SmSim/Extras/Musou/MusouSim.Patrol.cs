using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The moving homes of Musou Mode's patrols and hunter columns (the owner, 2026-11-06: "patrols that both patrol on set routes and also hunt down the players no matter where they are"; <c>Campaign/TfwBattle.Patrols.cs</c>
/// moves them; every value ours). A leashed Ork whose home moves (<see cref="MoveHome"/>) follows it at the home's own velocity plus a pull towards it, so a marching column walks instead of running and stopping; one with a
/// still home (every other leashed Ork: garrisons, crowds, musters) goes back to it as before. Nothing here runs unless a battle moves a home, so the 5-wave run never sees it.
/// </summary>
public sealed partial class MusouSim
{
    /// <summary>How hard a marching Ork closes on its moving home: metres a second per metre behind (ours).</summary>
    public const float HomePull = 1.5f;

    readonly Vector2[] _homeVelocity = new Vector2[Capacity];   // its home's velocity (zero: a still home)

    /// <summary>
    /// Moves leashed enemy <paramref name="id"/>'s home to <paramref name="home"/>, moving at <paramref name="velocity"/> (m/s, flat): it then wakes by the new home (<see cref="SpawnLeashed"/>'s aggro) and walks after it.
    /// False for an unknown, dying or unleashed one.
    /// </summary>
    public bool MoveHome(ushort id, Vector3 home, Vector2 velocity)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _group[i] == 0 || _state[i] == MusouEnemyState.Dying || !float.IsFinite(home.X) || !float.IsFinite(home.Y) || !float.IsFinite(home.Z)) return false;
        _home[i] = home;
        _homeVelocity[i] = float.IsFinite(velocity.X) && float.IsFinite(velocity.Y) ? velocity : Vector2.Zero;
        return true;
    }

    /// <summary>The home of leashed enemy <paramref name="id"/> (null: unknown or unleashed).</summary>
    public Vector3? HomeOf(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _group[id - 1] != 0 ? _home[id - 1] : null;

    /// <summary>Frees one leashed enemy alone, for good and in no pack (a straggler a patrol left behind: it hunts on its own). False for an unknown or unleashed one.</summary>
    public bool Unleash(ushort id)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _group[i] == 0) return false;
        _group[i] = 0; _aggro[i] = float.PositiveInfinity; _pack[i] = 0; _homeVelocity[i] = Vector2.Zero;
        return true;
    }

    /// <summary>A marching Ork's way: its home's velocity and a pull towards the home, at most its run speed.</summary>
    Vector2 MarchWay(int i, MusouTypeSpec spec)
    {
        var way = _homeVelocity[i] + new Vector2(_home[i].X - _position[i].X, _home[i].Y - _position[i].Y) * HomePull;
        float length = way.Length();
        return length > spec.Speed ? way / length * spec.Speed : length < 0.05f ? Vector2.Zero : way;
    }
}
