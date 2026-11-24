using System;
using System.Collections.Generic;
using SmNet;
using SmSim;

namespace SmBots;

/// <summary>
/// The owner's hunt (2026-11-16; Classic PvP, Annihilation and Seize Ground; test script, **ours**, not the original AI; engine-free choice in <see cref="BotHunt"/>): "make
/// bots leave patrol when no targets are found - they should look for and hunt down enemies that are not in the spawn area. the intent of this hunting is to clear space for
/// their teammates to safely capture points or take up more powerful positions." The spawn area is the spawn room plus the 25 m no-loiter zone round it (owner 2026-11-07: a bot never loiters at the enemy's spawn); no enemy in it is hunted
/// (<c>--bot-hunt-zone</c>, default off, also takes those outside the room). A bot with nobody to fight or chase hunts the nearest enemy outside the spawn area (<see cref="BotHunt"/>),
/// by the same path walk and the same combat as a chase (the chase already takes every enemy outside the spawn area at any range, so by default this adds nothing in Annihilation). A retreating bot does not hunt. A Seize Ground bot keeps its point
/// unless it is surplus there (<see cref="BotSeizeSurplus"/>: 2 lower-id teammates already inside), when it hunts an enemy near the points and goes back once none is left; with every point held it hunts as in Annihilation.
/// What a bot knows: the snapshots give every client every player's state and the chase has always used it, so the hunt adds no channel and no tracking beyond that.
/// Musou bots return earlier (<c>ExtrasBotInput</c>). <c>--bot-no-hunt</c> turns off Seize Ground's surplus hunt and the zone hunt.
/// </summary>
public sealed partial class ClassicBotBrain
{
    bool BotNoHunt => CmdFlag("bot-no-hunt");
    /// <summary><c>--bot-hunt-zone</c> (default off): the hunt also takes enemies in the no-loiter zone round their spawn (outside the room), as the first version did.</summary>
    bool BotHuntZone => CmdFlag("bot-hunt-zone");
    readonly List<HuntCandidate> _huntList = new();
    readonly BotHunt _botHunt = new();
    int _huntLogged, _huntLastTarget = -1;

    /// <summary>
    /// The enemy to hunt when the chase has none (see the class); null with hunting off, while retreating, or when every living enemy is in its spawn room. A new hunt is logged.
    /// </summary>
    PlayerState? BotHuntTarget(PlayerState me)
    {
        if (BotNoHunt || BotRetreating || me.Team == Team.None || !me.Alive) { _botHunt.End(); _huntLastTarget = -1; return null; }
        var list = _huntList; list.Clear();
        foreach (var o in _lastOthers)
            if (o.Alive && World.Hostile(me.Team, o.Team)) list.Add(new HuntCandidate(o.PlayerId, o.Position, InEnemyRoom(me, o), InEnemyZone(me, o)));
        if (_botHunt.Pick(me.Position, list, BotHuntZone) is not { } id) { _huntLastTarget = -1; return null; }
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

    readonly BotSeizeSurplus _seizeSurplus = new();

    /// <summary>
    /// Seize Ground (see <see cref="BotSeizeSurplus"/>, the rule): true when this bot is surplus on its point (2 or more living teammates with a lower id inside its zone) and an
    /// enemy outside its spawn area (room and no-loiter zone) stands within <see cref="SeizeHuntNearPoint"/> m of any point; <see cref="BotSeizeInput"/> then hands the bot to the
    /// chase and fight code, which hunts the nearest such enemy. False the moment no such enemy remains (the bot is back on its point).
    /// </summary>
    bool BotSeizeSurplusHunts(PlayerState me, SeizeGroundInfo layout, ControlPointInfo goal)
    {
        if (BotNoHunt || BotRetreating || me.Team == Team.None) { _seizeSurplus.End(); return false; }
        int lower = 0;
        foreach (var o in _lastOthers)
            if (o.Alive && o.Team == me.Team && o.PlayerId > 0 && o.PlayerId < me.PlayerId
                && new Vector2(o.Position.X - goal.Center.X, o.Position.Y - goal.Center.Y).Length() <= goal.Radius) lower++;
        bool near = false;
        foreach (var o in _lastOthers)
        {
            if (!o.Alive || !World.Hostile(me.Team, o.Team) || InEnemyRoom(me, o) || InEnemyZone(me, o)) continue;
            foreach (var p in layout.Points)
                if (new Vector2(o.Position.X - p.Center.X, o.Position.Y - p.Center.Y).Length() <= SeizeHuntNearPoint) { near = true; break; }
            if (near) break;
        }
        int before = _seizeSurplus.Starts;
        bool hunts = _seizeSurplus.Step(_connectedFor, lower, near);
        if (_seizeSurplus.Starts != before && before < 200) GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} hunt {_seizeSurplus.Starts} (seize): {lower} lower-id teammates stay on its point, enemy near the points");
        return hunts;
    }

    /// <summary>How near a point an enemy must be to be worth hunting from a point (m; ours, an estimate).</summary>
    const float SeizeHuntNearPoint = 60f;
}
