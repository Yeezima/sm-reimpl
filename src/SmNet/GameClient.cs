using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// The client's connection. <see cref="NoAnswer"/> (fix/connect-timeout): nothing parseable came back to the Hellos for <see cref="ClientOptions.ConnectTimeout"/>
/// (no server there, or one of another version that drops the Hello unread); the client stops and says so (<see cref="ConnectFailure"/>).
/// </summary>
public enum ClientStatus { Connecting, Connected, Rejected, Disconnected, NoAnswer }

/// <summary>
/// The local player's hits on others over some snapshots (protocol 12; <see cref="GameClient.TakeHits"/>): how many, the
/// shield and health damage dealt (to 0.1), kills, and the latest hit: victim id, head zone, whether it killed, melee, blast;
/// the victim's health and shield after it (to 1/255 of the maximum).
/// </summary>
/// <param name="LastArmoured">Protocol 22: the latest hit struck the victim's raised armour shield (else flesh; hit feedback).</param>
public sealed record HitReport(int Hits, float ShieldDamage, float HealthDamage, int Kills, int LastVictim, bool LastHead, bool LastKilled, bool LastMelee, bool LastSplash,
    float VictimHealth, float VictimShield, bool LastArmoured = false);

/// <summary>Client tuning. All values are provisional prototype defaults (docs/architecture.md), not original values.</summary>
public sealed record ClientOptions
{
    public TimeSpan InterpolationDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Ticks the newest input should reach the server ahead of the tick that applies it (absorbs jitter).</summary>
    public int TargetInputLead { get; init; } = 2;
    /// <summary>Inputs repeated in every packet, so a few lost packets lose no input.</summary>
    public int InputRedundancy { get; init; } = 8;
    public TimeSpan HelloInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>No snapshot for this long while connected means the server is gone.</summary>
    public TimeSpan ServerTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// fix/connect-timeout (ours): while connecting, no answer to the Hellos for this long (no Challenge, Welcome or Reject; counted from the first Hello, again
    /// from the end of the map load a Challenge started) gives up with <see cref="ClientStatus.NoAnswer"/> instead of waiting forever.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = ConnectFailure.DefaultTimeout;
    /// <summary>Unacknowledged inputs kept for replay; older ones are dropped (the server ignores them anyway).</summary>
    public int MaxHistory { get; init; } = 240;
    /// <summary>Correction errors kept for diagnostics (oldest dropped first).</summary>
    public int MaxCorrectionSamples { get; init; } = 100_000;
    /// <summary>Loads the map a server names (from this machine's install). Null: flat ground only.</summary>
    public Func<string, LoadedMap?>? LoadMap { get; init; }
    /// <summary>The class to play (index into <see cref="SimConfig.Classes"/>), or <see cref="Protocol.ServerDefaultClass"/>.</summary>
    public byte RequestedClass { get; init; } = Protocol.ServerDefaultClass;
    /// <summary>The primary to play: index into the class's primary choices (<see cref="Loadouts.Choices"/>), or <see cref="Protocol.DefaultChoice"/>.</summary>
    public byte RequestedPrimary { get; init; } = Protocol.DefaultChoice;
    /// <summary>The pistol to play: index into the class's pistol choices, or <see cref="Protocol.DefaultChoice"/>.</summary>
    public byte RequestedPistol { get; init; } = Protocol.DefaultChoice;
    /// <summary>The melee weapon to play: index into the class's <see cref="ClassLoadout.MeleeChoices"/>, or <see cref="Protocol.DefaultChoice"/>.</summary>
    public byte RequestedMelee { get; init; } = Protocol.DefaultChoice;
    /// <summary>The thrown item to carry: index into the class's <see cref="ClassLoadout.EquipmentChoices"/>, or <see cref="Protocol.DefaultChoice"/> (protocol 8).</summary>
    public byte RequestedEquipment { get; init; } = Protocol.DefaultChoice;
    /// <summary>The side to play (protocol 10; <see cref="Team.None"/>: the class's side). The server may move the player to balance the sides.</summary>
    public Team RequestedSide { get; init; }

