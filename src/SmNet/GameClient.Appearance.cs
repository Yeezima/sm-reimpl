using SmSim;

namespace SmNet;

/// <summary>
/// Another player's appearance as the server last sent it (protocol 22): its version, race (protocol 28: <see cref="SmSim.Race.Orks"/> too) and code
/// (re-sanitized by this client; an Ork's holds only its look, <see cref="Races.OrkLookOf"/>).
/// </summary>
public sealed record RemoteAppearance(ushort Version, Race Race, AppearanceCode Code);

/// <summary>
/// Custom classes and appearances on the client (protocol 22; docs/customizer-architecture.md section 4): <see cref="ChangeSetup"/> (resent
/// until the matching <see cref="SetupAck"/>), appearance queries for the scoreboard's unknown versions, others' custom emblems pulled from
/// the server (<see cref="BlobPull"/>, retried after a timeout, kept in memory only), and the own emblems served to the server when it asks.
/// The UI reads <see cref="AppearanceOf"/>, <see cref="Emblem"/>, <see cref="LastSetupAck"/> and drains <see cref="TakeAppearanceChanges"/> /
/// <see cref="TakeEmblemArrivals"/> once per frame.
/// </summary>
public sealed partial class GameClient
{
    /// <summary>
    /// The own emblems the server may pull, verified once when a setup names them (no file read or hash per request): every one this session's setups
    /// named (a handful at most). The old setup's stay served after a <see cref="ChangeSetup"/>: the look shown until the respawn still needs its emblem.
    /// </summary>
    readonly Dictionary<EmblemHash, byte[]> _ownEmblems = new();
    ushort _setupSequence;
    Setup? _setupPending;
    TimeSpan _setupSentAt = TimeSpan.MinValue;
    readonly Dictionary<int, RemoteAppearance> _appearances = new();
    readonly Dictionary<int, TimeSpan> _queriedAt = new();
    readonly Dictionary<EmblemHash, byte[]> _emblems = new();
    /// <summary>When each cached emblem was last used (stored or read): the least recently used one not in use goes first.</summary>
    readonly Dictionary<EmblemHash, long> _emblemUse = new();
    long _emblemClock;
    TimeSpan _nextEmblemCheck = TimeSpan.MinValue;
    readonly Dictionary<EmblemHash, EmblemPull> _pulls = new();
    readonly Dictionary<EmblemHash, TimeSpan> _failedEmblems = new();
    readonly List<int> _appearanceChanges = new();
    readonly List<EmblemHash> _emblemArrivals = new();

    sealed class EmblemPull(BlobPull pull)
    {
        public BlobPull Pull { get; } = pull;
        public int Retries;
        public TimeSpan RetryAt = TimeSpan.MinValue;
    }

    /// <summary>The newest <see cref="SetupAck"/> for the newest <see cref="ChangeSetup"/>, or null before one arrives.</summary>
    public SetupAck? LastSetupAck { get; private set; }
    /// <summary>A <see cref="ChangeSetup"/> is still waiting for its acknowledgement (being resent).</summary>
    public bool SetupPending => _setupPending is not null;

    /// <summary>
    /// Asks the server to change this player's loadout and appearances from its next respawn (our default): indices for the class the
    /// player plays now on its side (a class of the other side maps by role, indices by position); the codes for both races. Resent every
    /// <see cref="ClientOptions.SetupResendInterval"/> until the matching <see cref="SetupAck"/>. Returns the request's sequence.
    /// </summary>
    /// <param name="orkLook">Protocol 28: the look worn while playing the Ork Boy (0..4, <see cref="Races.OrkLooks"/>), or
    /// <see cref="Races.OrkLookAutomatic"/> (by player id).</param>
    public ushort ChangeSetup(LoadoutChoice loadout, AppearanceCode spaceMarine, AppearanceCode chaos, byte orkLook = Races.OrkLookAutomatic)
    {
        _setupSequence++;
        RememberOwnEmblems(spaceMarine, chaos, replace: false);   // the old ones stay served until the server acknowledges
        _setupPending = new Setup(_session, _setupSequence, Protocol.Quantize(loadout), spaceMarine, chaos, orkLook);
        _setupSentAt = TimeSpan.MinValue;
        return _setupSequence;
    }

    /// <summary>Another player's appearance as last received (null: none received, or the default look).</summary>
    public RemoteAppearance? AppearanceOf(int playerId) => _appearances.GetValueOrDefault(playerId);

    /// <summary>A copy of a verified emblem (16,384 bytes of BC3) this client holds: one of its own, or one pulled from the server; null otherwise.</summary>
    public byte[]? Emblem(EmblemHash hash)
    {
        if (_emblems.TryGetValue(hash, out var data)) { _emblemUse[hash] = ++_emblemClock; return (byte[])data.Clone(); }
        return OwnEmblem(hash) is { } own ? (byte[])own.Clone() : null;
    }

