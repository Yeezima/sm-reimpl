using System.Numerics;

namespace SmSim.Extras.Musou;

public sealed partial class MusouSim
{
    // ---- Expansion S1: the hook an Ork blast uses to set off Bomb Squigs (nothing spawns them until S2; the list stays empty until then).
    readonly List<(ushort Id, int Credit)> _detonations = new();

    /// <summary>
    /// Queues every Bomb Squig within <paramref name="radius"/> of <paramref name="point"/> to detonate on the next tick (one entry per Squig; a chain is the next blast queueing the next ones);
    /// <paramref name="credit"/> is the player who caused it (0: none). Returns how many were queued. S2 reads them with <see cref="TakeDetonations"/>.
    /// </summary>
    public int QueueSquigDetonations(Vector3 point, float radius, int credit, Span<ushort> scratch)
    {
        int n = Query(point, radius, scratch, verticalReach: radius), queued = 0;
        for (int k = 0; k < n; k++)
        {
            int i = scratch[k] - 1;
            if (_type[i] != MusouEnemyType.BombSquig || _state[i] == MusouEnemyState.Dying) continue;
            bool already = false;
            foreach (var d in _detonations) if (d.Id == scratch[k]) { already = true; break; }
            if (already) continue;
            _detonations.Add((scratch[k], credit)); queued++;
        }
        return queued;
    }

    /// <summary>Squig detonations queued and not yet taken.</summary>
    public int PendingDetonations => _detonations.Count;

    /// <summary>The queued Squig detonations (id, credit), moved into <paramref name="into"/>.</summary>
    public void TakeDetonations(List<(ushort Id, int Credit)> into) { into.AddRange(_detonations); _detonations.Clear(); }
}
