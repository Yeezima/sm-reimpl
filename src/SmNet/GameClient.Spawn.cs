using SmSim;

namespace SmNet;

/// <summary>Protocol 58 (fix/pvp-deploy-spawn): the classic PvP respawn choice from the client (<see cref="SpawnRequest"/>; the rules are the server's, <see cref="World.SetSpawnChoice"/>).</summary>
public sealed partial class GameClient
{
    ushort _spawnRevision;
    SpawnChoice _spawnSent;
    bool _spawnSentAny;

    /// <summary>
    /// Sends the respawn choice. One datagram, unacknowledged and idempotent: the caller resends its current choice while the player is dead. False, and nothing sent,
    /// unless connected as a player.
    /// </summary>
    public bool SendSpawnChoice(SpawnChoice choice)
    {
        if (Status != ClientStatus.Connected || _options.Observer || choice.Target is < 0 or > ushort.MaxValue) return false;
        // Protocol 89: a changed choice is a new one (next revision, wrapping); a resend of the same choice keeps its revision.
        if (_spawnSent != choice || !_spawnSentAny) { _spawnRevision++; _spawnSent = choice; _spawnSentAny = true; }
        return SendSpawnChoiceAt(choice, _spawnRevision);
    }

    /// <summary>One request with an explicit revision (what <see cref="SendSpawnChoice"/> sends; tests use it to replay a stale or duplicate datagram). Same conditions as <see cref="SendSpawnChoice"/>.</summary>
    public bool SendSpawnChoiceAt(SpawnChoice choice, ushort revision)
    {
        if (Status != ClientStatus.Connected || _options.Observer || choice.Target is < 0 or > ushort.MaxValue) return false;
        _transport.Send(Protocol.Write(new SpawnRequest(_session, choice.Kind, choice.Target, revision)));
        return true;
    }
}
