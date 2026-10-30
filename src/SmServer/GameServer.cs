using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using SmNet;
using SmSim;

namespace SmServer;

/// <summary>Server tuning. Provisional prototype defaults (docs/architecture.md), not original values.</summary>
public sealed partial record ServerOptions
{
    public int SnapshotRate { get; init; } = 20;
    /// <summary>
    /// Test aid (<c>--bot-damage-scale</c>; owner, 2026-10-29): every hit dealt by a client whose Hello marks it a test bot (<see cref="Hello.Bot"/>,
    /// protocol 37) is scaled by this (<see cref="World.SetDamageScale"/>); 1 (default): off.
    /// </summary>
    public float BotDamageScale { get; init; } = 1f;
    public int MaxPlayers { get; init; } = Protocol.MaxPlayers;
    /// <summary>
    /// Protocol 46 (ours; observer tooling): spectators admitted besides the <see cref="MaxPlayers"/> players (0 admits none; <c>--max-spectators</c>), at most
    /// <see cref="Protocol.MaxSpectators"/> so the Scoreboard lists all of them. They never count toward <see cref="MaxPlayers"/>, teams, scores or balance.
    /// </summary>
    public int MaxSpectators { get; init; } = Protocol.MaxSpectators;
    /// <summary>Inputs further ahead than this many ticks are ignored (protects the buffer).</summary>
    public int MaxInputLead { get; init; } = 120;
    /// <summary>A player not heard from for this long is removed and its address forgotten.</summary>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// A player silent this long gets a neutral input (no movement, same heading) instead of the repeated last input, so a
    /// departed or frozen client does not keep running until <see cref="PeerTimeout"/>. Shorter gaps (lost packets) repeat.
    /// </summary>
    public TimeSpan NeutralInputAfter { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Packets handled per <see cref="GameServer.Update"/>; the rest wait, so a flood cannot stop the simulation.</summary>
    public int MaxPacketsPerUpdate { get; init; } = 1024;
    /// <summary>
    /// Largest client interpolation delay honoured by lag compensation: a shot may rewind targets to at most this far
    /// (plus 2 ticks of slack) behind the newest snapshot the shooter has acknowledged.
    /// </summary>
    public TimeSpan MaxInterpolationDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>
    /// Class (index into <see cref="SimConfig.Classes"/>) for a client that asks for the default or for a class that does
    /// not exist. Out of range itself: class 0.
    /// </summary>
    public int DefaultClass { get; init; }
    /// <summary>
    /// The match rules (protocol 10): null (the default) keeps the free-for-all of earlier milestones; the dedicated server
    /// runs Annihilation (<see cref="MatchRules"/>).
    /// </summary>
    public MatchRules? Match { get; init; }
    /// <summary>How often every client gets a <see cref="Scoreboard"/> (ours).</summary>
    public TimeSpan ScoreboardInterval { get; init; } = TimeSpan.FromSeconds(1);

    // ---- custom classes and appearances (protocol 22; docs/customizer-architecture.md section 4; limits ours) ----
    /// <summary>Which unlocks are allowed (<see cref="UnlockMode.All"/>, the only mode; <c>--unlocks all</c>).</summary>
    public UnlockMode Unlocks { get; init; } = UnlockMode.All;
    /// <summary>The appearance rules (the catalogue's ranges); null: the code's structure only (tests, no install).</summary>
    public IAppearanceRules? AppearanceRules { get; init; }
    /// <summary>Whether custom emblems are pulled, kept and served (<c>--no-custom-emblems</c>: never; everyone sees the fallback badge).</summary>
    public bool CustomEmblems { get; init; } = true;
    /// <summary>At most one new Setup per peer this often (a resend of the same sequence is answered at any time).</summary>
    public TimeSpan SetupInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>At most one new emblem hash per peer this often.</summary>
    public TimeSpan NewEmblemInterval { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Chunks served to one peer per second (token bucket, this burst).</summary>
    public int MaxServedChunksPerSecond { get; init; } = 64;
    /// <summary>Player ids answered per peer per second (token bucket, this burst).</summary>
    public int MaxQueriedIdsPerSecond { get; init; } = 32;
    /// <summary>Verified emblems kept (16,384 bytes each; in-use ones are pinned).</summary>
    public int MaxCachedEmblems { get; init; } = 32;
    /// <summary>Emblem uploads in progress at once (all peers).</summary>
    public int MaxEmblemUploads { get; init; } = 4;
    /// <summary>Retries of an upload that timed out before giving up (the chunks held are kept).</summary>
    public int EmblemUploadRetries { get; init; } = 2;
    /// <summary>How long a player (and its address) whose upload failed verification may not upload (the uploader is penalised, not the hash).</summary>
    public TimeSpan CorruptEmblemBlacklist { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>The longest an upload may take (a trickle of chunks must not hold a slot); then its peer backs off.</summary>
    public TimeSpan MaxUploadTime { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>A peer whose upload timed out or ran too long waits this long before the next try.</summary>
    public TimeSpan EmblemFailureBackoff { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How often the server looks again for each peer's emblems it lacks (deferred or failed uploads are retried).</summary>
    public TimeSpan EmblemRetryInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Authoritative server: refuses incompatible clients at the handshake, buffers each player's inputs by tick, runs the
/// shared <see cref="World"/> at a fixed tick, and sends snapshots at their own rate. A missing input repeats the last one.
/// A player is created only after the client echoes a stateless challenge cookie (so a spoofed source address cannot
/// create players), and every input must carry the player's session token. Silent players time out.
/// Hosted by the console app (UDP) or in-process (loopback) for local matches and tests.
/// </summary>
public sealed partial class GameServer
{
    readonly IServerTransport _transport;
    readonly IClock _clock;
    readonly ServerOptions _options;
    readonly ulong _contentFingerprint;
    readonly byte[] _cookieSecret = RandomNumberGenerator.GetBytes(32);
    readonly Dictionary<int, Peer> _peers = new();   // by connection id
    TimeSpan? _nextTickAt, _nextSnapshotAt, _nextScoreboardAt;
    int _nextPlayerId = 1;

    public World World { get; }
    /// <summary>The ids of the connected players (not the spectators, protocol 46).</summary>
    public IReadOnlyCollection<int> Players => _peers.Values.Where(p => !p.Spectator).Select(p => p.PlayerId).ToList();
    /// <summary>The ids of the connected spectators (protocol 46).</summary>
    public IReadOnlyCollection<int> Spectators => _peers.Values.Where(p => p.Spectator).Select(p => p.PlayerId).ToList();
    public int Rejected { get; private set; }
    public int TimedOut { get; private set; }

    sealed class Peer(int playerId, uint nonce, uint session)
    {
        public int PlayerId { get; } = playerId;
        public uint Nonce { get; } = nonce;
        public uint Session { get; } = session;
        public SortedDictionary<uint, PlayerInput> Inputs { get; } = new();
        public PlayerInput LastApplied;
        public uint LastAppliedFromClient;
        public uint NewestReceived;
        /// <summary>Newest snapshot tick the client says it has received; only moves forward.</summary>
        public uint AckedSnapshot;
        /// <summary>The acknowledged snapshot when each buffered input first arrived (what the client had seen when it aimed).</summary>
        public Dictionary<uint, uint> AckAt { get; } = new();
        /// <summary>Acknowledged snapshot that goes with <see cref="LastApplied"/>.</summary>
        public uint LastAppliedAck;
        public TimeSpan LastHeard;
        /// <summary>Newest kill-feed sequence the client has received (it acknowledged a snapshot carrying it); entries go out oldest first after it.</summary>
        public int AckedKill;
        /// <summary>For each snapshot sent: the newest kill sequence it carried.</summary>
        public Dictionary<uint, int> SentKills { get; } = new();
        /// <summary>Running totals of this player's hits on others since it joined (protocol 12; <see cref="HitView"/>).</summary>
        public int HitCount, KillCount, LastVictim;
        public double ShieldDealt, HealthDealt;
        public HitFlags LastFlags;
        public float VictimHealth, VictimShield;
        public HitView Hits => new(HitCount & 0xFFFF, (int)((long)Math.Round(ShieldDealt * 10) & 0xFFFF), (int)((long)Math.Round(HealthDealt * 10) & 0xFFFF), KillCount & 0xFF,
            LastVictim, LastFlags, VictimHealth, VictimShield);
        /// <summary>Custom classes and appearances (protocol 22).</summary>
        public AppearanceState Appearance { get; } = new();
        /// <summary>The Hello marked a test bot (protocol 37).</summary>
        public bool Bot;
        /// <summary>Protocol 46: the Hello marked a spectator: no player in the world, no input, no score; it only receives.</summary>
        public bool Spectator;
        /// <summary>Protocol 46: a spectator's camera (game metres, clamped to the map), from its batches; null until one arrives.</summary>
        public Vector3? Camera;
        /// <summary>Display name (protocol 41, sanitised; "" none: "P&lt;id&gt;"); <see cref="NameRace"/> the race whose pool gave it (None: not a pool name).</summary>
        public string Name = "";
        public Race NameRace;
    }

    /// <summary>The map this server runs, or null for the flat placeholder ground.</summary>
    public GameMap? Map { get; }

    public GameServer(SimConfig config, ulong contentFingerprint, IServerTransport transport, IClock clock, ServerOptions? options = null, GameMap? map = null)
    {
        if (map is not null && !Protocol.IsMapName(map.Name)) throw new ArgumentException($"'{map.Name}' is not a map name (letters, digits, '_'; at most {Protocol.MaxMapName}).");
        Map = map;
        _options = options ?? new ServerOptions();
        if (_options.Match is { Mode: GameMode.SeizeGround } && (map?.ControlPoints?.Count ?? 0) == 0)
            throw new ArgumentException("Seize Ground needs a map with control points.");
        foreach (var p in map?.ControlPoints ?? [])
            if (!Protocol.IsMapName(p.Name)) throw new ArgumentException($"Control point name '{p.Name}' cannot travel (letters, digits, '_'; at most {Protocol.MaxMapName}).");
        World = new World(config, map?.Collision, map?.Spawns) { BoundBodies = true, Rules = _options.Match, ControlPoints = map?.ControlPoints ?? [], StartAreas = map?.StartAreas ?? [], SpawnBlockers = map?.Blockers ?? map?.Collision?.Blockers ?? [] };
        _emblems = new EmblemCache(Math.Max(1, _options.MaxCachedEmblems));
        _contentFingerprint = contentFingerprint; _transport = transport; _clock = clock;
    }

    public void Update()
    {
        RefreshBans(_clock.Now);   // hardening (GameServer.Guard.cs): bans, per-address rules, idle kick, crash isolation
        for (int handled = 0; handled < _options.MaxPacketsPerUpdate && _transport.TryReceive(out int connection, out var packet); handled++)
        {
            if (IsBanned(connection)) continue;   // banned sources get no reply
            try { Dispatch(connection, packet); }
            catch (Exception e) when (Recoverable(e)) { OnPacketFault(connection, packet, e); }
        }
        Guard("emblem uploads", StepUploads);

        var now = _clock.Now;
        foreach (var (connection, peer) in _peers.Where(kv => now - kv.Value.LastHeard > _options.PeerTimeout).ToList())
        {
            RemovePeer(connection, peer, reason: "timed out");
            TimedOut++;
        }
        KickIdle(now);

        var tickLength = TimeSpan.FromSeconds(World.Config.TickSeconds);
        var snapshotLength = TimeSpan.FromSeconds(1.0 / _options.SnapshotRate);
        _nextTickAt ??= now;
        _nextSnapshotAt ??= now;
        _nextScoreboardAt ??= now;
        for (int ran = 0; now >= _nextTickAt; ran++)
        {
            if (_options.MaxCatchUpTicks > 0 && ran >= _options.MaxCatchUpTicks) { SkipLateTicks(now, tickLength); break; }
            GuardTick();   // a fault is logged; too many in a row stop the server for its supervisor (GameServer.Guard.cs)
            _nextTickAt += tickLength;
            if (_nextTickAt > _nextSnapshotAt) { Guard("snapshots", SendSnapshots); _nextSnapshotAt += snapshotLength; }
            if (_nextTickAt > _nextScoreboardAt) { Guard("scoreboards", SendScoreboards); _nextScoreboardAt += _options.ScoreboardInterval; }
            Guard("bars", SendBars);   // protocol 44: Tactical Readout / Signum Link (GameServer.Bars.cs)
            Guard("pickups", SendPickups);   // protocol 26: dropped pickups, outside the snapshots (GameServer.Pickups.cs)
            Guard("extras", SendExtrasDue);   // added content's envelope, never on a faithful server (GameServer.Envelope.cs)
        }
    }

    /// <summary>Handles one received packet; <see cref="Update"/> catches a throw and drops only that sender (hardening M3).</summary>
    void Dispatch(int connection, byte[] packet)
    {
        var message = Protocol.Read(packet);
        PacketHookForTests?.Invoke(connection, message);
        switch (message)
        {
            case Hello h: OnHello(connection, h); break;
            case InputBatch b when _peers.TryGetValue(connection, out var peer) && b.Session == peer.Session: OnInputs(peer, b); NoteInputs(connection, b); break;
            case Leave l when _peers.TryGetValue(connection, out var leaving) && l.Session == leaving.Session: RemovePeer(connection, leaving); break;
            // Protocol 22: every client message needs the session; a chunk is taken only from the connection being pulled.
            case Setup s when _peers.TryGetValue(connection, out var setting) && s.Session == setting.Session: OnSetup(connection, setting, s); NoteActivity(connection); break;
            case AppearanceQuery q when _peers.TryGetValue(connection, out var asking) && q.Session == asking.Session: OnAppearanceQuery(connection, asking, q); break;
            case BlobRequest r when _peers.TryGetValue(connection, out var pulling) && r.Session == pulling.Session: OnBlobRequest(connection, pulling, r); break;
            case BlobChunk c when _peers.ContainsKey(connection): OnBlobChunk(connection, c); NoteActivity(connection); break;
            case null: RefuseOldHello(connection, packet); break;
        }
    }

    void OnHello(int connection, Hello h)
    {
        if (h.Cookie != Cookie(connection, h.Nonce))
        {
            // First contact (or a stale cookie): no state, and nothing larger than the request goes back to an address
            // that has not proven it receives (not even a refusal).
            _transport.Send(connection, Protocol.Write(new Challenge(h.Nonce, Cookie(connection, h.Nonce), h.SentMs, Map?.Name ?? "")));
            return;
        }
        RejectReason? reason =
            h.ProtocolVersion != Protocol.Version ? RejectReason.ProtocolMismatch :
            h.ContentFingerprint != _contentFingerprint ? RejectReason.ContentMismatch :
            h.ConfigHash != World.Config.Hash() ? RejectReason.ConfigMismatch :
            h.MapHash != (Map?.Hash ?? 0) ? RejectReason.MapMismatch : null;
        if (reason is { } incompatible) { NoteRefusal(connection, incompatible, h); Refuse(connection, incompatible, h.Nonce); return; }

        if (_peers.TryGetValue(connection, out var existing) && existing.Nonce == h.Nonce)
        {
            // Our Welcome was lost and the same client asks again: answer with the same player. Not play: LastHeard stays, so
            // repeated Hellos cannot hold a slot (hardening M2).
            _transport.Send(connection, Protocol.Write(new Welcome(existing.PlayerId, World.Tick, World.Config.TickRate, h.Nonce, h.SentMs, existing.Session)));
            return;
        }
        if (!AdmitJoin(connection, h.Nonce, existing)) return;   // per-address cap, held respawn, join rate (GameServer.Guard.cs)
        // A new client on a reused address (e.g. NAT port reuse after a restart): the old player is gone.
        if (existing is not null) RemovePeer(connection, existing, forgetAddress: false, reason: "replaced by a new client at the same address");
        if (h.Spectator)
        {
            if (Math.Min(_options.MaxSpectators, Protocol.MaxSpectators) <= _peers.Values.Count(p => p.Spectator)) { Refuse(connection, RejectReason.ServerFull, h.Nonce); return; }
        }
        else if (_peers.Values.Count(p => !p.Spectator) >= _options.MaxPlayers) { Refuse(connection, RejectReason.ServerFull, h.Nonce); return; }

        var peer = new Peer(NextPlayerId(), h.Nonce, BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)) | 1)
        {
            LastHeard = _clock.Now, AckedKill = World.LastKillSequence,   // the feed starts at the join
        };
        _peers[connection] = peer;
        _transport.Pin(connection);   // an authenticated player's address must never be evicted by a flood
        if (h.Spectator) { JoinSpectator(connection, peer, h); return; }
        // Class and loadout are kept across respawns until a Setup changes them (protocol 22); invalid choices get the class default.
        // With match rules the class may move to the other side to balance the teams (World.AssignClass); the choice indices, which a
        // client sends for the class it asked for, then carry over by position to the other side's class.
        // Ork O5: a class without a side (the Ork Boy) keeps its class and joins the side it asks for under the same balance rule
        // (World.AssignSide; no request: the smaller side); a class with a side gets that side back.
        int playerClass = World.AssignClass(ClassFor(h.RequestedClass), SideFor(h.RequestedSide));
        var team = World.AssignSide(playerClass, SideFor(h.RequestedSide));
        // Added content E2: a refused class (the Ork Boy without the Orks, or one the config does not have) takes none of its item choices along: the default class's defaults.
        bool classRefused = h.RequestedClass != Protocol.ServerDefaultClass && !ContentGate.ClassOffered(World.Config, h.RequestedClass);
        int Choice(byte requested) => classRefused ? -1 : ChoiceFor(requested);
        var spawned = World.Spawn(peer.PlayerId, playerClass, Choice(h.RequestedPrimary), Choice(h.RequestedPistol), Choice(h.RequestedMelee),
            Choice(h.RequestedEquipment), Choice(h.RequestedPerk1), Choice(h.RequestedPerk2), team: team, secondChoice: Choice(h.RequestedSecond));
        NotePistolDefault(peer.PlayerId, playerClass, Choice(h.RequestedPistol), spawned.Pistol);
        if (h.Bot && _options.BotDamageScale != 1f)   // test aid (--bot-damage-scale)
        {
            World.SetDamageScale(peer.PlayerId, _options.BotDamageScale);
            Log?.Invoke($"player {peer.PlayerId} is a test bot: the damage it deals x {_options.BotDamageScale:0.###} (--bot-damage-scale)");
        }
        JoinAppearance(connection, peer, spawned, h);
        peer.Bot = h.Bot;
        if (h.Bot) NameBot(peer);   // names/bot-lore (protocol 41): a bot's name from its race's pool
        _transport.Send(connection, Protocol.Write(new Welcome(peer.PlayerId, World.Tick, World.Config.TickRate, h.Nonce, h.SentMs, peer.Session)));
        NoteJoin(connection, peer.PlayerId);
        PlayerJoined?.Invoke(spawned);
    }

    /// <summary>A spectator's claimed camera kept inside the map's bounds (plus a margin to fly past the walls); a non-finite one reads as the map's middle (the origin without a map).</summary>
    internal Vector3 ClampCamera(Vector3 at)
    {
        Vector3 lo = new(-2000f), hi = new(2000f);
        if (Map is { } map)
        {
            const float margin = 30f;
            lo = Vector3.Min(map.Collision.Weapons.Min, map.Collision.Character.Min) - new Vector3(margin);
            hi = Vector3.Max(map.Collision.Weapons.Max, map.Collision.Character.Max) + new Vector3(margin);
        }
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) return (lo + hi) / 2f;
        return Vector3.Clamp(at, lo, hi);
    }

    /// <summary>Test seam: a spectator's clamped camera.</summary>
    internal Vector3? CameraOfForTests(int id) => _peers.Values.FirstOrDefault(p => p.PlayerId == id)?.Camera;

    /// <summary>
    /// Protocol 46 (ours; observer tooling): a spectator joins. It has an id and a session like a player but no world player: nothing is spawned, so it has no team, score, body
    /// or hit shape and nothing can target it; it is listed in the Scoreboard as a spectator and sent every snapshot (everyone, slim). It is not asked to be active (<see cref="KickIdle"/>).
    /// </summary>
    void JoinSpectator(int connection, Peer peer, Hello h)
    {
        peer.Spectator = true;
        peer.Name = $"Spectator {peer.PlayerId}";
        var a = peer.Appearance;   // it may query the others' looks and pull their emblems like a player; it has no look of its own
        a.Chunks = new TokenBucket(_options.MaxServedChunksPerSecond, _options.MaxServedChunksPerSecond);
        a.Queries = new TokenBucket(_options.MaxQueriedIdsPerSecond, _options.MaxQueriedIdsPerSecond);
        a.Race = Race.None;
        _transport.Send(connection, Protocol.Write(new Welcome(peer.PlayerId, World.Tick, World.Config.TickRate, h.Nonce, h.SentMs, peer.Session)));
        NoteJoin(connection, peer.PlayerId, spectator: true);
    }

    /// <summary>Server notes (a pistol defaulted for lack of True Grit, an emblem refused); the console server prints them.</summary>
    public event Action<string>? Log;

    /// <summary>Logs a requested pistol-slot bolter that the True Grit rule replaced with the class default (<see cref="Loadouts.PistolPerkRule"/>).</summary>
    void NotePistolDefault(int playerId, int playerClass, int pistolChoice, int pistolGot)
    {
        var pistols = Loadouts.Choices(World.Config, playerClass, WeaponSlot.Pistol);
        if (pistolChoice < 0 || pistolChoice >= pistols.Count || pistols[pistolChoice] == pistolGot) return;
        var wanted = World.Config.Weapons[pistols[pistolChoice]];
        if (!Loadouts.IsPistolBolter(wanted)) return;
        string got = pistolGot >= 0 && pistolGot < World.Config.Weapons.Count ? World.Config.Weapons[pistolGot].Name : "none";
        Log?.Invoke($"player {playerId}: {wanted.Name} needs the {Loadouts.TrueGritPerk} perk (True Grit rule); pistol {got} instead");
    }

    /// <summary>
    /// A Hello of another protocol version that this one cannot read (an older client): refused with <see cref="RejectReason.ProtocolMismatch"/>
    /// instead of silence, when the refusal is not larger than the request (no amplification; <see cref="Protocol.PeekHello"/>).
    /// </summary>
    void RefuseOldHello(int connection, byte[] packet)
    {
        if (Protocol.PeekHello(packet) is not { } peek || peek.Version == Protocol.Version) return;
        var reject = Protocol.Write(new Reject(RejectReason.ProtocolMismatch, $"server protocol {Protocol.Version}", peek.Nonce));
        if (reject.Length > packet.Length) return;
        Rejected++;
        _transport.Send(connection, reject);
    }

    /// <summary>Raised when a player joins, with its spawned state (class and chosen loadout; <see cref="Loadouts.Describe"/>).</summary>
    public event Action<PlayerState>? PlayerJoined;

    /// <summary>A requested loadout choice as <see cref="World.Spawn"/> takes it: <see cref="Protocol.DefaultChoice"/> is -1 (the class default).</summary>
    public static int ChoiceFor(byte requested) => requested == Protocol.DefaultChoice ? -1 : requested;

    /// <summary>A requested side as <see cref="World.AssignClass"/> takes it: 1 Space Marines, 2 Chaos, anything else none (the class's side).</summary>
    public static Team SideFor(byte requested) => requested is (byte)Team.SpaceMarines or (byte)Team.Chaos ? (Team)requested : Team.None;

    /// <summary>
    /// Player ids run 1..65535 (the kill feed carries them in 16 bits) and wrap, skipping ids still in use; with at most
    /// <see cref="ServerOptions.MaxPlayers"/> players a free one always exists.
    /// </summary>
    /// <summary>Test seam: the id the next player tries first.</summary>
    internal int NextPlayerIdForTests { set => _nextPlayerId = value; }

    /// <summary>Test seam: the input last applied for <paramref name="id"/> (as stored: e.g. with added content's action key dropped when no extra reads it).</summary>
    internal PlayerInput LastAppliedForTests(int id) => _peers.Values.Single(p => p.PlayerId == id).LastApplied;

    int NextPlayerId()
    {
        var used = _peers.Values.Select(p => p.PlayerId).ToHashSet();
        while (used.Contains(_nextPlayerId) || _nextPlayerId is < 1 or > ushort.MaxValue)
            _nextPlayerId = _nextPlayerId is < 1 or >= ushort.MaxValue ? 1 : _nextPlayerId + 1;
        int id = _nextPlayerId;
        _nextPlayerId = id >= ushort.MaxValue ? 1 : id + 1;
        return id;
    }

    /// <summary>The requested class when it exists, else the default (provisional: the original front end's class choice is not reimplemented).</summary>
    public int ClassFor(byte requested)
    {
        int count = World.Config.Classes.Count;
        // Added content E2: a class the profile does not have (the Ork Boy without the Orks) is not offered; the default class is played instead.
        if (requested != Protocol.ServerDefaultClass && requested < count && ContentGate.ClassOffered(World.Config, requested)) return requested;
        return _options.DefaultClass >= 0 && _options.DefaultClass < count && ContentGate.ClassOffered(World.Config, _options.DefaultClass) ? _options.DefaultClass : 0;
    }

    /// <summary>
    /// The refusal text for another config hash. The usual causes: another <c>--content</c> profile (added content, docs/added-content.md; the text
    /// names the server's profile and extras wire version) or <c>--ork-data</c> on one side only (Ork O5 review). Keep it within 200 characters:
    /// <c>Protocol.Write</c> cuts a <see cref="Reject"/>'s detail there (pinned for every known flag by <c>ContentProfileTests</c>).
    /// </summary>
    public static string ConfigMismatchDetail(ContentExtras serverExtras) =>
        $"gameplay tuning differs from the server's (content profile {ContentProfiles.Describe(serverExtras)}): " +
        $"join with --content {ContentProfiles.Name(serverExtras)} (--ork-data only if the server has it)";

    void Refuse(int connection, RejectReason reason, uint nonce)
    {
        Rejected++;
        string detail = reason switch
        {
            RejectReason.ProtocolMismatch => $"server protocol {Protocol.Version}",
            RejectReason.ContentMismatch => ContentMismatchDetail(),
            RejectReason.ConfigMismatch => ConfigMismatchDetail(World.Config.Extras),
            RejectReason.MapMismatch => $"map '{Map?.Name}' could not be loaded or differs from the server's",
            _ => "server is full",
        };
        _transport.Send(connection, Protocol.Write(new Reject(reason, detail, nonce)));
    }

    /// Stateless: a keyed hash of the connection and nonce, so only a client that receives at its address can echo it.
    ulong Cookie(int connection, uint nonce)
    {
        Span<byte> input = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(input, connection);
        BinaryPrimitives.WriteUInt32LittleEndian(input[4..], nonce);
        return BinaryPrimitives.ReadUInt64LittleEndian(HMACSHA256.HashData(_cookieSecret, input)) | 1;
    }

    void RemovePeer(int connection, Peer peer, bool forgetAddress = true, string reason = "left")
    {
        NoteLeave(connection, peer, reason);
        _peers.Remove(connection);
        World.Remove(peer.PlayerId);
        if (forgetAddress) _transport.Disconnect(connection);
    }

    void OnInputs(Peer peer, InputBatch batch)
    {
        peer.LastHeard = _clock.Now;
        if (batch.SnapshotTick <= World.Tick)
        {
            peer.AckedSnapshot = Math.Max(peer.AckedSnapshot, batch.SnapshotTick);
            // That snapshot carried the kills after the ones acknowledged when it was sent, so everything up to its newest has arrived.
            if (peer.SentKills.TryGetValue(peer.AckedSnapshot, out int newest)) peer.AckedKill = Math.Max(peer.AckedKill, newest);
            foreach (var old in peer.SentKills.Keys.Where(k => k <= peer.AckedSnapshot).ToList()) peer.SentKills.Remove(old);
        }
        if (peer.Spectator)
        {
            // Protocol 46: nothing a spectator sends acts on the world (and nothing piles up for it); its newest input carries its camera (MoveX, MoveY, Yaw = x, y, z).
            if (batch.Inputs.Length > 0 && batch.Inputs.MaxBy(i => i.Tick) is var cam) peer.Camera = ClampCamera(new Vector3(cam.MoveX, cam.MoveY, cam.Yaw));
            return;
        }
        foreach (var input in batch.Inputs)
        {
            // Only future ticks matter; anything older was already simulated (with the repeated input).
            if (input.Tick <= World.Tick || input.Tick > World.Tick + _options.MaxInputLead) continue;
            var clean = input.Sanitized();
            // Added content's action key (input bit 13) is kept only when the match runs added content that reads it (docs/added-content.md section 7).
            if ((World.Config.Extras & ContentExtras.ActionKeyUsers) == 0) clean = clean with { Buttons = clean.Buttons & ~InputButtons.ExtraAction };
            peer.Inputs[input.Tick] = clean;
            peer.AckAt.TryAdd(input.Tick, peer.AckedSnapshot);
            peer.NewestReceived = Math.Max(peer.NewestReceived, input.Tick);
        }
    }

    void StepWorld()
    {
        uint tick = World.Tick + 1;
        var byPlayer = _peers.Values.Where(p => !p.Spectator).ToDictionary(p => p.PlayerId);   // protocol 46: a spectator is not in the world
        World.Step(id =>
        {
            var peer = byPlayer[id];
            if (peer.Inputs.Remove(tick, out var input))
            {
                peer.LastApplied = input; peer.LastAppliedFromClient = tick;
                peer.LastAppliedAck = peer.AckAt.Remove(tick, out uint ack) ? ack : peer.AckedSnapshot;
            }
            else if (_clock.Now - peer.LastHeard > _options.NeutralInputAfter)
                peer.LastApplied = new PlayerInput(tick, 0, 0, peer.LastApplied.Yaw, InputButtons.None);
            else
            {
                // A repeated input keeps its view relative to its own tick, so the rewind does not grow while packets are lost.
                var last = peer.LastApplied;
                peer.LastApplied = last with { Tick = tick, ViewTick = last.ViewTick == 0 ? 0 : last.ViewTick + (tick - last.Tick),
                    BodyTick = last.BodyTick == 0 ? 0 : last.BodyTick + (tick - last.Tick) };   // protocol 43: the bodies too
            }
            return WithBoundedBody(WithBoundedView(peer.LastApplied, BoundedViewTick(peer, peer.LastApplied.ViewTick)), peer.LastAppliedAck, BodySlack);
        });
        foreach (var peer in byPlayer.Values)
        {
            foreach (var stale in peer.Inputs.Keys.TakeWhile(t => t <= World.Tick).ToList()) peer.Inputs.Remove(stale);
            foreach (var stale in peer.AckAt.Keys.Where(t => t <= World.Tick).ToList()) peer.AckAt.Remove(stale);
        }
        CountHits(byPlayer);
        PromoteAppearances();   // protocol 22: respawned players take their pending appearance
        // Ray and cone shots for the next snapshots' shot records (protocol 16); projectiles travel as projectile records.
        foreach (var shot in World.LastShots) if (shot.Projectile == 0) _pendingShots.Add(shot);
        if (_pendingShots.Count > MaxPendingShots) _pendingShots.RemoveRange(0, _pendingShots.Count - MaxPendingShots);
        NotePickups();   // fidelity gap 1 (GameServer.Pickups.cs)
        Stepped?.Invoke(World);
    }

    /// <summary>Shots fired since the last snapshots were sent (each goes out once; protocol 16).</summary>
    readonly List<ShotEvent> _pendingShots = new();
    /// <summary>A bound on <see cref="_pendingShots"/> (the oldest go first); far above what 16 players fire between snapshots.</summary>
    const int MaxPendingShots = 256;

    /// <summary>
    /// The shot records for one recipient (protocol 16): every ray or cone shot in <paramref name="shots"/> but the recipient's
    /// own (its client draws those from its own prediction), nearest first by the distance from <paramref name="viewer"/> to the
    /// shot's line (origin to end), then newest first (ours: the original's relevance rules are not known), at most
    /// <see cref="Protocol.MaxSnapshotShots"/>. The writer keeps as many of the first ones as fit the packet; the rest are not
    /// sent again (a lost snapshot loses its shots: they are only effects). A shot that went nowhere (end at its origin and
    /// no hit: a stage that fires nothing hit-scan) is left out.
    /// </summary>
    public static ShotView[] ShotsFor(IReadOnlyList<ShotEvent> shots, int recipient, Vector3 viewer, uint snapshotTick) =>
        shots.Where(s => s.Projectile == 0 && s.Shooter != recipient && (s.Hit != ShotHit.None || s.End != s.Origin))
            .OrderBy(s => DistanceToSegment(viewer, s.Origin, s.End)).ThenByDescending(s => s.Tick).ThenBy(s => s.Shooter)
            .Take(Protocol.MaxSnapshotShots)
            .Select(s => new ShotView(s.Shooter, s.Weapon, s.Stage, s.Hit, s.Surface, s.End, (int)Math.Min(snapshotTick - Math.Min(s.Tick, snapshotTick), (uint)Protocol.MaxShotAge), s.Armoured))
            .ToArray();

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float lengthSquared = ab.LengthSquared();
        float t = lengthSquared > 1e-12f ? Math.Clamp(Vector3.Dot(p - a, ab) / lengthSquared, 0f, 1f) : 0f;
        float d = Vector3.Distance(p, a + ab * t);
        return float.IsFinite(d) ? d : float.MaxValue;
    }

    /// <summary>
    /// Adds this tick's hits (<see cref="World.LastHits"/>) to each shooter's running totals (protocol 12): self damage is not
    /// counted; the latest hit names the victim and its health and shield at the end of the tick.
    /// </summary>
    void CountHits(Dictionary<int, Peer> byPlayer)
    {
        foreach (var h in World.LastHits)
        {
            if (h.Shooter == h.Target || !byPlayer.TryGetValue(h.Shooter, out var peer)) continue;
            peer.HitCount++;
            peer.ShieldDealt += h.ShieldDamage; peer.HealthDealt += h.HealthDamage;
            if (h.Killed) peer.KillCount++;
            peer.LastVictim = h.Target is >= 0 and <= ushort.MaxValue ? h.Target : 0;
            peer.LastFlags = (h.Zone == HitZone.Head ? HitFlags.Head : 0) | (h.Killed ? HitFlags.Killed : 0) | (h.Melee ? HitFlags.Melee : 0) | (h.Splash ? HitFlags.Splash : 0)
                | (h.Armoured ? HitFlags.Armoured : 0);
            var victim = World.Players.TryGetValue(h.Target, out var v) ? v : default;
            var body = Loadouts.Body(World.Config, victim);   // M43: the victim's perks (Iron Halo: a 150 shield is full)
            peer.VictimHealth = victim.Alive && body.HealthMax > 0f ? Math.Clamp(victim.Health / body.HealthMax, 0f, 1f) : 0f;
            peer.VictimShield = victim.Alive && body.ShieldMax > 0f ? Math.Clamp(victim.Shield / body.ShieldMax, 0f, 1f) : 0f;
        }
    }

    /// <summary>Raised after every world tick (its shots, hits and detonations are in <see cref="World.LastShots"/> and the like).</summary>
    public event Action<World>? Stepped;

    /// <summary>
    /// The projectile records for one recipient, at most <see cref="Protocol.MaxSnapshotProjectiles"/>: recent detonations
    /// first (newest first), then projectiles nearest to the recipient's player (ours: the original's relevance rules are
    /// not known). Ids travel modulo 65536 and a snapshot may list each once: a record whose 16-bit id is already listed
    /// is left out (a projectile that detonated travels only as its detonation).
    /// </summary>
    public static ProjectileView[] ProjectilesFor(World world, Vector3 viewer) => ProjectilesFor(world.Projectiles, world.RecentDetonations, viewer);

    /// <summary>As above from explicit lists (projectiles oldest first, detonations oldest first).</summary>
    public static ProjectileView[] ProjectilesFor(IReadOnlyList<ProjectileState> projectiles, IReadOnlyList<DetonationEvent> detonations, Vector3 viewer)
    {
        var views = new List<ProjectileView>(Protocol.MaxSnapshotProjectiles);
        var listed = new HashSet<int>();
        for (int i = detonations.Count - 1; i >= 0 && views.Count < Protocol.MaxSnapshotProjectiles; i--)
        {
            var d = detonations[i];
            if (!listed.Add(d.Projectile & 0xFFFF)) continue;
            views.Add(new ProjectileView(d.Projectile & 0xFFFF, d.Weapon, d.Stage, d.Position, ProjectileFlags.Detonated | (d.Thrown ? ProjectileFlags.Thrown : 0)));
        }
        foreach (var q in projectiles.OrderBy(q => Vector3.DistanceSquared(q.Position, viewer)).ThenBy(q => q.Id))
        {
            if (views.Count >= Protocol.MaxSnapshotProjectiles) break;
            if (!listed.Add(q.Id & 0xFFFF)) continue;
            var flags = q.Motion switch { ProjectileMotion.Stuck => ProjectileFlags.Stuck, ProjectileMotion.Attached => ProjectileFlags.Attached, _ => ProjectileFlags.None };
            views.Add(new ProjectileView(q.Id & 0xFFFF, q.Weapon, q.Stage, q.Position, flags | (q.Thrown ? ProjectileFlags.Thrown : 0)));

        }
        return views.ToArray();
    }

    /// <summary>
    /// Lag compensation trusts the client's view tick only as far back as the snapshot it had acknowledged when the
    /// input arrived allows: that snapshot minus the interpolation delay and 4 ticks. 0 (not set, or no snapshot yet)
    /// means no rewind.
    /// A client can still fake lag by acknowledging late; <see cref="SimConfig.MaxRewindTicks"/> caps that.
    /// </summary>
    /// <summary>
    /// fix/hitboxes (protocol 37): an input with its view tick replaced by the bounded one; the fraction belongs to the tick the client named, so a moved
    /// view tick drops it (0).
    /// </summary>
    public static PlayerInput WithBoundedView(PlayerInput input, uint bounded) =>
        input with { ViewTick = bounded, ViewFraction = bounded == input.ViewTick ? input.ViewFraction : 0f };

    /// <summary>
    /// sim/player-collision (protocol 43): an input's body tick no older than the snapshot its batch acknowledged (<paramref name="ack"/>) less
    /// <paramref name="slack"/> ticks (raised to that bound when older; an honest client names the snapshot it acknowledges, a later copy of a lost
    /// input at most a few ticks newer), so a client cannot collide with an old world to walk into players. 0 (none) and an unknown ack stay.
    /// </summary>
    public static PlayerInput WithBoundedBody(PlayerInput input, uint ack, uint slack) =>
        input.BodyTick == 0 || ack == 0 || (long)input.BodyTick >= (long)ack - slack ? input : input with { BodyTick = (uint)Math.Max(1L, (long)ack - slack) };

    /// <summary>The body tick's slack behind the acknowledged snapshot: the view's (the longest interpolation delay and 4 ticks).</summary>
    uint BodySlack => (uint)Math.Ceiling(_options.MaxInterpolationDelay.TotalSeconds * World.Config.TickRate) + 4;

    uint BoundedViewTick(Peer peer, uint viewTick)
    {
        if (viewTick == 0 || peer.LastAppliedAck == 0) return World.Tick + 1;
        // 4 ticks of slack: 2 for timing, 2 for an input whose first copies were lost (a later copy carries a newer ack).
        long slack = (long)Math.Ceiling(_options.MaxInterpolationDelay.TotalSeconds * World.Config.TickRate) + 4;
        return (uint)Math.Max(viewTick, Math.Max(0L, (long)peer.LastAppliedAck - slack));
    }

    void SendSnapshots()
    {
        // Hardening: a recipient whose snapshot fails is dropped alone (after the loop), and the shots always clear.
        List<(int Connection, Peer Peer, Exception Error)>? failed = null;
        try
        {
            var players = World.Players.Values.ToArray();
            var match = MatchOf(World);
            var points = PointsOf(World);
            foreach (var (connection, peer) in _peers)
                try { SendSnapshotTo(connection, peer, players, match, points); }
                catch (Exception e) when (Recoverable(e)) { (failed ??= []).Add((connection, peer, e)); }
        }
        finally { _pendingShots.Clear(); }
        foreach (var (connection, peer, error) in failed ?? []) OnRecipientFault(connection, peer, "snapshot", error);
    }

    void SendSnapshotTo(int connection, Peer peer, PlayerState[] players, MatchView match, ControlPointView[] points)
    {
        SnapshotHookForTests?.Invoke(peer.PlayerId);
        // Per recipient: its own player in full, everyone else slim (no enemy health, ammunition or reload windows).
        int lead = peer.NewestReceived == 0 ? Protocol.NoLead : (int)((long)peer.NewestReceived - World.Tick);
        var viewer = World.Players.TryGetValue(peer.PlayerId, out var self) ? self.Position : peer.Camera ?? Vector3.Zero;   // protocol 46: a spectator's is its camera
        var kills = KillsFor(World, peer.AckedKill);
        if (kills.Length > 0) peer.SentKills[World.Tick] = kills[^1].Sequence;
        while (peer.SentKills.Count > 256) peer.SentKills.Remove(peer.SentKills.Keys.Min());
        _transport.Send(connection, Protocol.Write(new Snapshot(World.Tick, peer.LastAppliedFromClient, lead, players, Self: peer.PlayerId,
            Projectiles: ProjectilesFor(World, viewer), Match: match, Kills: kills, Points: points, Hits: peer.Spectator ? null : peer.Hits,
            Shots: ShotsFor(_pendingShots, peer.PlayerId, viewer, World.Tick))));
    }

    /// <summary>The match block every snapshot carries.</summary>
    public static MatchView MatchOf(World world) =>
        new(MatchFlowWire.ToWire(world.State), world.Winner, world.TicksLeft, world.ScoreOf(Team.SpaceMarines), world.ScoreOf(Team.Chaos), world.Rules?.Mode ?? GameMode.Annihilation);

    /// <summary>Every control point's state (protocol 11): in Seize Ground only (Annihilation and free-for-all send none).</summary>
    public static ControlPointView[] PointsOf(World world) => world.Rules is { Mode: GameMode.SeizeGround }
        ? world.Points.Select((p, i) => new ControlPointView(p.Owner, p.Side, world.ProgressOf(i), p.Contested)).ToArray()
        : [];

    /// <summary>Seize Ground's rules and point layout for the scoreboard (null outside Seize Ground).</summary>
    public static SeizeGroundInfo? SeizeOf(World world) => world.Rules is { Mode: GameMode.SeizeGround } r
        ? new SeizeGroundInfo(r.CaptureTime, r.NeutraliseTime, r.PointScore, r.ScoreInterval,
            world.ControlPoints.Select(p => new ControlPointInfo(p.Name, p.Center, p.Radius, p.HalfHeight)).ToArray())
        : null;

    /// <summary>
    /// The kill feed for a snapshot to a client that has received every death up to <paramref name="acked"/>: the oldest
    /// later ones in <see cref="World.KillLog"/>, at most <see cref="Protocol.MaxSnapshotKills"/>, oldest first. They repeat
    /// until a snapshot carrying them is acknowledged, so no death is skipped however many happen at once (ours).
    /// </summary>
    public static KillView[] KillsFor(World world, int acked) =>
        world.KillLog.Where(k => k.Sequence > acked).Take(Protocol.MaxSnapshotKills)
            .Select(k => new KillView(k.Sequence & 0xFFFF, k.Killer, k.Victim, k.Cause, k.Index, k.Direction)).ToArray();

    /// <summary>The scoreboard every client gets: the rules and each player's side, class, kills and deaths (by id).</summary>
    public Scoreboard ScoreboardOf() => new(World.Tick, World.Rules is not null, World.Rules?.ScoreLimit ?? 0, World.Rules?.TimeLimit ?? 0f, World.Rules?.FriendlyFire ?? true,
        World.Players.Values.OrderBy(p => p.PlayerId).Select(p =>
        {
            var s = World.Scores.GetValueOrDefault(p.PlayerId);
            var peer = PeerOf(p.PlayerId);
            return new ScoreEntry(p.PlayerId, p.Team, p.Class, s.Kills, s.Deaths, peer?.Appearance.Version ?? 0,
                Loadouts.EquipmentIndex(World.Config, p),   // protocol 40 (perks/inert-2)
                peer?.Name ?? "");                         // protocol 41 (names/bot-lore)
        }).ToArray(), SeizeOf(World),
        Spectators: _peers.Values.Where(p => p.Spectator).OrderBy(p => p.PlayerId).Take(Protocol.MaxSpectators).Select(p => new SpectatorEntry(p.PlayerId, p.Name)).ToArray() is { Length: > 0 } listed ? listed : null);   // protocol 46

    void SendScoreboards()
    {
        if (_peers.Count == 0) return;
        RefreshBotNames();   // names/bot-lore: a bot whose race changed takes a name of its new race
        var packet = Protocol.Write(ScoreboardOf());
        foreach (var connection in _peers.Keys) _transport.Send(connection, packet);
    }
}
