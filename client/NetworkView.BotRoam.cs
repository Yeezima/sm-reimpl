using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmClient;

/// <summary>
/// The owner's Classic PvP bot behaviour of 2026-11-07 (test script, **ours**, not the original AI; engine-free parts in <see cref="BotMapPlan"/> and <see cref="BotRoamer"/>):
/// "when there are no enemies left on the map (outside of the enemy spawn zone) - or after wiping the enemy team, the bots should default to holding defensive positions
/// around the center of the map. the bots should generally take different routes out around the map as well and spread out more on annihilation game mode in particular."
/// and "the bot should never wait directly outside the spawn area of the enemy. they will simply roam around the map until the enemy has spread out and pressured the center
/// of the map after respawning and returning." Here:
/// <list type="bullet">
/// <item>an enemy in its spawn room or in the no-loiter zone around it is not chased (<see cref="BotChaseTarget"/>); one the bot sees is still fought;</item>
/// <item>with nobody to chase, the bot walks to its own centre hold (by team rank), holds it facing the enemy's side, then roams a point and comes back
/// (<see cref="BotIdleInput"/>);</item>
/// <item>Annihilation: each life starts with a lane leg (left, middle or right; by rank and life) and a long chase bends through a via point by rank, so a side's bots
/// spread over the map (<see cref="BotLaneInput"/>, <see cref="BotChaseVia"/>). Seize Ground keeps its point logic; the idle hold runs only when it falls back to the
/// Annihilation behaviour (every point held).</item>
/// </list>
/// The plan is built on the thread pool after the spawn watch; until then the bot behaves as before, except that it never chases into the spawn room.
/// </summary>
public partial class NetworkView
{
    BotRoamer? _botRoamer;
    int _botLife = -1;
    bool _botWasAlive;
    string _botIdleNote = "";
    int _viaFor = -1;
    V3? _viaPoint;
    double _viaSince;
    bool _viaDone;

    /// <summary>The current side's map plan once built (null before, without teams, or when none could be built).</summary>
    BotMapPlan? Plan
    {
        get
        {
            if (!_bot || _navCurrent is not { } slot) return null;
            _ = Watch;   // the watch's build goes first (the door)
            if (!slot.PlanDone && slot.PlanTask is null && slot.Graph is { } graph && (slot.WatchDone || BotNoSpawnWait) && _navMap is { } map && _navWorld is { } world
                && _client is { } client && _mapCentre3 is { } centre)
            {
                var team = client.Predicted.Team;
                var door = slot.Watch?.Door;
                var cancel = _navCancel.Token;
                slot.PlanTask = Task.Run(() =>
                {
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var own = map.StartAreas.FirstOrDefault(a => a.Side == team);
                    var theirs = map.StartAreas.FirstOrDefault(a => a.Side == MatchRules.Other(team));
                    if (team == Team.None || own is null || theirs is null) return ((BotMapPlan?)null, "no start area for both sides");
                    var plan = BotMapPlan.Build(graph, own.Position, theirs.Position, door, centre, world, cancel);
                    return (plan, $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
                }, cancel);
            }
            if (!slot.PlanDone && slot.PlanTask is { IsCompleted: true } task)
            {
                slot.PlanTask = null;
                slot.PlanDone = true;
                if (task.IsCompletedSuccessfully)
                {
                    slot.Plan = task.Result.Item1;
                    static string P(V3 p) => $"{p.X:F0},{p.Y:F0},{p.Z:F0}";
                    GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} map plan ({task.Result.Item2}): {slot.Plan?.Note ?? task.Result.Item2}");
                    if (slot.Plan is { } p)
                        GD.Print($"  holds [{string.Join(" ", p.CentreHolds.Select(P))}] roam [{string.Join(" ", p.RoamPoints.Select(P))}] lanes [{string.Join(" ", p.Lanes.Select(P))}]");
                }
                else GD.PrintErr($"map plan failed: {task.Exception?.GetBaseException().Message}");
            }
            return slot.Plan;
        }
    }

    /// <summary>Whether <paramref name="enemy"/> stands in the no-loiter zone around its own spawn (<see cref="BotMapPlan.InNoLoiterZone"/>); false until the plan is built.</summary>
    bool InEnemyZone(PlayerState me, PlayerState enemy) =>
        me.Team != Team.None && enemy.Team == MatchRules.Other(me.Team) && Plan is { } plan && plan.InNoLoiterZone(enemy.Position);

    /// <summary>The bot's rank on its side by player id (0 for the lowest id): picks its hold, roam sequence, lane and via side.</summary>
    int BotRank(PlayerState me) => _lastOthers.Count(o => o.Team == me.Team && o.PlayerId > 0 && o.PlayerId < me.PlayerId);

    /// <summary>Counts the bot's lives (called every bot tick): a respawn starts a new lane leg and sends the idle cycle back to the hold.</summary>
    void BotTrackLife(PlayerState me)
    {
        if (me.Alive && !_botWasAlive) { _botLife++; _botRoamer?.Reset(); _viaFor = -1; }
        _botWasAlive = me.Alive;
    }

    /// <summary>The bot has an enemy to fight: the lane leg ends and the idle cycle starts again at the hold afterwards.</summary>
    void BotNoteFight()
    {
        _botRoamer?.EndLane();
        _botRoamer?.Reset();
        _viaFor = -1;
    }

