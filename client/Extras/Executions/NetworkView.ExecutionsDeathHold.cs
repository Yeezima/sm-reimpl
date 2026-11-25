using SmNet;
using SmSim;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X5: Classic PvP Extended's death-pose hold (owner 2026-11-25, "Hold the death pose briefly"). A remote victim of a hostile <see cref="KillCause.Melee"/> kill keeps
/// its death pose for the finisher window (<see cref="ExecutionRules.DeathPoseHoldSeconds"/>, 0.8 s) from the kill before the ragdoll takes it: the death clip plays as the marine
/// (or Ork) death hold plays it, and once that clip reaches its powered-ragdoll tag the pose stays frozen until the window ends. A Start for this victim ends the hold early (its
/// proxy takes the body over and the death never becomes a ragdoll); a respawn ends it too (the core drops the queued death). The respawn timer is the server's and never waits.
/// Not in Musou Mode or Warfront (their deaths are their own), not for the own death (the death camera never waits), and never without the executions flag (the faithful game).
/// Which pose is held (the clip frozen at the ragdoll tag) is an <b>interpretation</b>.
/// </summary>
public partial class NetworkView
{
    partial void ExecutionsDeathHold(int id, CharacterViewer character, PlayerState state, bool own, ref bool hold)
    {
        if (_config is not { } config || config.Extras.HasFlag(ContentExtras.Musou)) return;
        if (_execProxiedVictims.Contains(id)) { if (IsInstanceValid(character)) character.Visible = false; hold = true; return; }   // its proxy draws the body
        if (own || !_deathCauses.TryGetValue(id, out var cause)) return;
        var kill = cause.Kill;
        if (kill.Cause != KillCause.Melee || kill.Killer == id || kill.Killer < 0 || !World.Hostile(ExecTeamOf(kill.Killer), ExecTeamOf(id))) return;
        if (!ExecutionPresentMath.DeathHeld(_connectedFor - cause.At, ExecutionRules.DeathPoseHoldSeconds(config))) return;
        hold = true;
        if (!IsInstanceValid(character)) return;
        if (IsOrkCharacter(character) ? OrkDeathHold(id, character, state, own) : MarineDeathHold(id, character, state, own)) return;   // the death clip still plays
        character.Visible = true;   // past the clip's ragdoll tag: the pose it reached stays
    }

    Team ExecTeamOf(int id)
    {
        if (_client is not null && id == _client.PlayerId) return _client.Predicted.Team;
        foreach (var o in _lastOthers) if (o.PlayerId == id) return o.Team;
        return Team.None;
    }
}
