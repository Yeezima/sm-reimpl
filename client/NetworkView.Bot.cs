using System;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// M19: the test bot's line of sight, walking and chasing, shared by the Annihilation bot (<see cref="BotInput"/>) and the
/// Seize Ground bot (<see cref="BotSeizeInput"/>). On a map the bot only fires at an enemy it can see within
/// <see cref="BotFightRange"/> (a ray from its eye to the enemy's chest against the shot collision); otherwise it walks
/// toward the nearest living enemy's last shown position, and with no enemy alive toward the map's centre, along A* paths
/// on the navigation graph (NetworkView.Nav.cs; until the graph is built: straight, detouring when stuck). Without map
/// collision (flat ground) nothing changes: every living enemy is a target at any range. Test script, not the original AI.
/// </summary>
public partial class NetworkView
{
    const float BotChaseSprint = 30f;           // metres beyond which a chasing bot sprints (test script)
    const float BotCentreArrived = 8f;          // metres from the map centre at which the idle bot stops walking to it (test script)

    /// <summary>The mean of the map's spawn points on the ground plane (game space), set when the map loads; null on flat ground.</summary>
    Vector2? _mapCentre;
    /// <summary>The bot's last Annihilation decision (fighting or chasing), for its log.</summary>
    string _botNote = "";

    /// <summary>
    /// Whether the target's chest can be seen from the bot's eye: no surface of the shot collision on the straight line.
    /// Always true without map collision.
    /// </summary>
    bool BotSees(PlayerState me, PlayerState target)
    {
        if (_client?.Collision is not { } world) return true;
        var from = me.Position + new System.Numerics.Vector3(0, 0, _config!.EyeHeight);
        var to = target.Position + new System.Numerics.Vector3(0, 0, BotTargetHeight);
        var line = to - from;
        float length = line.Length();
        return length <= 0.3f || world.Weapons.Raycast(from, line / length, length - 0.15f) is null;
    }

    /// <summary>
    /// Whether the bot should fight <paramref name="target"/> now: on a map, within <see cref="BotFightRange"/> (horizontally,
    /// returned in <paramref name="distance"/>) and visible (<see cref="BotSees"/>); on flat ground always. A fight is
    /// written to <see cref="_botNote"/>.
    /// </summary>
    bool BotCanFight(PlayerState me, PlayerState target, out float distance)
    {
        distance = MathF.Sqrt((target.Position.X - me.Position.X) * (target.Position.X - me.Position.X) + (target.Position.Y - me.Position.Y) * (target.Position.Y - me.Position.Y));
        if (_client?.Collision is null) { _botNote = $"fighting P{target.PlayerId} at {distance:F0} m"; return true; }
        // M44a: farther than the old 25 m up to the weapon's range (or --bot-range), line of sight the only limit; never at an enemy in its spawn room.
        if (distance > BotEngageRange(me) || InEnemyRoom(me, target) || !BotSees(me, target)) return false;
        _botNote = $"fighting P{target.PlayerId} at {distance:F0} m (visible)";
        return true;
    }

    /// <summary>
    /// The enemy to fight: the nearest living enemy that passes <see cref="BotCanFight"/> (so a visible enemy is not passed
    /// over for a nearer one behind a wall), its horizontal distance in <paramref name="distance"/>; null when none does.
    /// </summary>
    PlayerState? BotFightTarget(PlayerState me, out float distance)
    {
        distance = float.MaxValue;
        foreach (var other in _lastOthers.Where(o => o.Alive && (me.Team == Team.None || World.Hostile(me.Team, o.Team)))
                     .OrderBy(o => System.Numerics.Vector3.DistanceSquared(o.Position, me.Position)))
            if (BotCanFight(me, other, out distance)) return other;
        distance = float.MaxValue;
        return null;
    }

