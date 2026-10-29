using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;

namespace SmClient;

/// <summary>
/// M44a: how a test bot treats an enemy inside its spawn room (test script, **ours**, not the original AI). The spawn room is sealed
/// by <see cref="SpawnBlocker"/> boxes the bot cannot enter (<c>CollisionWorld.CharacterFor</c>), so a path to a target inside ends
/// at the blocker, and the old chase made the bot stand against it, hugging the wall outside the door. The engine-free part is
/// <see cref="SpawnRoomWatch"/> (tested): the room is the floor an enemy can walk from its start area without coming out among floor the bot reaches,
/// the door the first such cell, and the hold points reachable cells 8 to 15 m from the door with a clear line to it. Here:
/// <list type="bullet">
/// <item>an enemy standing in the room (<see cref="InEnemyRoom"/>) is neither chased nor shot (no spawn killing through the blockers, which shots pass);</item>
/// <item>with no other enemy to go for, the bot walks to a hold point (its id picks one, so a side's bots spread) and stands there facing the door;</item>
/// <item>when the enemy walks out (its cell is no longer in the room) the normal fight and chase resume.</item>
/// </list>
/// The watch is built once per side and graph on the thread pool; until ready, or without teams, blockers or a graph, the bot behaves as before.
/// <c>--bot-no-spawn-wait</c> turns it off (before/after measurements).
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--bot-no-spawn-wait</c> (test aid): the old chase, hugging the blocker outside the other side's spawn room.</summary>
    static readonly bool BotNoSpawnWait = CmdFlag("bot-no-spawn-wait");

    /// <summary>The current side's spawn watch once its build has finished (null before, or when none could be built).</summary>
    SpawnRoomWatch? Watch
    {
        get
        {
            if (BotNoSpawnWait || _navCurrent is not { } slot) return null;
            if (!slot.WatchDone && slot.WatchTask is null && slot.Graph is { } graph && _navMap is { } map && _navWorld is { } world && _config is { } config && _client is { } client)
            {
                var team = client.Predicted.Team;
                var cancel = _navCancel.Token;
                slot.WatchTask = Task.Run(() =>
                {
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var own = map.StartAreas.FirstOrDefault(a => a.Side == team);
                    var theirs = map.StartAreas.FirstOrDefault(a => a.Side == MatchRules.Other(team));
                    if (own is null || theirs is null) return ((SpawnRoomWatch?)null, "the map has no start area for both sides");
                    var enemyGraph = BotNavigation.LoadOrBuildWalk(map, world, config, MatchRules.Other(team), BotNavigation.CacheDirectory, out _);
                    var w = SpawnRoomWatch.Build(graph, enemyGraph, team, own.Position, theirs.Position, world, config.EyeHeight, cancel);
                    return (w, $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
                }, cancel);
            }
            if (!slot.WatchDone && slot.WatchTask is { IsCompleted: true } task)
            {
                slot.WatchTask = null;
                slot.WatchDone = true;
                if (task.IsCompletedSuccessfully)
                {
                    slot.Watch = task.Result.Item1;
                    GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} spawn watch ({task.Result.Item2}): {slot.Watch?.Note ?? task.Result.Item2}");
                }
                else GD.PrintErr($"spawn watch failed: {task.Exception?.GetBaseException().Message}");
            }
            return slot.Watch;
        }
    }

    /// <summary>Whether <paramref name="enemy"/> stands in its own spawn room, which the bot cannot enter (<see cref="SpawnRoomWatch.InRoom"/>); false until the watch is built.</summary>
    bool InEnemyRoom(PlayerState me, PlayerState enemy) =>
        me.Team != Team.None && Watch is { } w && enemy.Team == w.Enemy && w.InRoom(enemy.Position);

    /// <summary>The nearest living enemy that is not in its spawn room (the chase target); null when there is none.</summary>
    PlayerState? BotChaseTarget(PlayerState me)
    {
        PlayerState? best = null;
        float bestDistance = float.MaxValue;
        foreach (var other in _lastOthers)
        {
            if (!other.Alive || (me.Team != Team.None && !World.Hostile(me.Team, other.Team)) || InEnemyRoom(me, other)) continue;
            float d = V3.DistanceSquared(other.Position, me.Position);
            if (d < bestDistance) { bestDistance = d; best = other; }
        }
        return best;
    }

    string _botHoldNote = "";
    double _botHoldLogAt;

    /// <summary>
    /// The waiting bot (see the class): when every living enemy is in its spawn room, walk to this bot's hold point and stand there facing
    /// the door. Null when the watch is not ready, no enemy is in the room, or there is no hold point (the old chase runs then).
    /// </summary>
    PlayerInput? BotHoldInput(PlayerState me, uint tick)
    {
        if (!me.Alive || Watch is not { Door: { } door, Holds.Count: > 0 } w) return null;
        PlayerState? sealedEnemy = null;
        foreach (var o in _lastOthers) if (o.Alive && World.Hostile(me.Team, o.Team) && InEnemyRoom(me, o)) { sealedEnemy = o; break; }
        if (sealedEnemy is not { } waitingFor) return null;
        int id = Math.Max(_client?.PlayerId ?? 0, 0);
        var hold = w.Holds[(id * 5) % w.Holds.Count];
        var (to, speed, arrived, dropAhead) = BotSteer(me, hold, "hold at the spawn door");
        float away = MathF.Sqrt((hold.X - me.Position.X) * (hold.X - me.Position.X) + (hold.Y - me.Position.Y) * (hold.Y - me.Position.Y));
        float toDoor = MathF.Sqrt((door.X - me.Position.X) * (door.X - me.Position.X) + (door.Y - me.Position.Y) * (door.Y - me.Position.Y));
        _botNote = $"waiting for P{waitingFor.PlayerId} in its spawn room, {toDoor:F0} m from the door{(away > 1.5f ? $" (walking to the hold point, {away:F0} m)" : "")}";
        _botHoldNote = _botNote;
        _pitch = 0f;
        if (arrived || away <= 1.0f || to.LengthSquared() < 1e-6f)
        {
            _yaw = MathF.Atan2(-(door.X - me.Position.X), door.Y - me.Position.Y);
            return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, 0f);
        }
        _yaw = MathF.Atan2(-to.X, to.Y);
        float remaining = _navAgent is { Path: not null } agent ? agent.Remaining : away;
        return BotMoveInput(tick, to * speed, (remaining > BotChaseSprint && !dropAhead && _navAgent is not { OnJumpLink: true } ? InputButtons.Sprint : InputButtons.None) | _navJump);
    }

    /// <summary>The bot's wait line every 5 s while it is waiting at a door: where it stands, how far from the door and from the nearest blocker that stops it (test measurement).</summary>
    void LogSpawnWait(PlayerState me)
    {
        if (!_bot || _botHoldNote == "" || _connectedFor - _botHoldLogAt < 5.0 || _client?.Collision is not { } world) return;
        _botHoldLogAt = _connectedFor;
        float best = float.MaxValue;
        foreach (var b in world.Blockers) if (b.Blocks(me.Team)) best = MathF.Min(best, b.HorizontalDistance(me.Position));
        var door = Watch?.Door;
        string toDoor = door is { } d ? $"{MathF.Sqrt((d.X - me.Position.X) * (d.X - me.Position.X) + (d.Y - me.Position.Y) * (d.Y - me.Position.Y)):F1} m from the door" : "no door";
        GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} wait: at {me.Position.X:F1},{me.Position.Y:F1},{me.Position.Z:F1}, {toDoor}, {best:F1} m from the nearest blocker that stops me; {_botHoldNote}");
        _botHoldNote = "";
    }
}
