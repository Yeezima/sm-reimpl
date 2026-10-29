using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// Custom classes and appearances on the server (protocol 22; docs/customizer-architecture.md section 4): the Hello's setup at the join,
/// <see cref="Setup"/> changes applied at the next respawn and acknowledged (<see cref="SetupAck"/>, idempotent per sequence), a server-wide
/// appearance version per player (0 the default look) in the scoreboard, <see cref="AppearanceQuery"/> answers, and custom emblems:
/// pulled from the client that names them (<see cref="BlobPull"/>), verified, cached and served to the others. Abuse limits per peer in
/// <see cref="ServerOptions"/>.
/// </summary>
public sealed partial class GameServer
{
    ushort _appearanceCounter;
    readonly EmblemCache _emblems;
    readonly Dictionary<EmblemHash, Upload> _uploads = new();
    /// <summary>
    /// Addresses (connection ids) whose upload failed verification, and until when they may not upload: the uploader is penalised, not the hash
    /// (another player naming the same emblem can still upload it). The peer itself carries the same penalty.
    /// </summary>
    readonly Dictionary<int, TimeSpan> _bannedUploaders = new();

    sealed class Upload(BlobPull pull, int connection, Peer peer, TimeSpan started)
    {
        public BlobPull Pull { get; } = pull;
        public int Connection { get; } = connection;
        /// <summary>The peer pulled from: a new client on the same address (a rejoin) is another peer and is never asked for this upload.</summary>
        public Peer Peer { get; } = peer;
        public TimeSpan Started { get; } = started;
        public int Retries;
    }

    /// <summary>A player's appearance state (one per peer).</summary>
    sealed class AppearanceState
    {
        /// <summary>The race the shown look is for (protocol 28: follows the class, <see cref="Appearances.RaceOf(SimConfig, PlayerState)"/>).</summary>
        public Race Race = Race.SpaceMarines;
        /// <summary>The accepted code (rules applied), emblem included even before it arrived.</summary>
        public AppearanceCode Wanted;
        /// <summary>The version of what others see (0: the default look).</summary>
        public ushort Version;
        /// <summary>A Setup's code waiting for the next respawn, its race (a class change to or from the Ork Boy changes it then), and the version it will have.</summary>
        public AppearanceCode? Pending;
        public Race PendingRace = Race.SpaceMarines;
        public ushort PendingVersion;
        /// <summary>Emblem hashes this client named in its current setup (both races): the only ones the server asks it for.</summary>
        public HashSet<EmblemHash> Own = new();
        public ushort SetupSequence;
        public SetupAck? LastAck;
        /// <summary>The sequence of an accepted Setup whose loadout waits for the next respawn (the log says when it applied), or null.</summary>
        public ushort? PendingSetup;
        public TimeSpan LastSetupAt = TimeSpan.MinValue, LastNewEmblemAt = TimeSpan.MinValue;
        public TokenBucket Chunks = null!, Queries = null!;
        /// <summary>No upload from this peer before this time: a failed or slow upload's back-off (the emblem waits, then is retried).</summary>
        public TimeSpan UploadBlockedUntil = TimeSpan.MinValue;
        /// <summary>No upload from this peer before this time: the penalty for a blob that failed verification (its emblems are refused).</summary>
        public TimeSpan UploadBannedUntil = TimeSpan.MinValue;
        /// <summary>When the server next checks this peer's missing emblems (the retry of a deferred or failed upload).</summary>
        public TimeSpan NextEmblemCheck = TimeSpan.MinValue;
    }

    /// <summary>The appearance version and code others see for <paramref name="playerId"/> (version 0: the default look), or null when it is not a player here.</summary>
    public (ushort Version, Race Race, AppearanceCode Code)? AppearanceOf(int playerId)
    {
        if (PeerOf(playerId) is not { } peer) return null;
        var a = peer.Appearance;
        return (a.Version, a.Race, Published(a));
    }

    /// <summary>Whether the server holds a verified emblem with this hash.</summary>
    public bool HasEmblem(EmblemHash hash) => _emblems.Contains(hash);
    /// <summary>Uploads in progress (tests, diagnostics).</summary>
    public int EmblemUploads => _uploads.Count;

    /// <summary>A class's name for the log ('sm - devastator'), or its index when out of range.</summary>
    string ClassName(int playerClass) => playerClass >= 0 && playerClass < World.Config.Classes.Count ? $"'{World.Config.Classes[playerClass].Name}'" : $"class {playerClass}";

