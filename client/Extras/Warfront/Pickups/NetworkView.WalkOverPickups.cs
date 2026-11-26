using System.Collections.Generic;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront;

namespace SmClient;

/// <summary>
/// Warfront's walk-over pickups on the client (owner 2026-11-25; <see cref="WarfrontPickupRules"/>): a powerup or an ammunition box shows no press or hold prompt, since the server takes it when the player walks
/// over it. A crate keeps its E prompt (the server walks one over only when it replaces nothing, which the client does not know; one that would replace a weapon needs E), as does a weapon drop. The take sound
/// plays when the server's list (a drop) or its pickup event (a box) says it was taken; a drop taken the tick it lay was never drawn, so it sounds here once per id. Each body returns at once unless the match
/// has the Warfront flag.
/// </summary>
public partial class NetworkView
{
    readonly HashSet<ushort> _wfDropsHeard = new();
    object? _wfDropsHeardClient;
    string? _wfDropsHeardMap;

    /// <summary>Drop ids start again at 1 on a new stage or map (a new server process or a rejoin): the heard ids are forgotten when the connection or its map changes.</summary>
    void WarfrontDropsHeardFresh()
    {
        string? map = _client?.MapName;
        if (ReferenceEquals(_wfDropsHeardClient, _client) && _wfDropsHeardMap == map) return;
        _wfDropsHeard.Clear();
        _wfDropsHeardClient = _client;
        _wfDropsHeardMap = map;
    }

    partial void WarfrontDropWalksOver(in MusouDropRecord drop, ref bool walks)
    {
        if (WarfrontOn && !drop.Crate && MusouPowerupRules.IsPowerup(drop.Item)) walks = true;
    }

    partial void WarfrontPickupWalkOver(ref bool walks)
    {
        if (WarfrontOn) walks = true;
    }

    partial void WarfrontDropPicked(in MusouDropRecord drop, bool heard, PlayerState me)
    {
        if (!WarfrontOn) return;
        WarfrontDropsHeardFresh();
        if (!_wfDropsHeard.Add(drop.Id) || heard) return;   // the list is resent while the drop is listed: once per id
        MusouDropSound(drop.Position, me);
    }

    partial void WarfrontDropLive(ushort id)
    {
        if (!WarfrontOn) return;
        WarfrontDropsHeardFresh();
        _wfDropsHeard.Remove(id);
    }
}
