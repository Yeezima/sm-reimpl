using System.Numerics;

namespace SmSim.Extras.Musou;

public sealed partial class MusouSim
{
    /// <summary>
    /// musou/boss-collision: the box the server last quantised the entity records over (its send hook sets it every tick; null before the first send). The players' bodies
    /// record of the big Orks (<see cref="MusouSolids"/>) is quantised over it, so it holds the floats a client decodes.
    /// </summary>
    public MusouBox? WireBox { get; set; }

    /// <summary>The solid big Orks in play now (<see cref="MusouSolids.Is"/>, <see cref="MusouSolids.Counts"/>), in id order, at their raw positions, into <paramref name="into"/> (cleared first).</summary>
    public void Solids(List<MusouSolid> into)
    {
        into.Clear();
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i] || !MusouSolids.Is(_type[i]) || !MusouSolids.Counts(_state[i])) continue;
            var p = _position[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
            into.Add(new MusouSolid((ushort)(i + 1), _type[i], p));
        }
    }
}
