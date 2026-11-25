using System;
using System.Collections.Generic;
using System.Linq;
using SmContent;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmBots;

/// <summary>
/// Classic strategy, M16 (protocol 11): the Seize Ground bot (moved from the client's NetworkView.Seize.cs, whose point markers and showcase cameras stay there):
/// it heads for a point its side does not hold fully (or a contested one), a side's bots spread over those points (<see cref="BotSeizePoint"/>), along a navigation
/// path (ClassicBotBrain.Nav.cs), firing at enemies it can see within its weapon's range on the way (M19: not at ones behind walls), and once inside fights as before
/// but stays in. With every point held it plays as in Annihilation. Test script, not the original AI.
/// </summary>
public sealed partial class ClassicBotBrain
{
    const float BotFightRange = 25f;            // metres within which the bot fights on its way to a point (test script)
    const float BotPointInside = 0.6f;          // fraction of the point radius the bot walks to before it counts as in (test script)
    // Bot: stuck detection on the way to a goal (BotWalkDirection).
    Vector2 _botLastCheckPos;
    double _botLastCheckAt, _botDetourUntil, _botWalkAt = double.NegativeInfinity;
    int _botDetours;
    // Detour directions tried in turn when stuck (degrees from the direction to the point; test script).
    static readonly float[] BotDetourAngles = [90f, -90f, 180f, 135f, -135f];
    /// <summary>The bot's last Seize Ground decision, for its log.</summary>
    string _botSeizeNote = "";

    /// <summary>The letter of control point <paramref name="index"/> (A, B, C in the layout's order), as the client's HUD names it.</summary>
    static string PointLetter(int index) => BotLabels.PointLetter(index);