    Peer? PeerOf(int playerId) => _peers.Values.FirstOrDefault(p => p.PlayerId == playerId);

    /// <summary>The next appearance version: 1..65535, skipping 0 on the wrap (0 is the default look).</summary>
    ushort NextVersion()
    {
        _appearanceCounter = (ushort)(_appearanceCounter == ushort.MaxValue ? 1 : _appearanceCounter + 1);
        return _appearanceCounter;
    }

    /// <summary>Test seam: the counter's current value (the next version is one more, skipping 0).</summary>
    internal ushort AppearanceCounterForTests { set => _appearanceCounter = value; }

    /// <summary>What others see: the wanted code, with the fallback badge in place of an emblem the server does not hold (yet).</summary>
    AppearanceCode Published(AppearanceState a) =>
        a.Wanted.UsesEmblem && !_emblems.Contains(a.Wanted.Emblem) ? Appearances.WithoutEmblem(a.Wanted) : a.Wanted;

    /// <summary>The code the server accepts: the rules (or the structure alone without them); no emblem when custom emblems are off or the hash is refused.</summary>
    AppearanceCode Validate(AppearanceCode code, Race race, int playerClass) => EmblemPolicy(Ruled(code, race, playerClass));

    /// <summary>
    /// The code after the rules (or the structure alone without rules). An Ork's code always follows <see cref="OrkAppearanceRules"/> (protocol 28:
    /// only a look, never an emblem, so nothing is ever pulled for an Ork), whatever rules the server has.
    /// </summary>
    AppearanceCode Ruled(AppearanceCode code, Race race, int playerClass) =>
        race == Race.Orks ? OrkAppearanceRules.Apply(code) : _options.AppearanceRules?.Apply(code, race, playerClass) ?? code.Sanitized();

    /// <summary>No emblem when custom emblems are off (the fallback badge instead). An emblem not held yet stays wanted: <see cref="Published"/> shows the fallback.</summary>
    AppearanceCode EmblemPolicy(AppearanceCode c) => c.UsesEmblem && !_options.CustomEmblems ? Appearances.WithoutEmblem(c) : c;

    /// <summary>Whether <paramref name="connection"/> / <paramref name="a"/> carries the penalty for a blob that failed verification.</summary>
    bool UploadBanned(int connection, AppearanceState a)
    {
        var now = _clock.Now;
        if (now < a.UploadBannedUntil) return true;
        if (!_bannedUploaders.TryGetValue(connection, out var until)) return false;
        if (now < until) return true;
        _bannedUploaders.Remove(connection);
        return false;
    }

