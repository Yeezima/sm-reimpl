using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The owner's hunt (2026-11-16; Classic PvP, Annihilation and Seize Ground; test script, **ours**, not the original AI; engine-free choice in <see cref="BotHunt"/>): "make
/// bots leave patrol when no targets are found - they should look for and hunt down enemies that are not in the spawn area. the intent of this hunting is to clear space for
/// their teammates to safely capture points or take up more powerful positions." A bot with nobody to fight or chase (<see cref="BotChaseTarget"/>: every living enemy is in its
/// spawn room or in the no-loiter zone round it) used to walk to its centre hold and roam. Now it hunts the nearest enemy outside the spawn room (<see cref="BotHunt"/>),
/// by the same path walk and the same combat as a chase, instead. Never into the room: an enemy in it is not a candidate; a retreating bot does not hunt; a Seize Ground bot
/// with a point to take or hold keeps that duty (<see cref="BotSeizeInput"/> runs first; hunting is only what the bot does when that returns null, with every point held).
/// What a bot knows: the snapshots give every client every player's state and the chase has always used it, so the hunt adds no channel and no tracking beyond that.
/// Musou bots return earlier (<c>ExtrasBotInput</c>). <c>--bot-no-hunt</c> restores the old patrol (centre hold and roam).
/// </summary>
public partial class NetworkView
{
    static readonly bool BotNoHunt = CmdFlag("bot-no-hunt");
    readonly BotHunt _botHunt = new();
    int _huntLogged, _huntLastTarget = -1;

    /// <summary>
    /// The enemy to hunt when the chase has none (see the class); null with hunting off, while retreating, or when every living enemy is in its spawn room. A new hunt is logged.
    /// </summary>
    PlayerState? BotHuntTarget(PlayerState me)
    {
        if (BotNoHunt || BotRetreating || me.Team == Team.None || !me.Alive) { _botHunt.End(); _huntLastTarget = -1; return null; }
        var list = new List<HuntCandidate>();
        foreach (var o in _lastOthers)
            if (o.Alive && World.Hostile(me.Team, o.Team)) list.Add(new HuntCandidate(o.PlayerId, o.Position, InEnemyRoom(me, o), InEnemyZone(me, o)));
        if (_botHunt.Pick(me.Position, list) is not { } id) { _huntLastTarget = -1; return null; }
        foreach (var o in _lastOthers)
        {
            if (o.PlayerId != id) continue;
            if (id != _huntLastTarget)
            {
                _huntLastTarget = id;
                if (_huntLogged++ < 200)
                    GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} hunt {_botHunt.Hunts} on P{id} at {new Vector2(o.Position.X - me.Position.X, o.Position.Y - me.Position.Y).Length():F0} m{(_botHunt.InZone ? " (outside its room, in the zone round it)" : "")}");
            }
            return o;
        }
        return null;
    }
}
