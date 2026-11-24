using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmSim;

/// <summary>
/// Warfront's walk-over pickups in the World (owner 2026-11-25; <see cref="WarfrontPickupRules"/>). From Musou's drop step, before the presses: each living human (never an AI Marine or a retainer) takes
/// every own live drop that is not a weapon (a powerup starts its buff) and the ammunition and equipment boxes that lie under his feet. A crate is walked over only when taking it fills a free slot or refills a
/// weapon already owned (the lead's decision, 2026-11-25, after the owner's "weapons stay on E": never a swap by accident); a crate that would replace a weapon stays and needs E. A box that would add nothing stays
/// on the floor, as does a crate that cannot be given now; both are taken on a later tick when they can be. Each returns at once unless the run's Warfront object is set, so Musou Mode and Classic keep the press and the hold.
/// </summary>
public sealed partial class World
{
    readonly List<int> _walkOverIds = new();
    readonly List<(float D2, int Player, int Index)> _walkOverClaims = new();
    readonly HashSet<int> _walkOverServed = new();
    readonly List<int> _walkOverTaken = new();

    partial void WarfrontWalksOver(in MusouDrop drop, ref bool walks)
    {
        if (_tfw?.Warfront is not null && WalksOverNow(drop)) walks = true;
    }

    /// <summary>Whether <paramref name="drop"/> goes by walking over it now: a powerup always, a crate unless it would replace a weapon of its player's.</summary>
    bool WalksOverNow(in MusouDrop drop) => WarfrontPickupRules.WalksOver(drop) && !(drop.Crate && CrateReplaces(drop.Player, drop.Item));

    /// <summary>
    /// Whether taking a crate of <paramref name="item"/> would replace something of player <paramref name="id"/>'s (the rule of <c>TfwTake</c>, read without taking): a melee item other than the kit's melee, or a
    /// weapon not owned that finds no free extra slot (<see cref="TfwRewards.SlotFor"/> then replaces the extra in hand or the last one). A weapon owned refills and a free slot fills: neither replaces.
    /// </summary>
    bool CrateReplaces(int id, ushort item)
    {
        if (_tfw is null || !_players.TryGetValue(id, out var p)) return true;
        var kit = _tfw.Rewards.Kit(id, p.Class);
        if (MusouDropItem.IsMelee(item)) return kit.Melee != MusouDropItem.Index(item);
        int weapon = MusouDropItem.Index(item);
        var a = ArsenalOf(id);
        int second = Loadouts.SecondIndex(Config, p);
        if (weapon == a.HomePrimary || weapon == a.HomePistol || weapon == second || kit.Holds(weapon)) return false;
        int slot = TfwRewards.SlotFor(kit, second >= 0 ? 1 : 0, ExtraInHand(a, p), a.LastInHand);
        return kit.Weapons[slot] >= 0;
    }

    partial void WarfrontWalkOver()
    {
        if (_tfw?.Warfront is null) return;
        _walkOverIds.Clear();
        foreach (var (id, p) in _players) if (p.Alive && !TfwContracts.IsPuppet(id)) _walkOverIds.Add(id);   // the AI Marines (the retainers among them) pick nothing up
        if (_walkOverIds.Count == 0) return;
        _walkOverIds.Sort();
        if (_state == MatchState.Playing && _musouDrops.Count > 0) WalkOverDrops();
        if (Config.Pickups is { } spec && _pickups.Count > 0 && PickupsActive) WalkOverBoxes(spec);
    }

    /// <summary>Each walker's own live drops that go by walking and lie under his feet: taken (the drop ends as picked), or left lying when they cannot be given now.</summary>
    void WalkOverDrops()
    {
        foreach (int id in _walkOverIds)
        {
            for (int i = 0; i < _musouDrops.Count; i++)
            {
                if (!_players.TryGetValue(id, out var p) || !p.Alive) break;
                var d = _musouDrops[i];
                if (d.Player != id || d.State != MusouDropState.Live || !WarfrontPickupRules.Over(d.Position, p.Position) || !WalksOverNow(d)) continue;
                bool given = MusouPowerupRules.IsPowerup(d.Item) ? MusouGiveBuff(id, MusouPowerupRules.KindOf(d.Item)) : MusouGiveItem(id, d.Item);
                if (!given) continue;   // stays on the floor (the press path's rule: a drop that cannot be given now stays)
                MusouEndDrop(i, MusouDropState.Picked);
                _musouHolds.Remove(id);
            }
        }
    }

    /// <summary>
    /// The boxes under the walkers' feet, granted as the core grants a press's claims (<c>StepPickups</c>): nearest first (ties: the lower player id, then the lower box id), one box per player a tick and one
    /// player per box, but only a claim whose box adds something (<see cref="Pickups.Collect"/>); a box that adds nothing to its claimant stays for the next claim or a later tick. The event is logged as a press's.
    /// </summary>
    void WalkOverBoxes(PickupSpec spec)
    {
        _walkOverClaims.Clear();
        foreach (int id in _walkOverIds)
        {
            var p = _players[id];
            for (int i = 0; i < _pickups.Count; i++)
            {
                var q = _pickups[i];
                if (q.Type >= 0 && q.Type < spec.Types.Count && WarfrontPickupRules.Over(q.Position, p.Position)) _walkOverClaims.Add((Vector3.DistanceSquared(q.Position, p.Position), id, i));
            }
        }
        if (_walkOverClaims.Count == 0) return;
        _walkOverClaims.Sort((a, b) => a.D2 != b.D2 ? a.D2.CompareTo(b.D2) : a.Player != b.Player ? a.Player.CompareTo(b.Player) : _pickups[a.Index].Id.CompareTo(_pickups[b.Index].Id));
        _walkOverServed.Clear();
        _walkOverTaken.Clear();
        foreach (var (_, id, index) in _walkOverClaims)
        {
            if (_walkOverServed.Contains(id) || _walkOverTaken.Contains(index)) continue;
            var q = _pickups[index];
            var (after, gain) = Pickups.Collect(Config, _players[id], spec.Types[q.Type]);
            if (!gain.Any) continue;   // nothing to add (full): it stays
            _players[id] = after;
            AddPickupEvent(new PickupEvent(Tick, q.Id, q.Type, q.Position, PickupEnd.Collected, id, gain));
            _walkOverServed.Add(id);
            _walkOverTaken.Add(index);
        }
        _walkOverTaken.Sort();
        for (int k = _walkOverTaken.Count - 1; k >= 0; k--) _pickups.RemoveAt(_walkOverTaken[k]);
    }
}