    /// <summary>Players whose appearance changed since the last call (each once; also when it went back to the default look).</summary>
    public IReadOnlyList<int> TakeAppearanceChanges()
    {
        var list = _appearanceChanges.Distinct().ToList();
        _appearanceChanges.Clear();
        return list;
    }

    /// <summary>Emblems that arrived and verified since the last call (redraw the pads that show them).</summary>
    public IReadOnlyList<EmblemHash> TakeEmblemArrivals()
    {
        var list = _emblemArrivals.ToList();
        _emblemArrivals.Clear();
        return list;
    }

    /// <summary>An own emblem's verified bytes when the setup names it (<see cref="RememberOwnEmblems"/>), else null.</summary>
    byte[]? OwnEmblem(EmblemHash hash) => _ownEmblems.GetValueOrDefault(hash);

    /// <summary>
    /// Reads and verifies, once, the own emblems the codes name (<see cref="ClientOptions.EmblemSource"/>); with <paramref name="replace"/> the others are
    /// dropped (the Hello's setup), else they are kept (the server asks only for hashes the current or the shown look names).
    /// </summary>
    void RememberOwnEmblems(AppearanceCode spaceMarine, AppearanceCode chaos, bool replace)
    {
        var wanted = new[] { spaceMarine.Sanitized(), chaos.Sanitized() }.Where(c => c.UsesEmblem).Select(c => c.Emblem).ToHashSet();
        if (replace) foreach (var old in _ownEmblems.Keys.Where(h => !wanted.Contains(h)).ToList()) _ownEmblems.Remove(old);
        if (_options.EmblemSource is null) return;
        foreach (var hash in wanted)
            if (!_ownEmblems.ContainsKey(hash) && _options.EmblemSource(hash) is { } data && BlobTransfer.Verify(hash, data)) _ownEmblems[hash] = (byte[])data.Clone();
    }



    /// <summary>Messages of protocol 22 the client handles once connected.</summary>
    void OnAppearanceMessage(Message m)
    {
        switch (m)
        {
            case SetupAck ack when _setupPending is { } pending && ack.Sequence == pending.Sequence:
                LastSetupAck = ack;
                _setupPending = null;
                break;
            case Appearance a: OnAppearance(a); break;
            case BlobRequest r:
                // The server pulls an own emblem: only hashes of the current setup, only verified bytes.
                if (OwnEmblem(r.Hash) is { } blob) _transport.Send(Protocol.Write(BlobTransfer.Chunk(r.Hash, blob, r.Chunk)));
                break;
            case BlobChunk c when _pulls.TryGetValue(c.Hash, out var pull):
                switch (pull.Pull.Accept(c))
                {
                    case BlobChunkResult.Completed:
                        _pulls.Remove(c.Hash);
                        Keep(c.Hash, pull.Pull.Data!);
                        _emblemArrivals.Add(c.Hash);
                        break;
                    case BlobChunkResult.Corrupt:
                        // One retry from nothing, then the hash is given up on for a while (the fallback badge stays).
                        if (pull.Retries++ >= 1) { _pulls.Remove(c.Hash); _failedEmblems[c.Hash] = _clock.Now + _options.EmblemFailureBackoff; }
                        else pull.RetryAt = _clock.Now + _options.EmblemRetryDelay;
                        break;
                }
                break;
        }
    }

    void OnAppearance(Appearance a)
    {
        if (!ContentGate.OffersRace(_config.Extras, a.Race)) return;   // added content E2: race 3 (Orks) means nothing in a profile without the Orks
        // Never trust the server's indices; an Ork's code is rebuilt from its look alone (protocol 28).
        var limits = a.Race == Race.Chaos ? _options.ChaosLimits : _options.SpaceMarineLimits;
        var code = a.Race == Race.Orks ? OrkAppearanceRules.Apply(a.Code) : limits is null ? a.Code.Sanitized() : a.Code.Sanitized(limits);
        if (!_options.CustomEmblems) code = Appearances.WithoutEmblem(code);
        var appearance = new RemoteAppearance(a.Version, a.Race, code);
        if (_appearances.TryGetValue(a.PlayerId, out var old) && old == appearance) return;
        if (a.Version == 0) { if (_appearances.Remove(a.PlayerId)) _appearanceChanges.Add(a.PlayerId); return; }
        _appearances[a.PlayerId] = appearance;
        _appearanceChanges.Add(a.PlayerId);
        if (code.UsesEmblem) WantEmblem(code.Emblem);
    }

