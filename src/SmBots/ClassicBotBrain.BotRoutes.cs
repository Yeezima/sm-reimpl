using System;
using System.Collections.Generic;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmBots;

/// <summary>
/// Bots A2, Classic strategy (owner 2026-11-23; test script, **ours**, not the original AI; engine-free parts in BotMapPlan.Routes.cs): "make the bots fan out more. on maps
/// that aren't manufactorum they need to be able to navigate all around the map, not just walk in a straight line down the center. / tacticals and devastators look for covered
/// defensive positons and high ground, assault marines and tactical sometimes flank the enemy via alternate routes. / bots also need to be aware that they can drop off open
/// ledges." Annihilation (Seize Ground keeps its point logic), on every Classic map with a plan:
/// <list type="bullet">
/// <item><b>a route per life</b>: each life the bot takes one of the plan's spawn-to-spawn routes (<see cref="BotMapPlan.Routes"/>), spread by team rank and life, so a side's
/// bots leave by different routes; its first leg walks that route (in place of the old lane leg) until an enemy is fought or comes within <see cref="RouteBreak"/> m;</item>
/// <item><b>route chases</b>: a chase of an enemy farther than <see cref="BotMapPlan.ViaMinDistance"/> m goes along a route (its own one preferred, the most direct one
/// avoided) toward the route point nearest the enemy, when that is at most <see cref="RouteDetour"/> (a flanker: <see cref="FlankDetour"/>) times the straight distance
/// (in place of the old via point);</item>
/// <item><b>flanks</b>: some lives (Assault <see cref="AssaultFlankChance"/>, Tactical <see cref="TacticalFlankChance"/>) the bot is a flanker: its route is an outer one and its
/// chases prefer it more strongly;</item>
/// <item><b>vantages</b>: some lives (Devastator <see cref="DevastatorVantageChance"/>, Tactical <see cref="TacticalVantageChance"/>), when a chase is about to meet the
/// enemy (<see cref="VantageEnemyMin"/> to <see cref="VantageEnemyMax"/> m), the bot stops at a vantage within <see cref="VantageReach"/> m (high ground, cover, sight over the
/// middle; <see cref="BotMapPlan.Vantages"/>), holds it <see cref="VantageHoldMin"/> to <see cref="VantageHoldMax"/> s facing the chased enemy and fights what it sees from there
/// with the ordinary fight (unchanged: it may roll and advance); it leaves after <see cref="VantageQuiet"/> s without a fight, when the enemy comes within
/// <see cref="VantageBreak"/> m or the hold ends, then waits <see cref="VantageCooldown"/> s before the next.</item>
/// </list>
/// Drops: routes and chases are planned over the bot's graph with its drop links (on the maps under the new-map rules the drops reach <see cref="BotNavigation"/>'s higher
/// limit). <c>--bot-old-routes</c> restores the lane leg, the via point and no vantages (the plan still builds the routes; nothing uses them).
/// </summary>
public sealed partial class ClassicBotBrain
{
    bool BotOldRoutes => CmdFlag("bot-old-routes");

    /// <summary>A route leg ends when the chased enemy is within this many metres (as the lane leg; ours).</summary>
    public const float RouteBreak = 30f;
    /// <summary>Metres within which a route point counts as passed, and seconds a route point may take before it is skipped (ours).</summary>
    public const float RoutePass = 5f;
    public const double RoutePointTimeout = 20, RouteLegTimeout = 120;
    /// <summary>A route chase is taken when its length (to the route, along it, then straight to the enemy) is at most this times the straight distance (ours).</summary>
    public const float RouteDetour = 1.6f, FlankDetour = 2.0f;
    /// <summary>Route chase cost weights (ours): the bot's own route, a flanker's own route, the most direct route.</summary>
    public const float OwnRouteWeight = 0.8f, FlankRouteWeight = 0.6f, DirectRouteWeight = 1.15f;
    /// <summary>Per-life chances (ours, estimates): an Assault or Tactical flanks; a Tactical takes vantages (a Devastator always does).</summary>
    public const double AssaultFlankChance = 0.5, TacticalFlankChance = 0.3, TacticalVantageChance = 0.3, DevastatorVantageChance = 0.7;
    /// <summary>Vantage timing and distances (s, m; ours).</summary>
    public const double VantageHoldMin = 8, VantageHoldMax = 14, VantageQuiet = 5, VantageCooldown = 30, VantageWalkTimeout = 15;
    /// <summary>A vantage is given up for the life when its path is partial or longer than this times the straight distance plus <see cref="VantagePathSlack"/> m (ours).</summary>
    public const float VantagePathStretch = 1.6f, VantagePathSlack = 10f;
    public const float VantageReach = 35f, VantageEnemyMin = 25f, VantageEnemyMax = 75f, VantageBreak = 18f, VantageArrived = 1.5f, VantageStray = 2.5f;

