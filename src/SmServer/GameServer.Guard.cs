using System.Net;
using System.Net.Sockets;
using SmNet;
using SmSim;

namespace SmServer;

// Server hardening for a public dedicated server (branch server/hardening): per-address player cap and join rate, idle kick,
// bans, crash isolation of packet handlers and ticks, a bound on tick catch-up, and an operator log with addresses.
// Every value here is ours (spec/behavior.md "Dedicated server hardening"; docs/changes-from-original.md).

public sealed partial record ServerOptions
{
    /// <summary>Default of <see cref="MaxPlayersPerAddress"/> (ours).</summary>
    public const int DefaultMaxPlayersPerAddress = 4;
    /// <summary>Default of <see cref="JoinInterval"/> (ours).</summary>
    public static readonly TimeSpan DefaultJoinInterval = TimeSpan.FromSeconds(5);
    /// <summary>Default of <see cref="IdleKick"/> (ours).</summary>
    public static readonly TimeSpan DefaultIdleKick = TimeSpan.FromSeconds(300);
    /// <summary>Default of <see cref="MaxCatchUpTicks"/> (ours).</summary>
    public const int DefaultMaxCatchUpTicks = 5;
    /// <summary>Default of <see cref="MaxConsecutiveTickFaults"/>: 2 s of ticks at 30 Hz (ours).</summary>
    public const int DefaultMaxConsecutiveTickFaults = 60;

    /// <summary>
    /// At most this many players from one IP address (0: no cap). Loopback is exempt unless <see cref="LimitLoopback"/>. Only connections whose
    /// transport knows their address (<see cref="IServerTransport.EndPointOf"/>) are counted. A Hello over the cap gets
    /// <see cref="RejectReason.ServerFull"/> with a detail naming the cap.
    /// </summary>
    public int MaxPlayersPerAddress { get; init; } = DefaultMaxPlayersPerAddress;
    /// <summary>
    /// At most one accepted new join per IP address this often (<see cref="TimeSpan.Zero"/>: no limit). A Hello inside the window
    /// gets no answer, so the client's resent Hello gets in once the window has passed. A replacement on the same address (new
    /// nonce) counts as a join.
    /// </summary>
    public TimeSpan JoinInterval { get; init; } = DefaultJoinInterval;
    /// <summary>
    /// A player that sends no real input (movement or a button) and no customizer change (a Setup, an emblem upload) for this
    /// long while the match is being played is dropped (<see cref="TimeSpan.Zero"/>: never). Warm-up and the end of a round do
    /// not count. Only connections with an address are watched (in-process loopback endpoints without one are not).
    /// </summary>
    public TimeSpan IdleKick { get; init; } = DefaultIdleKick;
    /// <summary>The most ticks one <see cref="GameServer.Update"/> runs to catch up after a stall; the rest are skipped (0: no cap).</summary>
    public int MaxCatchUpTicks { get; init; } = DefaultMaxCatchUpTicks;
    /// <summary>
    /// After this many ticks in a row have failed, <see cref="GameServer.FatalError"/> is set and the console server exits with
    /// code 4 for its supervisor to restart it (0: never).
    /// </summary>
    public int MaxConsecutiveTickFaults { get; init; } = DefaultMaxConsecutiveTickFaults;
    /// <summary>A ban file: one IP address or CIDR range per line, '#' starts a comment; re-read when it changes. Null: no bans.</summary>
    public string? BanFile { get; init; }
    /// <summary>How often the server looks at the ban file's time and size.</summary>
    public TimeSpan BanFileCheckInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>A short note on the server's game build for content refusals (e.g. "Steam build 7412325"); null: the content fingerprint only.</summary>
    public string? BuildHint { get; init; }
    /// <summary>
    /// Whether loopback addresses (127.0.0.0/8, ::1) are held to <see cref="MaxPlayersPerAddress"/>, <see cref="JoinInterval"/>,
    /// the per-address respawn hold and <see cref="IdleKick"/>. False (the default, <c>--limit-loopback</c> sets it): the owner's
    /// playtests run up to 12 bot clients on this machine, so loopback is exempt (ours). Bans apply either way.
    /// </summary>
    public bool LimitLoopback { get; init; }
}