    void WantEmblem(EmblemHash hash)
    {
        if (!_options.CustomEmblems || _emblems.ContainsKey(hash) || _pulls.ContainsKey(hash) || OwnEmblem(hash) is not null) return;
        if (_failedEmblems.TryGetValue(hash, out var until)) { if (_clock.Now < until) return; _failedEmblems.Remove(hash); }
        if (_pulls.Count >= _options.MaxEmblemPulls) return;   // StepAppearance asks again every EmblemRecheckInterval
        _pulls[hash] = new EmblemPull(new BlobPull(hash));
    }

    /// <summary>Whether a known appearance shows the emblem (such emblems are never evicted).</summary>
    bool EmblemInUse(EmblemHash hash) => _appearances.Values.Any(a => a.Code.UsesEmblem && a.Code.Emblem == hash);

    /// <summary>Keeps a verified emblem; beyond <see cref="ClientOptions.MaxCachedEmblems"/> the least recently used one no known appearance shows goes.</summary>
    void Keep(EmblemHash hash, byte[] data)
    {
        _emblems[hash] = data;
        _emblemUse[hash] = ++_emblemClock;
        while (_emblems.Count > Math.Max(1, _options.MaxCachedEmblems))
        {
            var victim = _emblems.Keys.Where(h => h != hash && !EmblemInUse(h)).OrderBy(h => _emblemUse.GetValueOrDefault(h)).Select(h => (EmblemHash?)h).FirstOrDefault();
            if (victim is not { } v) break;   // every one is in use: at most one per player, so the cache stays small
            _emblems.Remove(v);
            _emblemUse.Remove(v);
        }
    }

    /// <summary>The scoreboard names versions: ask for the unknown ones (ids 1..65535 only, each at most every <see cref="ClientOptions.AppearanceQueryInterval"/>, 16 per query).</summary>
    void OnScoreboardAppearances(Scoreboard b)
    {
        var now = _clock.Now;
        var ids = new List<int>();
        foreach (var row in b.Players)
        {
            if (row.PlayerId is < 1 or > ushort.MaxValue || row.PlayerId == PlayerId) continue;   // a hostile server must not break the query
            if (row.AppearanceVersion == 0)
            {
                if (_appearances.Remove(row.PlayerId)) _appearanceChanges.Add(row.PlayerId);   // back to the default look
                continue;
            }
            if (_appearances.TryGetValue(row.PlayerId, out var known) && known.Version == row.AppearanceVersion) continue;
            if (_queriedAt.TryGetValue(row.PlayerId, out var at) && now - at < _options.AppearanceQueryInterval) continue;
            if (ids.Count >= Protocol.MaxAppearanceQuery) break;
            ids.Add(row.PlayerId);
            _queriedAt[row.PlayerId] = now;
        }
        foreach (int gone in _appearances.Keys.Where(id => !b.Players.Any(r => r.PlayerId == id)).ToList()) { _appearances.Remove(gone); _queriedAt.Remove(gone); }
        if (ids.Count > 0) _transport.Send(Protocol.Write(new AppearanceQuery(_session, ids.ToArray())));
    }

    /// <summary>Each update while connected: resend a pending Setup, drive the emblem pulls.</summary>
    void StepAppearance()
    {
        var now = _clock.Now;
        // Emblems skipped (pull cap) or given up on (after the back-off) are wanted again.
        if (now >= _nextEmblemCheck)
        {
            _nextEmblemCheck = now + _options.EmblemRecheckInterval;
            foreach (var a in _appearances.Values) if (a.Code.UsesEmblem) WantEmblem(a.Code.Emblem);
        }
        if (_setupPending is { } setup && (_setupSentAt == TimeSpan.MinValue || now - _setupSentAt >= _options.SetupResendInterval))
        {
            _transport.Send(Protocol.Write(setup with { Session = _session }));
            _setupSentAt = now;
        }
        long nowMs = (long)now.TotalMilliseconds;
        foreach (var (hash, pull) in _pulls.ToList())
        {
            if (pull.Pull.State == BlobPullState.TimedOut)
            {
                if (pull.Retries >= _options.EmblemPullRetries) { _pulls.Remove(hash); _failedEmblems[hash] = now + _options.EmblemFailureBackoff; continue; }
                if (pull.RetryAt == TimeSpan.MinValue) { pull.RetryAt = now + _options.EmblemRetryDelay; continue; }
            }
            if (pull.RetryAt != TimeSpan.MinValue)
            {
                if (now < pull.RetryAt) continue;
                pull.RetryAt = TimeSpan.MinValue;
                if (pull.Pull.State == BlobPullState.TimedOut) pull.Retries++;
                pull.Pull.Retry();
            }
            foreach (var r in pull.Pull.Requests(nowMs, _session)) _transport.Send(Protocol.Write(r));
        }
    }
}
