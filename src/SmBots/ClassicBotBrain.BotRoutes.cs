using System;
using System.Collections.Generic;
using SmContent;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmBots;

/// <summary>
/// Bots A2, Classic strategy (owner 2026-11-23; test script, **ours**, not the original AI; engine-free parts in BotMapPlan.Routes.cs): "make the bots fan out more. on maps
/// that aren't manufactorum they need to be able to navigate all around the map, not just walk in a straight line down the center. / tacticals and devastators look for covered
/// defensive positons and high ground, assault marines and tactical sometimes flank the enemy via alternate routes. / bots also need to be aware that they can drop off open
/// ledges." Annihilation (Seize Ground keeps its point logic), on the Classic maps under the new-map rules (not Manufactorum, which keeps its lanes, via points and posts):
/// <list type="bullet">
/// <item><b>a route per life</b>: each life the bot takes one of the plan's spawn-to-spawn routes (<see cref="BotMapPlan.Routes"/>), spread by team rank and life, so a side's
/// bots leave by different routes; its first leg walks that route (in place of the old lane leg) to the middle of the map (<see cref="BotMapPlan.RouteLegTo"/>), or until an enemy is fought or comes within <see cref="RouteBreak"/> m; a route without progress is dropped for the life;</item>
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
    /// <summary><c>--bot-old-drops</c>: the 8 m drop limit on every map (the graph and cache keys from before bots A2; <see cref="BotNavigation.Setup"/>).</summary>
    bool BotOldDrops => CmdFlag("bot-old-drops");

    /// <summary>A route leg ends when the chased enemy is within this many metres (as the lane leg; ours).</summary>
    public const float RouteBreak = 30f;
    /// <summary>Metres within which a route point counts as passed, and seconds a route point may take before it is skipped (ours).</summary>
    public const float RoutePass = 5f;
    public const double RoutePointTimeout = 20, RouteLegTimeout = 60;
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
    /// <summary>Most metres the height term of <see cref="BotMapPlan.Near"/> adds to a vantage's distance (re-review 2026-11-25: a vantage up a tall ramp is still in reach; ours).</summary>
    public const float VantageHeightCap = 15f;
    public const float VantageReach = 35f, VantageEnemyMin = 25f, VantageEnemyMax = 75f, VantageBreak = 18f, VantageArrived = 1.5f, VantageStray = 2.5f;

    int _routeLife = -1, _routeOfLife = -1, _routePoint;
    double _routePointSince, _routeLegSince;
    bool _routeLegDone = true, _routeFlank, _routeVantages;
    int _routeViaFor = -1, _routeViaRoute = -1;
    double _routeViaAt = -1;
    /// <summary>Metres of a route chase left past the route point walked to now (the chase's sprint counts them).</summary>
    float _routeViaRemaining;
    /// <summary>A route walk (leg or chase) with no progress (<see cref="RouteStallGain"/> m in <see cref="RouteStallSeconds"/> s) drops that route for the rest of the life: a leg
    /// ends, a chase takes another route or goes straight (ours; Hab Center's floors stood bots still on route points of another floor).</summary>
    public const float RouteStallGain = 1f;
    public const double RouteStallSeconds = 3;
    /// <summary>On the maps under the new-map rules (not with <c>--bot-old-drops</c>) a path's end counts as reached only within this many metres of height (<see cref="NavAgent.ArriveMaxDz"/>; ours).</summary>
    public const float ArriveMaxDz = 1.5f;
    readonly HashSet<int> _routeDropped = new();
    BotMapPlan? _routePlan;
    /// <summary>A route that stalled this many times for the bot (over its lives, on the same plan) is avoided for the rest of the match (4v4 rerun: one Hab Center drop the bots
    /// could not take stalled the same route every life; ours).</summary>
    public const int RouteAvoidAfterStalls = 2;
    readonly Dictionary<int, int> _routeStalls = new();
    /// <summary>Test view: stalls per route on this plan.</summary>
    internal IReadOnlyDictionary<int, int> RouteStalls => _routeStalls;
    bool RouteAvoided(int r) => _routeStalls.GetValueOrDefault(r) >= RouteAvoidAfterStalls;
    /// <summary>The match is starting (its countdown): the stall memory of the last match goes (a same-map rematch may keep the plan).</summary>
    internal void ForgetRouteStalls() => _routeStalls.Clear();
    void NoteRouteStall(int r) { _routeDropped.Add(r); _routeStalls[r] = _routeStalls.GetValueOrDefault(r) + 1; }
    int _vantage = -1, _vantageLast = -1;
    bool _vantageArrived;
    double _vantageSince, _vantageUntil, _vantageNext, _vantageArrivedAt, _lastFightAt = double.MinValue;
    readonly HashSet<int> _vantageBad = new();

    /// <summary>
    /// Whether bots A2's routes, flanks and vantages run: Annihilation, on a map under the new-map rules (the owner: "on maps that aren't manufactorum"; Manufactorum keeps its
    /// lanes, via points and posts), a plan with routes, not <c>--bot-old-routes</c>. The 12 m drops are the graph's and apply in both modes (<see cref="BotNavigation.HigherDrops"/>).
    /// </summary>
    bool RoutesOn(BotMapPlan plan) => RoutesAllowed(_navMap?.Name ?? "", SeizeGround, BotOldRoutes, plan.Routes.Count);

    /// <summary>The <see cref="RoutesOn"/> rule as a pure predicate (tested): a map under the new-map rules, Annihilation, not <c>--bot-old-routes</c>, at least one route.</summary>
    internal static bool RoutesAllowed(string map, bool seizeGround, bool oldRoutes, int routes) =>
        !oldRoutes && !seizeGround && routes > 0 && BotNavigation.NewMapRules(map);

    /// <summary>Test view: the life's route, whether its leg is over, and the routes dropped this life.</summary>
    internal int RouteOfLife => _routeOfLife;
    internal bool RouteLegDone => _routeLegDone;
    internal IReadOnlyCollection<int> RoutesDropped => _routeDropped;

    string BotRole(PlayerState me) => _config is { } c ? Loadouts.ClassOf(c, me.Class)?.Role ?? "" : "";

    /// <summary>A new life: picks its route, whether it flanks and whether it takes vantages (seeded by player id and life).</summary>
    internal void BotRouteLife(PlayerState me, BotMapPlan plan)
    {
        if (_routeLife == _botLife && ReferenceEquals(_routePlan, plan)) return;   // a new plan (another class's graph) starts the life's choices again
        if (!ReferenceEquals(_routePlan, plan)) _routeStalls.Clear();   // the stall memory belongs to a plan's routes
        _routeLife = _botLife; _routePlan = plan;
        int id = Math.Max(_client?.PlayerId ?? 0, 0);
        double roll = new Random(id * 7919 + Math.Max(_botLife, 0) * 104723 + 11).NextDouble();
        string role = BotRole(me);
        _routeFlank = role == "assault" ? roll < AssaultFlankChance : role == "tactical" && roll < TacticalFlankChance;
        _routeVantages = (role == "heavy" && roll >= 1 - DevastatorVantageChance) || (role == "tactical" && !_routeFlank && roll < TacticalFlankChance + TacticalVantageChance);
        int n = plan.Routes.Count, rank = BotRank(me), life = Math.Max(_botLife, 0);
        _routeOfLife = n == 0 ? -1 : _routeFlank && n >= 2 ? ((rank + life) % 2 == 0 ? 0 : n - 1) : (BotMapPlan.Spread(rank, n) + life) % n;
        // A route avoided after repeated stalls gives way to the next one that is not (none left: no route leg this life).
        for (int k = 0; k < n && _routeOfLife >= 0 && RouteAvoided(_routeOfLife); k++) _routeOfLife = k == n - 1 ? -1 : (_routeOfLife + 1) % n;
        _routePoint = 0; _routePointSince = _routeLegSince = _connectedFor; _routeLegDone = _routeOfLife < 0; _routeDropped.Clear();
        foreach (var kv in _routeStalls) if (kv.Value >= RouteAvoidAfterStalls) _routeDropped.Add(kv.Key);   // nor for route chases
        _routeViaFor = -1; _routeViaRoute = -1;
        _vantage = -1; _vantageNext = 0; _vantageBad.Clear();
        GD.Print($"t {_connectedFor:F1} s: id {id} life {_botLife}: {(_routeOfLife < 0 ? "no route" : $"route {_routeOfLife + 1}")} of {n}{(_routeOfLife == plan.DirectRoute ? " (direct)" : "")}{(_routeFlank ? ", flanker" : "")}{(_routeVantages ? ", vantages" : "")} ({role}, rank {rank})");
    }

    /// <summary>The life's first leg along its route (see the class); null when it is over, in Seize Ground, without routes or with <c>--bot-old-routes</c>.</summary>
    internal PlayerInput? BotRouteLegInput(PlayerState me, uint tick, PlayerState? chased, BotMapPlan plan)
    {
        BotRouteLife(me, plan);
        if (_routeLegDone || _routeOfLife < 0 || _routeOfLife >= plan.Routes.Count || _routeOfLife >= plan.RouteLegEnds.Count) { _routeLegDone = true; return null; }
        if (chased is { } c && Flat(c.Position, me.Position) < RouteBreak) { _routeLegDone = true; return null; }
        var route = plan.Routes[_routeOfLife];
        // The leg ends near the middle (BotMapPlan.RouteLegTo): beyond it the chase (along a route) or the idle hold at the centre takes over, never a walk to the enemy's spawn.
        int end = Math.Min(plan.RouteLegEnds[_routeOfLife], route.Count - 1);
        if (_routePoint > end) { _routeLegDone = true; return null; }
        // Skip ahead to a later point that is already nearer (a drop or a jump passed several); distances tell stacked floors apart (BotMapPlan.Near).
        for (int k = _routePoint + 1; k <= end && k <= _routePoint + 3; k++)
            if (BotMapPlan.Near(route[k], me.Position) < BotMapPlan.Near(route[_routePoint], me.Position)) { _routePoint = k; _routePointSince = _connectedFor; }
        if (BotMapPlan.Near(route[_routePoint], me.Position) <= RoutePass || _connectedFor - _routePointSince > RoutePointTimeout)
        {
            _routePoint++; _routePointSince = _connectedFor;
        }
        if (_routePoint <= end && RouteStalled(route[_routePoint]))
        {
            // No progress toward the point (a point on another floor, a spot the bot cannot reach): the route is dropped for this life and the leg ends.
            GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} route {_routeOfLife + 1} point {_routePoint + 1} stalled: route dropped for this life, leg ends");
            NoteRouteStall(_routeOfLife);
            _routeLegDone = true; return null;
        }
        if (_routePoint > end || _connectedFor - _routeLegSince > RouteLegTimeout) { _routeLegDone = true; return null; }
        var goal = route[_routePoint];
        _botNote = $"route {_routeOfLife + 1} leg, point {_routePoint + 1}/{route.Count}";
        NoteIdle($"life {_botLife}: route {_routeOfLife + 1} leg, point {_routePoint + 1}/{route.Count} {goal.X:F0},{goal.Y:F0},{goal.Z:F0}");
        float after = 0f;
        for (int k = _routePoint + 1; k <= end; k++) after += V3.Distance(route[k - 1], route[k]);
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
                if (_routeDropped.Contains(r)) continue;
                var (cost, _, _) = BotMapPlan.RouteChase(plan.Routes[r], me.Position, enemy.Position, RoutePass);
                if (float.IsInfinity(cost)) continue;
                float weight = r == _routeOfLife ? (_routeFlank ? FlankRouteWeight : OwnRouteWeight) : r == plan.DirectRoute ? DirectRouteWeight : 1f;
                if (cost * weight < best && cost <= (_routeFlank ? FlankDetour : RouteDetour) * direct) { best = cost * weight; _routeViaRoute = r; }
            }
            if (_routeViaRoute != before && _routeViaRoute >= 0)
                NoteIdle($"chase P{enemy.PlayerId} along route {_routeViaRoute + 1}{(_routeViaRoute == _routeOfLife ? " (own)" : "")}{(_routeFlank ? " flanking" : "")}");
        }
        if (_routeViaRoute < 0 || _routeViaRoute >= plan.Routes.Count || _routeDropped.Contains(_routeViaRoute)) return null;
        var (along, next, _) = BotMapPlan.RouteChase(plan.Routes[_routeViaRoute], me.Position, enemy.Position, RoutePass);
        if (next is { } stalledAt && RouteStalled(stalledAt))
        {
            GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} route chase stalled at {stalledAt.X:F0},{stalledAt.Y:F0},{stalledAt.Z:F0}: route {_routeViaRoute + 1} dropped for this life");
            NoteRouteStall(_routeViaRoute);
            _routeViaFor = -1;   // choose again next tick
            return null;
        }
        _routeViaRemaining = next is { } n ? MathF.Max(0f, along - Flat(me.Position, n)) : 0f;
        return next;
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
                float vMe = BotMapPlan.Near(v.Spot, me.Position, VantageHeightCap);   // a vantage on another floor is far (review 2026-11-25), the height term capped
                if (i == _vantageLast || _vantageBad.Contains(i) || vMe > VantageReach || vEnemy < VantageBreak + 2f || vEnemy > toEnemy + 5f) continue;
                options.Add((i, v.Score - 0.03f * vMe));
            }
            if (options.Count == 0) { _vantageNext = _connectedFor + 5; return null; }
            options.Sort((a, b) => b.Score.CompareTo(a.Score));
            int pick = options[Math.Min(rank % 2, options.Count - 1)].Index;   // two holders of a side split the best two
            _vantage = pick; _vantageArrived = false; _vantageSince = _connectedFor;
            var chosen = plan.Vantages[pick];
            GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} vantage {pick + 1} {chosen.Spot.X:F0},{chosen.Spot.Y:F0},{chosen.Spot.Z:F0} (rise {chosen.Rise:F1} m{(chosen.Covered ? ", cover" : "")}, sight {chosen.Sight})");
        }
        if (_vantage >= plan.Vantages.Count) { _vantage = -1; return null; }   // the plan changed under it
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
    bool RouteStalled(V3 goal) =>
        // 4v4 rerun 2026-11-25: a jump-pack bot standing at a take-off cell waiting for power (or in the jump) is not stalled; Hab Center dropped 30 of 61 routes, 17 of them Assault.
        _stallWatch.Step(_connectedFor, goal, _client!.Predicted.Position, paused: _navAgent is { } agent && (agent.WaitingAtTakeOff || agent.JumpInProgress));   // not the walk to the take-off (OnJumpLink covers that too)

    readonly RouteStallWatch _stallWatch = new();

    static float Flat(V3 a, V3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>
/// Bots A2's route stall watch (engine-free, tested): a walk toward a route point is stalled when its 3D distance has not fallen by <see cref="ClassicBotBrain.RouteStallGain"/> m
/// in <see cref="ClassicBotBrain.RouteStallSeconds"/> s of route walking. A new goal, or a gap of more than half a second between steps (a fight, a vantage), starts the watch again;
/// a paused step (the bot waits at a jump take-off for power, or is in the jump) freezes the watch: its time never counts toward a stall, the time before and after it does.
/// </summary>
public sealed class RouteStallWatch
{
    double _since, _seen = double.MinValue;
    float _best;
    V3 _goal;

    /// <summary>One step at <paramref name="now"/> (s) of the walk from <paramref name="here"/> toward <paramref name="goal"/>; true once when the walk has stalled (the watch then starts again).</summary>
    public bool Step(double now, V3 goal, V3 here, bool paused = false)
    {
        float d = V3.Distance(here, goal);
        if (V3.DistanceSquared(goal, _goal) > 0.25f || now - _seen > 0.5) { _goal = goal; _best = d; _since = now; }
        else if (paused) _since += now - _seen;   // frozen, not reset: short unpaused stretches still add up
        _seen = now;
        if (paused) return false;
        if (d < _best - ClassicBotBrain.RouteStallGain) { _best = d; _since = now; }
        if (now - _since < ClassicBotBrain.RouteStallSeconds) return false;
        _since = now; _best = d;
        return true;
    }
}
