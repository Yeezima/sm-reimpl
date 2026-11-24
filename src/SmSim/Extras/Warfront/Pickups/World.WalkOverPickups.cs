using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmSim;

/// <summary>
/// Warfront's walk-over pickups in the World (owner 2026-11-25; <see cref="WarfrontPickupRules"/>). From Musou's drop step, before the presses: each living human (never an AI Marine or a retainer) takes
/// every own live drop that is not a weapon (a powerup starts its buff, a crate gives its weapon) and the ammunition and equipment boxes that lie under his feet. A box that would add nothing stays on the floor,
/// as does a crate that cannot be given now; both are taken on a later tick when they can be. Each returns at once unless the run's Warfront object is set, so Musou Mode and Classic keep the press and the hold.
/// </summary>
public sealed partial class World
{
    readonly List<int> _walkOverIds = new();
    readonly List<(float D2, int Index)> _walkOverBoxes = new();
    readonly List<int> _walkOverTaken = new();

    partial void WarfrontWalksOver(in MusouDrop drop, ref bool walks)
    {
        if (_tfw?.Warfront is not null && WarfrontPickupRules.WalksOver(drop)) walks = true;
    }

    partial void WarfrontWalkOver()
    {
        if (_tfw?.Warfront is null) return;
        _walkOverIds.Clear();
        foreach (var (id, p) in _players) if (p.Alive && !TfwContracts.IsPuppet(id)) _walkOverIds.Add(id);   // the AI Marines (the retainers among them) pick nothing up
        if (_walkOverIds.Count == 0) return;
        _walkOverIds.Sort();   // contested boxes: the lower id first (ours, as the core's tie rule)
        if (_state == MatchState.Playing && _musouDrops.Count > 0) WalkOverDrops();
        if (Config.Pickups is { } spec && _pickups.Count > 0 && PickupsActive) WalkOverBoxes(spec);
    }

    /// <summary>Each walker's own live drops that are walked over and lie under his feet: taken (the drop ends as picked), or left lying when they cannot be given now.</summary>
    void WalkOverDrops()
    {
        foreach (int id in _walkOverIds)
        {
            for (int i = 0; i < _musouDrops.Count; i++)
            {
                if (!_players.TryGetValue(id, out var p) || !p.Alive) break;
                var d = _musouDrops[i];
                if (d.Player != id || d.State != MusouDropState.Live || !WarfrontPickupRules.WalksOver(d) || !WarfrontPickupRules.Over(d.Position, p.Position)) continue;
                bool given = MusouPowerupRules.IsPowerup(d.Item) ? MusouGiveBuff(id, MusouPowerupRules.KindOf(d.Item)) : MusouGiveItem(id, d.Item);
                if (!given) continue;   // stays on the floor (the press path's rule: a drop that cannot be given now stays)
                MusouEndDrop(i, MusouDropState.Picked);
                _musouHolds.Remove(id);
            }
        }
    }

    /// <summary>Each walker's boxes under his feet, nearest first: one that adds something is collected by the core's own rule (<see cref="Pickups.Collect"/>), the event logged as a press's would be.</summary>
    void WalkOverBoxes(PickupSpec spec)
    {
        foreach (int id in _walkOverIds)
        {
            if (_pickups.Count == 0) return;
            if (!_players.TryGetValue(id, out var p) || !p.Alive) continue;
            _walkOverBoxes.Clear();
            for (int i = 0; i < _pickups.Count; i++)
            {
                var q = _pickups[i];
                if (q.Type >= 0 && q.Type < spec.Types.Count && WarfrontPickupRules.Over(q.Position, p.Position)) _walkOverBoxes.Add((Vector3.DistanceSquared(q.Position, p.Position), i));
            }
            if (_walkOverBoxes.Count == 0) continue;
            _walkOverBoxes.Sort((a, b) => a.D2 != b.D2 ? a.D2.CompareTo(b.D2) : _pickups[a.Index].Id.CompareTo(_pickups[b.Index].Id));
            var taken = _walkOverTaken;
            taken.Clear();
            foreach (var (_, index) in _walkOverBoxes)
            {
                var q = _pickups[index];
                var (after, gain) = Pickups.Collect(Config, _players[id], spec.Types[q.Type]);
                if (!gain.Any) continue;   // nothing to add (full): it stays for later or for another player
                _players[id] = after;
                AddPickupEvent(new PickupEvent(Tick, q.Id, q.Type, q.Position, PickupEnd.Collected, id, gain));
                taken.Add(index);
            }
            taken.Sort();
            for (int k = taken.Count - 1; k >= 0; k--) _pickups.RemoveAt(taken[k]);
        }
    }
}