public sealed partial class GameServer
{
    /// <summary>
    /// Operator notes with addresses (joins, leaves and why, refusals, bans, idle kicks, handler faults, skipped ticks). Kept apart
    /// from <see cref="Log"/>, which carries gameplay notes.
    /// </summary>
    public event Action<string>? OperatorLog;

    /// <summary>Packet handlers and ticks that threw (each was caught and logged; the server went on).</summary>
    public int Faults { get; private set; }
    /// <summary>Ticks skipped because one update would have had to run more than <see cref="ServerOptions.MaxCatchUpTicks"/>.</summary>
    public long SkippedTicks { get; private set; }
    /// <summary>Players dropped for idling (<see cref="ServerOptions.IdleKick"/>).</summary>
    public int IdleKicked { get; private set; }
    /// <summary>Hellos left unanswered by the join rate limit or a held respawn.</summary>
    public int JoinsDeferred { get; private set; }
    /// <summary>Packets dropped because their source is banned.</summary>
    public long BannedPackets { get; private set; }

    /// <summary>Test seam: runs before each received message is handled (a throw here is a handler fault).</summary>
    internal Action<int, Message?>? PacketHookForTests { get; set; }
    /// <summary>Test seam: runs before each recipient's snapshot is built, with its player id (a throw here is a recipient fault).</summary>
    internal Action<int>? SnapshotHookForTests { get; set; }

    /// <summary>
    /// Set (once) when <see cref="ServerOptions.MaxConsecutiveTickFaults"/> ticks in a row have failed: the simulation is broken, and
    /// the console server exits with code 4 so a supervisor restarts it. Null while the server is healthy.
    /// </summary>
    public string? FatalError { get; private set; }
    int _consecutiveTickFaults;

    /// <summary>The bans in force (from <see cref="ServerOptions.BanFile"/>, or set directly). Connected players it covers are dropped at the next update.</summary>
    public BanList Bans
    {
        get => _bans;
        set { _bans = value; _bansChanged = true; }
    }

    BanList _bans = BanList.Empty;
    bool _bansChanged;
    BanFile? _banFile;
    TimeSpan _nextBanCheckAt = TimeSpan.MinValue;
    readonly Dictionary<int, Guarded> _guarded = new();             // by connection: every player whose address is known, and its activity
    readonly Dictionary<IPAddress, TimeSpan> _lastJoinAt = new();   // newest accepted join per address
    readonly Dictionary<IPAddress, TimeSpan> _respawnHold = new();  // an address whose dead player left: no new join before this
    readonly Dictionary<IPAddress, TimeSpan> _noteThrottle = new(); // last deferral or refusal note per address
    TimeSpan? _lastFaultNote, _lastSkipNote;
    int _faultsUnreported;
    long _skipsUnreported;

    /// <summary>Faults logged in full before the log is throttled, and the throttle's period (ours).</summary>
    const int FaultsLoggedInFull = 20;
    static readonly TimeSpan NoteThrottle = TimeSpan.FromSeconds(10);

    sealed class Guarded(IPEndPoint? endPoint, TimeSpan now)
    {
        public IPEndPoint? EndPoint { get; } = endPoint;
        public IPAddress? Address { get; } = endPoint is null ? null : BanList.Normalize(endPoint.Address);
        public TimeSpan LastActive { get; set; } = now;
    }

    /// <summary>The address of a connection for the operator log ("connection N" when the transport has none).</summary>
    string Where(int connection) => _guarded.TryGetValue(connection, out var g) && g.EndPoint is { } kept ? kept.ToString()
        : _transport.EndPointOf(connection) is { } ep ? ep.ToString() : $"connection {connection}";