    /// <summary>Penalises the uploader of a blob that failed verification (pruning expired penalties on the way).</summary>
    void BanUploader(int connection, Peer? peer)
    {
        var now = _clock.Now;
        foreach (var expired in _bannedUploaders.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _bannedUploaders.Remove(expired);
        _bannedUploaders[connection] = now + _options.CorruptEmblemBlacklist;
        if (peer is not null) peer.Appearance.UploadBannedUntil = now + _options.CorruptEmblemBlacklist;
    }

    /// <summary>
    /// The race a player of this class and side is drawn as (protocol 28, <see cref="Appearances.RaceOf(SimConfig,int,Team)"/>). Race 3 (Orks) is refused in a
    /// profile without the Orks (added content E2, <see cref="ContentGate"/>): the side's race instead. No class can be an Ork there (<c>ClassFor</c>,
    /// <c>World.SetNextLoadout</c>); this is the second guard, and the Hello's and Setup's Ork look byte is ignored on the same rule.
    /// </summary>
    Race PlayableRace(int playerClass, Team team)
    {
        var race = Appearances.RaceOf(World.Config, playerClass, team);
        return ContentGate.OffersRace(World.Config.Extras, race) ? race : team == Team.Chaos ? Race.Chaos : Race.SpaceMarines;
    }

    /// <summary>The join: the Hello's appearance for the player's race applies at once.</summary>
    void JoinAppearance(int connection, Peer peer, PlayerState spawned, Hello h)
    {
        var a = peer.Appearance;
        a.Chunks = new TokenBucket(_options.MaxServedChunksPerSecond, _options.MaxServedChunksPerSecond);
        a.Queries = new TokenBucket(_options.MaxQueriedIdsPerSecond, _options.MaxQueriedIdsPerSecond);
        a.Race = PlayableRace(spawned.Class, spawned.Team);
        a.Own = OwnHashes(h.SpaceMarineAppearance, h.ChaosAppearance);
        a.Wanted = Validate((h with { OrkLook = ContentGate.OrkLook(h.OrkLook, World.Config.Extras) }).AppearanceFor(a.Race), a.Race, spawned.Class);
        a.Version = a.Wanted.IsDefault ? (ushort)0 : NextVersion();
        EnsureEmblem(connection, peer, a.Wanted);
    }

    static HashSet<EmblemHash> OwnHashes(params AppearanceCode[] codes) =>
        codes.Select(c => c.Sanitized()).Where(c => c.UsesEmblem).Select(c => c.Emblem).ToHashSet();

    /// <summary>
    /// Makes sure the emblem a code names is held or being pulled; the SetupAck flags that say how it stands: none needed (0); being pulled, or waiting
    /// for a free upload slot, for this peer's one upload, or for the one-new-emblem-per-<see cref="ServerOptions.NewEmblemInterval"/> limit
    /// (<see cref="SetupFlags.EmblemUploading"/>: <see cref="StepUploads"/> retries it); refused (<see cref="SetupFlags.EmblemRejected"/>: custom emblems
    /// off, a hash the client's setup does not name, or an uploader penalised for a blob that failed verification).
    /// </summary>
    SetupFlags EnsureEmblem(int connection, Peer peer, AppearanceCode code)
    {
        var a = peer.Appearance;
        if (!code.UsesEmblem) return SetupFlags.None;
        var hash = code.Emblem;
        if (_emblems.Contains(hash)) return SetupFlags.None;
        if (_uploads.ContainsKey(hash)) return SetupFlags.EmblemUploading;   // being pulled (from this client or another one naming it)
        var now = _clock.Now;
        if (!_options.CustomEmblems || !a.Own.Contains(hash) || UploadBanned(connection, a)) return SetupFlags.EmblemRejected;
        bool deferred = now < a.UploadBlockedUntil
            || (a.LastNewEmblemAt != TimeSpan.MinValue && now - a.LastNewEmblemAt < _options.NewEmblemInterval)
            || _uploads.Count >= _options.MaxEmblemUploads
            || _uploads.Values.Any(u => ReferenceEquals(u.Peer, peer));   // at most one upload per peer
        if (deferred) return SetupFlags.EmblemUploading;   // queued: StepUploads tries again
        a.LastNewEmblemAt = now;
        _uploads[hash] = new Upload(new BlobPull(hash), connection, peer, now);
        return SetupFlags.EmblemUploading;
    }

    /// <summary>A Setup (protocol 22): idempotent per sequence, at most one new request per <see cref="ServerOptions.SetupInterval"/>.</summary>
    void OnSetup(int connection, Peer peer, Setup s)
    {
        var a = peer.Appearance;
        peer.LastHeard = _clock.Now;
        if (a.LastAck is { } previous && s.Sequence == a.SetupSequence) { _transport.Send(connection, Protocol.Write(previous)); return; }   // a resend
        if (a.LastAck is not null && (short)(s.Sequence - a.SetupSequence) <= 0) return;   // older than the one answered
        var now = _clock.Now;
        if (a.LastSetupAt != TimeSpan.MinValue && now - a.LastSetupAt < _options.SetupInterval) return;   // too soon: the client resends
        if (!World.Players.TryGetValue(peer.PlayerId, out var player)) return;
        var requested = Protocol.Quantize(s.Loadout);
        if (World.SetNextLoadout(peer.PlayerId, requested) is not { } resolved) return;
        a.LastSetupAt = now;
        var pistols = Loadouts.Choices(World.Config, resolved.Class, WeaponSlot.Pistol);
        NotePistolDefault(peer.PlayerId, resolved.Class, requested.Pistol, resolved.Pistol >= 0 && resolved.Pistol < pistols.Count ? pistols[resolved.Pistol] : Loadouts.WeaponIndex(World.Config, resolved.Class, WeaponSlot.Pistol));
        int requestedClass = requested.Class >= 0 && requested.Class < World.Config.Classes.Count ? World.ClassForSide(requested.Class, player.Team) : resolved.Class;
        bool classRefused = requested.Class >= 0 && !ContentGate.ClassOffered(World.Config, requested.Class);   // added content E2: also a class beyond a faithful config's six
        bool defaulted = classRefused || requestedClass != resolved.Class
            || Differs(requested.Primary, resolved.Primary) || Differs(requested.Pistol, resolved.Pistol) || Differs(requested.Melee, resolved.Melee)
            || Differs(requested.Equipment, resolved.Equipment) || Differs(requested.Perk1, resolved.Perk1) || Differs(requested.Perk2, resolved.Perk2);
        a.Own = OwnHashes(s.SpaceMarineAppearance, s.ChaosAppearance, a.Wanted);   // the look shown until the respawn still needs its emblem
        // Protocol 28: the race of the class the player will have (a change to or from the Ork Boy changes it at the respawn; the side stays).
        var race = PlayableRace(resolved.Class, player.Team);
        var sent = (s with { OrkLook = ContentGate.OrkLook(s.OrkLook, World.Config.Extras) }).AppearanceFor(race);
        var ruled = Ruled(sent, race, resolved.Class);
        defaulted |= ruled != sent || (race == Race.Orks && s.OrkLook != Races.OrkLookAutomatic && s.OrkLook >= Races.OrkLooks);   // an unknown look: automatic
        var code = EmblemPolicy(ruled);
        // The same look of the same race keeps its version (a repeated pending one, or the one shown now); a new look gets a new one.
        a.PendingVersion = code.IsDefault ? (ushort)0
            : a.Pending == code && a.PendingRace == race ? a.PendingVersion
            : code == a.Wanted && race == a.Race ? a.Version : NextVersion();
        a.Pending = code;
        a.PendingRace = race;
        var flags = SetupFlags.Pending | EnsureEmblem(connection, peer, code) | (defaulted ? SetupFlags.Defaulted : 0);
        if (ruled.UsesEmblem && !code.UsesEmblem) flags |= SetupFlags.EmblemRejected;
        a.SetupSequence = s.Sequence;
        a.LastAck = new SetupAck(s.Sequence, resolved, flags, a.PendingVersion);
        a.PendingSetup = s.Sequence;
        _transport.Send(connection, Protocol.Write(a.LastAck));
        Log?.Invoke($"player {peer.PlayerId} setup {s.Sequence}: next life {ClassName(resolved.Class)} (now {ClassName(player.Class)}, {player.Team})"
            + (requested.Class >= 0 && requested.Class < World.Config.Classes.Count && requested.Class != resolved.Class ? $"; asked for {ClassName(requested.Class)}, a class of the other side: the {player.Team} class of the same role instead" : "")
            + (defaulted ? "; some choices defaulted" : ""));

        static bool Differs(int requested, int resolved) => requested >= 0 && requested != resolved;
    }

    /// <summary>
    /// After a tick: players who respawned take their pending appearance with the version their SetupAck named; a pending look equal to the one shown keeps
    /// the current version (it may have moved on since the Setup, e.g. when the emblem arrived: going back would show clients a version they cached with
    /// the fallback badge). The race changes here too (protocol 28: a class change to or from the Ork Boy).
    /// </summary>
    void PromoteAppearances()
    {
        foreach (int id in World.LastRespawns)
        {
            if (PeerOf(id) is { Appearance: { PendingSetup: { } applied } setup } && World.Players.TryGetValue(id, out var respawned))
            {
                setup.PendingSetup = null;
                Log?.Invoke($"player {id} respawned as {ClassName(respawned.Class)} (setup {applied} applied)");
            }
            if (PeerOf(id) is not { Appearance: { Pending: { } code } a }) continue;
            if (code != a.Wanted || a.PendingRace != a.Race) { a.Wanted = code; a.Race = a.PendingRace; a.Version = a.PendingVersion; }
            a.Pending = null;
        }
    }

    /// <summary>An appearance query: answers for players with a non-default look, within the peer's id budget.</summary>
    void OnAppearanceQuery(int connection, Peer peer, AppearanceQuery q)
    {
        peer.LastHeard = _clock.Now;
        var now = _clock.Now;
        foreach (int id in q.PlayerIds)
        {
            if (!peer.Appearance.Queries.TryTake(now)) break;
            if (PeerOf(id) is not { } other || other.Appearance.Version == 0) continue;
            var a = other.Appearance;
            _transport.Send(connection, Protocol.Write(new Appearance(id, a.Version, a.Race, Published(a))));
        }
    }

    /// <summary>A client asks for a chunk: served only for a verified blob a connected player shows, within the peer's chunk budget.</summary>
    void OnBlobRequest(int connection, Peer peer, BlobRequest r)
    {
        peer.LastHeard = _clock.Now;
        if (!_options.CustomEmblems || !_emblems.Contains(r.Hash) || !_peers.Values.Any(p => Shows(p.Appearance, r.Hash))) return;
        if (!peer.Appearance.Chunks.TryTake(_clock.Now)) return;
        _transport.Send(connection, Protocol.Write(BlobTransfer.Chunk(r.Hash, _emblems.Get(r.Hash)!, r.Chunk)));
    }

    bool Shows(AppearanceState a, EmblemHash hash) => a.Wanted.UsesEmblem && a.Wanted.Emblem == hash;
    bool Pinned(EmblemHash hash) => _peers.Values.Any(p => Shows(p.Appearance, hash) || p.Appearance.Pending is { UsesEmblem: true } c && c.Emblem == hash);

    /// <summary>A chunk of an upload: taken only from the connection the server is pulling that blob from.</summary>
    void OnBlobChunk(int connection, BlobChunk c)
    {
        if (!_uploads.TryGetValue(c.Hash, out var upload) || upload.Connection != connection
            || !_peers.TryGetValue(connection, out var current) || !ReferenceEquals(current, upload.Peer)) return;
        if (_peers.TryGetValue(connection, out var peer)) peer.LastHeard = _clock.Now;
        switch (upload.Pull.Accept(c))
        {
            case BlobChunkResult.Completed:
                _uploads.Remove(c.Hash);
                if (!_emblems.Add(c.Hash, upload.Pull.Data!, Pinned)) { Log?.Invoke($"emblem {c.Hash}: the cache is full of emblems in use; not kept"); break; }
                // The look that was waiting for it changes now: a new version, so clients ask again and pull the emblem.
                foreach (var p in _peers.Values.Where(p => Shows(p.Appearance, c.Hash))) p.Appearance.Version = NextVersion();
                break;
            case BlobChunkResult.Corrupt:
                _uploads.Remove(c.Hash);
                BanUploader(connection, upload.Peer);
                Log?.Invoke($"emblem {c.Hash} from player {upload.Peer.PlayerId} failed verification; that player may not upload for {_options.CorruptEmblemBlacklist.TotalSeconds} s");
                break;
        }
    }

    /// <summary>
    /// Each update: drives the uploads (requests, retries after a timeout; an upload past <see cref="ServerOptions.MaxUploadTime"/> or out of retries
    /// ends with a back-off for its peer), then, every <see cref="ServerOptions.EmblemRetryInterval"/> per peer, starts the uploads that were deferred
    /// or failed for every emblem a peer's current or pending look names and the server lacks (within the caps).
    /// </summary>
    void StepUploads()
    {
        var now = _clock.Now;
        long nowMs = (long)now.TotalMilliseconds;
        foreach (var (hash, upload) in _uploads.ToList())
        {
            if (!_peers.TryGetValue(upload.Connection, out var peer) || !ReferenceEquals(peer, upload.Peer) || !peer.Appearance.Own.Contains(hash)) { _uploads.Remove(hash); continue; }
            bool slow = now - upload.Started > _options.MaxUploadTime;   // a trickle of chunks must not hold a slot
            if (upload.Pull.State == BlobPullState.TimedOut && !slow)
            {
                if (upload.Retries++ >= _options.EmblemUploadRetries) slow = true;
                else upload.Pull.Retry();
            }
            if (slow)
            {
                _uploads.Remove(hash);
                peer.Appearance.UploadBlockedUntil = now + _options.EmblemFailureBackoff;
                continue;
            }
            foreach (var r in upload.Pull.Requests(nowMs, 0)) _transport.Send(upload.Connection, Protocol.Write(r));
        }
        if (!_options.CustomEmblems) return;
        foreach (var (connection, peer) in _peers)
        {
            var a = peer.Appearance;
            if (now < a.NextEmblemCheck) continue;
            a.NextEmblemCheck = now + _options.EmblemRetryInterval;
            foreach (var code in a.Pending is { } pending ? new[] { a.Wanted, pending } : [a.Wanted])
                if (code.UsesEmblem && !_emblems.Contains(code.Emblem) && !_uploads.ContainsKey(code.Emblem)) EnsureEmblem(connection, peer, code);
        }
    }
}
