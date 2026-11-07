using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmClient;

/// <summary>
/// The owner's melee push (2026-11-08; test script, **ours**, not the original AI; engine-free parts in <see cref="BotPush"/>, <see cref="BotApproach"/> and
/// <see cref="BotHopper"/>): "periodically choose to close in and attempt melee combat. but taking a route that minimizes the enemy's ability to see the bot on the
/// approach. raptor and assault marines in particular try to stay low but approach the enemy with hops and dashes, then jump no them and shoot/melee." Here:
/// <list type="bullet">
/// <item>every 15 to 30 s a bot may start a push on the enemy it would chase (chance by role: jump pack 75 %, Tactical 35 %, heavy 15 %);</item>
/// <item>it walks (sprinting while unseen) through a concealed approach planned off the frame loop (a cover cell and a staging cell 4 to 11 m from the enemy that
/// the enemy's eye cannot see), and while it sees the enemy it keeps closing (firing, weaving) instead of standing to strafe;</item>
/// <item>a jump-pack bot alternates forward dashes and small hops on the way and jumps onto the enemy from 16 m or less (the jump-and-pound script, limited to
/// that range while pushing), then shoots and melees as before; the others charge and melee at 9 m or less as before;</item>
/// <item>the push ends when the enemy dies or goes back into its spawn zone, the bot dies, 4 s after reaching melee reach, or after 30 s.</item>
/// </list>
/// Annihilation only (Seize Ground keeps its point logic; Musou bots return earlier, <c>ExtrasBotInput</c>). <c>--bot-no-push</c> turns it off.
/// </summary>
public partial class NetworkView
{
    static readonly bool BotNoPush = CmdFlag("bot-no-push");
    /// <summary>Metres within which a pushing bot without a jump pack hands over to the charge and melee script (the charge's 4 to 9 m).</summary>
    const float PushCloseRange = 9f;
    /// <summary>Metres from an approach point at which it counts as passed, and the enemy movement (m) or age (s) that plans it again.</summary>
    const float ApproachPass = 2.5f, ApproachReplanMove = 5f;
    const double ApproachReplanAge = 4.0;

    BotPush? _botPush;
    BotHopper? _botHopper;
    Task<ApproachPlan>? _approachTask;
    ApproachPlan? _approach;
    int _approachFor = -1, _approachIndex;
    V3 _approachEnemyAt;
    double _approachAt = double.NegativeInfinity;
    int _pushLogged;

