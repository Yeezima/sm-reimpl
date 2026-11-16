using SmSim;

namespace SmNet;

/// <summary>
/// Protocol 80 (dev-tools; ours): the developer tools on the client. <see cref="DevState"/> arrives only from a server that has them on (<c>--dev-tools</c>, loopback only); the panel shows it and
/// sends <see cref="DevCommand"/> requests. Nothing here runs against a server that sends no state: <see cref="DevActive"/> stays false and the prediction is untouched.
/// </summary>
public sealed partial class GameClient
{
    /// <summary>Seconds a state stays valid (the server sends one a second and on every change).</summary>
    public const double DevStateLifetime = 3.5;
    DevState? _dev;
    TimeSpan _devAt;

    void OnDevState(DevState s) { _dev = s; _devAt = _clock.Now; }

    /// <summary>The newest developer-tools state while it is fresh, else null (a server without the tools never sends one).</summary>
    public DevState? Dev => _dev is not null && (_clock.Now - _devAt).TotalSeconds <= DevStateLifetime ? _dev : null;

    /// <summary>Whether the server announced the developer tools (recently).</summary>
    public bool DevActive => Dev is not null;

    /// <summary>The flags the own prediction mirrors: the server's, while they are announced; none otherwise.</summary>
    DevToolsFlags DevPredicted => _dev is not null ? (Dev?.Flags ?? DevToolsFlags.None) : DevToolsFlags.None;

    /// <summary>The own predicted state after a step with the developer tools' top-up applied the way the server applies it after its tick (no change without flags).</summary>
    PlayerState DevTopUp(PlayerState s) => _dev is null ? s : DevTools.TopUp(s, _config, DevPredicted);

    /// <summary>Sends a developer-tools request (one datagram, unacknowledged: the panel shows the server's state, so a lost click shows as no change). False, and nothing sent, unless connected as a player.</summary>
    public bool SendDevCommand(DevCommandKind kind, byte argument, bool value, int extra = 0)
    {
        if (Status != ClientStatus.Connected || _options.Observer) return false;
        _transport.Send(Protocol.Write(new DevCommand(_session, kind, argument, value, extra)));
        return true;
    }

    // ---- cancel-editor (protocol 80): the cancel table, kept the same as the server's ----
    readonly Dictionary<int, DevCancelSync> _cancelChunks = new();
    uint _cancelChunkHash;
    TimeSpan _cancelAsked = TimeSpan.MinValue;
    /// <summary>The server's table hash from its last beacon or sync (0 before any), and when it came.</summary>
    uint _serverCancelHash;
    TimeSpan _serverCancelAt = TimeSpan.MinValue;
    /// <summary>How many times the server's table has replaced this client's (the panel redraws on a change).</summary>
    public int CancelSyncs { get; private set; }

    /// <summary>The cancel table in force on this client (its prediction plays by it).</summary>
    public CancelData CancelTable => _config.Cancel.Active;

    /// <summary>
    /// Whether this client plays by the same cancel table as the server (its hash equals the one the server last sent, and that was within the beacon's lifetime). False while no server hash arrived, so the panel
    /// can show "waiting" rather than "in sync".
    /// </summary>
    public bool CancelInSync => _serverCancelAt != TimeSpan.MinValue && (_clock.Now - _serverCancelAt).TotalSeconds <= DevStateLifetime && _serverCancelHash == CancelTable.Hash;

    /// <summary>The server's table hash (0 until one arrives).</summary>
    public uint ServerCancelHash => _serverCancelHash;

    void OnDevCancelSync(DevCancelSync s)
    {
        _serverCancelHash = s.Hash; _serverCancelAt = _clock.Now;
        if (s.Chunks == 0)
        {
            // a beacon: ask for the table when ours differs (at most once a second)
            if (s.Hash != CancelTable.Hash && (_clock.Now - _cancelAsked).TotalSeconds >= 1.0) { _cancelAsked = _clock.Now; SendDevCommand(DevCommandKind.CancelSync, 0, true); }
            return;
        }
        if (s.Hash != _cancelChunkHash) { _cancelChunks.Clear(); _cancelChunkHash = s.Hash; }
        _cancelChunks[s.Chunk] = s;
        if (_cancelChunks.Count < s.Chunks || Enumerable.Range(0, s.Chunks).Any(i => !_cancelChunks.ContainsKey(i))) return;
        var changes = new List<CancelChange>();
        foreach (var chunk in Enumerable.Range(0, s.Chunks).Select(i => _cancelChunks[i]))
            foreach (var e in chunk.Entries) if (Protocol.FromWire(e, _config) is { } ch) changes.Add(ch);
        _cancelChunks.Clear();
        var table = CancelData.FromShipped(changes);
        _config.Cancel.Set(table.Hash == CancelData.Shipped.Hash ? null : table);
        CancelSyncs++;
    }

    /// <summary>Sends cancel-table edits (one datagram per <see cref="Protocol.MaxCancelEntries"/> entries). False, and nothing sent, unless connected as a player.</summary>
    public bool SendCancelEdit(DevCancelOp op, IReadOnlyList<CancelChange> changes)
    {
        if (Status != ClientStatus.Connected || _options.Observer) return false;
        var wire = new List<CancelWire>();
        foreach (var ch in changes) if (Protocol.ToWire(ch, _config) is { } e) wire.Add(e);
        if (op == DevCancelOp.ResetAll) { _transport.Send(Protocol.Write(new DevCancelEdit(_session, op, []))); return true; }
        for (int i = 0; i < Math.Max(1, wire.Count); i += Protocol.MaxCancelEntries)
            _transport.Send(Protocol.Write(new DevCancelEdit(_session, op, wire.Skip(i).Take(Protocol.MaxCancelEntries).ToList())));
        return true;
    }
}
