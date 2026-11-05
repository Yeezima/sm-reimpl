using SmNet;
using SmSim;

namespace SmServer;

/// <summary>Protocol 57 (fix/pvp-deploy-spawn): a client's classic PvP respawn choice (<see cref="SpawnRequest"/>), handed to the world, which checks it again at each respawn.</summary>
public sealed partial class GameServer
{
    /// <summary>Respawn choices taken per peer per second (a client resends its choice about twice a second while dead; ours).</summary>
    const double SpawnRequestRate = 8;

    /// <summary>Choices the world took and those dropped (a spectator, outside classic PvP, an invalid target, over the rate).</summary>
    public int SpawnRequestsAccepted { get; private set; }
    public int SpawnRequestsDropped { get; private set; }

    void OnSpawnRequest(Peer peer, SpawnRequest request)
    {
        peer.LastHeard = _clock.Now;
        if (peer.Spectator) { SpawnRequestsDropped++; return; }
        peer.SpawnRequests ??= new TokenBucket(SpawnRequestRate, SpawnRequestRate);
        if (!peer.SpawnRequests.TryTake(_clock.Now)) { SpawnRequestsDropped++; return; }
        var before = World.SpawnChoiceOf(peer.PlayerId);
        var choice = new SpawnChoice(request.Kind, request.Target);
        if (!World.SetSpawnChoice(peer.PlayerId, choice)) { SpawnRequestsDropped++; return; }
        SpawnRequestsAccepted++;
        if (before != choice) Log?.Invoke($"player {peer.PlayerId} respawn choice: {choice.Kind}{(choice.Kind == SpawnChoiceKind.SpawnRoom ? "" : " " + choice.Target)}");
    }
}