    void NoteIdle(string note)
    {
        if (note == _botIdleNote) return;
        _botIdleNote = note;
        GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} idle: {note}");
    }

    /// <summary>A walk input toward <paramref name="goal"/> along the graph, sprinting while far (as the chase does).</summary>
    PlayerInput BotWalkTo(PlayerState me, uint tick, V3 goal, string purpose)
    {
        var (to, speed, _, dropAhead) = BotSteer(me, goal, purpose);
        if (to.LengthSquared() < 1e-6f) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        _yaw = MathF.Atan2(-to.X, to.Y);
        _pitch = 0f;
        float remaining = _navAgent is { Path: not null } agent ? agent.Remaining : new Vector2(me.Position.X, me.Position.Y).DistanceTo(new Vector2(goal.X, goal.Y));
        return BotMoveInput(tick, to * speed, (remaining > BotChaseSprint && !dropAhead && _navAgent is not { OnJumpLink: true } ? InputButtons.Sprint : InputButtons.None) | _navJump);
    }

    /// <summary>
    /// Nobody to chase (every living enemy is in or near its spawn, or none is alive): the bot's centre hold, held facing the enemy's side, then a roam point and back
    /// (<see cref="BotRoamer"/>). Null without a plan (the old walk to the centre runs then).
    /// </summary>
    PlayerInput? BotIdleInput(PlayerState me, uint tick)
    {
        if (!me.Alive || me.Team == Team.None || Plan is not { } plan) return null;
        _botRoamer ??= new BotRoamer(Math.Max(_client?.PlayerId ?? 0, 0) * 7919 + 17);
        int rank = BotRank(me);
        bool anyAlive = _lastOthers.Any(o => o.Alive && World.Hostile(me.Team, o.Team));
        if (_botRoamer.IdleGoal(plan, rank, me.Position, _connectedFor, out bool holding) is not { } goal) return null;
        string why = anyAlive ? "enemies only in or near their spawn" : "no enemy alive";
        if (holding)
        {
            var face = plan.EnemyDoor ?? plan.EnemyStart;
            _yaw = MathF.Atan2(-(face.X - me.Position.X), face.Y - me.Position.Y);
            _pitch = 0f;
            _botNote = $"holding centre spot {goal.X:F0},{goal.Y:F0} ({why})";
            NoteIdle($"holding centre spot {goal.X:F0},{goal.Y:F0},{goal.Z:F0} (rank {rank}; {why})");
            return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        }
        string what = _botRoamer.Doing == BotRoamer.Activity.Hold ? "to centre spot" : "roaming to";
        _botNote = $"{what} {goal.X:F0},{goal.Y:F0} ({why})";
        NoteIdle($"{what} {goal.X:F0},{goal.Y:F0},{goal.Z:F0} (rank {rank}; {why})");
        return BotWalkTo(me, tick, goal, _botRoamer.Doing == BotRoamer.Activity.Hold ? "centre hold" : "roam");
    }

    /// <summary>
    /// Annihilation: the first leg of a life goes through this life's lane (<see cref="BotRoamer.LaneGoal"/>), unless the chased enemy is already near (30 m). Null when the
    /// leg is over, in Seize Ground, or without a plan.
    /// </summary>
    PlayerInput? BotLaneInput(PlayerState me, uint tick, PlayerState? chased)
    {
        if (SeizeGround || !me.Alive || me.Team == Team.None || Plan is not { } plan) return null;
        _botRoamer ??= new BotRoamer(Math.Max(_client?.PlayerId ?? 0, 0) * 7919 + 17);
        if (chased is { } c && V3.Distance(c.Position, me.Position) < 30f) { _botRoamer.EndLane(); return null; }
        if (_botRoamer.LaneGoal(plan, BotRank(me), _botLife, me.Position, _connectedFor) is not { } lane) return null;
        _botNote = $"lane leg to {lane.X:F0},{lane.Y:F0}";
        NoteIdle($"life {_botLife}: lane leg to {lane.X:F0},{lane.Y:F0},{lane.Z:F0} (rank {BotRank(me)})");
        return BotWalkTo(me, tick, lane, "lane");
    }

    /// <summary>
    /// Annihilation: the point a long chase of <paramref name="enemy"/> bends through (<see cref="BotMapPlan.ViaFor"/>, by rank), kept until reached (4 m) or 20 s; null
    /// when there is none, it was passed, or in Seize Ground.
    /// </summary>
    V3? BotChaseVia(PlayerState me, PlayerState enemy)
    {
        if (SeizeGround || Plan is not { } plan) return null;
        if (enemy.PlayerId != _viaFor)
        {
            _viaFor = enemy.PlayerId; _viaSince = _connectedFor; _viaDone = false;
            _viaPoint = plan.ViaFor(me.Position, enemy.Position, BotRank(me));
            if (_viaPoint is { } v) NoteIdle($"chase P{enemy.PlayerId} via {v.X:F0},{v.Y:F0},{v.Z:F0} (rank {BotRank(me)})");
        }
        if (_viaDone || _viaPoint is not { } via) return null;
        if (new Vector2(me.Position.X - via.X, me.Position.Y - via.Y).Length() <= BotRoamer.PassRadius || _connectedFor - _viaSince > 20.0) { _viaDone = true; return null; }
        return via;
    }
}
