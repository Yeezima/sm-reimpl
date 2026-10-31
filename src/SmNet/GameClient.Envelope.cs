using SmSim;

namespace SmNet;

/// <summary>
/// The extras envelope on the client (added content E3, ported by the loyalist barks without a protocol bump: extras wire 2; docs/added-content.md): every section of every <see cref="ExtrasUpdate"/>
/// received while connected is queued with its message's server tick, and <see cref="TakeExtras"/> hands them out once each, oldest first.
/// The core reads no payload; one consumer (the client's added content, through its own hook) takes the queue and passes each section to the
/// feature that owns its tag, which skips the tags it does not know. Nothing is deduplicated or reordered here: datagrams can arrive late or
/// twice, so a feature judges by the tick and its own sequence numbers. A client whose match runs no added content
/// (<see cref="SimConfig.Extras"/> None) drops the messages (<see cref="ExtrasIgnored"/>); at most <see cref="MaxQueuedExtras"/> sections wait
/// (one more drops the oldest), so a view that never takes them cannot make the queue grow.
/// </summary>
public sealed partial class GameClient
{
    readonly List<ExtrasSection> _extras = new();
    /// <summary>Most sections waiting for <see cref="TakeExtras"/>; one more drops the oldest.</summary>
    public const int MaxQueuedExtras = 256;

    /// <summary>Extras messages accepted (queued).</summary>
    public int ExtrasMessages { get; private set; }
    /// <summary>Extras messages dropped because this client's match runs no added content.</summary>
    public int ExtrasIgnored { get; private set; }
    /// <summary>Sections dropped from a full queue.</summary>
    public int ExtrasOverflow { get; private set; }

    void OnExtras(ExtrasUpdate u)
    {
        if (_config.Extras == ContentExtras.None) { ExtrasIgnored++; return; }
        ExtrasMessages++;
        foreach (var (tag, payload) in u.Sections) _extras.Add(new ExtrasSection(u.ServerTick, tag, payload));
        int over = _extras.Count - MaxQueuedExtras;
        if (over > 0) { _extras.RemoveRange(0, over); ExtrasOverflow += over; }
    }

    /// <summary>
    /// Sends one added-content command to the server (message kind 17, <see cref="ExtrasCommandMessage"/>). False, and nothing sent, unless connected to a match that runs added
    /// content reading commands (<c>ContentExtras.CommandUsers</c>). One datagram, unacknowledged: a feature that needs it delivered resends until its own state echoes it.
    /// </summary>
    public bool SendExtrasCommand(byte tag, byte[] payload)
    {
        if (Status != ClientStatus.Connected || (_config.Extras & ContentExtras.CommandUsers) == 0) return false;
        _transport.Send(Protocol.Write(new ExtrasCommandMessage(_session, tag, payload)));
        return true;
    }

    /// <summary>The extras sections received since the last call (each once), oldest first.</summary>
    public IReadOnlyList<ExtrasSection> TakeExtras()
    {
        if (_extras.Count == 0) return [];
        var list = _extras.ToArray();
        _extras.Clear();
        return list;
    }
}