    /// <summary>A loopback address while <see cref="ServerOptions.LimitLoopback"/> is off: free of the per-address limits and the idle kick.</summary>
    bool Exempt(IPAddress address) => !_options.LimitLoopback && IPAddress.IsLoopback(address);

    IPAddress? AddressOf(int connection) => _transport.EndPointOf(connection) is { } ep ? BanList.Normalize(ep.Address) : null;

    void Note(string line)
    {
        try { OperatorLog?.Invoke(line); } catch (Exception e) when (Recoverable(e)) { }   // a failing log sink must not stop the server
    }

    static bool Recoverable(Exception e) => e is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    // ---- bans ----

    /// <summary>Re-reads the ban file when it changed (every <see cref="ServerOptions.BanFileCheckInterval"/>) and drops banned players.</summary>
    void RefreshBans(TimeSpan now)
    {
        if (_options.BanFile is { } path && now >= _nextBanCheckAt)
        {
            _nextBanCheckAt = now + _options.BanFileCheckInterval;
            _banFile ??= new BanFile(path);
            try
            {
                if (_banFile.Refresh())
                {
                    Bans = _banFile.Bans;
                    Note($"ban file {path}: {_banFile.Bans.Count} entries{(_banFile.Exists ? "" : " (file not found)")}" +
                        (_banFile.Errors.Count > 0 ? $"; ignored lines: {string.Join("; ", _banFile.Errors.Take(5))}" : ""));
                }
            }
            catch (Exception e) when (Recoverable(e)) { Note($"ban file {path}: not read ({e.GetType().Name}: {e.Message}); the previous list stays"); }
        }
        if (!_bansChanged) return;
        _bansChanged = false;
        foreach (var (connection, peer) in _peers.ToList())
            if (IsBanned(connection)) RemovePeer(connection, peer, reason: "banned");
    }

    bool IsBanned(int connection)
    {
        if (_bans.Count == 0 || AddressOf(connection) is not { } address || !_bans.Contains(address)) return false;
        BannedPackets++;
        return true;
    }

    // ---- joins ----

    /// <summary>
    /// Whether a compatible Hello may create a player now (before a player on the same connection is replaced): the per-address cap
    /// (refused with a reason), a held respawn of a dead player that left from this address and the join rate (no answer: the client
    /// resends and gets in later). Connections without an address are always admitted.
    /// </summary>
    bool AdmitJoin(int connection, uint nonce, Peer? replacing)
    {
        var now = _clock.Now;
        // A new client on the connection of a dead player waits out that player's respawn time (a re-join is no faster respawn).
        if (replacing is not null && World.Players.TryGetValue(replacing.PlayerId, out var old) && !old.Alive && old.RespawnLeft > 0f)
        {
            JoinsDeferred++;
            if (AddressOf(connection) is not { } at || Throttled(at, now))
                Note($"join from {Where(connection)} deferred: player {replacing.PlayerId} there is dead ({old.RespawnLeft:0.0} s to respawn)");
            return false;
        }
        if (AddressOf(connection) is not { } address || Exempt(address)) return true;
        if (_options.MaxPlayersPerAddress > 0)
        {
            int here = _guarded.Count(kv => kv.Key != connection && address.Equals(kv.Value.Address));
            if (here >= _options.MaxPlayersPerAddress)
            {
                if (Throttled(address, now)) Note($"refused {Where(connection)}: already {here} players from {address} (--max-per-ip {_options.MaxPlayersPerAddress})");
                Rejected++;
                _transport.Send(connection, Protocol.Write(new Reject(RejectReason.ServerFull, $"at most {_options.MaxPlayersPerAddress} players from one address", nonce)));
                return false;
            }
        }
        string? wait = null;
        if (_respawnHold.TryGetValue(address, out var holdUntil))
        {
            if (now < holdUntil) wait = $"its dead player's respawn time ({(holdUntil - now).TotalSeconds:0.0} s left)";
            else _respawnHold.Remove(address);
        }
        if (wait is null && _options.JoinInterval > TimeSpan.Zero && _lastJoinAt.TryGetValue(address, out var last))
        {
            if (now - last < _options.JoinInterval) wait = $"one join per {_options.JoinInterval.TotalSeconds:0.#} s per address";
            else _lastJoinAt.Remove(address);
        }
        if (wait is null) return true;
        JoinsDeferred++;
        if (Throttled(address, now)) Note($"join from {Where(connection)} deferred: {wait}");
        return false;
    }