    /// <summary>Per bot tick (Classic PvP): starts and ends pushes (<see cref="BotPush.Step"/>).</summary>
    void BotPushStep(PlayerState me)
    {
        if (BotNoPush || _config is null || _client?.Collision is null || me.Team == Team.None) return;
        _botPush ??= new BotPush(Math.Max(_client.PlayerId, 0) * 104729 + 3);
        if (!me.Alive) { if (_botPush.Active) { _botPush.End(_connectedFor); GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} push ends: died"); } return; }
        string role = Loadouts.ClassOf(_config, me.Class)?.Role ?? "";
        var candidate = BotChaseTarget(me);
        float distance = candidate is { } c ? new Vector2(c.Position.X - me.Position.X, c.Position.Y - me.Position.Y).Length() : float.MaxValue;
        var target = _botPush.Active ? _lastOthers.FirstOrDefault(o => o.PlayerId == _botPush.Target) : default;
        bool alive = _botPush.Active && target.PlayerId == _botPush.Target && target.Alive && !InEnemyRoom(me, target) && !InEnemyZone(me, target);
        bool inMelee = alive && (new Vector2(target.Position.X - me.Position.X, target.Position.Y - me.Position.Y).Length() <= BotMeleeDistance + 1f || me.MeleeStep > 0 || me.Rush != RushPhase.None);
        bool was = _botPush.Active;
        bool now = _botPush.Step(_connectedFor, role, candidate?.PlayerId ?? -1, distance, alive, inMelee);
        if (now && !was)
        {
            _botRushNext = Math.Min(_botRushNext, _connectedFor);   // the charge is ready once in reach
            _approachFor = -1;
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} push {_botPush.Pushes} starts on P{_botPush.Target} at {distance:F0} m ({role})");
        }
        else if (was && !now) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} push ends ({(alive ? inMelee ? "melee done" : "timeout" : "target dead or back in its spawn")}); dashes {_botHopper?.Dashes ?? 0}, hops {_botHopper?.Hops ?? 0}");
    }

    /// <summary>The enemy of the push under way (null without one).</summary>
    PlayerState? PushTarget()
    {
        if (_botPush is not { Active: true } push) return null;
        foreach (var o in _lastOthers) if (o.PlayerId == push.Target && o.Alive) return o;
        return null;
    }

    /// <summary>The next point of the concealed approach to <paramref name="enemy"/> (planned on the thread pool, again when it moved or aged); null: go straight at it.</summary>
    V3? BotApproachGoal(PlayerState me, PlayerState enemy)
    {
        if (_approachTask is { IsCompleted: true } task)
        {
            _approachTask = null;
            if (task.IsCompletedSuccessfully)
            {
                _approach = task.Result; _approachIndex = 0;
                if (_pushLogged++ < 40)
                    GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} approach to P{enemy.PlayerId}: {string.Join(" ", _approach.Waypoints.Select(p => $"{p.X:F0},{p.Y:F0},{p.Z:F0}"))} seen {_approach.Exposure * 100f:F0} % (straight {_approach.DirectExposure * 100f:F0} %)");
            }
        }
        bool stale = enemy.PlayerId != _approachFor || V3.Distance(enemy.Position, _approachEnemyAt) > ApproachReplanMove || _connectedFor - _approachAt > ApproachReplanAge;
        if (stale && _approachTask is null && _nav is { } graph && _client?.Collision is { } world && _config is { } config)
        {
            if (enemy.PlayerId != _approachFor) _approach = null;
            _approachFor = enemy.PlayerId; _approachEnemyAt = enemy.Position; _approachAt = _connectedFor;
            var from = me.Position; var at = enemy.Position; float eye = config.EyeHeight; var cancel = _navCancel.Token;
            _approachTask = Task.Run(() => BotApproach.Plan(graph, world, from, at, eye, cancel), cancel);
        }
        if (_approach is not { } plan) return null;
        while (_approachIndex < plan.Waypoints.Count && new Vector2(plan.Waypoints[_approachIndex].X - me.Position.X, plan.Waypoints[_approachIndex].Y - me.Position.Y).Length() <= ApproachPass) _approachIndex++;
        return _approachIndex < plan.Waypoints.Count ? plan.Waypoints[_approachIndex] : null;
    }

    /// <summary>A jump-pack bot's dash or hop press on the approach (<see cref="BotHopper"/>), else nothing; a hop's hold is kept by the jump script's fields.</summary>
    InputButtons BotHopButtons(PlayerState me, uint tick, float distance)
    {
        if (_config is not { } c || Loadouts.JumpPack(c, me) is not { } pack) return InputButtons.None;
        if (_botJumpHolding && tick <= _botJumpHoldUntil) return InputButtons.Jump;   // a hop's hold under way
        _botHopper ??= new BotHopper(Math.Max(_client?.PlayerId ?? 0, 0) * 31 + 5);
        bool free = me.Grounded && me.Jump == JumpPhase.None && !me.JumpHeld && me.MeleeStep == 0 && !me.Throwing && me.SwitchLeft <= 0f && !me.Charging && me.PendingShot == 0;
        bool canDash = free && Loadouts.Evade(c, me) is { Dash: true } && !Evade.Cooling(me);
        int hopTicks = NavJumpFlight.ShortestJumpHold(c, me.Class) + 1;
        float hopCost = pack.CostAt(hopTicks * c.TickSeconds);
        var move = _botHopper.Next(_connectedFor, canDash, free, me.JumpPower, hopCost, pack.CostAt(pack.MaxHold), distance);
        if (move == BotHopper.Move.Hop) { _botJumpHoldUntil = tick + (uint)hopTicks; _botJumpHolding = true; }
        return move == BotHopper.Move.None ? InputButtons.None : InputButtons.Jump;
    }

    /// <summary>
    /// The push while the enemy is not in sight (called first in <see cref="BotChaseInput"/>): through the concealed approach, sprinting, a jump-pack bot dashing
    /// and hopping. Null without a push.
    /// </summary>
    PlayerInput? BotPushChaseInput(PlayerState me, uint tick)
    {
        if (!me.Alive || PushTarget() is not { } enemy) return null;
        var via = BotApproachGoal(me, enemy);
        var goal = via ?? BotPathGoal(enemy.Position);
        float distance = new Vector2(enemy.Position.X - me.Position.X, enemy.Position.Y - me.Position.Y).Length();
        _botNote = $"pushing P{enemy.PlayerId} at {distance:F0} m{(via is null ? "" : " (concealed)")}";
        var input = BotWalkTo(me, tick, goal, via is null ? $"push P{enemy.PlayerId}" : $"push P{enemy.PlayerId} concealed");
        var hop = BotHopButtons(me, tick, distance);
        return hop == InputButtons.None ? input : input with { Buttons = (input.Buttons & ~InputButtons.Sprint) | hop };
    }

    /// <summary>
    /// The push while the bot sees <paramref name="target"/> (in the fight, after the jump script): farther than the hand-over range it keeps closing along the
    /// approach (weaving, firing, a jump-pack bot dashing and hopping) instead of strafing; within it (16 m with a jump pack, else 9 m) null, so the jump, charge and
    /// melee scripts run.
    /// </summary>
    PlayerInput? BotPushFightInput(PlayerState me, PlayerState target, uint tick, float horizontal)
    {
        if (PushTarget() is not { } enemy || enemy.PlayerId != target.PlayerId || _config is null) return null;
        if (horizontal <= PushCloseRange) return null;   // the charge and melee scripts (a jump pack's jump onto the target ran before this, from 16 m)
        if (Loadouts.JumpPack(_config, me) is not null && horizontal <= BotHopper.JumpInRange && _connectedFor - _botLastJump < BotJumpEvery + 1.0) return null;   // just jumped at it: the air script runs
        var goal = BotApproachGoal(me, enemy) ?? BotPathGoal(enemy.Position);
        var (to, speed, _, _) = BotSteer(me, goal, $"push P{enemy.PlayerId}");
        if (to.LengthSquared() < 1e-6f) to = new Vector2(target.Position.X - me.Position.X, target.Position.Y - me.Position.Y).Normalized();
        var weapon = BotWeaponButtons(me, tick, target) & ~InputButtons.Switch;
        _botAimNow = true; _botAimTarget = target.PlayerId;
        BotNoteEngagement(horizontal);
        _botNote = $"pushing P{target.PlayerId} at {horizontal:F0} m (in sight)";
        var hop = BotHopButtons(me, tick, horizontal);
        return BotMoveInput(tick, BotWeave(me, to, horizontal) * MathF.Max(speed, 0.6f), weapon | hop | _navJump);
    }
}
