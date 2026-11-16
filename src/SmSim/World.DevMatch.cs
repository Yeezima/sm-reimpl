using System.Numerics;

namespace SmSim;

/// <summary>What a developer-tools teleport goes to (protocol 80).</summary>
public enum DevTeleportKind : byte
{
    /// <summary>A side's spawn room (index 0: the Loyalists, 1: Chaos).</summary>
    SpawnRoom = 0,
    /// <summary>A control point (index into <see cref="World.ControlPoints"/>).</summary>
    ControlPoint = 1,
    /// <summary>Another living player (index: its player id).</summary>
    Player = 2,
}

/// <summary>
/// Developer tools that act on the match and on single players (owner 2026-11-17; ours; reached only through the server's gate, <c>GameServer.DevTools.cs</c>): end, restart or skip the
/// match, kill a player, teleport the requester. Each goes through the world's own rules (the normal end, the normal round start, the Deploy's suicide, the spawn and point spot searches).
/// </summary>
public sealed partial class World
{
    /// <summary>Ends the current match now by the score (higher wins, equal is a draw), as the time limit would; false unless a match is being played or counted down.</summary>
    public bool DevEndMatch()
    {
        if (Rules is null || State is not (MatchState.Playing or MatchState.Countdown)) return false;
        int a = ScoreOf(Team.SpaceMarines), b = ScoreOf(Team.Chaos);
        End(a > b ? Team.SpaceMarines : b > a ? Team.Chaos : Team.None);
        return true;
    }

    /// <summary>
    /// Ends the match as <see cref="DevEndMatch"/> and shortens the end screen's restart clock to <paramref name="restartTicks"/>, so the next match starts (on a rotating server, the next map:
    /// its clients are told and rejoin) after a moment; an already ended match is only hurried. False unless a match is being played, counted down or ended.
    /// </summary>
    public bool DevSkipMatch(uint restartTicks)
    {
        if (Rules is null) return false;
        if (State != MatchState.Ended && !DevEndMatch()) return false;
        TicksLeft = Math.Min(TicksLeft, Math.Max(1u, restartTicks));
        return true;
    }

    /// <summary>
    /// Restarts the match on the same map with fresh scores and points: the round start of an ended match (everyone respawns at a spawn; the countdown first when the rules have one). With one side
    /// empty the world waits in warm-up as it always does. False without match rules.
    /// </summary>
    public bool DevRestartMatch()
    {
        if (Rules is null) return false;
        bool both = CountSide(Team.SpaceMarines) > 0 && CountSide(Team.Chaos) > 0;
        _state = MatchState.Warmup;
        TicksLeft = 0;
        Winner = Team.None;
        DevFlags &= ~DevToolsFlags.Indefinite;
        Array.Clear(_teamScores);
        foreach (var id in _scores.Keys.ToList()) _scores[id] = default;
        ResetPoints();
        if (both) BeginRound(respawnWithoutCountdown: true);
        return true;
    }

    /// <summary>Kills a living player as the in-match Deploy does (a suicide: a death on the scoreboard, no killer; it respawns the tick after). False for a dead or unknown player or an ended match.</summary>
    public bool DevKill(int playerId)
    {
        if (State == MatchState.Ended || !_players.TryGetValue(playerId, out var p) || !p.Alive) return false;
        _deployNow.Add(playerId);
        return true;
    }

    /// <summary>
    /// Moves a living player to a spot the world already trusts for spawning: the next free spot of a side's spawn room (<c>TryStartAreaSpawn</c>, else the shared spawn markers), a free spot on a
    /// control point (<c>PointSpot</c>: on the floor, a capsule fitting, outside kill volumes, clear of other players), or a free spot in the ring behind another living player
    /// (<c>HomerSpotNear</c>, the Teleport Homer perk's: on the floor, in line of sight, a capsule fitting, clear of players). The body stops (velocity, jump, charge and roll cleared) and faces
    /// the way the spawn or the target does. False, with the reason, when the player is dead, the target does not exist or no free spot is found; nothing moves then.
    /// </summary>
    public bool DevTeleport(int playerId, DevTeleportKind kind, int index, out string why)
    {
        why = "";
        if (!_players.TryGetValue(playerId, out var me) || !me.Alive) { why = "the player is not alive"; return false; }
        Vector3 position; float yaw;
        switch (kind)
        {
            case DevTeleportKind.SpawnRoom:
            {
                var side = index == 1 ? Team.Chaos : Team.SpaceMarines;
                if (!TryStartAreaSpawn(playerId, side, out position, out yaw))
                {
                    if (_spawns.Length == 0) { why = "this map has no spawn room"; return false; }
                    (position, yaw) = ChooseSpawn(playerId, side);
                }
                break;
            }
            case DevTeleportKind.ControlPoint:
            {
                if (index < 0 || index >= _pointSpecs.Length) { why = "no such control point"; return false; }
                if (PointSpot(index, playerId) is not { } spot) { why = "no free spot on that point"; return false; }
                position = spot;
                yaw = FacingMiddle(spot, me.Yaw);
                break;
            }
            case DevTeleportKind.Player:
            {
                if (index == playerId || !_players.TryGetValue(index, out var target) || !target.Alive) { why = "that player is not alive"; return false; }
                if (HomerSpotNear(target, playerId) is not { } spot) { why = "no free spot next to that player"; return false; }
                position = spot;
                yaw = target.Yaw;
                break;
            }
            default: why = "unknown target"; return false;
        }
        _players[playerId] = me with
        {
            Position = position, Yaw = yaw, Velocity = Vector3.Zero, Grounded = true,
            Jump = JumpPhase.None, JumpTicks = 0, JumpMark = 0, JumpHolding = false, JumpCut = false, JumpSmall = false, JumpAirOpen = false, JumpSlash = false,
            Rush = RushPhase.None, RushTicks = 0, EvadeTicks = 0, EvadeLanded = false,
        };
        return true;
    }
}