    /// <summary>True (and the note's time is kept) when no note about this address was written in the last <see cref="NoteThrottle"/>.</summary>
    bool Throttled(IPAddress address, TimeSpan now)
    {
        if (_noteThrottle.TryGetValue(address, out var at) && now - at < NoteThrottle) return false;
        _noteThrottle[address] = now;
        if (_noteThrottle.Count > 4096)
            foreach (var old in _noteThrottle.Where(kv => now - kv.Value >= NoteThrottle).Select(kv => kv.Key).ToList()) _noteThrottle.Remove(old);
        return true;
    }

    /// <summary>Records an accepted join (address, join time, activity) and logs it with the address.</summary>
    void NoteJoin(int connection, int playerId, bool spectator = false)
    {
        var now = _clock.Now;
        var endPoint = _transport.EndPointOf(connection);
        var g = new Guarded(endPoint, now);
        _guarded[connection] = g;
        if (g.Address is { } address)
        {
            _lastJoinAt[address] = now;
            if (_lastJoinAt.Count > 4096)
                foreach (var old in _lastJoinAt.Where(kv => now - kv.Value >= _options.JoinInterval).Select(kv => kv.Key).ToList()) _lastJoinAt.Remove(old);
        }
        Note($"{Describe(playerId)} joined{(spectator ? " as a spectator" : "")} from {endPoint?.ToString() ?? $"connection {connection}"} ({_peers.Values.Count(p => !p.Spectator)} players, {_peers.Values.Count(p => p.Spectator)} spectators)");
    }

    /// <summary>
    /// Before a player is removed: logs who left from where and why, and, when the player was dead, holds new joins from its address
    /// until its respawn time would have run out (a re-join is never a faster respawn; hardening S3).
    /// </summary>
    void NoteLeave(int connection, Peer peer, string reason)
    {
        var now = _clock.Now;
        string where = Where(connection);
        if (_guarded.Remove(connection, out var g) && g.Address is { } address
            && World.Players.TryGetValue(peer.PlayerId, out var state) && !state.Alive && state.RespawnLeft > 0f)
        {
            var until = now + TimeSpan.FromSeconds(state.RespawnLeft);
            if (!_respawnHold.TryGetValue(address, out var held) || held < until) _respawnHold[address] = until;
        }
        Note($"{Describe(peer.PlayerId)}{(peer.Spectator ? " (spectator)" : "")} left from {where}: {reason} ({_peers.Values.Count(p => !p.Spectator && !ReferenceEquals(p, peer))} players, {_peers.Values.Count(p => p.Spectator && !ReferenceEquals(p, peer))} spectators)");
    }

    // ---- activity ----

    /// <summary>Real input (movement or a button) in a batch from a player keeps it from the idle kick.</summary>
    void NoteInputs(int connection, InputBatch batch)
    {
        if (!_guarded.TryGetValue(connection, out var g)) return;
        foreach (var input in batch.Inputs)
            if (IsRealInput(input)) { g.LastActive = _clock.Now; return; }
    }

    /// <summary>
    /// Customizer use (a Setup, an emblem chunk) keeps a player from the idle kick (it may sit in the class creator a while).
    /// Appearance queries and blob requests are not counted: the client sends them by itself when others change their looks.
    /// </summary>
    void NoteActivity(int connection)
    {
        if (_guarded.TryGetValue(connection, out var g)) g.LastActive = _clock.Now;
    }