    int _routeLife = -1, _routeOfLife = -1, _routePoint;
    double _routePointSince, _routeLegSince;
    bool _routeLegDone = true, _routeFlank, _routeVantages;
    int _routeViaFor = -1, _routeViaRoute = -1;
    double _routeViaAt = -1;
    /// <summary>Metres of a route chase left past the route point walked to now (the chase's sprint counts them).</summary>
    float _routeViaRemaining;
    /// <summary>A route walk with no progress (<see cref="RouteStallGain"/> m in <see cref="RouteStallSeconds"/> s) skips the point (the second in a leg ends the leg); a stalled
    /// route chase gives way to the straight chase for <see cref="RouteViaBlock"/> s (ours; Hab Center's floors stood bots still on route points of another floor).</summary>
    public const float RouteStallGain = 1f;
    public const double RouteStallSeconds = 3, RouteViaBlock = 10;
    int _routeStalls;
    double _routeViaBlockedUntil, _stallSince, _stallSeen = double.MinValue;
    float _stallBest;
    V3 _stallGoal;
    int _vantage = -1, _vantageLast = -1;
    bool _vantageArrived;
    double _vantageSince, _vantageUntil, _vantageNext, _vantageArrivedAt, _lastFightAt = double.MinValue;
    readonly HashSet<int> _vantageBad = new();

    /// <summary>Whether bots A2's routes run (Annihilation, a plan with routes, not <c>--bot-old-routes</c>).</summary>
    bool RoutesOn(BotMapPlan plan) => !BotOldRoutes && !SeizeGround && plan.Routes.Count > 0;

    string BotRole(PlayerState me) => _config is { } c ? Loadouts.ClassOf(c, me.Class)?.Role ?? "" : "";

    /// <summary>A new life: picks its route, whether it flanks and whether it takes vantages (seeded by player id and life).</summary>
    void BotRouteLife(PlayerState me, BotMapPlan plan)
    {
        if (_routeLife == _botLife) return;
        _routeLife = _botLife;
        int id = Math.Max(_client?.PlayerId ?? 0, 0);
        double roll = new Random(id * 7919 + Math.Max(_botLife, 0) * 104723 + 11).NextDouble();
        string role = BotRole(me);
        _routeFlank = role == "assault" ? roll < AssaultFlankChance : role == "tactical" && roll < TacticalFlankChance;
        _routeVantages = (role == "heavy" && roll >= 1 - DevastatorVantageChance) || (role == "tactical" && !_routeFlank && roll < TacticalFlankChance + TacticalVantageChance);
        int n = plan.Routes.Count, rank = BotRank(me), life = Math.Max(_botLife, 0);
        _routeOfLife = _routeFlank && n >= 2 ? ((rank + life) % 2 == 0 ? 0 : n - 1) : (BotMapPlan.Spread(rank, n) + life) % n;
        _routePoint = 0; _routePointSince = _routeLegSince = _connectedFor; _routeLegDone = false; _routeStalls = 0; _routeViaBlockedUntil = 0;
        _routeViaFor = -1;
        _vantage = -1; _vantageNext = 0; _vantageBad.Clear();
        GD.Print($"t {_connectedFor:F1} s: id {id} life {_botLife}: route {_routeOfLife + 1} of {n}{(_routeOfLife == plan.DirectRoute ? " (direct)" : "")}{(_routeFlank ? ", flanker" : "")}{(_routeVantages ? ", vantages" : "")} ({role}, rank {rank})");
    }

