using System;
using SmNet;
using SmSim;

namespace SmBots;

/// <summary>
/// Classic strategy, M19: the Annihilation bot's chase when it has nothing to fight (moved from the client's old NetworkView.Bot.cs; the sight and walking it uses stay in
/// ClassicBotBrain.Bot.cs): the push, the post, the chase or hunt, the lane leg, the idle cycle and the walk to the map centre. Test script, not the original AI.
/// </summary>
public sealed partial class ClassicBotBrain
{
    /// <summary>
    /// Annihilation bot, when there is nothing to fight (<see cref="BotCanFight"/> said no): on a map, follow a path (<see cref="BotSteer"/>)
    /// toward the nearest living enemy's last shown position, sprinting while more than <see cref="BotChaseSprint"/> m of
    /// path remain (not toward a drop), facing the way it walks and not firing; with no enemy alive, toward the map's centre
    /// until within <see cref="BotCentreArrived"/> m. Null (the bot runs its old circle) on flat ground, when dead, or with
    /// no enemy and no known centre.
    /// </summary>
    PlayerInput? BotChaseInput(PlayerState me, uint tick)
    {
        if (_client?.Collision is null || !me.Alive) return null;
        System.Numerics.Vector3 goal;
        float distance;
        string purpose;
        // M44a: an enemy in its spawn room cannot be reached (the blockers) and is not chased; since 2026-11-07 neither is one in the no-loiter zone around it, and with
        // only such enemies (or none alive) the bot holds its centre spot and roams (ClassicBotBrain.BotRoam.cs; the old wait at the door only with --bot-door-wait).
        // 2026-11-08: a melee push goes first, through its concealed approach (ClassicBotBrain.BotPush.cs).
        if (BotPushChaseInput(me, tick) is { } pushInput) return pushInput;
        // 2026-11-08: a ramp or balcony holder goes to its post and holds it instead of chasing (ClassicBotBrain.BotPosts.cs).
        if (BotPostInput(me, tick) is { } postInput) return postInput;
        var chased = BotChaseTarget(me);
        chased ??= BotHuntTarget(me);   // 2026-11-16: nobody to chase: hunt an enemy outside its spawn room instead of patrolling (ClassicBotBrain.BotHunt.cs)
        if (chased is null && BotHoldInput(me, tick) is { } holdInput) return holdInput;
        // 2026-11-07, Annihilation: a life's first leg goes through its lane, so a side's bots leave the spawn by different routes (ClassicBotBrain.BotRoam.cs).
        if (BotLaneInput(me, tick, chased) is { } laneInput) return laneInput;
        if (chased is null && BotIdleInput(me, tick) is { } idleInput) return idleInput;
        if (chased is { } enemy)
        {
            // Stream C: with an ally already engaging it, come in from the side (a flank point; ClassicBotBrain.BotTactics.cs). 2026-11-07: else a long chase bends
            // through a via point by rank (Annihilation; ClassicBotBrain.BotRoam.cs).
            var flank = BotFlankGoal(me, enemy);
            var via = flank is null ? BotChaseVia(me, enemy) : null;
            goal = flank ?? via ?? BotPathGoal(enemy.Position);
            distance = new Vector2(me.Position.X, me.Position.Y).DistanceTo(new Vector2(goal.X, goal.Y));
            _botNote = $"chasing P{enemy.PlayerId} at {distance:F0} m{(distance <= BotFightRange ? " (not visible)" : "")}{(flank is null ? "" : " flank")}";
            if (via is not null) _botNote += " via";
            purpose = flank is not null ? $"flank P{enemy.PlayerId}" : via is not null ? $"chase P{enemy.PlayerId} via" : $"chase P{enemy.PlayerId}";
        }
        else if (_mapCentre is { } centre && new Vector2(me.Position.X, me.Position.Y).DistanceTo(centre) is var toCentre && toCentre > BotCentreArrived)
        {
            goal = _mapCentre3 ?? new System.Numerics.Vector3(centre.X, centre.Y, me.Position.Z);
            distance = toCentre;
            _botNote = $"no enemy alive, walking to the map centre {distance:F0} m";
            purpose = "map centre";
        }
        else return null;
        // Path following on the navigation graph (ClassicBotBrain.Nav.cs); the straight walk with detours until it is built.
        var (to, speed, _, dropAhead) = BotSteer(me, goal, purpose);
        if (Nav is null && _connectedFor < _botDetourUntil) _botNote += " detour";
        if (to.LengthSquared() < 1e-6f) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        _yaw = MathF.Atan2(-to.X, to.Y);
        _pitch = 0f;
        float remaining = Nav is not null && _navAgent is { Path: not null } agent ? agent.Remaining : distance;
        return BotMoveInput(tick, to * speed, (remaining > BotChaseSprint && !dropAhead && _navAgent is not { OnJumpLink: true } ? InputButtons.Sprint : InputButtons.None) | _navJump);
    }
}