    // ---- custom classes and appearances (protocol 22) ----
    /// <summary>Perk slot 1: index into the class's <see cref="ClassLoadout.PerkChoices"/>, or <see cref="Protocol.DefaultChoice"/> (the class default perk).</summary>
    public byte RequestedPerk1 { get; init; } = Protocol.DefaultChoice;
    /// <summary>Perk slot 2: index into the class's perk choices, or <see cref="Protocol.DefaultChoice"/> (none).</summary>
    public byte RequestedPerk2 { get; init; } = Protocol.DefaultChoice;
    /// <summary>The appearance for each race (the server uses the one of the side it puts the player on); default: the original look.</summary>
    public AppearanceCode SpaceMarineAppearance { get; init; }
    public AppearanceCode ChaosAppearance { get; init; }
    /// <summary>Protocol 28: the look worn while playing the Ork Boy (0..4, <see cref="Races.OrkLooks"/>), or <see cref="Races.OrkLookAutomatic"/> (by player id).</summary>
    public byte OrkLook { get; init; } = Races.OrkLookAutomatic;
    /// <summary>The bytes of an own emblem by hash (from the profile's emblem files), or null; the server pulls the ones the setup names. Null: none served.</summary>
    public Func<EmblemHash, byte[]?>? EmblemSource { get; init; }
    /// <summary>Whether others' custom emblems are pulled and shown (<c>--no-custom-emblems</c> / the settings toggle: the fallback badge instead).</summary>
    public bool CustomEmblems { get; init; } = true;
    /// <summary>The catalogue's limits per race, to re-sanitize codes the server sends (null: the structure only).</summary>
    public AppearanceLimits? SpaceMarineLimits { get; init; }
    public AppearanceLimits? ChaosLimits { get; init; }
    /// <summary>How often an unacknowledged Setup is sent again.</summary>
    public TimeSpan SetupResendInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>The same player id is queried at most this often.</summary>
    public TimeSpan AppearanceQueryInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Emblem pulls at once, retries after a timeout (after <see cref="EmblemRetryDelay"/>), and how long a failed hash is left alone.</summary>
    public int MaxEmblemPulls { get; init; } = 4;
    public int EmblemPullRetries { get; init; } = 2;
    public TimeSpan EmblemRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan EmblemFailureBackoff { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Others' emblems kept in memory (never written to disk); the ones a known appearance shows are never evicted.</summary>
    public int MaxCachedEmblems { get; init; } = 32;
    /// <summary>How often missing emblems of known appearances are wanted again (a skipped or failed pull is retried).</summary>
    public TimeSpan EmblemRecheckInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>M40 T2: remote players are drawn at a smoothed render clock (<see cref="RenderClock"/>); false: re-anchored to every snapshot as before (test aid for before/after numbers).</summary>
    public bool SmoothRenderClock { get; init; } = true;
    /// <summary>feel/melee-lock: the client picks melee locks (<see cref="MeleeLock.Choose"/>); false sends none (test aid <c>--no-melee-lock</c>, before/after numbers).</summary>
    public bool MeleeLock { get; init; } = true;
    /// <summary>Protocol 37 (test aid): this client is a test bot (<c>--bot</c>); the Hello says so, and a server with <c>--bot-damage-scale</c> scales the damage it deals.</summary>
    public bool Bot { get; init; }
}

/// <summary>
/// Engine-free client side of the protocol: handshake, per-tick input sampling, prediction of the local player with the
/// shared <see cref="Movement"/> step, reconciliation against authoritative snapshots (replaying unacknowledged inputs),
/// and interpolation of remote players. The Godot client and tests drive it by calling <see cref="Update"/>.
/// </summary>
public sealed partial class GameClient
{
    readonly SimConfig _config;
    readonly ulong _contentFingerprint;
    readonly IClientTransport _transport;
    readonly IClock _clock;
    readonly ClientOptions _options;
    readonly uint _nonce;
    readonly SortedDictionary<uint, (PlayerInput Input, PlayerState State)> _history = new();
    readonly List<(uint Tick, Dictionary<int, PlayerState> Players, Dictionary<int, ProjectileView> Projectiles)> _snapshots = new();
    /// <summary>Detonations received and not yet handed out by <see cref="TakeDetonations"/>, with the server tick they arrived at.</summary>
    readonly List<(uint Tick, ProjectileView Blast)> _pendingDetonations = new();
    /// <summary>Projectile ids whose detonation was already queued (a detonation stays in a few snapshots), oldest first.</summary>
    readonly Queue<int> _seenDetonationOrder = new();
    readonly HashSet<int> _seenDetonations = new();
    readonly List<float> _corrections = new();
    TimeSpan _helloSentAt = TimeSpan.MinValue, _nextTickAt, _leadHoldUntil;
    /// <summary>fix/connect-timeout: when the wait for an answer began (the first Hello, or the end of the map load a Challenge started); MinValue before the first Hello.</summary>
    TimeSpan _waitingSince = TimeSpan.MinValue;
    ulong _cookie;
    uint _session;
    uint _latestSnapshotTick;
    /// <summary>The server tick of the newest snapshot received (0 before the first).</summary>
    public uint LatestSnapshotTick => _latestSnapshotTick;
    bool _hasAuthoritativeState;
    TimeSpan _latestSnapshotAt;

    public ClientStatus Status { get; private set; } = ClientStatus.Connecting;
    public RejectReason? RejectedBecause { get; private set; }
    public string? RejectDetail { get; private set; }
    public int PlayerId { get; private set; } = -1;
    /// <summary>The client's current tick: the tick of the newest input it has sampled and predicted.</summary>
    public uint Tick { get; private set; }
    public PlayerState Predicted { get; private set; }
    /// <summary>
    /// The predicted state one tick before <see cref="Predicted"/> (after a reconciliation: the replay's last-but-one state). A renderer draws
    /// the own player between ticks with <see cref="DrawPair"/> (M36, feel/controls; not this with <see cref="TickFraction"/>: after an early tick
    /// the drawn pair is the one before; client presentation only: the simulation, collision and shots use <see cref="Predicted"/>).
    /// </summary>
    public PlayerState PreviousPredicted { get; private set; }
    /// <summary>feel/bullets: the predicted state after the input of <paramref name="tick"/> (kept until the server acknowledges the tick), so a shot of a catch-up frame is drawn from its own tick, not the frame's last one.</summary>
    public bool TryPredictedAfter(uint tick, out PlayerState state)
    {
        if (_history.TryGetValue(tick, out var entry)) { state = entry.State; return true; }
        state = default; return false;
    }
    /// <summary>feel/controls: the predicted state one tick before <see cref="PreviousPredicted"/> (drawn while the newest tick ran early, <see cref="DrawPair"/>).</summary>
    PlayerState _prePrevious;
    /// <summary>
    /// How far through the current tick the client is (0 just after a tick ran, 1 when the next is due), see <see cref="ViewSmoothing.TickFraction"/>;
    /// the fraction of <see cref="DrawPair"/> (while the newest tick ran early: through the tick before it, on the schedule). Pair it only with
    /// <see cref="DrawPair"/>'s states, never with <see cref="PreviousPredicted"/> and <see cref="Predicted"/> (after an early tick those are one
    /// tick ahead of it); clip times use <see cref="ClipFraction"/>.
    /// </summary>
    public float TickFraction => DrawPair().Fraction;
    /// <summary>feel/controls: the newest tick ran early (<see cref="Update(Func{uint, PlayerInput}, bool)"/>) and no scheduled tick has run since; the time it ran.</summary>
    TimeSpan? _earlyRanAt;
    /// <summary>feel/controls: ticks run early on an input edge since the client started (diagnostics, test aids).</summary>
    public int EarlyTicks { get; private set; }

    /// <summary>
    /// The two predicted states to draw the own player between and the fraction (M36; feel/controls). Normally <see cref="PreviousPredicted"/> and
    /// <see cref="Predicted"/> by <see cref="ViewSmoothing.TickFraction"/>. After an early tick, until that tick's scheduled time, the pair before it
    /// on the schedule (<see cref="EarlyTick.DrawsBehind"/>): the drawn position and camera follow the same timeline as without the early tick
    /// (no step, no stall); the early tick's state is drawn from its scheduled time on. Its events, velocity and clips show at once.
    /// </summary>
    public (PlayerState From, PlayerState To, float Fraction) DrawPair()
    {
        double now = _clock.Now.TotalSeconds, next = _nextTickAt.TotalSeconds, tick = _config.TickSeconds;
        return EarlyTick.DrawsBehind(_earlyRanAt is not null, now, next, tick)
            ? (_prePrevious, PreviousPredicted, ViewSmoothing.TickFraction(now, next - tick, tick))
            : (PreviousPredicted, Predicted, ViewSmoothing.TickFraction(now, next, tick));
    }