    /// <summary>
    /// Seize Ground bot input, or null to play as in Annihilation (not Seize Ground, dead, no layout, or every point held
    /// fully and uncontested, or already inside the chosen point: then the normal fight runs, and with nothing to fight the bot chases the nearest enemy or walks to the map centre).
    /// </summary>
    PlayerInput? BotSeizeInput(PlayerState me, uint tick, double phase)
    {
        if (!SeizeGround || !me.Alive || _world.SeizeLayout is not { } layout || _botMelee || BotGoal is not null || BotRoute is not null) return null;
        var points = _client.Points;
        int best = BotSeizePoint(me, layout, points);
        if (best < 0) { _botSeizeNote = "all points held"; return null; }
        var goal = layout.Points[best];
        if (BotSeizeSurplusHunts(me, layout, goal)) { _botSeizeNote = $"surplus on point {PointLetter(best)}: hunting"; return null; }   // 2026-11-16 (ClassicBotBrain.BotHunt.cs)
        float bestDistance = new Vector2(goal.Center.X - me.Position.X, goal.Center.Y - me.Position.Y).Length();
        // Fight only an enemy it can see within BotFightRange (M19); otherwise it keeps walking without aiming at it.
        var target = BotFightTarget(me, out float targetDistance);
        bool fight = target is not null;
        bool inside = bestDistance <= goal.Radius * BotPointInside;
        _botSeizeNote = $"goal {PointLetter(best)} {bestDistance:F1} m{(inside ? " inside" : "")}{(Nav is null && _connectedFor < _botDetourUntil ? " detour" : "")}{(fight ? $" fighting at {targetDistance:F0} m (visible)" : "")}";
        if (inside)
        {
            if (fight) return null;                                                         // fight as usual, staying near
            return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);              // hold the point
        }
        // On the way: path following to the point (ClassicBotBrain.Nav.cs; until the graph is built, straight with detours).
        var (to, speed, _, dropAhead) = BotSteer(me, BotPointStand(best, goal), $"point {PointLetter(best)}");
        if (to.LengthSquared() < 1e-6f) to = new Vector2(goal.Center.X - me.Position.X, goal.Center.Y - me.Position.Y).Normalized();
        InputButtons buttons = InputButtons.None;
        if (fight && target is { } enemy)
        {
            // Aim and shoot at the enemy while walking to the point.
            float dx = enemy.Position.X - me.Position.X, dy = enemy.Position.Y - me.Position.Y;
            float dz = enemy.Position.Z + AimHeightOf(enemy) - (me.Position.Z + _config!.EyeHeight);
            _yaw = MathF.Atan2(-dx, dy);
            _pitch = Math.Clamp(MathF.Atan2(dz, targetDistance), -MaxPitchDown, MaxPitchUp);
            buttons = BotWeaponButtons(me, tick, enemy) | BotAimButton(me, targetDistance);   // stream C: aim mode in a gunfight (ClassicBotBrain.BotTactics.cs)
            _botAimNow = true;                  // M44a: the aim error applies to this ranged shot (ClassicBotBrain.BotAim.cs)
            _botAimTarget = enemy.PlayerId;     // the trigger discipline's reaction delay
            BotNoteEngagement(targetDistance);
        }
        else
        {
            _yaw = MathF.Atan2(-to.X, to.Y);
            _pitch = 0f;
            if (bestDistance > 8f && !dropAhead && _navAgent is not { OnJumpLink: true }) buttons = InputButtons.Sprint;
        }
        // On a jump link (slowing to the take-off cell, standing, pressing) the agent's speed counts; else full speed as before.
        return BotMoveInput(tick, _navAgent is { OnJumpLink: true } ? to * speed : to, buttons | _navJump);
    }

    /// <summary>
    /// bots/new-maps round 2 (ours): where a Seize Ground bot walks to for point <paramref name="index"/>: on the new maps the graph cell nearest the marker inside the capture zone (the marker can float far above the
    /// floor: Hab Center's point D, 7 m, which the planner's 6 m height limit read as off the graph, and its bots stood at their spawn rooms for the whole match); the marker itself elsewhere (Manufactorum, no graph).
    /// </summary>
    V3 BotPointStand(int index, ControlPointInfo goal)
    {
        if (BotNoEscape || !BotNavigation.NewMapRules(_navMap?.Name ?? "") || _nav is not { } graph) return goal.Center;
        if (!ReferenceEquals(graph, _botStandGraph)) { _botStandGraph = graph; _botStand.Clear(); }
        if (!_botStand.TryGetValue(index, out var stand))
        {
            int n = graph.NearestInCylinder(goal.Center, goal.Radius * BotPointInside, goal.HalfHeight);
            _botStand[index] = stand = n >= 0 ? graph.Position(n) : goal.Center;
        }
        return stand;
    }

    NavGraph? _botStandGraph;
    readonly Dictionary<int, V3> _botStand = new();

    /// <summary>The point the Seize Ground bot heads for (sticky while still wanted).</summary>
    int _botSeizeTarget = -1;

    /// <summary>
    /// The Seize Ground bot's point (test script): of the points its side does not hold fully (or that are contested), in
    /// order of distance from the side's start area (so the order does not change as the bot walks), the bot takes the one
    /// at its rank among its side's players by id (wrapping), so a side's bots spread over the points; chosen again when the
    /// wanted points or its rank change (so after taking its own flank a side moves on to the middle and the enemy's flank).
    /// -1 when every point is held.
    /// </summary>
    int BotSeizePoint(PlayerState me, SeizeGroundInfo layout, IReadOnlyList<ControlPointView> points)
    {
        bool Wanted(int i) => i < points.Count && (points[i].Owner != me.Team || points[i].Contested || points[i].Progress < 0.999f);
        long set = 0;
        for (int i = 0; i < layout.Points.Length && i < 63; i++) if (Wanted(i)) set |= 1L << i;
        int id = _client?.PlayerId ?? 0;
        int rank = _lastOthers.Count(o => o.Team == me.Team && o.PlayerId < id);
        // Same wanted points and team rank as when it chose: keep the point.
        if (set == _botSeizeWanted && rank == _botSeizeRank && _botSeizeTarget >= 0 && _botSeizeTarget < layout.Points.Length && Wanted(_botSeizeTarget)) return _botSeizeTarget;
        _botSeizeWanted = set;
        _botSeizeRank = rank;
        var home = _namedPoints.TryGetValue(me.Team == Team.SpaceMarines ? "marines" : "chaos", out var area) ? area : me.Position;
        var wanted = Enumerable.Range(0, layout.Points.Length).Where(Wanted)
            .OrderBy(i => System.Numerics.Vector3.Distance(layout.Points[i].Center, home)).ToList();
        int before = _botSeizeTarget;
        _botSeizeTarget = wanted.Count == 0 ? -1 : wanted[rank % wanted.Count];
        if (_botSeizeTarget != before) _navAgent?.Reset();
        return _botSeizeTarget;
    }

    long _botSeizeWanted = -1;
    int _botSeizeRank = -1;
}
