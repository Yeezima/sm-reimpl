using SmSim;

namespace SmNet;

/// <summary>Protocol 57 (fix/pvp-deploy-spawn): the classic PvP respawn choice from the client (<see cref="SpawnRequest"/>; the rules are the server's, <see cref="World.SetSpawnChoice"/>).</summary>
public sealed partial class GameClient
{
    /// <summary>
    /// Sends the respawn choice. One datagram, unacknowledged and idempotent: the caller resends its current choice while the player is dead. False, and nothing sent,
    /// unless connected as a player.
    /// </summary>
    public bool SendSpawnChoice(SpawnChoice choice)
    {
        if (Status != ClientStatus.Connected || _options.Observer || choice.Target is < 0 or > ushort.MaxValue) return false;
        _transport.Send(Protocol.Write(new SpawnRequest(_session, choice.Kind, choice.Target)));
        return true;
    }
}
