using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// What the battle flow's armies (expansion S6, <c>Campaign/TfwBattle.Armies.cs</c>) ask of the pool: scans without the nearest-first order of <see cref="Query"/>, so a 100 m look costs one pass. Nothing here
/// changes an enemy; nothing calls it outside a war's battle.
/// </summary>
public sealed partial class MusouSim
{
    /// <summary>Whether any living enemy of a kind <paramref name="kind"/> accepts stands within <paramref name="radius"/> of <paramref name="centre"/> (flat; any height).</summary>
    public bool AnyWithin(Vector3 centre, float radius, Func<MusouEnemyType, bool> kind)
    {
        float r2 = radius * radius;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i] || _state[i] == MusouEnemyState.Dying || !kind(_type[i])) continue;
            float dx = _position[i].X - centre.X, dy = _position[i].Y - centre.Y;
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    /// <summary>Living enemies within <paramref name="radius"/> of <paramref name="centre"/> (flat, within <paramref name="verticalReach"/> in height).</summary>
    public int CountWithin(Vector3 centre, float radius, float verticalReach = 2.5f)
    {
        int n = 0;
        float r2 = radius * radius;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i] || _state[i] == MusouEnemyState.Dying) continue;
            var d = _position[i] - centre;
            if (MathF.Abs(d.Z) <= verticalReach && d.X * d.X + d.Y * d.Y <= r2) n++;
        }
        return n;
    }
}
