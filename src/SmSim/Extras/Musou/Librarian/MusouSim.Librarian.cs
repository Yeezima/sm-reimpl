using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>The Librarian's part of the enemy pool (musou-balance 2026-11-11): every slot's place and whether it is alive, for the World's short history the chain lightning is picked on.</summary>
public sealed partial class MusouSim
{
    /// <summary>
    /// Every slot's feet into <paramref name="positions"/>, its type into <paramref name="types"/> and whether it is alive and in play (used, not dying, health above 0) into <paramref name="alive"/>
    /// (each at least <see cref="Capacity"/> long). No allocation.
    /// </summary>
    internal void CopyPlaces(Span<Vector3> positions, Span<MusouEnemyType> types, Span<bool> alive)
    {
        for (int i = 0; i < Capacity; i++)
        {
            bool live = _used[i] && _state[i] != MusouEnemyState.Dying && _health[i] > 0f;
            alive[i] = live;
            if (!live) continue;
            positions[i] = _position[i];
            types[i] = _type[i];
        }
    }

    /// <summary>Whether the enemy <paramref name="id"/> is alive and in play now (used, not dying, health above 0).</summary>
    internal bool AliveNow(ushort id)
    {
        int i = id - 1;
        return i >= 0 && i < Capacity && _used[i] && _state[i] != MusouEnemyState.Dying && _health[i] > 0f;
    }

    /// <summary>The feet of slot <paramref name="id"/> now (only meaningful while <see cref="AliveNow"/>).</summary>
    internal Vector3 FeetNow(ushort id) => _position[id - 1];

    /// <summary>The type of slot <paramref name="id"/> now (only meaningful while <see cref="AliveNow"/>).</summary>
    internal MusouEnemyType TypeNow(ushort id) => _type[id - 1];
}