    /// <summary>
    /// The unit direction (ground plane, game space) to walk straight toward <paramref name="goal"/>: the fallback while the
    /// navigation graph is not built (<see cref="BotSteer"/>). When the bot has moved less than
    /// 1 m in 1.5 s of walking it detours for 1.5 s at one of <see cref="BotDetourAngles"/> off the direct line (a different
    /// one each time, offset by player id). Time spent not walking (fighting, holding a point) does not count as being stuck.
    /// </summary>
    Vector2 BotWalkDirection(PlayerState me, Vector2 goal)
    {
        var here = new Vector2(me.Position.X, me.Position.Y);
        var to = goal - here;
        to = to.LengthSquared() > 1e-6f ? to.Normalized() : new Vector2(0, 1);
        if (_connectedFor - _botWalkAt > 0.5) { _botLastCheckPos = here; _botLastCheckAt = _connectedFor; }
        _botWalkAt = _connectedFor;
        if (_connectedFor - _botLastCheckAt >= 1.5)
        {
            if (here.DistanceTo(_botLastCheckPos) < 1.0f && _connectedFor >= _botDetourUntil) { _botDetourUntil = _connectedFor + 1.5; _botDetours++; }
            _botLastCheckPos = here; _botLastCheckAt = _connectedFor;
        }
        if (_connectedFor < _botDetourUntil)
        {
            int id = Math.Max(_client?.PlayerId ?? 0, 0);
            to = to.Rotated(Mathf.DegToRad(BotDetourAngles[(_botDetours + id) % BotDetourAngles.Length]));
        }
        return to;
    }

    /// <summary>
    /// M44a: the point a path to an enemy at <paramref name="p"/> is planned to: <paramref name="p"/> itself, or the floor under it when it is in the
    /// air (a jump pack: the graph has no floor within 3 m of it, so a plan to it failed and the bot walked straight at it, into walls).
    /// </summary>
    System.Numerics.Vector3 BotPathGoal(System.Numerics.Vector3 p)
    {
        if (_nav is not { } graph || _client?.Collision is not { } world || graph.Nearest(p, 2f, 3f) >= 0) return p;
        return world.Character.Raycast(p + new System.Numerics.Vector3(0, 0, 0.3f), -System.Numerics.Vector3.UnitZ, 80f) is { } hit ? hit.Point : p;
    }

    /// <summary>An input moving in the ground-plane direction <paramref name="to"/> (unit, game space) while facing the current yaw.</summary>
    PlayerInput BotMoveInput(uint tick, Vector2 to, InputButtons buttons)
    {
        var forward = new Vector2(-MathF.Sin(_yaw), MathF.Cos(_yaw));
        var right = new Vector2(forward.Y, -forward.X);
        return new PlayerInput(tick, to.Dot(right), to.Dot(forward), _yaw, buttons, _pitch);
    }

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
        // M44a: an enemy in its spawn room cannot be reached (the blockers) and is not chased; with only such enemies the bot waits at a hold point 8-15 m from the door (NetworkView.BotSpawn.cs).
        var chased = BotChaseTarget(me);
        if (chased is null && BotHoldInput(me, tick) is { } holdInput) return holdInput;
        if ((chased ?? NearestLivingTarget(me)) is { } enemy)
        {
            goal = BotPathGoal(enemy.Position);
            distance = new Vector2(me.Position.X, me.Position.Y).DistanceTo(new Vector2(goal.X, goal.Y));
            _botNote = $"chasing P{enemy.PlayerId} at {distance:F0} m{(distance <= BotFightRange ? " (not visible)" : "")}";
            purpose = $"chase P{enemy.PlayerId}";
        }
        else if (_mapCentre is { } centre && new Vector2(me.Position.X, me.Position.Y).DistanceTo(centre) is var toCentre && toCentre > BotCentreArrived)
        {
            goal = _mapCentre3 ?? new System.Numerics.Vector3(centre.X, centre.Y, me.Position.Z);
            distance = toCentre;
            _botNote = $"no enemy alive, walking to the map centre {distance:F0} m";
            purpose = "map centre";
        }
        else return null;
        // Path following on the navigation graph (NetworkView.Nav.cs); the straight walk with detours until it is built.
        var (to, speed, _, dropAhead) = BotSteer(me, goal, purpose);
        if (Nav is null && _connectedFor < _botDetourUntil) _botNote += " detour";
        if (to.LengthSquared() < 1e-6f) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        _yaw = MathF.Atan2(-to.X, to.Y);
        _pitch = 0f;
        float remaining = Nav is not null && _navAgent is { Path: not null } agent ? agent.Remaining : distance;
        return BotMoveInput(tick, to * speed, (remaining > BotChaseSprint && !dropAhead && _navAgent is not { OnJumpLink: true } ? InputButtons.Sprint : InputButtons.None) | _navJump);
    }
}