    /// <summary>
    /// The fraction to draw the own player's clips at (a tick back plus this fraction of a tick; feel/controls, <see cref="OwnClipClock"/>): the
    /// <see cref="TickFraction"/>, but after an early tick the time since it ran over the time until the next scheduled tick
    /// (<see cref="EarlyTick.ClipFraction"/>), so a clip it started begins at once and runs on without a stop.
    /// </summary>
    public float ClipFraction => _earlyRanAt is { } ran
        ? EarlyTick.ClipFraction(_clock.Now.TotalSeconds, ran.TotalSeconds, _nextTickAt.TotalSeconds)
        : ViewSmoothing.TickFraction(_clock.Now.TotalSeconds, _nextTickAt.TotalSeconds, _config.TickSeconds);
    /// <summary>Counts reconciliations that replaced the prediction; <see cref="LastCorrection"/> is the last one's move of the predicted position (new minus old).</summary>
    public int CorrectionSerial { get; private set; }
    public Vector3 LastCorrection { get; private set; }
    /// <summary>Round trip measured during the handshake.</summary>
    public TimeSpan HandshakeRoundTrip { get; private set; }
    /// <summary>For each snapshot: distance between what the client had predicted for that server tick and the server's state.</summary>
    public IReadOnlyList<float> CorrectionErrors => _corrections;

    public GameClient(SimConfig config, ulong contentFingerprint, IClientTransport transport, IClock clock, ClientOptions? options = null, uint nonce = 1)
    {
        _config = config; _contentFingerprint = contentFingerprint; _transport = transport; _clock = clock;
        _options = options ?? new ClientOptions(); _nonce = nonce;
        RememberOwnEmblems(_options.SpaceMarineAppearance, _options.ChaosAppearance, replace: true);
    }

    /// <summary>
    /// Processes received packets, then runs every client tick that is due, sampling one input per tick. With <paramref name="tickNow"/>
    /// (feel/controls: a game button was pressed or let go since the last sample) the next tick also runs now when no scheduled tick ran in this
    /// update and the client is not already a tick ahead of its schedule (<see cref="EarlyTick.Allowed"/>): the input keeps its tick number and the
    /// schedule is kept. Early ticks can follow one another (one per scheduled tick); the client is never more than one tick ahead.
    /// </summary>
    public void Update(Func<uint, PlayerInput> sampleInput, bool tickNow = false)
    {
        while (_transport.TryReceive(out var packet))
            switch (Protocol.Read(packet))
            {
                case Challenge c when c.Nonce == _nonce && Status == ClientStatus.Connecting:
                    _cookie = c.Cookie;
                    _helloSentAt = TimeSpan.MinValue;   // answer the challenge right away
                    // One load per connection attempt: a later Challenge naming another map (a confused or hostile
                    // sender) cannot make the client load again and again.
                    if (!_mapAttempted)
                    {
                        _mapAttempted = true;
                        LoadMap(c.Map);
                        // Loading (and the host drawing the map for the first time) can stall; answer on the next update
                        // so the stall does not count as round-trip time.
                        _deferHello = true;
                    }
                    break;
                case Welcome w when w.Nonce == _nonce && Status == ClientStatus.Connecting: OnWelcome(w); break;
                case Reject r when r.Nonce == _nonce && Status == ClientStatus.Connecting:
                    Status = ClientStatus.Rejected; RejectedBecause = r.Reason; RejectDetail = r.Detail; break;
                case Snapshot s when Status == ClientStatus.Connected: OnSnapshot(s); break;
                case Scoreboard b when Status == ClientStatus.Connected && (Scoreboard is null || b.ServerTick > Scoreboard.ServerTick): Scoreboard = b; OnScoreboardAppearances(b); NoteNames(b); break;
                case Message m and (SetupAck or Appearance or BlobRequest or BlobChunk) when Status == ClientStatus.Connected: OnAppearanceMessage(m); break;
                case PickupUpdate p when Status == ClientStatus.Connected: OnPickups(p); break;   // protocol 26 (GameClient.Pickups.cs)
                case ExtrasUpdate x when Status == ClientStatus.Connected: OnExtras(x); break;   // added content's envelope (GameClient.Envelope.cs)
            }

        var now = _clock.Now;
        if (Status == ClientStatus.Connecting)
        {
            // The map load and the host's first draw of it can stall for seconds: the wait for the Welcome starts again after them.
            if (_deferHello) { _deferHello = false; _waitingSince = now; return; }
            if (_waitingSince == TimeSpan.MinValue) _waitingSince = now;
            else if (ConnectFailure.TimedOut(now, _waitingSince, _options.ConnectTimeout)) { Status = ClientStatus.NoAnswer; return; }
            if (_helloSentAt == TimeSpan.MinValue || now - _helloSentAt >= _options.HelloInterval)
            {
                _transport.Send(Protocol.Write(new Hello(Protocol.Version, _contentFingerprint, _config.Hash(), _nonce, Milliseconds(now), _cookie, _mapHash, _options.RequestedClass,
                    _options.RequestedPrimary, _options.RequestedPistol, _options.RequestedMelee, _options.RequestedEquipment, (byte)_options.RequestedSide,
                    _options.RequestedPerk1, _options.RequestedPerk2, _options.SpaceMarineAppearance, _options.ChaosAppearance, _options.OrkLook, _options.Bot ? Hello.BotFlag : (byte)0)));
                _helloSentAt = now;
            }
            return;
        }
        if (Status != ClientStatus.Connected) return;
        if (now - _latestSnapshotAt > _options.ServerTimeout) { Status = ClientStatus.Disconnected; return; }
        StepAppearance();
        StepRenderClock(now);

        var tickLength = TimeSpan.FromSeconds(_config.TickSeconds);
        bool ticked = false;
        while (now >= _nextTickAt)
        {
            _earlyRanAt = null;
            StepTick(sampleInput);
            _nextTickAt += tickLength;
            ticked = true;
        }
        // feel/controls: an input edge runs the next tick now (EarlyTick). Its number, its one tick of simulation and the schedule are unchanged.
        // A scheduled tick that ran in this update already sampled the edge: no second tick (it would only use up the one-tick allowance).
        if (tickNow && !ticked && EarlyTick.Allowed(now.TotalSeconds, _nextTickAt.TotalSeconds, _config.TickSeconds))
        {
            StepTick(sampleInput);
            _nextTickAt += tickLength;
            _earlyRanAt = now;
            EarlyTicks++;
        }
    }