    /// <summary>The life's first leg along its route (see the class); null when it is over, in Seize Ground, without routes or with <c>--bot-old-routes</c>.</summary>
    PlayerInput? BotRouteLegInput(PlayerState me, uint tick, PlayerState? chased, BotMapPlan plan)
    {
        BotRouteLife(me, plan);
        if (_routeLegDone || _routeOfLife < 0 || _routeOfLife >= plan.Routes.Count) return null;
        if (chased is { } c && Flat(c.Position, me.Position) < RouteBreak) { _routeLegDone = true; return null; }
        var route = plan.Routes[_routeOfLife];
        // Skip ahead to a later point that is already nearer (a drop or a jump passed several).
        for (int k = _routePoint + 1; k < route.Count && k <= _routePoint + 3; k++)
            if (Flat(route[k], me.Position) < Flat(route[_routePoint], me.Position)) { _routePoint = k; _routePointSince = _connectedFor; }
        if (Flat(route[_routePoint], me.Position) <= RoutePass && MathF.Abs(route[_routePoint].Z - me.Position.Z) < 3f || _connectedFor - _routePointSince > RoutePointTimeout)
        {
            _routePoint++; _routePointSince = _connectedFor;
        }
        if (_routePoint < route.Count && RouteStalled(route[_routePoint]))
        {
            // No progress toward the point (a point on another floor, a spot the bot cannot reach): skip it; the second stall in a leg ends the leg.
            GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} route {_routeOfLife + 1} point {_routePoint + 1} stalled{(_routeStalls > 0 ? ", leg ends" : ", skipped")}");
            if (++_routeStalls >= 2) { _routeLegDone = true; return null; }
            _routePoint++; _routePointSince = _connectedFor;
        }
        if (_routePoint >= route.Count || _connectedFor - _routeLegSince > RouteLegTimeout) { _routeLegDone = true; return null; }
        var goal = route[_routePoint];
        _botNote = $"route {_routeOfLife + 1} leg, point {_routePoint + 1}/{route.Count}";
        NoteIdle($"life {_botLife}: route {_routeOfLife + 1} leg, point {_routePoint + 1}/{route.Count} {goal.X:F0},{goal.Y:F0},{goal.Z:F0}");
        float after = 0f;
        for (int k = _routePoint + 1; k < route.Count; k++) after += Flat(route[k - 1], route[k]);
        return BotWalkTo(me, tick, goal, "route", after);
    }

    /// <summary>
    /// The route point a chase of <paramref name="enemy"/> walks to (see the class), or null for the straight chase: an enemy within <see cref="BotMapPlan.ViaMinDistance"/>
    /// m, no route within the detour, or the bot already at the route point nearest the enemy. The route is chosen when the chase starts and every 3 s.
    /// </summary>
    V3? BotRouteVia(PlayerState me, PlayerState enemy, BotMapPlan plan)
    {
        BotRouteLife(me, plan);
        float direct = Flat(me.Position, enemy.Position);
        if (direct < BotMapPlan.ViaMinDistance) return null;
        if (enemy.PlayerId != _routeViaFor || _connectedFor - _routeViaAt > 3.0)
        {
            int before = _routeViaRoute;
            _routeViaFor = enemy.PlayerId; _routeViaAt = _connectedFor; _routeViaRoute = -1;
            float best = (_routeFlank ? FlankDetour : RouteDetour) * direct;
            for (int r = 0; r < plan.Routes.Count; r++)
            {
                var (cost, _, _) = RouteChase(plan.Routes[r], me.Position, enemy.Position);
                if (float.IsInfinity(cost)) continue;
                float weight = r == _routeOfLife ? (_routeFlank ? FlankRouteWeight : OwnRouteWeight) : r == plan.DirectRoute ? DirectRouteWeight : 1f;
                if (cost * weight < best && cost <= (_routeFlank ? FlankDetour : RouteDetour) * direct) { best = cost * weight; _routeViaRoute = r; }
            }
            if (_routeViaRoute != before && _routeViaRoute >= 0)
                NoteIdle($"chase P{enemy.PlayerId} along route {_routeViaRoute + 1}{(_routeViaRoute == _routeOfLife ? " (own)" : "")}{(_routeFlank ? " flanking" : "")}");
        }
        if (_routeViaRoute < 0 || _routeViaRoute >= plan.Routes.Count || _connectedFor < _routeViaBlockedUntil) return null;
        var (along, next, _) = RouteChase(plan.Routes[_routeViaRoute], me.Position, enemy.Position);
        if (next is { } stalledAt && RouteStalled(stalledAt))
        {
            GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} route chase stalled at {stalledAt.X:F0},{stalledAt.Y:F0},{stalledAt.Z:F0}: straight chase for {RouteViaBlock:F0} s");
            _routeViaBlockedUntil = _connectedFor + RouteViaBlock;
            return null;
        }
        _routeViaRemaining = next is { } n ? MathF.Max(0f, along - Flat(me.Position, n)) : 0f;
        return next;
    }

    /// <summary>
    /// A chase along <paramref name="route"/>: its cost (flat metres to the nearest route point, along the route to the point nearest the enemy, then straight on), the next
    /// route point to walk to (the first at least <see cref="RoutePass"/> m away toward that point) and that point's index; infinite cost when the bot is already at it.
    /// </summary>
    static (float Cost, V3? Next, int Target) RouteChase(IReadOnlyList<V3> route, V3 me, V3 enemy)
    {
        int near = 0, target = 0;
        for (int i = 1; i < route.Count; i++)
        {
            if (Flat(route[i], me) < Flat(route[near], me)) near = i;
            if (Flat(route[i], enemy) < Flat(route[target], enemy)) target = i;
        }
        if (near == target) return (float.PositiveInfinity, null, target);
        int step = target > near ? 1 : -1;
        float cost = Flat(me, route[near]);
        V3? next = null;
        for (int i = near; i != target; i += step)
        {
            cost += Flat(route[i], route[i + step]);
            if (next is null && Flat(route[i + step], me) > RoutePass) next = route[i + step];
        }
        if (next is null && Flat(route[near], me) > RoutePass) next = route[near];
        cost += Flat(route[target], enemy);
        return (cost, next, target);
    }

    /// <summary>
    /// A vantage holder (see the class): walks to its vantage, holds it facing <paramref name="chased"/>, leaves it at the hold's end or when the enemy closes in. Null when the
    /// bot takes no vantages this life, is cooling down, retreating, or has no enemy to watch for.
    /// </summary>
    PlayerInput? BotVantageInput(PlayerState me, uint tick, PlayerState? chased, BotMapPlan plan)
    {
        BotRouteLife(me, plan);
        if (!_routeVantages || plan.Vantages.Count == 0 || BotRetreating || chased is not { } enemy) { EndVantage(null); return null; }
        if (_vantage < 0)
        {
            if (_connectedFor < _vantageNext) return null;
            float toEnemy = Flat(enemy.Position, me.Position);
            if (toEnemy < VantageEnemyMin || toEnemy > VantageEnemyMax) return null;   // a vantage is a stop on a chase that is about to meet the enemy
            int rank = BotRank(me);
            var options = new List<(int Index, float Score)>();
            for (int i = 0; i < plan.Vantages.Count; i++)
            {
                var v = plan.Vantages[i];
                float vEnemy = Flat(v.Spot, enemy.Position);
                if (i == _vantageLast || _vantageBad.Contains(i) || Flat(v.Spot, me.Position) > VantageReach || vEnemy < VantageBreak + 2f || vEnemy > toEnemy + 5f) continue;
                options.Add((i, v.Score - 0.03f * Flat(v.Spot, me.Position)));
            }
            if (options.Count == 0) { _vantageNext = _connectedFor + 5; return null; }
            options.Sort((a, b) => b.Score.CompareTo(a.Score));
            int pick = options[Math.Min(rank % 2, options.Count - 1)].Index;   // two holders of a side split the best two
            _vantage = pick; _vantageArrived = false; _vantageSince = _connectedFor;
            var chosen = plan.Vantages[pick];
            GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} vantage {pick + 1} {chosen.Spot.X:F0},{chosen.Spot.Y:F0},{chosen.Spot.Z:F0} (rise {chosen.Rise:F1} m{(chosen.Covered ? ", cover" : "")}, sight {chosen.Sight})");
        }
        var spot = plan.Vantages[_vantage].Spot;
        if (Flat(enemy.Position, me.Position) < VantageBreak) { EndVantage("enemy close"); return null; }
        float away = Flat(spot, me.Position);
        bool on = away <= VantageArrived && MathF.Abs(spot.Z - me.Position.Z) < 2f;
        if (!_vantageArrived)
        {
            if (on)
            {
                _vantageArrived = true;
                _vantageArrivedAt = _connectedFor;
                _vantageUntil = _connectedFor + VantageHoldMin + new Random(Math.Max(_client?.PlayerId ?? 0, 0) * 31 + _botLife * 7 + _vantage).NextDouble() * (VantageHoldMax - VantageHoldMin);
            }
            else if (_connectedFor - _vantageSince > VantageWalkTimeout) { EndVantage("not reached"); return null; }
        }
        if (_vantageArrived && _connectedFor >= _vantageUntil) { EndVantage("held"); return null; }
        // Nothing fought from it for VantageQuiet s since arriving (or since the last fight): back to the chase.
        if (_vantageArrived && _connectedFor - Math.Max(_vantageArrivedAt, _lastFightAt) > VantageQuiet) { EndVantage("quiet"); return null; }
        if (!on && (!_vantageArrived || away > VantageStray || MathF.Abs(spot.Z - me.Position.Z) >= 2f))
        {
            _botNote = $"vantage-walk {_vantage + 1}: {away:F0} m";
            var walk = BotWalkTo(me, tick, spot, "vantage");
            // A vantage whose path is partial or much longer than the straight line is given up for this life (VantagePathStretch).
            if (!_vantageArrived && _connectedFor - _vantageSince > 1.5 && _navAgent is { Path: { } path } agent && Flat(agent.PlannedGoal, spot) < 2f
                && (path.Partial || agent.Remaining > VantagePathStretch * away + VantagePathSlack))
            {
                _vantageBad.Add(_vantage);
                EndVantage(path.Partial ? "path partial" : $"path {agent.Remaining:F0} m for {away:F0} m");
                _vantageLast = -1; _vantageNext = _connectedFor + 1;
                return null;
            }
            return walk;
        }
        _yaw = MathF.Atan2(-(enemy.Position.X - me.Position.X), enemy.Position.Y - me.Position.Y);
        _pitch = 0f;
        _botNote = $"vantage-hold {_vantage + 1}, watching P{enemy.PlayerId}";
        return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
    }

    void EndVantage(string? why)
    {
        if (_vantage < 0) return;
        if (why is not null) GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} vantage {_vantage + 1} ends: {why}");
        _vantageLast = _vantage; _vantage = -1; _vantageNext = _connectedFor + VantageCooldown;
    }

    /// <summary>
    /// Whether the walk toward <paramref name="goal"/> (a route point) has made no progress: the 3D distance has not fallen by <see cref="RouteStallGain"/> m in
    /// <see cref="RouteStallSeconds"/> s of route walking. A new goal, or a gap of more than half a second between calls (a fight, a vantage), starts the watch again.
    /// </summary>
    bool RouteStalled(V3 goal)
    {
        var here = _client!.Predicted.Position;
        float d = V3.Distance(here, goal);
        if (V3.DistanceSquared(goal, _stallGoal) > 0.25f || _connectedFor - _stallSeen > 0.5) { _stallGoal = goal; _stallBest = d; _stallSince = _connectedFor; }
        _stallSeen = _connectedFor;
        if (d < _stallBest - RouteStallGain) { _stallBest = d; _stallSince = _connectedFor; }
        if (_connectedFor - _stallSince < RouteStallSeconds) return false;
        _stallSince = _connectedFor; _stallBest = d;
        return true;
    }

    static float Flat(V3 a, V3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