    /// <summary>
    /// Movement or a button held (looking around alone does not count: a parked client can turn). Added content's action key alone
    /// (<see cref="InputButtons.ExtraAction"/>: the loyalist barks' shout) is not play either.
    /// </summary>
    public static bool IsRealInput(PlayerInput input) =>
        (input.Buttons & ~InputButtons.ExtraAction) != InputButtons.None || (float.IsFinite(input.MoveX) && input.MoveX != 0f) || (float.IsFinite(input.MoveY) && input.MoveY != 0f);

    /// <summary>Drops players idle for <see cref="ServerOptions.IdleKick"/> while the match is being played (warm-up and round ends do not count).</summary>
    void KickIdle(TimeSpan now)
    {
        if (_options.IdleKick <= TimeSpan.Zero) return;
        bool playing = World.Rules is null || World.State == MatchState.Playing;
        foreach (var (connection, peer) in _peers.ToList())
        {
            if (peer.Spectator) continue;   // protocol 47: a spectator has nothing to press
            if (!_guarded.TryGetValue(connection, out var g) || g.Address is not { } address || Exempt(address)) continue;
            if (!playing) { g.LastActive = now; continue; }
            if (now - g.LastActive < _options.IdleKick) continue;
            RemovePeer(connection, peer, reason: $"idle for {_options.IdleKick.TotalSeconds:0} s (no movement or buttons)");
            IdleKicked++;
        }
    }

    // ---- crash isolation ----

    /// <summary>A packet handler threw: logged (throttled), and a player on that connection is dropped (the fault came from its data).</summary>
    void OnPacketFault(int connection, byte[] packet, Exception e)
    {
        Faults++;
        string where = Where(connection);
        if (ShouldNoteFault())
            Note($"packet from {where} ({packet.Length} bytes) failed: " + (Faults <= 5 ? e.ToString() : $"{e.GetType().Name}: {e.Message}") + Unreported());
        try
        {
            if (_peers.TryGetValue(connection, out var peer)) RemovePeer(connection, peer, reason: $"dropped after its packet failed ({e.GetType().Name})");
            else _transport.Disconnect(connection);
        }
        catch (Exception again) when (Recoverable(again)) { Faults++; }
    }