    /// <summary>Samples, predicts and sends the next tick's input.</summary>
    void StepTick(Func<uint, PlayerInput> sampleInput)
    {
        Tick++;
        // The view tick tells the server which past world this input was aimed at (lag compensation).
        // Quantized as the wire will carry it, so the prediction steps exactly the input the server applies.
        // fix/hitboxes (protocol 37): the view tick is the tick at or before the render tick, the fraction how far toward the next one (the server rewinds there).
        var (viewTick, viewFraction) = ViewOf(RenderTick());
        var input = Protocol.Quantize(sampleInput(Tick).Sanitized() with { Tick = Tick, ViewTick = viewTick, ViewFraction = viewFraction });
        // feel/melee-lock (protocol 32): the melee lock is picked from the others as this client shows them at the input's view tick (the server checks
        // it in its rewound world at the same tick), and the prediction steps with that view.
        // Only on ticks that may lock (MeleeLock.Wants): their view tick is moved to the nearest snapshot this client holds, so its view of the others is
        // exactly the server's rewound world at that tick and every lock decision (cone, leash, height, line of sight) agrees (a tick at most: lag compensation
        // of a shot in such a tick moves by as much). Other ticks build no view.
        IReadOnlyDictionary<int, PlayerState>? view = null;
        // feel/buffer-speed: a buffered melee press replayed this tick counts as a press (InputBuffer.Replayed), so it picks or keeps its lock like a fresh one.
        var (lockState, lockInput) = InputBuffer.Replayed(Predicted, Match.Predicted(_latestSnapshotTick, input), _config);
        if (_options.MeleeLock && MeleeLock.Wants(lockState, lockInput) && HeldSnapshotTick(RenderTick()) is uint held)
        {
            input = input with { ViewTick = held, ViewFraction = 0f };   // fix/hitboxes: exactly the held snapshot
            view = ViewAt(held);
            // feel/melee-charge-2: the lock held last tick, else the one remembered from the last attack or charge (MeleeLock.LockMemory), so a quick follow-up keeps its target.
            int previous = MeleeLockTarget > 0 ? MeleeLockTarget : RecentMeleeLock(Tick);
            input = input with { MeleeTarget = MeleeLock.Choose(lockState, lockInput with { ViewTick = held }, view, _config, Collision, previous) };
        }
        MeleeLockTarget = input.MeleeTarget;
        if (input.MeleeTarget > 0) (_recentLock, _recentLockTick) = (input.MeleeTarget, Tick);
        _prePrevious = PreviousPredicted;
        PreviousPredicted = Predicted;
        // The raw input is stored and sent (the server drops the attack buttons of a countdown tick itself); only the prediction drops them, by tick (protocol 25).
        Predicted = Combat.StepPlayer(Predicted, Match.Predicted(_latestSnapshotTick, input), _config, out var events, Collision, view);
        if (events != default)
        {
            _ownEvents.Add((Tick, events));
            if (_ownEvents.Count > 256) _ownEvents.RemoveAt(0);
        }
        _history[Tick] = (input, Predicted);
        while (_history.Count > _options.MaxHistory) _history.Remove(_history.Keys.First());
        var recent = _history.Keys.Reverse().Take(_options.InputRedundancy).Select(t => _history[t].Input).ToArray();
        _transport.Send(Protocol.Write(new InputBatch(recent, _session, _latestSnapshotTick)));
    }

    /// <summary>The map the server named in its Challenge ("" for flat ground) and its collision, loaded from this install.</summary>
    public string MapName { get; private set; } = "";
    bool _deferHello, _mapAttempted;
    ulong _mapHash;
    public CollisionWorld? Collision { get; private set; }
    /// <summary>Why the named map could not be loaded (the server will then refuse the map hash), or null.</summary>
    public string? MapError { get; private set; }

