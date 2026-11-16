using SmSim.Extras.Warfront;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Warfront's part of the run (slice W3, docs/added-content.md section 11): the node battle's commands, no war clock, no transport or supply cost, the fallen human's spectator time and the wipe rule.
/// Every hook returns at once unless <see cref="Warfront"/> is set (only a run with the Warfront flag).
/// </summary>
public sealed partial class TfwRun
{
    /// <summary>The run's Warfront rules (null: not a Warfront run).</summary>
    public WarfrontBattleRules? Warfront { get; internal set; }

    readonly Dictionary<int, int> _spectating = new();   // a fallen human past the revive window: ticks left of its spectator time
    int _warfrontRate = 30;

    partial void WarfrontReset()
    {
        _spectating.Clear();
        Warfront?.EndBattle();
    }

    partial void WarfrontWarClock(ref bool tick)
    {
        if (Warfront is not null) tick = false;   // the Musou war does not move by itself in Warfront
    }

    partial void WarfrontCommand(World w, int player, TfwCommandRecord c, int rate, ref bool handled)
    {
        if (Warfront is not { } rules) return;
        switch (c.Command)
        {
            case TfwCommand.Deploy:
                handled = true;
                if (player != (rules.Leader?.Invoke() ?? HostOf(w.Players.Keys))) return;   // the leader only
                int transport = Transport;
                Transport = Math.Max(transport, TfwBattleRules.DeployCost);   // no transport cost: the stock is put back whatever the deploy did
                Deploy(w, player, c.Argument, rate);
                Transport = transport;
                break;
            case TfwCommand.RequestPickup or TfwCommand.CancelPickup or TfwCommand.Redeploy or TfwCommand.ReturnToShip:
                handled = true;   // no pickup, no redeploy for supplies, no recall in Warfront
                break;
        }
    }

    partial void WarfrontBattleCreated(World w, TfwBattle battle, TfwLane lane, int node)
    {
        if (Warfront is not { } rules) return;
        battle.WarfrontNode = true;
        rules.Lane = lane;
        rules.Node = node;
        rules.InNode = true;
        _spectating.Clear();
    }

    partial void WarfrontRetainer(int player, ref bool spawn)
    {
        if (Warfront is not null) spawn = false;   // the squad system (W9) brings the retainers
    }

    partial void WarfrontBattleWon(World w, ref bool handled)
    {
        if (Warfront is null) return;
        handled = true;
        Leave(w, left: false, TfwMapResult.Won);   // no victory wave, no pickup: everyone back in the hub the same tick
    }

    partial void WarfrontLeft(World w, TfwMapResult result)
    {
        if (Warfront is not { } rules) return;
        bool was = rules.InNode;
        _spectating.Clear();
        rules.EndBattle();
        if (!was) return;
        rules.NodeEnded?.Invoke(rules.Lane, rules.Node, result);
        rules.NodeResolved?.Invoke(rules.Lane, rules.Node, result == TfwMapResult.Won);
    }

    /// <summary>
    /// The fallen of a node battle (owner's rule, spec "Warfront: the fallen"): downed for the revive window (always open), then a spectator for <see cref="WarfrontBattleRules.SpectatorSeconds"/> from its end, then a fresh life at the
    /// landing zone if a human stands or a retainer stands at that moment. The battle is a wipe the tick no human stands, none is downed with an open window and no retainer stands.
    /// </summary>
    partial void WarfrontDowned(World w, int rate, ref bool handled)
    {
        if (Warfront is not { } rules || Battle is not { } battle) return;
        handled = true;
        _warfrontRate = Math.Max(1, rate);
        int window = Math.Max(1, (int)MathF.Round(WarfrontBattleRules.ReviveWindowSeconds * rate));
        int spectate = Math.Max(1, (int)MathF.Round(WarfrontBattleRules.SpectatorSeconds * rate));
        int standing = 0;
        foreach (int id in _deployed) if (w.Players[id].Alive) standing++;
        bool anyStands = standing > 0 || rules.AnyRetainerStanding();
        bool windowOpen = false;
        foreach (int id in _deployed)
        {
            if (w.Players[id].Alive) { _reviveTicks.Remove(id); _spectating.Remove(id); continue; }
            if (_spectating.TryGetValue(id, out int left))
            {
                if (left > 1) { _spectating[id] = left - 1; continue; }
                if (!anyStands) { _spectating[id] = 1; continue; }   // nobody to come back to yet: the respawn waits (or the battle is already a wipe, below)
                _spectating.Remove(id);
                _reviveTicks.Remove(id);
                var layout = battle.Layout;
                w.TfwRespawnAt(id, LandingAt(w, layout, id, _landingSlots.GetValueOrDefault(id, _deployed.IndexOf(id))), layout.FaceCentre(layout.LandingZone));
                continue;
            }
            if (!_reviveTicks.TryGetValue(id, out int open)) open = window;
            open = Math.Max(0, open - 1);
            _reviveTicks[id] = open;
            if (open > 0) windowOpen = true;
            else _spectating[id] = spectate;   // the window is over: spectator, the 60 s from here
        }
        if (anyStands || windowOpen) return;
        _spectating.Clear();   // a wipe discards every pending respawn
        Leave(w, left: false, TfwMapResult.Lost);
    }

    /// <summary>The revive of <paramref name="target"/> inside its window by a teammate or a retainer (the later slices call it; no input is bound). False outside the window.</summary>
    public bool WarfrontRevive(World w, int target)
    {
        if (Warfront is null || !ReviveOpen(target) || !w.TfwRevive(target)) return false;
        _reviveTicks.Remove(target);
        return true;
    }

    /// <summary>Where <paramref name="player"/> stands as a fallen human: in its revive window, a spectator, or neither (with the seconds left).</summary>
    public WarfrontDown WarfrontDownState(int player)
    {
        if (_spectating.TryGetValue(player, out int spectate)) return new(WarfrontDownPhase.Spectator, spectate / (float)_warfrontRate);
        return ReviveOpen(player) ? new(WarfrontDownPhase.Downed, ReviveTicks(player) / (float)_warfrontRate) : new(WarfrontDownPhase.None, 0f);
    }
}