    /// <summary>Runs one part of the update; an exception is logged (throttled) and the server goes on.</summary>
    void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception e) when (Recoverable(e))
        {
            Faults++;
            if (ShouldNoteFault())
                Note($"tick {World.Tick}: {what} failed: " + (Faults <= 5 ? e.ToString() : $"{e.GetType().Name}: {e.Message}") + Unreported());
        }
    }

    /// <summary>
    /// Runs one world tick; a fault is logged (throttled) and the server goes on. <see cref="ServerOptions.MaxConsecutiveTickFaults"/>
    /// failures in a row set <see cref="FatalError"/> (logged at once, whatever the throttle).
    /// </summary>
    void GuardTick()
    {
        try
        {
            StepWorld();
            _consecutiveTickFaults = 0;
        }
        catch (Exception e) when (Recoverable(e))
        {
            Faults++;
            _consecutiveTickFaults++;
            if (ShouldNoteFault())
                Note($"tick {World.Tick}: tick failed: " + (Faults <= 5 ? e.ToString() : $"{e.GetType().Name}: {e.Message}") + Unreported());
            if (FatalError is null && _options.MaxConsecutiveTickFaults > 0 && _consecutiveTickFaults >= _options.MaxConsecutiveTickFaults)
            {
                FatalError = $"{_consecutiveTickFaults} ticks in a row failed (last: {e.GetType().Name}: {e.Message}); the simulation cannot go on";
                Note("FATAL: " + FatalError);
            }
        }
    }

    /// <summary>Building or sending one recipient's packet failed: logged (throttled) and only that player is dropped.</summary>
    void OnRecipientFault(int connection, Peer peer, string what, Exception e)
    {
        Faults++;
        if (ShouldNoteFault())
            Note($"tick {World.Tick}: {what} for player {peer.PlayerId} failed: " + (Faults <= 5 ? e.ToString() : $"{e.GetType().Name}: {e.Message}") + Unreported());
        try
        {
            if (_peers.TryGetValue(connection, out var current) && ReferenceEquals(current, peer))
                RemovePeer(connection, peer, reason: $"dropped after its {what} failed ({e.GetType().Name})");
        }
        catch (Exception again) when (Recoverable(again)) { Faults++; }
    }

    bool ShouldNoteFault()
    {
        var now = _clock.Now;
        if (Faults <= FaultsLoggedInFull || _lastFaultNote is not { } noted || now - noted >= NoteThrottle) { _lastFaultNote = now; return true; }
        _faultsUnreported++;
        return false;
    }

    string Unreported()
    {
        if (_faultsUnreported == 0) return "";
        string s = $" ({_faultsUnreported} more faults not logged)";
        _faultsUnreported = 0;
        return s;
    }

    // ---- tick catch-up ----

    /// <summary>
    /// After <see cref="ServerOptions.MaxCatchUpTicks"/> ticks in one update: skips the ticks still due, keeping the tick phase, and
    /// moves the snapshot and scoreboard times up to now (a stall does not produce a burst of packets). Logged, throttled.
    /// </summary>
    void SkipLateTicks(TimeSpan now, TimeSpan tickLength)
    {
        if (_nextTickAt is not { } next || now < next) return;
        long skipped = (now - next).Ticks / tickLength.Ticks + 1;
        _nextTickAt = next + TimeSpan.FromTicks(tickLength.Ticks * skipped);
        if (_nextSnapshotAt < now) _nextSnapshotAt = now;
        if (_nextScoreboardAt < now) _nextScoreboardAt = now;
        SkippedTicks += skipped;
        _skipsUnreported += skipped;
        if (_lastSkipNote is { } noted && now - noted < NoteThrottle) return;
        _lastSkipNote = now;
        Note($"tick {World.Tick}: the server fell behind; skipped {_skipsUnreported} ticks ({SkippedTicks} in all)");
        _skipsUnreported = 0;
    }

    // ---- refusals ----

    /// <summary>The refusal's detail for a content mismatch, with the server's build hint and content fingerprint.</summary>
    string ContentMismatchDetail() =>
        $"game content differs from the server's install (server: {(_options.BuildHint is { } hint ? hint + ", " : "")}content {_contentFingerprint:X16})";

    /// <summary>Logs a refused handshake with what differed (throttled per address).</summary>
    void NoteRefusal(int connection, RejectReason reason, Hello h)
    {
        if (AddressOf(connection) is { } address && !Throttled(address, _clock.Now)) return;
        string what = reason switch
        {
            RejectReason.ProtocolMismatch => $"client protocol {h.ProtocolVersion}, server {Protocol.Version}",
            RejectReason.ContentMismatch => $"client content {h.ContentFingerprint:X16}, server {_contentFingerprint:X16}{(_options.BuildHint is { } hint ? $" ({hint})" : "")}",
            RejectReason.ConfigMismatch => $"client config {h.ConfigHash:X16}, server {World.Config.Hash():X16}",
            RejectReason.MapMismatch => $"client map hash {h.MapHash:X16}, server {Map?.Hash ?? 0:X16} ({Map?.Name ?? "flat"})",
            _ => reason.ToString(),
        };
        Note($"refused {Where(connection)}: {reason}: {what}");
    }
}

/// <summary>Banned IP addresses and CIDR ranges (IPv4 and IPv6; IPv4-mapped IPv6 addresses count as IPv4).</summary>
public sealed class BanList
{
    public static readonly BanList Empty = new([]);
    readonly (byte[] Network, int Bits)[] _ranges;

    BanList((byte[] Network, int Bits)[] ranges) => _ranges = ranges;

