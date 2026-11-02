using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmContent.Extras.LoyalistBarks;
using SmNet;
using SmNet.Extras.LoyalistBarks;
using SmSim;

namespace SmClient;

/// <summary>
/// Added content "loyalist barks" in the networked view (the owner, 2026-10-29; docs/added-content.md section 7). Only when the match runs
/// <see cref="ContentExtras.LoyalistBarks"/> and not with <c>--no-loyalist-barks</c>:
/// <list type="bullet">
/// <item>Only with <c>--loyalist-auto-barks</c> (off by default since the owner's decision of 2026-10-29: the key alone):
/// loyalist Space Marines (never Chaos, never Orks) bark the owner's own voice pack (32 takes embedded in this assembly, <see cref="LoyalistBarkPack"/>;
/// all decoded on a worker when the match starts) at the charge, a kill, a melee attack, the start of bolt-weapon fire and (own player only) the first
/// damage after a quiet spell, by the rules of <see cref="LoyalistBarkRules"/> (chances, 8 s per speaker, 20 s per phrase, no immediate repeat of a take,
/// one line at a time, SM1's own speech first). Rolls and takes are seeded by the event, so clients that see the same event mostly agree.</item>
/// <item>T: FOR THE EMPEROR for a living loyalist, at any time, at most every 2 s (in ticks, as the server counts). The key's bit
/// (<see cref="InputButtons.ExtraAction"/>, held <see cref="LoyalistShoutWire.HoldTicks"/> ticks) is never read by the simulation: no action is
/// interrupted, no animation plays. The own player hears the take at once; the server tells every client (<see cref="LoyalistShoutWire"/>) with the
/// player's shout sequence, from which every client picks the same take (<see cref="LoyalistBarkRules.KeyTake"/>).</item>
/// <item>Others are heard in 3D from their characters, the own player without position, on the sound bus (the master and <c>--sound-gain</c> apply).
/// Nothing is decided while the sound system or the takes are not ready (no cooldown is spent on a silent bark).</item>
/// </list>
/// Test aids: <c>--no-loyalist-barks</c>, <c>--log-sound loyalist</c> (every decision), <c>--loyalist-gain &lt;dB&gt;</c>, <c>--bot-shout &lt;s&gt;</c> (a bot presses
/// T every s seconds).
/// </summary>
public partial class NetworkView
{
    static readonly string[] BarkArgs = OS.GetCmdlineUserArgs();
    static readonly bool NoLoyalistBarks = BarkArgs.Contains("--no-loyalist-barks");
    /// <summary><c>--loyalist-auto-barks</c>: the automatic barks (charge, kill, melee, fire, under fire). Off by default (owner, 2026-10-29: only
    /// the FOR THE EMPEROR key in the release); the key is not affected.</summary>
    static readonly bool LoyalistAutoBarks = BarkArgs.Contains("--loyalist-auto-barks");
    static string? BarkArg(string name) => Array.IndexOf(BarkArgs, name) is var i && i >= 0 && i + 1 < BarkArgs.Length ? BarkArgs[i + 1] : null;
    /// <summary><c>--loyalist-gain &lt;dB&gt;</c>: added to the barks' level (ours; default 0 on top of <see cref="BarkBaseGainDb"/>).</summary>
    static readonly float LoyalistGainDb = float.TryParse(BarkArg("--loyalist-gain"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) ? g : 0f;
    /// <summary><c>--bot-shout &lt;s&gt;</c> (test aid): a bot presses the FOR THE EMPEROR key every s seconds while alive.</summary>
    static readonly double BotShoutEvery = double.TryParse(BarkArg("--bot-shout"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double e) ? e : 0;

    /// <summary>The barks' level (dB, ours): the pack is normalised toward -16 LUFS; with the bus's -7 dB master this sits them near the M33 speech.</summary>
    const float BarkBaseGainDb = -2f;
    /// <summary>Within this distance a bark is heard at full level (metres, ours).</summary>
    const float BarkUnitSize = 8f;
    /// <summary>Beyond this distance a bark is not heard (metres, ours).</summary>
    const float BarkMaxDistance = 70f;

    sealed class BarkSpeaker
    {
        public Node? Player;          // AudioStreamPlayer3D (others) or AudioStreamPlayer (own)
        public double Until;          // when its line ends (connection seconds)
        public bool Key;              // the line is a key shout (SM1 speech does not cut it)
        public string Line = "";
        public PlayerState Prev;
        public bool Has;
        public double LastShotAt = double.NegativeInfinity, LastHurtAt = double.NegativeInfinity;
    }

    LoyalistBarkPack? _barkPack;
    LoyalistBarkRules? _barkRules;
    Task<List<(string Id, byte[]? Data)>>? _barkLoad;              // the worker: every take's embedded bytes
    Queue<(string Id, byte[]? Data)>? _barkBytes;                  // read, not yet built (main thread, BarkStreamsPerFrame a frame)
    Dictionary<string, AudioStreamOggVorbis?>? _barkStreams;   // every take, by id; null = it did not load (kept, never retried)
    string _barkNote = "not loaded";
    bool _barkLoadTried;
    readonly Dictionary<int, BarkSpeaker> _barkSpeakers = new();
    readonly LoyalistShoutInbox _shoutInbox = new();
    readonly List<KillView> _barkKills = new();
    readonly Dictionary<string, int> _barkCounts = new(StringComparer.Ordinal);
    // Reused every frame (no per-frame allocation).
    readonly Dictionary<int, PlayerState> _barkPlayers = new();
    readonly List<int> _barkGone = new();
    readonly List<LoyalistShout> _shoutPlay = new();
    // The key.
    bool _shoutLatched, _shoutKeyWasDown;
    int _shoutHoldLeft;               // ticks the bit is still sent for the current press
    bool _shoutReleased = true;       // the last sample went without the bit (a new press needs an edge)
    uint? _ownShoutTick;              // the input tick of the own last accepted-looking press (the client's cooldown, in ticks like the server's)
    (uint Tick, byte Sequence)? _ownShoutPending;
    double _botShoutAt, _barkLogAt;

    bool BarksActive => !NoLoyalistBarks && _config is { } c && (c.Extras & ContentExtras.ActionKeyUsers) != 0;   // Musou Mode has the key too (owner, 2026-11-02), not the automatic barks (BarkSay)

    bool IsLoyalist(PlayerState s) => _config is not null && Races.Of(_config, s.Class, s.Team) == Race.SpaceMarines;

    partial void ExtrasInput(ref PlayerInput input)
    {
        TfwTestInput(ref input);   // Musou/Tfw/NetworkView.TfwMelee.cs: the scripted player's buttons, nothing without it
        if (!BarksActive || _client is null || _config is null) return;
        var me = _client.Predicted;
        bool canShout = me.Alive && !OwnDead && IsLoyalist(me);
        if (_bot && BotShoutEvery > 0 && canShout && _connectedFor > 3.0 && _connectedFor - _botShoutAt >= BotShoutEvery)
        {
            _botShoutAt = _connectedFor;
            _shoutLatched = true;
        }
        if (!canShout) { _shoutLatched = false; _shoutHoldLeft = 0; }
        // A press (latched by the frame poll) starts a hold of HoldTicks ticks, so one lost or late input does not lose it; the server acts on the edge.
        if (_shoutHoldLeft == 0 && _shoutLatched && _shoutReleased)
        {
            _shoutLatched = false;
            if (LoyalistShoutWire.CooledDown(_ownShoutTick, input.Tick, _config.TickRate))
            {
                _shoutHoldLeft = LoyalistShoutWire.HoldTicks;
                _ownShoutTick = input.Tick;
                _ownShoutPending = (input.Tick, _shoutInbox.NextOwnSequence);   // heard in this frame's ExtrasFrame, not on the input path
            }
            else BarkCount("Key cooling down (own)");
        }
        if (_shoutHoldLeft > 0)
        {
            input = input with { Buttons = input.Buttons | InputButtons.ExtraAction };
            _shoutHoldLeft--;
            _shoutReleased = false;
        }
        else _shoutReleased = true;
    }

    partial void ExtrasKill(KillView k)
    {
        if (BarksActive && k.Killer != k.Victim && k.Killer >= 0) _barkKills.Add(k);
    }

    /// <summary>
    /// This feature's part of the frame hook (called by the dispatcher <c>NetworkView.Extras.cs</c>, which drains the envelope queue once a frame):
    /// <paramref name="sections"/> are this frame's sections with <see cref="LoyalistShoutWire.Tag"/>, oldest first.
    /// </summary>
    void LoyalistBarksFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, IReadOnlyList<ExtrasSection> sections)
    {
        if (!BarksActive || _client is not { Status: ClientStatus.Connected } client || _config is null) return;
        // The key (human players): a press, latched between input samples; nothing else reads T.
        bool down = !_bot && !PickerBusy && ControlsInput.Held(GameAction.Shout);
        if (down && !_shoutKeyWasDown) _shoutLatched = true;
        _shoutKeyWasDown = down;

        int ownId = client.PlayerId;
        uint serverTick = client.LatestSnapshotTick;
        _barkPlayers.Clear();
        _barkPlayers[ownId] = me;
        foreach (var o in others) _barkPlayers[o.PlayerId] = o;

        // Others' shouts from the server (the envelope's sections with this feature's tag, routed here by the dispatcher). Read even while the takes
        // load, so the own shout sequence stays known.
        _shoutPlay.Clear();
        foreach (var section in sections)
        {
            if (LoyalistShoutWire.Read(section.Payload) is not { } shouts) { BarkCount("malformed shout section"); continue; }
            _shoutInbox.Take(shouts, ownId, serverTick, _config.TickRate, _shoutPlay);
        }

        // Nothing is decided until the sound system and every take are ready: no cooldown is spent on a bark nobody hears.
        if (NoSound || Lean || _sound is null || !BarksReady())   // perf/lean-bots: no take is loaded without sound (BarksReady starts the load)
        {
            if (_barkKills.Count > 0 || _shoutPlay.Count > 0 || _ownShoutPending is not null) BarkCount(NoSound || Lean || _sound is null ? "skipped: no sound" : "skipped: takes loading");
            _barkKills.Clear(); _ownShoutPending = null;
            return;
        }

        if (_ownShoutPending is { } own)
        {
            _ownShoutPending = null;
            BarkSay(ownId, me, BarkMoment.Key, own.Sequence, own: true);
        }
        foreach (var shout in _shoutPlay)
            if (_barkPlayers.TryGetValue(shout.PlayerId, out var shouter) && shouter.Alive)
                BarkSay(shout.PlayerId, shouter, BarkMoment.Key, shout.Sequence, own: false);

        // Kills: the killer's bark (a living killer of an enemy).
        foreach (var k in _barkKills)
            if (_barkPlayers.TryGetValue(k.Killer, out var killer) && killer.Alive && (TeamOf(k.Killer) != TeamOf(k.Victim) || TeamOf(k.Killer) == Team.None))
                BarkSay(k.Killer, killer, BarkMoment.Kill, k.Sequence, own: k.Killer == ownId);
        _barkKills.Clear();

        // Own shots (prediction) and others' ray and cone shots: the start of firing after a pause, bolt weapons only ("Eat boltgun!").
        long bucket = serverTick / 15;   // half a second: clients that see an edge in nearby frames roll alike
        foreach (var (_, ev) in _frameOwnEvents) if (ev.Fired) BarkShot(ownId, me, bucket);
        foreach (var shot in _frameShots) if (_barkPlayers.TryGetValue(shot.Shooter, out var shooter)) BarkShot(shot.Shooter, shooter, bucket);

        foreach (var (id, s) in _barkPlayers)
        {
            var sp = BarkSpeakerOf(id);
            if (sp.Has)
            {
                var p = sp.Prev;
                if (!s.Alive && p.Alive) BarkStop(id, "death");
                if (s.Alive && p.Alive)
                {
                    if (s.Charging && !p.Charging) BarkSay(id, s, BarkMoment.Charge, bucket, own: id == ownId);
                    else if (s.MeleeStep > 0 && p.MeleeStep == 0 && !s.Charging) BarkSay(id, s, BarkMoment.Melee, bucket, own: id == ownId);
                    // The own player only (others' health is not on the wire): the first damage after a quiet spell.
                    if (id == ownId && (s.Shield < p.Shield - 0.01f || s.Health < p.Health - 0.01f))
                    {
                        if (_connectedFor - sp.LastHurtAt >= LoyalistBarkRules.QuietBeforeUnderFire) BarkSay(id, s, BarkMoment.UnderFire, bucket, own: true);
                        sp.LastHurtAt = _connectedFor;
                    }
                }
            }
            sp.Prev = s; sp.Has = true;
            BarkFollow(id, sp);
        }
        _barkGone.Clear();
        foreach (int id in _barkSpeakers.Keys) if (!_barkPlayers.ContainsKey(id)) _barkGone.Add(id);
        foreach (int gone in _barkGone)
        {
            BarkStop(gone, "left");
            if (_barkSpeakers[gone].Player is { } node && IsInstanceValid(node)) node.QueueFree();
            _barkRules?.Forget(gone);
            _barkSpeakers.Remove(gone);
        }

        if (_bot && _connectedFor - _barkLogAt >= 10.0)
        {
            _barkLogAt = _connectedFor;
            GD.Print($"t {_connectedFor:F0} s: id {ownId} loyalist barks: {(_barkCounts.Count == 0 ? "nothing yet" : string.Join(", ", _barkCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")))}");
        }
    }

    void BarkShot(int id, PlayerState s, long bucket)
    {
        var sp = BarkSpeakerOf(id);
        bool afterPause = _connectedFor - sp.LastShotAt >= LoyalistBarkRules.FirePause;
        sp.LastShotAt = _connectedFor;
        if (afterPause && s.Alive && IsLoyalist(s) && Loadouts.InHand(_config!, s) is { } w && w.Name.Contains("bolt", StringComparison.OrdinalIgnoreCase))
            BarkSay(id, s, BarkMoment.Fire, bucket, own: id == _client?.PlayerId);
    }

    BarkSpeaker BarkSpeakerOf(int id)
    {
        if (!_barkSpeakers.TryGetValue(id, out var sp)) _barkSpeakers[id] = sp = new BarkSpeaker();
        return sp;
    }

    /// <summary>A speaker's bark: the rules decide and pick the take; it plays (others 3D, own without position).</summary>
    void BarkSay(int id, PlayerState s, BarkMoment moment, long seed, bool own)
    {
        if (_config is null || _barkRules is null) return;
        if (moment != BarkMoment.Key && (!LoyalistAutoBarks || _config.Extras.HasFlag(ContentExtras.Musou))) return;
        var sp = BarkSpeakerOf(id);
        bool speaking = sp.Until > _connectedFor;
        bool sm1 = _sound is not null && _soundTracks.TryGetValue(id, out var track) && _sound.SpeechBusy(track.Emitter);
        var (clip, verdict, roll) = _barkRules.Decide(id, IsLoyalist(s), moment, seed, _connectedFor, speaking, sm1);
        string who = own ? $"P{id} (own)" : $"P{id}";
        if (clip is null)
        {
            BarkCount($"{moment} {verdict}");
            if (LogSound("loyalist") && verdict != BarkVerdict.NotLoyalist)
                GD.Print($"LOYALIST t {_connectedFor:F2} {who} {moment}: {verdict} (roll {roll:F2}, chance {LoyalistBarkRules.Chance(moment):F2})");
            return;
        }
        if (moment == BarkMoment.Key && _sound is not null && _soundTracks.TryGetValue(id, out var t)) _sound.StopSpeech(t.Emitter);   // the key cuts SM1's line
        BarkStop(id, null);
        double length = BarkPlay(id, sp, clip, own);
        sp.Until = _connectedFor + (length > 0 ? length : clip.Seconds);
        sp.Key = moment == BarkMoment.Key;
        sp.Line = clip.Id;
        BarkCount($"{moment} said");
        if (LogSound("loyalist") || (_bot && moment == BarkMoment.Key))
            GD.Print($"LOYALIST t {_connectedFor:F2} {who} {moment}: says {clip.Id} \"{clip.Text}\" ({clip.Seconds:F2} s{(length == 0 ? ", not played: the take did not load" : "")}; " +
                     (moment == BarkMoment.Key ? $"shout {seed})" : $"roll {roll:F2})"));
    }

    /// <summary>Plays a take on the speaker's player; returns its length (0 when the take did not load).</summary>
    double BarkPlay(int id, BarkSpeaker sp, BarkClip clip, bool own)
    {
        if (_barkStreams is null || !_barkStreams.TryGetValue(clip.Id, out var stream) || stream is null) return 0;
        string bus = AudioServer.GetBusIndex(SoundSystem.BusName) >= 0 ? SoundSystem.BusName : "Master";
        if (sp.Player is null || !IsInstanceValid(sp.Player))
        {
            if (own) sp.Player = new AudioStreamPlayer { Name = $"LoyalistBark{id}", Bus = bus };
            else sp.Player = new AudioStreamPlayer3D
            {
                Name = $"LoyalistBark{id}", Bus = bus, UnitSize = BarkUnitSize, MaxDistance = BarkMaxDistance,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance, DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
            };
            AddChild(sp.Player);
            BarkFollow(id, sp);
        }
        float db = BarkBaseGainDb + LoyalistGainDb + SoundGainDb + (_audio?.MasterBusVolume ?? 0f);
        switch (sp.Player)
        {
            case AudioStreamPlayer p2: p2.Stream = stream; p2.VolumeDb = db + SmClient.Customizer.Model.LiveOptions.GainDb(SmClient.Customizer.Model.SoundCategory.Voice); p2.Play(); break;
            case AudioStreamPlayer3D p3: p3.Stream = stream; p3.VolumeDb = db + SmClient.Customizer.Model.LiveOptions.GainDb(SmClient.Customizer.Model.SoundCategory.Voice); p3.Play(); break;
        }
        return stream.GetLength();
    }

    /// <summary>Keeps a 3D player at its character; a random bark gives way when Space Marine's own speech starts (M33 precedence).</summary>
    void BarkFollow(int id, BarkSpeaker sp)
    {
        if (sp.Player is AudioStreamPlayer3D p3 && IsInstanceValid(p3) && PlayerSoundPoint(id) is { } at) p3.GlobalPosition = at;
        if (sp.Until > _connectedFor && !sp.Key && _sound is not null && _soundTracks.TryGetValue(id, out var t) && _sound.SpeechBusy(t.Emitter))
            BarkStop(id, "SM1 speech");
    }

    void BarkStop(int id, string? why)
    {
        if (!_barkSpeakers.TryGetValue(id, out var sp) || sp.Until <= _connectedFor) return;
        switch (sp.Player)
        {
            case AudioStreamPlayer p2 when IsInstanceValid(p2): p2.Stop(); break;
            case AudioStreamPlayer3D p3 when IsInstanceValid(p3): p3.Stop(); break;
        }
        if (why is not null)
        {
            BarkCount($"cut by {why}");
            if (LogSound("loyalist")) GD.Print($"LOYALIST t {_connectedFor:F2} P{id}: {sp.Line} cut by {why}");
        }
        sp.Until = 0;
    }

    void BarkCount(string key) => _barkCounts[key] = _barkCounts.GetValueOrDefault(key) + 1;

    /// <summary>Streams built on the main thread per frame from the bytes the worker read (as <c>SoundSystem</c> builds its streams in <c>_Process</c>).</summary>
    const int BarkStreamsPerFrame = 2;

    /// <summary>
    /// Reads the embedded manifest once; a worker reads every take's bytes from the assembly, and the main thread builds the streams,
    /// <see cref="BarkStreamsPerFrame"/> a frame (the engine's stream objects are only made on the main thread). Started with the match, before
    /// the first press: a press never decodes. True once every take is built (a take that fails stays failed, never retried). False for good when
    /// the manifest is missing or malformed (a build without the assets): the feature is then off.
    /// </summary>
    bool BarksReady()
    {
        if (_barkRules is not null) return true;
        if (!_barkLoadTried)
        {
            _barkLoadTried = true;
            try
            {
                var json = EmbeddedExtras("loyalist-barks.json") ?? throw new System.IO.FileNotFoundException("the loyalist bark manifest is not embedded in this build");
                _barkPack = LoyalistBarkPack.Parse(System.Text.Encoding.UTF8.GetString(json));
                var pack = _barkPack;
                _barkLoad = Task.Run(() =>
                {
                    var bytes = new List<(string Id, byte[]? Data)>(pack.Clips.Count);
                    foreach (var c in pack.Clips)
                    {
                        byte[]? data = null;
                        try { data = EmbeddedExtras(System.IO.Path.GetFileName(c.File)); }
                        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { data = null; }
                        bytes.Add((c.Id, data));
                    }
                    return bytes;
                });
                _barkNote = "loading";
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or FormatException
                                           or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                _barkNote = "not available: " + ex.Message;
                GD.PrintErr("loyalist barks: " + _barkNote);
            }
            return false;
        }
        if (_barkPack is null) return false;
        if (_barkBytes is null)
        {
            if (_barkLoad is not { IsCompleted: true } load) return false;
            _barkLoad = null;
            if (load.IsFaulted || load.IsCanceled)
            {
                _barkPack = null;
                _barkNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
                GD.PrintErr("loyalist barks: " + _barkNote);
                return false;
            }
            _barkBytes = new Queue<(string, byte[]?)>(load.Result);
            _barkStreams = new Dictionary<string, AudioStreamOggVorbis?>(StringComparer.Ordinal);
        }
        for (int built = 0; built < BarkStreamsPerFrame && _barkBytes.Count > 0; built++)
        {
            var (id, data) = _barkBytes.Dequeue();
            AudioStreamOggVorbis? stream = null;
            if (data is not null) stream = AudioStreamOggVorbis.LoadFromBuffer(data);
            _barkStreams![id] = stream;   // a failure is kept as null and never retried
        }
        if (_barkBytes.Count > 0) return false;
        _barkRules = new LoyalistBarkRules(_barkPack);
        int failed = _barkStreams!.Values.Count(s => s is null);
        _barkNote = $"{_barkPack.Clips.Count} takes of {_barkPack.Clips.Select(c => c.Phrase).Distinct().Count()} phrases ({failed} failed to load); key T";
        GD.Print("loyalist barks: " + _barkNote);
        return true;
    }

    static byte[]? EmbeddedExtras(string name)
    {
        using var stream = typeof(NetworkView).Assembly.GetManifestResourceStream(EmbeddedExtrasPrefix + name);
        if (stream is null) return null;
        var ms = new System.IO.MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The prefix of the embedded added-content files' names (client/SmClient.csproj: <c>LogicalName="Extras/%(Filename)%(Extension)"</c>; a test pins both).</summary>
    const string EmbeddedExtrasPrefix = "Extras/";
}