    void LoadMap(string map)
    {
        MapName = map; Collision = null; MapError = null; _mapHash = 0;
        if (map.Length == 0) return;
        if (_options.LoadMap is null) { MapError = "this client cannot load maps"; return; }
        try
        {
            var loaded = _options.LoadMap(map);
            if (loaded is null) { MapError = $"map '{map}' is not installed"; return; }
            Collision = loaded.Collision; _mapHash = loaded.Hash;
        }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException))
        {
            // Any failure (corrupt data, out of memory, a reader bug) leaves the client without the map; the server
            // then refuses the map hash, and the reason is kept for the user.
            MapError = $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>Tells the server this player is leaving (sent three times; UDP is best effort). The client stops here.</summary>
    public void Leave()
    {
        if (Status == ClientStatus.Connected)
            for (int i = 0; i < 3; i++) _transport.Send(Protocol.Write(new Leave(_session)));
        Status = ClientStatus.Disconnected;
    }

    void OnWelcome(Welcome w)
    {
        var now = _clock.Now;
        // The Welcome echoes the send time of the Hello it answers, so resent Hellos do not shorten the measurement.
        HandshakeRoundTrip = TimeSpan.FromMilliseconds((uint)(Milliseconds(now) - w.EchoSentMs));
        PlayerId = w.PlayerId;
        _session = w.Session;
        _latestSnapshotAt = now;
        Status = ClientStatus.Connected;
        // The server was at w.ServerTick half a round trip ago; inputs need another half to arrive. Aim to be ahead by
        // the whole round trip plus a margin, so each input reaches the server before the tick that applies it.
        double rttTicks = HandshakeRoundTrip.TotalSeconds * _config.TickRate;
        Tick = w.ServerTick + (uint)Math.Ceiling(rttTicks) + (uint)_options.TargetInputLead;
        _nextTickAt = now;
        Predicted = default;
        PreviousPredicted = default;
        _prePrevious = default;
        _earlyRanAt = null;
    }

    /// <summary>
    /// States a server could plausibly send: positions and velocities finite, inside ±100 km and ±1 km/s; finite timers;
    /// a class this config has; weapons this config has (or -1) and a melee choice of that class (or -1); finite heat
    /// values; evade ticks and lock within a byte and a known direction (fields a slim record does not carry are 0 or -1 and pass).
    /// </summary>
    bool Plausible(PlayerState p) =>
        float.IsFinite(p.Position.X) && float.IsFinite(p.Position.Y) && float.IsFinite(p.Position.Z) && p.Position.Length() < 1e5f
        && float.IsFinite(p.Velocity.X) && float.IsFinite(p.Velocity.Y) && float.IsFinite(p.Velocity.Z) && p.Velocity.Length() < 1e3f
        && float.IsFinite(p.Yaw) && float.IsFinite(p.Pitch) && float.IsFinite(p.Health) && float.IsFinite(p.Shield)
        && float.IsFinite(p.Cooldown) && float.IsFinite(p.ReloadLeft) && float.IsFinite(p.SinceDamage) && float.IsFinite(p.RespawnLeft)
        && p.MeleeTime is >= 0f and < 1e3f && p.SwitchLeft is >= 0f and < 1e3f
        && p.Class >= 0 && p.Class < _config.Classes.Count
        && p.Primary >= -1 && p.Primary < _config.Weapons.Count && p.Pistol >= -1 && p.Pistol < _config.Weapons.Count
        && p.MeleeChoice >= -1 && p.MeleeChoice < _config.Classes[p.Class].MeleeChoices.Count
        && p.Heat is >= 0f and < 1e6f && p.OtherHeat is >= 0f and < 1e6f && float.IsFinite(p.SinceHeat) && float.IsFinite(p.OtherSinceHeat)
        && p.ChargeTime is >= 0f and < 1e6f && p.PendingLeft is >= 0f and < 1e3f && p.PendingShot >= 0 && p.ProjectilesOut >= 0
        && p.Equipment >= -1 && p.Equipment < _config.Thrown.Count && p.Grenades >= 0
        && p.GrenadeCooldown is >= 0f and < 1e3f && p.ThrowTime is >= 0f and < 1e3f
        && p.EvadeTicks is >= 0 and <= Evade.MaxTicks && p.EvadeLock is >= 0 and <= Evade.MaxTicks && Enum.IsDefined(p.EvadeDirection);

    /// <summary>
    /// A projectile this config can have launched: a weapon it has and a shot of that weapon that launches a projectile, or
    /// (<see cref="ProjectileFlags.Thrown"/>) a thrown item it has.
    /// </summary>
    bool Plausible(ProjectileView q) => Shots.ProjectileOf(_config, q.Weapon, q.Stage, q.Thrown) is not null;

    void OnSnapshot(Snapshot s)
    {
        if (s.ServerTick <= _latestSnapshotTick && _snapshots.Count > 0) return;   // late or duplicate
        if (!s.Players.All(Plausible)) return;   // a broken or hostile server: replaying from it could stall prediction
        var projectiles = s.Projectiles ?? [];
        if (!projectiles.All(Plausible)) return;
        _latestSnapshotTick = s.ServerTick;
        _latestSnapshotAt = _clock.Now;
        _snapshots.Add((s.ServerTick, s.Players.ToDictionary(p => p.PlayerId), projectiles.ToDictionary(q => q.Id)));
        if (_snapshots.Count > 64) _snapshots.RemoveAt(0);
        var matchBefore = Match;
        Match = s.Match ?? default;
        // weapons/spread: a round that began since the previous snapshot respawned everyone in place (the server rested every bloom).
        _roundRespawnPending |= _snapshots.Count > 1 && s.Match is { } m && SpreadReconcile.RoundRespawned(matchBefore.Effective(), m.Effective(), m.Mode);   // the countdown travels as Warmup with ticks left (MatchFlowWire)
        Points = s.Points ?? [];
        foreach (var k in s.Kills ?? [])
        {
            if (!_seenKills.Add(k.Sequence)) continue;   // a death repeats in a few snapshots
            _seenKillOrder.Enqueue(k.Sequence);
            while (_seenKillOrder.Count > 256) _seenKills.Remove(_seenKillOrder.Dequeue());
            _pendingKills.Add(k);
            if (_pendingKills.Count > 64) _pendingKills.RemoveAt(0);
        }
        foreach (var q in projectiles)
        {
            if ((q.Flags & ProjectileFlags.Detonated) == 0 || !_seenDetonations.Add(q.Id)) continue;
            _seenDetonationOrder.Enqueue(q.Id);
            while (_seenDetonationOrder.Count > 256) _seenDetonations.Remove(_seenDetonationOrder.Dequeue());
            _pendingDetonations.Add((s.ServerTick, q));
            if (_pendingDetonations.Count > 256) _pendingDetonations.RemoveAt(0);
        }
        foreach (var shot in s.Shots ?? [])
        {
            // Our own shots are drawn from our prediction; a weapon this client does not know is dropped (only an effect is lost).
            if (shot.Shooter == PlayerId || shot.Weapon >= _config.Weapons.Count) continue;
            _pendingShots.Add((s.ServerTick - (uint)Math.Min(shot.Age, (int)s.ServerTick), shot));
            if (_pendingShots.Count > 256) _pendingShots.RemoveAt(0);
        }

        // Only the full record of this client's own player can be replayed from (a slim one lacks ammunition and timers).
        if (s.Self != PlayerId) return;
        if (s.Hits is { } hits) OnHits(hits);
        var mine = s.Players.FirstOrDefault(p => p.PlayerId == PlayerId);
        if (mine.PlayerId != PlayerId) return;

        // Until the first authoritative state arrives, predictions start from nothing; do not count them as errors.
        if (_hasAuthoritativeState && _history.TryGetValue(s.ServerTick, out var predictedAtTick))
        {
            _corrections.Add(Vector3.Distance(predictedAtTick.State.Position, mine.Position));
            if (_corrections.Count > _options.MaxCorrectionSamples) _corrections.RemoveRange(0, _corrections.Count - _options.MaxCorrectionSamples);
        }
        _hasAuthoritativeState = true;

        // weapons/spread: the snapshot does not carry the scatter's bloom; keep the prediction's value for that tick (the same inputs
        // gave the server the same one; Spread.Carry rests it when the two disagree on weapon or life). Without a prediction for that tick
        // (before the first prediction, after a stall) the record's rest values stand: the newest prediction is ahead of the snapshot, and its
        // bloom would count the shots in between twice in the replay (review, weapons/spread: a lossy-link test found it).
        // A round respawn in between rests it too (a living player's record matches its prediction on every compared field). After a stall the
        // client restarts from rest while the server may still be warm: only the own tracer and impact picture can differ until the bloom settles.
        mine = SpreadReconcile.Choose(mine, _history.TryGetValue(s.ServerTick, out var predictedSpread) ? predictedSpread.State : null, _roundRespawnPending, out var spreadOutcome);
        _roundRespawnPending = false;
        SpreadOutcomes[(int)spreadOutcome]++;
        // perks/inert-2: the combat stims' time left is not on the wire either: derived from the record's item cooldown, past it the prediction's (Stims.Carry).
        mine = Stims.Carry(_config, mine, spreadOutcome is SpreadReconcile.Outcome.Carried or SpreadReconcile.Outcome.Mismatch ? predictedSpread.State : null);

        // Reconcile: take the authoritative state and replay every input the server has not applied yet.
        foreach (var old in _history.Keys.Where(t => t <= s.ServerTick).ToList()) _history.Remove(old);
        // After a client stall the snapshot can be ahead of the client's own tick: inputs for the ticks it already
        // contains can never be applied, so resume sampling after it instead of replaying them on top of it (overshoot).
        if (s.ServerTick > Tick) Tick = s.ServerTick;
        var state = mine;
        var beforeLast = mine;
        var twoBefore = mine;   // feel/controls: the state before beforeLast (DrawPair after an early tick)
        foreach (var tick in _history.Keys.ToList())
        {
            var input = _history[tick].Input;
            twoBefore = beforeLast;
            beforeLast = state;
            state = Combat.StepSelf(state, Match.Predicted(s.ServerTick, input), _config, out _, out _, Collision,
                input.MeleeTarget > 0 ? ViewAt(input.ViewTick) : null);   // the stored input is raw: the lock follows the newest snapshot (protocol 25); feel/melee-lock: the view at its view tick, only with a lock
            _history[tick] = (input, state);
        }
        LastCorrection = state.Position - Predicted.Position;
        CorrectionSerial++;
        _prePrevious = twoBefore;
        PreviousPredicted = beforeLast;
        Predicted = state;

        // Steer the lead without skipping tick numbers: run one extra tick now if inputs arrive late, pause one tick if
        // they arrive far too early. A change shows up in the feedback one round trip later, so wait that long between
        // changes; and there is no feedback until the server has received an input.
        var now = _clock.Now;
        if (s.InputLead == Protocol.NoLead || now < _leadHoldUntil) return;
        var tickLength = TimeSpan.FromSeconds(_config.TickSeconds);
        if (s.InputLead < _options.TargetInputLead - 1) { _nextTickAt -= tickLength; _leadHoldUntil = now + HandshakeRoundTrip; }
        else if (s.InputLead > _options.TargetInputLead + 3) { _nextTickAt += tickLength; _leadHoldUntil = now + HandshakeRoundTrip; }
    }

    HitView _hitTotals;   // the server's totals start at zero when the player joins
    HitReport? _pendingHits;

    /// <summary>The change in the server's running hit totals since the previous snapshot, added to the pending report.</summary>
    void OnHits(HitView h)
    {
        int count = (h.Hits - _hitTotals.Hits) & 0xFFFF;
        // More than half the counter's range at once can only be totals going backwards (a server that restarted them):
        // take them as the new baseline without reporting anything.
        if (count > 0 && count < 0x8000)
        {
            float shield = ((h.ShieldTenths - _hitTotals.ShieldTenths) & 0xFFFF) / 10f, health = ((h.HealthTenths - _hitTotals.HealthTenths) & 0xFFFF) / 10f;
            int kills = (h.Kills - _hitTotals.Kills) & 0xFF;
            var p = _pendingHits;
            var body = VictimBody(h.LastVictim);   // Ork O5: the victim's own maxima (the Ork Boy's body; equal to the marine's in Parity)
            _pendingHits = new HitReport((p?.Hits ?? 0) + count, (p?.ShieldDamage ?? 0f) + shield, (p?.HealthDamage ?? 0f) + health, (p?.Kills ?? 0) + kills,
                h.LastVictim, (h.LastFlags & HitFlags.Head) != 0, (h.LastFlags & HitFlags.Killed) != 0, (h.LastFlags & HitFlags.Melee) != 0, (h.LastFlags & HitFlags.Splash) != 0,
                h.VictimHealth * body.HealthMax, h.VictimShield * body.ShieldMax, (h.LastFlags & HitFlags.Armoured) != 0);
        }
        _hitTotals = h;
    }

    /// <summary>
    /// The body a hit's victim fills its health and shield fractions from (Ork O5): its class's own (<see cref="SimConfig.For"/>, as
    /// <see cref="Loadouts.Body"/> starts from) as the newest snapshot knows the victim; the config's own when the victim is not in it.
    /// Others' perks are not on the wire, so a perk's maxima (Iron Halo) are not applied (as before).
    /// </summary>
    SimConfig VictimBody(int victim) =>
        HitBars.BodyOf(_config, _snapshots.Count > 0 && _snapshots[^1].Players.TryGetValue(victim, out var v) ? v.Class : null);

    /// <summary>
    /// This player's hits on others since the last call (protocol 12), summed, with the latest hit's victim and the victim's
    /// health and shield after it; null when there were none. For a hit marker and the target's name and health bar.
    /// A lost snapshot loses nothing (the server sends running totals).
    /// </summary>
    public HitReport? TakeHits()
    {
        var report = _pendingHits;
        _pendingHits = null;
        return report;
    }

    readonly List<KillView> _pendingKills = new();
    readonly Queue<int> _seenKillOrder = new();
    readonly HashSet<int> _seenKills = new();

    /// <summary>The match as of the newest snapshot (protocol 10): state, winner, ticks left and team scores.</summary>
    public MatchView Match { get; private set; }

    bool _roundRespawnPending;
    /// <summary>weapons/spread: how many reconciles ended in each <see cref="SpreadReconcile.Outcome"/> (diagnostics and tests).</summary>
    public int[] SpreadOutcomes { get; } = new int[Enum.GetValues<SpreadReconcile.Outcome>().Length];
    /// <summary>The control points as of the newest snapshot (protocol 11; Seize Ground), in the scoreboard layout's order.</summary>
    public IReadOnlyList<ControlPointView> Points { get; private set; } = [];
    /// <summary>The newest scoreboard (rules and every player's kills and deaths), or null before the first.</summary>
    public Scoreboard? Scoreboard { get; private set; }

    readonly Dictionary<int, string> _names = new();
    /// <summary>The ids in <see cref="_names"/>, oldest name first (for the cap).</summary>
    readonly Queue<int> _nameOrder = new();
    /// <summary>Most names kept (players who left included); beyond it the oldest names of players not on the newest scoreboard go (ours).</summary>
    public const int MaxKeptNames = 256;

    /// <summary>
    /// names/bot-lore (protocol 41): the name to show for a player: the newest scoreboard's name, else the last one any scoreboard gave it this session
    /// (a player who left stays named in the kill feed and the summary; ids are not reused while the server runs), else "P&lt;id&gt;".
    /// </summary>
    public string NameOf(int playerId) => PlayerNames.Display(playerId, _names.GetValueOrDefault(playerId));

    /// <summary>Whether the scoreboard has given this player a name (a bot today).</summary>
    public bool HasName(int playerId) => _names.ContainsKey(playerId);

    void NoteNames(Scoreboard b)
    {
        foreach (var e in b.Players)
        {
            string name = PlayerNames.Sanitise(e.Name);
            if (name.Length > 0)
            {
                if (_names.TryAdd(e.PlayerId, name)) _nameOrder.Enqueue(e.PlayerId);
                else _names[e.PlayerId] = name;
            }
            else _names.Remove(e.PlayerId);
        }
        // The cap: drop the oldest names of players the board no longer lists (and ids already removed); listed players keep theirs.
        int guard = _nameOrder.Count;
        while ((_names.Count > MaxKeptNames || _nameOrder.Count > 2 * MaxKeptNames) && guard-- > 0)
        {
            int id = _nameOrder.Dequeue();
            if (!_names.ContainsKey(id)) continue;
            if (Array.Exists(b.Players, e => e.PlayerId == id)) { _nameOrder.Enqueue(id); continue; }
            _names.Remove(id);
        }
    }

    /// <summary>How many names this client keeps (tests: the cap).</summary>
    public int KeptNames => _names.Count;

    /// <summary>
    /// perks/inert-2 (protocol 40): another player's equipment item as the newest scoreboard names it (index into <see cref="SimConfig.Thrown"/>),
    /// or -1 when unknown (no scoreboard yet, no row, none). Slim snapshot records carry no equipment.
    /// </summary>
    public int EquipmentOf(int playerId)
    {
        if (Scoreboard is not { } b) return -1;
        foreach (var e in b.Players) if (e.PlayerId == playerId) return e.Equipment;
        return -1;
    }

    /// <summary>Kill-feed entries received since the last call, each once (oldest first; at most 64 kept).</summary>
    public IReadOnlyList<KillView> TakeKills()
    {
        var kills = _pendingKills.ToList();
        _pendingKills.Clear();
        return kills;
    }

    readonly RenderClock _renderClock = new();
    TimeSpan _renderClockAt;

    /// <summary>The raw estimate of the server tick to show: the newest snapshot's tick plus the time since it was processed, minus the interpolation delay.</summary>
    double RawRenderTick()
    {
        double serverTickNow = _latestSnapshotTick + (_clock.Now - _latestSnapshotAt).TotalSeconds * _config.TickRate;
        return serverTickNow - _options.InterpolationDelay.TotalSeconds * _config.TickRate;
    }

    /// <summary>Once per update: advances the smoothed render clock by the time since the last update toward <see cref="RawRenderTick"/> (M40 T2).</summary>
    void StepRenderClock(TimeSpan now)
    {
        double dt = (now - _renderClockAt).TotalSeconds;
        _renderClockAt = now;
        if (!_options.SmoothRenderClock || _snapshots.Count == 0) return;
        _renderClock.Update(RawRenderTick(), dt, _config.TickRate);
    }

    /// <summary>
    /// fix/hitboxes (protocol 37): an input's view of a render tick: the whole tick at or before it and the fraction toward the next, quantized as the wire
    /// carries it (1/256; a fraction that would round to the next tick stays just below it). Before the first tick (or not finite): (0, 0).
    /// </summary>
    public static (uint Tick, float Fraction) ViewOf(double renderTick)
    {
        if (!double.IsFinite(renderTick) || renderTick <= 0) return (0, 0f);
        double whole = Math.Floor(renderTick);
        float fraction = (float)(renderTick - whole);
        return ((uint)Math.Min(whole, uint.MaxValue), Math.Clamp(MathF.Floor(fraction * Protocol.ViewFractionScale), 0f, Protocol.ViewFractionScale - 1f) / Protocol.ViewFractionScale);
    }

    /// <summary>The (fractional) server tick remote players are currently shown at (smoothed: <see cref="RenderClock"/>; fixed within one update).</summary>
    double RenderTick()
    {
        if (_snapshots.Count == 0) return 0;
        return _options.SmoothRenderClock && _renderClock.Started ? _renderClock.Value : RawRenderTick();
    }

    /// <summary>
    /// Remote players interpolated <see cref="ClientOptions.InterpolationDelay"/> behind the newest server time.
    /// Returns the server tick shown (fractional) and the states. These are slim (<see cref="Protocol.Slim"/>): health,
    /// shield, ammunition and timers of other players are not sent and read as 0.
    /// </summary>
    public (double Tick, IReadOnlyList<PlayerState> Players) RemotePlayers()
    {
        if (_snapshots.Count == 0) return (0, []);
        double renderTick = RenderTick();
        int after = _snapshots.FindIndex(x => x.Tick >= renderTick);
        if (after <= 0)
            return (renderTick, Others(_snapshots[after < 0 ? ^1 : 0].Players).ToList());
        var (t0, p0, _) = _snapshots[after - 1];
        var (t1, p1, _) = _snapshots[after];
        float alpha = (float)((renderTick - t0) / (t1 - t0));
        var result = new List<PlayerState>();
        foreach (var (id, a) in p0)
        {
            if (id == PlayerId) continue;
            if (!p1.TryGetValue(id, out var b)) { result.Add(a); continue; }
            // A respawn (the countdown's, a round's, a death's) is a jump, not a run: shown at the old place until the pair ends, not streaked across the map.
            if (Vector3.Distance(a.Position, b.Position) > ViewSmoothing.TeleportDistance * Math.Max(1.0, t1 - t0)) { result.Add(a); continue; }            result.Add(a with { Position = Vector3.Lerp(a.Position, b.Position, alpha), Velocity = Vector3.Lerp(a.Velocity, b.Velocity, alpha), Yaw = LerpAngle(a.Yaw, b.Yaw, alpha) });
        }
        return (renderTick, result);

        IEnumerable<PlayerState> Others(Dictionary<int, PlayerState> players) => players.Values.Where(p => p.PlayerId != PlayerId);
    }

    /// <summary>feel/melee-lock: the melee lock sent with the newest input (a player id; 0 none). Presentation (camera nudge, distance clips) reads it.</summary>
    public int MeleeLockTarget { get; private set; }

    int _recentLock;
    uint _recentLockTick;

    /// <summary>
    /// feel/melee-charge-2 (ours): the last lock sent while it is at most <see cref="MeleeLock.LockMemory"/> old at tick <paramref name="tick"/>, else 0. Offered to
    /// <see cref="MeleeLock.Choose"/> as its previous lock; the server accepts it only where <see cref="MeleeLock.Target"/> does, as any other.
    /// </summary>
    int RecentMeleeLock(uint tick) =>
        _recentLock > 0 && tick >= _recentLockTick && (tick - _recentLockTick) * (double)_config.TickSeconds <= MeleeLock.LockMemory + 1e-6 ? _recentLock : 0;

    /// <summary>
    /// feel/melee-lock: the other players as this client shows them at server tick <paramref name="tick"/> (an input's view tick): interpolated
    /// between the snapshots around it as <see cref="RemotePlayers"/> does, the newest snapshot past them, the oldest before them; null before the first.
    /// The own player is left out. The server checks the lock in its world as it was at the same tick (<c>World.SeenBy</c>).
    /// </summary>
    public IReadOnlyDictionary<int, PlayerState>? ViewAt(uint tick)
    {
        if (_snapshots.Count == 0) return null;
        int after = _snapshots.FindIndex(x => x.Tick >= tick);
        if (after <= 0 || _snapshots[after].Tick == tick)
        {
            var (t, players, _) = _snapshots[after < 0 ? ^1 : after];
            return Between(t, players, t, players, tick, PlayerId);
        }
        var (t0, p0, _) = _snapshots[after - 1];
        var (t1, p1, _) = _snapshots[after];
        return Between(t0, p0, t1, p1, tick, PlayerId);
    }

    /// <summary>
    /// feel/melee-lock: the players other than <paramref name="self"/> at <paramref name="tick"/> between two snapshots (as <see cref="RemotePlayers"/> draws
    /// them): positions, velocities and yaws interpolated; a player who moved farther than a teleport (<see cref="ViewSmoothing.TeleportDistance"/> a tick: a
    /// respawn) or is missing from the second stays where the first had it. At either snapshot's tick, that snapshot exactly.
    /// </summary>
    public static Dictionary<int, PlayerState> Between(uint t0, IReadOnlyDictionary<int, PlayerState> p0, uint t1, IReadOnlyDictionary<int, PlayerState> p1, uint tick, int self)
    {
        var view = new Dictionary<int, PlayerState>();
        if (tick == t1 && t1 != t0) { foreach (var (id, b) in p1) if (id != self) view[id] = b; return view; }
        float alpha = t1 > t0 ? (float)Math.Clamp((double)((long)tick - t0) / (t1 - t0), 0.0, 1.0) : 0f;
        foreach (var (id, a) in p0)
        {
            if (id == self) continue;
            if (alpha <= 0f || !p1.TryGetValue(id, out var b) || Vector3.Distance(a.Position, b.Position) > ViewSmoothing.TeleportDistance * Math.Max(1.0, t1 - t0)) { view[id] = a; continue; }
            view[id] = a with { Position = Vector3.Lerp(a.Position, b.Position, alpha), Velocity = Vector3.Lerp(a.Velocity, b.Velocity, alpha), Yaw = LerpAngle(a.Yaw, b.Yaw, alpha) };
        }
        return view;
    }

    /// <summary>feel/melee-lock: the tick of the held snapshot nearest <paramref name="renderTick"/> (the older one on a tie), or null before the first.</summary>
    uint? HeldSnapshotTick(double renderTick)
    {
        uint? best = null; double bestGap = double.MaxValue;
        foreach (var (t, _, _) in _snapshots)
        {
            double gap = Math.Abs(t - renderTick);
            if (gap < bestGap) { best = t; bestGap = gap; }
        }
        return best;
    }

    /// <summary>
    /// Projectiles in flight or at rest, interpolated like remote players (<see cref="ClientOptions.InterpolationDelay"/>
    /// behind the newest server time; clients do not predict projectiles). Detonated ones are left out (see
    /// <see cref="TakeDetonations"/>). Positions are quantized to 1/64 m by the protocol.
    /// </summary>
    public IReadOnlyList<ProjectileView> Projectiles()
    {
        if (_snapshots.Count == 0) return [];
        double renderTick = RenderTick();
        int after = _snapshots.FindIndex(x => x.Tick >= renderTick);
        if (after <= 0) return Live(_snapshots[after < 0 ? ^1 : 0].Projectiles).ToList();
        var (t0, _, q0) = _snapshots[after - 1];
        var (t1, _, q1) = _snapshots[after];
        float alpha = (float)((renderTick - t0) / (t1 - t0));
        var result = new List<ProjectileView>();
        foreach (var a in Live(q0))
            result.Add(q1.TryGetValue(a.Id, out var b) && b.Weapon == a.Weapon && b.Stage == a.Stage && b.Thrown == a.Thrown
                ? a with { Position = Vector3.Lerp(a.Position, b.Position, alpha) }
                : a);
        return result;

        static IEnumerable<ProjectileView> Live(Dictionary<int, ProjectileView> all) => all.Values.Where(q => (q.Flags & ProjectileFlags.Detonated) == 0);
    }

    /// <summary>
    /// Detonations to draw now, each once: those received whose snapshot tick the interpolated view has reached (so a
    /// blast shows when the drawn projectile gets there). Weapon, stage and the Thrown flag name the projectile (its blasts are
    /// <see cref="ProjectileSpec.AllSplashes"/> of <see cref="Shots.ProjectileOf(SimConfig, int, int, bool)"/>; a lingering one
    /// lasts its Lifetime). A detonation in snapshots that were all lost is not seen.

    /// </summary>
    public IReadOnlyList<ProjectileView> TakeDetonations()
    {
        if (_pendingDetonations.Count == 0) return [];
        double renderTick = RenderTick();
        var due = _pendingDetonations.Where(d => d.Tick <= renderTick).Select(d => d.Blast).ToList();
        _pendingDetonations.RemoveAll(d => d.Tick <= renderTick);
        return due;
    }

    readonly List<(uint Tick, ShotView Shot)> _pendingShots = new();
    readonly List<(uint Tick, SelfEvents Events)> _ownEvents = new();

    /// <summary>
    /// The own player's predicted events (M26b, for sounds): each tick's <see cref="SelfEvents"/> (a shot left, its stage, a
    /// melee hit step, a throw, a jump-pack event) as the prediction stepped it, each once, oldest first. Replays after a
    /// correction do not add any; a predicted event the server then overrules is not taken back.
    /// </summary>
    public IReadOnlyList<(uint Tick, SelfEvents Events)> TakeOwnEvents()
    {
        if (_ownEvents.Count == 0) return [];
        var list = _ownEvents.ToList();
        _ownEvents.Clear();
        return list;
    }

    /// <summary>
    /// Other players' ray and cone shots to draw now (protocol 16), each once, oldest first: those received whose firing tick
    /// (the snapshot's tick minus <see cref="ShotView.Age"/>) the interpolated view has reached, so a tracer leaves the
    /// shooter where it is drawn. The end point is absolute (world space, 1/32 m steps); the start is the shooter's muzzle as
    /// drawn. Shots in lost snapshots, and any the server could not fit, are not seen. Own shots never arrive.
    /// </summary>
    public IReadOnlyList<ShotView> TakeShots()
    {
        if (_pendingShots.Count == 0) return [];
        double renderTick = RenderTick();
        var due = _pendingShots.Where(d => d.Tick <= renderTick).Select(d => d.Shot).ToList();
        _pendingShots.RemoveAll(d => d.Tick <= renderTick);
        return due;
    }

    static uint Milliseconds(TimeSpan t) => (uint)(long)t.TotalMilliseconds;

    static float LerpAngle(float a, float b, float t)
    {
        float d = (b - a) % (2 * MathF.PI);
        if (d > MathF.PI) d -= 2 * MathF.PI; else if (d < -MathF.PI) d += 2 * MathF.PI;
        return a + d * t;
    }
}