    public int Count => _ranges.Length;

    /// <summary>An address as the ban list compares it (IPv4-mapped IPv6 as IPv4).</summary>
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// Reads lines of "address" or "address/prefix"; '#' starts a comment, blank lines are skipped. Lines that are neither are
    /// listed in <paramref name="errors"/> (with their line numbers) and ignored.
    /// </summary>
    public static BanList Parse(IEnumerable<string> lines, List<string>? errors = null)
    {
        var ranges = new List<(byte[], int)>();
        int number = 0;
        foreach (var raw in lines)
        {
            number++;
            int hash = raw.IndexOf('#');
            string line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0) continue;
            if (TryParseRange(line, out var range)) ranges.Add(range);
            else errors?.Add($"line {number}: '{line}'");
        }
        return new BanList(ranges.ToArray());
    }

    static bool TryParseRange(string text, out (byte[] Network, int Bits) range)
    {
        range = default;
        int slash = text.IndexOf('/');
        string host = slash < 0 ? text : text[..slash];
        // A dot or a colon: IPAddress.TryParse also takes a bare number ("12345" as 0.0.48.57), never what an operator means.
        if (host.IndexOfAny(['.', ':']) < 0 || !IPAddress.TryParse(host, out var address)) return false;
        bool mapped = address.IsIPv4MappedToIPv6;
        address = Normalize(address);
        byte[] bytes = address.GetAddressBytes();
        int bits = bytes.Length * 8;
        if (slash >= 0)
        {
            if (!int.TryParse(text[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bits)) return false;
            if (mapped) bits -= 96;   // ::ffff:a.b.c.d/120 is a.b.c.d/24
            if (bits < 0 || bits > bytes.Length * 8) return false;
        }
        range = (bytes, bits);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        byte[] bytes = Normalize(address).GetAddressBytes();
        foreach (var (network, bits) in _ranges)
            if (network.Length == bytes.Length && PrefixMatches(network, bytes, bits)) return true;
        return false;
    }

    static bool PrefixMatches(byte[] a, byte[] b, int bits)
    {
        int whole = bits / 8;
        for (int i = 0; i < whole; i++) if (a[i] != b[i]) return false;
        int rest = bits % 8;
        if (rest == 0) return true;
        int mask = 0xFF << (8 - rest) & 0xFF;
        return (a[whole] & mask) == (b[whole] & mask);
    }
}

/// <summary>A ban file read again when its time or size changes (shared read: an editor may hold it open). A missing file bans no one.</summary>
public sealed class BanFile(string path)
{
    DateTime _stamp = DateTime.MinValue;
    long _length = -2;

    public string Path { get; } = path;
    public BanList Bans { get; private set; } = BanList.Empty;
    public bool Exists { get; private set; }
    /// <summary>The lines the last read ignored.</summary>
    public IReadOnlyList<string> Errors { get; private set; } = [];

    /// <summary>Reads the file when it appeared, disappeared or changed since the last read; true when <see cref="Bans"/> was replaced.</summary>
    public bool Refresh()
    {
        var info = new FileInfo(Path);
        info.Refresh();
        bool exists = info.Exists;
        DateTime stamp = exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        long length = exists ? info.Length : -1;
        if (stamp == _stamp && length == _length) return false;
        if (!exists)
        {
            (_stamp, _length, Exists, Bans, Errors) = (stamp, length, false, BanList.Empty, []);
            return true;
        }
        var lines = new List<string>();
        using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            while (reader.ReadLine() is { } line) lines.Add(line);
        var errors = new List<string>();
        Bans = BanList.Parse(lines, errors);
        // A file written in the last 2 s may still be being written: read it once more at the next check.
        (_stamp, _length, Exists, Errors) = (DateTime.UtcNow - stamp < TimeSpan.FromSeconds(2) ? DateTime.MinValue : stamp, length, true, errors);
        return true;
    }
}
