using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// Where a sound plays from (M26b): a fixed position, or a node it follows (a player, a projectile); its switch values
/// (<see cref="SoundSwitches"/>: the surface, the local-player group) and an ID for container histories and stops.
/// </summary>
public sealed class SoundEmitter
{
    static long _next = 1;
    public ulong Id { get; } = (ulong)Interlocked.Increment(ref _next);
    public Vector3 Position;
    public Func<Vector3?>? Follow;
    public SoundSwitches Switches = new();
    /// <summary>The local player's own emitter: its 3D sounds are kept at least <see cref="SoundSystem.OwnMinDistance"/> close.</summary>
    public bool Own;
    public string Label = "";
    /// <summary>A crowd's sounds counted together (<see cref="SoundSystem.GroupVoices"/>) and kept off the others' voices (a full pool costs them nothing); null: none.</summary>
    public string? Group;
    /// <summary>A one-shot place (an impact, a launch, a detonation): its container histories are dropped once its voices end.</summary>
    public bool OneShot;
    /// <summary>
    /// Game parameters (RTPC ID → value) the emitter provides, e.g. <see cref="VoiceMixer.Speed"/> (m/s) for a player. A voice reads them
    /// once when it starts (interpretation: a landing's speed must not fall to 0 while its sound plays); <c>distance</c> is the listener's
    /// distance and is read every frame.
    /// </summary>
    public Dictionary<uint, float> Parameters = new();

    public Vector3 Current => Follow?.Invoke() ?? Position;
}

/// <summary>
/// The two behaviours of the footsteps fix and which posts get them (<c>--sound-limits</c>): the nodes' "limit sound instances" (<see cref="InstanceLimiter"/>) and the volume
/// threshold on non-positioned voices (<see cref="VoiceAudibility"/>). <c>None</c>: neither, exactly the behaviour before the fix. <c>Footsteps</c> (default): both, for the posts of
/// <c>is_footstep_sound</c> commands (footsteps and landings) only. <c>All</c>: both, for every post.
/// </summary>
public enum InstanceLimits { None, Footsteps, All }

/// <summary>What <see cref="SoundSystem.PostSpeech"/> did: started a line, dropped it (the character was still speaking), found no line in the dialogue tree, or has no speech bank.</summary>
public enum SpeechResult { Started, Busy, NoLine, NoSpeech }

/// <summary>
/// Plays the catalogue's voices in Godot (M26b): decoded Ogg (<see cref="AudioCatalog.Ogg"/>, on worker threads) loaded with
/// <c>AudioStreamOggVorbis.LoadFromBuffer</c> into pooled <see cref="AudioStreamPlayer3D"/>s (positioned voices) and
/// <see cref="AudioStreamPlayer"/>s (non-positioned), with the data's volume, pitch and distance attenuation (the dry volume
/// curve applied each frame from the listener's distance; Godot's own attenuation off), a voice cap that steals the lowest
/// priority, then the quietest, then the oldest voice, continuous containers continued on time, Stop actions per emitter, and
/// a bus "SmSfx" (reverb send for the map's environment regions, then a hard limiter) into Master.
/// M33: the live mix of each voice (<see cref="VoiceMixer"/>: volume, pitch and low-pass from its parent chain, the output buses incl.
/// the master, RTPC curves for distance and speed, the attenuation's volume and low-pass curves, and Set/Reset pitch, volume, LPF and
/// mute actions with fades), low-pass through twenty filter buses, SetSwitch actions, and one-at-a-time character voices (barks).
/// **Provisional**: <see cref="GlobalGainDb"/> (ours), the reverb's settings, the LPF value → cutoff mapping and its filter (ours), Wwise
/// curve and fade shapes drawn as lines; relative volumes are the data's.
/// </summary>
public partial class SoundSystem : Node
{
    public const int Max3D = 48, Max2D = 16;
    /// <summary>Metres: own 3D voices are attenuated as if no farther than this (the camera sits behind the player).</summary>
    public const float OwnMinDistance = 1.5f;
    /// <summary>Seconds a voice may wait for its decode before it is dropped as late.</summary>
    public const double MaxLate = 0.35;
    public const string BusName = "SmSfx";

    /// <summary>Global gain on every voice (dB; ours: 0, i.e. none; the data's master bus volume, -7 dB, is part of each voice's mix).</summary>
    public float GlobalGainDb { get; set; } = 0f;

    /// <summary>
    /// <c>--sound-limits none|footsteps|all</c> (<see cref="InstanceLimits"/>): which posts get the instance limits and the 2D volume threshold. The footsteps' layers hold limits of 1 to
    /// 4 (a footstep cuts the tail of the last one); other sounds' nodes set them too (bolter shots 1 and 2) but that is not checked by listening. Default: the footstep posts only.
    /// </summary>
    public InstanceLimits Limits { get; set; } = InstanceLimits.Footsteps;

    /// <summary>
    /// <c>--footstep-gain &lt;dB&gt;</c> (ours; 0 by default): added to every footstep and landing voice, a listening aid on top of the owner's footstep level (<see cref="FootstepLevel"/>) and
    /// the RTPC volume ceiling (<see cref="RtpcCap"/>): docs/discovery.md, "Footsteps, second pass".
    /// </summary>
    public float FootstepGainDb { get; set; }

    /// <summary>
    /// <c>--rtpc-volume-cap peak|node|voice|off</c> (sound/footsteps-2): how positive RTPC volume offsets are bounded (<see cref="RtpcVolumeCap"/>; default peak,
    /// interpretation; <c>off</c> = the behaviour before the branch, the listening comparison).
    /// </summary>
    public RtpcVolumeCap RtpcCap { get; set; } = RtpcVolumeCaps.Default;

    /// <summary>Seconds a voice killed by an instance limit fades out over (ours: Wwise kills with a short fade, its length is not in the data).</summary>
    public const double KillFade = 0.03;

    /// <summary>Test aid: called with a line for every voice an instance limit kills or drops (<c>--log-sound footstep</c>).</summary>
    public Action<string>? LimitLog { get; set; }

    /// <summary>One filter bus per this many LPF units (0 = no filter, the bus <see cref="BusName"/>): 20 buses; ours.</summary>
    public const float LpfStep = 5f;
    public const int LpfBuses = 20;
    static readonly string[] LpfNames = Enumerable.Range(0, LpfBuses + 1).Select(i => $"SmLpf{i}").ToArray();
    public static string LpfBusName(int level) => LpfNames[Math.Clamp(level, 0, LpfBuses)];

    /// <summary>
    /// The cutoff (Hz) a Wwise LPF value stands for: exponential from 20 kHz at 0 to 150 Hz at 100. **Provisional approximation**
    /// (ours): the engine's mapping is not in the data.
    /// </summary>
    public static float CutoffHz(float lpf) => 20000f * MathF.Pow(150f / 20000f, Math.Clamp(lpf, 0f, 100f) / 100f);

    readonly AudioCatalog _catalog;
    readonly Dictionary<uint, AudioStreamOggVorbis?> _streams = new();
    readonly HashSet<uint> _decoding = new();
    readonly ConcurrentQueue<(uint Wem, byte[]? Ogg, double Ms)> _decoded = new();
    readonly SemaphoreSlim _workers = new(2, 2);
    readonly List<Pending> _pending = new();
    readonly List<Active> _active = new();
    readonly List<(SoundRepeat Repeat, SoundEmitter Emitter, double Due, string Event)> _repeats = new();
    readonly List<(uint Target, SoundEmitter Emitter, double Due)> _stops = new();
    readonly HashSet<SoundEmitter> _oneShots = new();
    readonly SoundModifiers _mods = new();
    readonly List<(double Due, SoundPropertyAction Action, ulong Emitter)> _properties = new();
    /// <summary>Emitter → time its current character voice ends: a new bark before then is dropped (the speech containers allow one instance).</summary>
    readonly Dictionary<ulong, double> _speechUntil = new();
    double _pruneAt;
    float _masterDb;
    double _forgetAt;
    readonly Stack<AudioStreamPlayer3D> _free3D = new();
    readonly Stack<AudioStreamPlayer> _free2D = new();
    int _created3D, _created2D;
    double _now;
    AudioEffectReverb? _reverb;
    float _reverbTarget;

    // Statistics for the log.
    public readonly Dictionary<string, (int Posts, int Voices)> ByEvent = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> Misses = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> ContByEvent = new();
    readonly Dictionary<string, Census> _census = new(StringComparer.OrdinalIgnoreCase);
    sealed class Census { public int Started, TwoD, Loops; public double SumDb; public HashSet<string> Labels = new(); }
    void Tally(Active a)
    {
        var c = _census.TryGetValue(a.Event, out var e) ? e : _census[a.Event] = new Census();
        c.Started++; if (!a.Voice.Is3D) c.TwoD++; if (a.Voice.LoopCount == 0) c.Loops++;
        c.SumDb += MixOf(a).VolumeDb;
        if (c.Labels.Count < 4) c.Labels.Add(a.Emitter.Label);
    }
    /// <summary>--log-sound census: one line per event (posts, voices started, 2D voices, looping voices, mean volume dB, continuations, emitter labels).</summary>
    public IEnumerable<string> CensusLines() => _census.OrderByDescending(kv => kv.Value.Started).Take(25).Concat(_census.Where(kv => ContByEvent.GetValueOrDefault(kv.Key) > 0)).Distinct().Select(kv =>
        $"CENSUS {kv.Key} posts {ByEvent.GetValueOrDefault(kv.Key).Posts} started {kv.Value.Started} 2D {kv.Value.TwoD} loopvoices {kv.Value.Loops} meandb {kv.Value.SumDb / Math.Max(1, kv.Value.Started):F1} cont {ContByEvent.GetValueOrDefault(kv.Key)} labels {string.Join('|', kv.Value.Labels)}");
    public int Posts, VoicesStarted, Steals, Late, PeakActive, Virtual, Culled, Revived, StreamsLoaded, UnsupportedActions, MissingMedia, Continuations;
    /// <summary>M33 counters: property actions applied, switch actions, bark posts, barks dropped (one at a time per character), barks with no line, voices started on a filter bus, bus changes.</summary>
    public int PropertyActions, SwitchActions, Barks, BarksDropped, BarksSilent, FilteredStarts, FilterChanges;
    /// <summary>Footsteps-fix counters: voices killed and dropped by instance limits, voices not started because they are under the volume threshold.</summary>
    public int LimitKills, LimitDrops, ThresholdCulls;
    public double DecodeMsTotal, DecodeMsMax;
    public int DecodesDone;

    sealed record Pending(SoundVoice Voice, SoundEmitter Emitter, double Due, string Event, long PostId, bool Limited, bool Footstep, VoiceOffset Offset);

    sealed class Active
    {
        public required SoundVoice Voice;
        public required SoundEmitter Emitter;
        public Node? Player;
        public required AudioStreamOggVorbis Stream;
        public required double Started;
        public required string Event;
        public float Effective;
        /// <summary>The emitter's game parameters at the start (read once).</summary>
        public Dictionary<uint, float>? Params;
        public int LpfLevel;
        public float AppliedPitch = 1f;
        /// <summary>The post that made the voice, and a unique handle for the limiter.</summary>
        public long PostId;
        public int Handle;
        /// <summary>The post asked for the instance limits and the 2D volume threshold (<c>--sound-limits</c>).</summary>
        public bool Limited;
        /// <summary>Posted for a footstep or landing (<see cref="FootstepGainDb"/> applies).</summary>
        public bool Footstep;
        /// <summary>What the post asked for on top of the data's mix (the owner's footstep level, <see cref="FootstepLevel"/>; the deeper impacts and the soft hit confirm, sound/impacts).</summary>
        public VoiceOffset Offset;
        /// <summary>Set when an instance limit killed the voice: it fades out and is released at this time.</summary>
        public double FadeUntil;
    }

    static int _handles;
    long _posts;

    public SoundSystem(AudioCatalog catalog) { _catalog = catalog; Name = "SoundSystem"; _masterDb = catalog?.MasterBusVolume ?? 0f; }
    public SoundSystem() : this(null!) { }

    public int ActiveCount => _active.Count(a => a.Player is not null);
    public int PendingCount => _pending.Count;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        int bus = AudioServer.GetBusIndex(BusName);
        if (bus < 0)
        {
            AudioServer.AddBus();
            bus = AudioServer.BusCount - 1;
            AudioServer.SetBusName(bus, BusName);
            AudioServer.SetBusSend(bus, "Master");
            _reverb = new AudioEffectReverb { RoomSize = 0.6f, Damping = 0.5f, Spread = 1f, Dry = 1f, Wet = 0f, PredelayMsec = 20f };
            AudioServer.AddBusEffect(bus, _reverb);
            AudioServer.AddBusEffect(bus, new AudioEffectHardLimiter { CeilingDb = -0.5f, PreGainDb = 0f });
        }
        else
        {
            // The bus was made by another SoundSystem (the menus' one, UiSounds, before a match): take its reverb so the map's environment regions still shape this one's sounds.
            for (int i = 0; i < AudioServer.GetBusEffectCount(bus) && _reverb is null; i++)
                if (AudioServer.GetBusEffect(bus, i) is AudioEffectReverb existing) _reverb = existing;
        }
        // The filter buses (M33): level k holds a low-pass at CutoffHz(k * LpfStep), sending to the reverb/limiter bus.
        for (int level = 1; level <= LpfBuses; level++)
        {
            string name = LpfBusName(level);
            if (AudioServer.GetBusIndex(name) >= 0) continue;
            AudioServer.AddBus();
            int index = AudioServer.BusCount - 1;
            AudioServer.SetBusName(index, name);
            AudioServer.SetBusSend(index, BusName);
            AudioServer.AddBusEffect(index, new AudioEffectLowPassFilter { CutoffHz = CutoffHz(level * LpfStep), Resonance = 0.5f, Db = AudioEffectFilter.FilterDB.Filter12Db });
        }
    }

    /// <summary>The reverb send for the listener's environment region (0 none; the map's amount, e.g. 0.3), eased over ~0.5 s.</summary>
    public void SetReverb(float amount, float roomSize)
    {
        _reverbTarget = Math.Clamp(amount, 0f, 1f);
        if (_reverb is not null && amount > 0f) _reverb.RoomSize = Math.Clamp(roomSize, 0f, 1f);
    }

    /// <summary>Starts decoding these .wems in the background (prefetch, so a first shot is not late).</summary>
    public void Prefetch(IEnumerable<uint> wems)
    {
        foreach (var w in wems) Request(w);
    }

    void Request(uint wem)
    {
        if (_streams.ContainsKey(wem) || !_decoding.Add(wem)) return;
        Task.Run(async () =>
        {
            await _workers.WaitAsync().ConfigureAwait(false);
            try
            {
                var ogg = _catalog.Ogg(wem, out double ms);
                _decoded.Enqueue((wem, ogg, ms));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _decoded.Enqueue((wem, null, 0));
            }
            finally { _workers.Release(); }
        });
    }

    /// <summary>
    /// Posts an event for an emitter now; returns the catalogue's answer (null when the name is empty). <paramref name="footstep"/>: an <c>is_footstep_sound</c>
    /// post (instance limits, <see cref="FootstepGainDb"/>); <paramref name="offset"/>: added to every voice of the post (volume, pitch, low-pass: the owner's
    /// footstep level, the deeper impacts and the soft hit confirm). <paramref name="once"/>: play the first pick only and drop the container's continuation (BR-03).
    /// <paramref name="limited"/>: the data's instance limits and the 2D threshold apply whatever <see cref="Limits"/> says (a crowd's posts).
    /// </summary>
    public SoundPlay? Post(string eventName, SoundEmitter emitter, float extraDelay = 0f, bool footstep = false, VoiceOffset offset = default, bool once = false, bool limited = false)
    {
        if (string.IsNullOrEmpty(eventName) || _catalog is null) return null;
        Posts++;
        var play = _catalog.Post(eventName, emitter.Switches, emitter.Id);
        var (p, v) = ByEvent.GetValueOrDefault(eventName);
        ByEvent[eventName] = (p + 1, v + play.Voices.Count);
        if (NetworkView.LogSoundKinds.Contains("post")) GD.Print($"POST {eventName} label '{emitter.Label}' at {emitter.Current.X:F1},{emitter.Current.Y:F1},{emitter.Current.Z:F1} found {play.Found} voices {play.Voices.Count}");   // --log-sound post (map-silence test aid)
        if (!play.Found) { Misses[eventName] = Misses.GetValueOrDefault(eventName) + 1; return play; }
        if (once) play.Repeats.Clear();   // BR-03: a trigger-rate container asks to be continued for ever; a one-shot post plays its first pick only
        Schedule(play, emitter, extraDelay, eventName, footstep, offset, limited);
        if (emitter.OneShot) _oneShots.Add(emitter);
        return play;
    }

    /// <summary>
    /// Posts a character voice line (a bark, M33) for an emitter: the speech bank's dialogue event for the binding's kind with the
    /// character and the binding's arguments (<c>autodetect</c> threat reads as <c>high</c>: interpretation). One line at a time per
    /// emitter (the speech containers allow one instance; a line posted while the last plays is dropped, interpretation).
    /// </summary>
    public SpeechResult PostSpeech(SpeechBinding b, string character, SoundEmitter emitter, float extraDelay = 0f, VoiceOffset offset = default, bool limited = false)
    {
        if (_catalog is null || character.Length == 0) return SpeechResult.NoSpeech;
        Posts++;
        string label = $"speech/{b.Kind}/{(b.Type.Length > 0 ? b.Type : b.State)}";
        if (_speechUntil.TryGetValue(emitter.Id, out double until) && _now + extraDelay < until)
        {
            BarksDropped++;
            return SpeechResult.Busy;
        }
        string threat = b.Threat.Length == 0 || b.Threat.Equals("autodetect", StringComparison.OrdinalIgnoreCase) ? "high" : b.Threat;
        var play = _catalog.PostSpeech(b.Kind, character, b.Type, b.State, threat, emitter.Switches, emitter.Id);
        Barks++;
        var (p, v) = ByEvent.GetValueOrDefault(label);
        ByEvent[label] = (p + 1, v + play.Voices.Count);
        if (!play.Found) return SpeechResult.NoSpeech;
        if (play.Voices.Count == 0) { BarksSilent++; return SpeechResult.NoLine; }
        double end = _now + extraDelay + play.Voices.Max(x => x.Delay + _catalog.Duration(x.WemId) / Math.Max(0.05f, x.PitchScale));
        _speechUntil[emitter.Id] = end;   // armed at the post; Start re-arms it from the real start and frees it when the voice is culled or late
        Schedule(play, emitter, extraDelay, label, offset: offset, force: limited);
        if (emitter.OneShot) _oneShots.Add(emitter);
        return SpeechResult.Started;
    }

    static bool IsSpeech(string evt) => evt.StartsWith("speech/", StringComparison.Ordinal);

    /// <summary>The voices of an emitter group playing or about to (<see cref="SoundEmitter.Group"/>): all of them, and the character lines among them. Virtual voices do not count.</summary>
    public (int Voices, int Speech) GroupVoices(string group)
    {
        int voices = 0, speech = 0;
        foreach (var a in _active) if (a.Player is not null && a.Emitter.Group == group) { voices++; if (IsSpeech(a.Event)) speech++; }
        foreach (var p in _pending) if (p.Emitter.Group == group) { voices++; if (IsSpeech(p.Event)) speech++; }
        return (voices, speech);
    }

    /// <summary>Whether a character voice line (<see cref="PostSpeech"/>) of the emitter is playing or about to (a line posted now would be dropped as busy).</summary>
    public bool SpeechBusy(SoundEmitter emitter) => _speechUntil.TryGetValue(emitter.Id, out double until) && _now < until;

    /// <summary>A bark that never sounded (culled, late) frees its character's slot, unless another voice of the emitter is still pending or playing.</summary>
    void FreeSpeech(string evt, SoundEmitter emitter)
    {
        if (!IsSpeech(evt)) return;
        if (_pending.Any(x => x.Emitter == emitter && IsSpeech(x.Event)) || _active.Any(x => x.Emitter == emitter && IsSpeech(x.Event))) return;
        _speechUntil.Remove(emitter.Id);
    }

    void Schedule(SoundPlay play, SoundEmitter emitter, float extraDelay, string evt, bool footstep = false, VoiceOffset offset = default, bool force = false)
    {
        long post = ++_posts;
        bool limited = force || Limits == InstanceLimits.All || (Limits == InstanceLimits.Footsteps && footstep);
        UnsupportedActions += play.UnsupportedActions;
        MissingMedia += play.MissingMedia;
        foreach (var (target, delay) in play.Stops) _stops.Add((target, emitter, _now + extraDelay + delay));
        foreach (var a in play.Properties) _properties.Add((_now + extraDelay + a.Delay, a, emitter.Id));
        SwitchActions += play.SwitchSets.Count;
        ApplyStops();   // stops due now act before this event's own voices start
        foreach (var voice in play.Voices)
        {
            Request(voice.WemId);
            _pending.Add(new Pending(voice, emitter, _now + extraDelay + voice.Delay, evt, post, limited, footstep, offset));
        }
        foreach (var r in play.Repeats) _repeats.Add((r, emitter, _now + extraDelay + r.Delay + r.After, evt));
    }

    /// <summary>One-shot emitters with nothing pending, playing or continuing: their container histories are dropped.</summary>
    void ForgetIdleOneShots()
    {
        if (_oneShots.Count == 0) return;
        var busy = new HashSet<SoundEmitter>(_pending.Select(p => p.Emitter).Concat(_active.Select(a => a.Emitter)).Concat(_repeats.Select(r => r.Emitter)));
        _oneShots.RemoveWhere(e =>
        {
            if (busy.Contains(e)) return false;
            _catalog.ForgetEmitter(e.Id);
            _mods.ForgetEmitter(e.Id);
            _properties.RemoveAll(x => x.Emitter == e.Id && x.Action.EmitterScope);   // a global Set/Reset outlives the emitter
            return true;
        });
    }

    /// <summary>Stops every voice and continuation of an emitter (a projectile gone, a player left).</summary>
    public void StopEmitter(SoundEmitter emitter)
    {
        _pending.RemoveAll(p => p.Emitter == emitter);
        _repeats.RemoveAll(r => r.Emitter == emitter);
        foreach (var a in _active.Where(a => a.Emitter == emitter).ToList()) Release(a);
        _oneShots.Remove(emitter);
        _catalog.ForgetEmitter(emitter.Id);
        _mods.ForgetEmitter(emitter.Id);
        _properties.RemoveAll(x => x.Emitter == emitter.Id && x.Action.EmitterScope);
        _speechUntil.Remove(emitter.Id);
    }

    /// <summary>
    /// Ends an emitter's endless sounds (sound/shield-regen): its continuous containers' next picks (e.g. the broken-shield warning, a trigger-rate pick every
    /// 0.7 s) and its looping voices; one-shots already playing finish. For a player who died: the character's looping sounds end with it, as when the game's
    /// entity goes away (interpretation), so no loop outlives the life that started it. Returns how many were ended.
    /// </summary>
    public int StopLoops(SoundEmitter emitter)
    {
        int n = _repeats.RemoveAll(r => r.Emitter == emitter);
        foreach (var a in _active.Where(a => a.Emitter == emitter && a.Voice.LoopCount == 0).ToList()) { Release(a); n++; }
        n += _pending.RemoveAll(p => p.Emitter == emitter && p.Voice.LoopCount == 0);
        return n;
    }

    /// <summary>
    /// Cuts off the character voice line an emitter is speaking or about to speak, so the next line is not dropped as busy (Ork O8: a death yell takes the place of the grunt
    /// that was still playing; the emitter's other sounds go on).
    /// </summary>
    public void StopSpeech(SoundEmitter emitter)
    {
        _pending.RemoveAll(p => p.Emitter == emitter && IsSpeech(p.Event));
        foreach (var a in _active.Where(a => a.Emitter == emitter && IsSpeech(a.Event)).ToList()) Release(a);
        _speechUntil.Remove(emitter.Id);
    }

    void ApplyProperties()
    {
        for (int i = 0; i < _properties.Count; i++)
        {
            var (due, action, emitter) = _properties[i];
            if (due > _now + 1e-6) continue;
            _properties.RemoveAt(i--);
            _mods.Apply(action, emitter, due);                 // a late frame does not shorten a fade
            PropertyActions++;
        }
    }

    void ApplyStops()
    {
        for (int i = _stops.Count - 1; i >= 0; i--)
        {
            var (target, emitter, due) = _stops[i];
            if (due > _now + 1e-6) continue;
            _stops.RemoveAt(i);
            _pending.RemoveAll(p => p.Emitter == emitter && p.Due <= due && p.Voice.Path.Contains(target));
            _repeats.RemoveAll(r => r.Emitter == emitter && r.Repeat.Path.Contains(target));
            foreach (var a in _active.Where(a => a.Emitter == emitter && a.Voice.Path.Contains(target)).ToList()) Release(a);
        }
    }

    public override void _Process(double delta)
    {
        _now += delta;
        while (_decoded.TryDequeue(out var d))
        {
            _decoding.Remove(d.Wem);
            AudioStreamOggVorbis? stream = null;
            if (d.Ogg is not null)
            {
                stream = AudioStreamOggVorbis.LoadFromBuffer(d.Ogg);
                if (stream is not null) StreamsLoaded++;
                else _catalog.Discard(d.Wem);   // a bad cached file is decoded again next session
            }
            _streams[d.Wem] = stream;
            DecodesDone++;
            DecodeMsTotal += d.Ms;
            DecodeMsMax = Math.Max(DecodeMsMax, d.Ms);
        }
        ApplyStops();
        ApplyProperties();
        if (_now >= _pruneAt)
        {
            _pruneAt = _now + 1.0;
            _mods.Prune(_now);
            foreach (var id in _speechUntil.Where(kv => kv.Value < _now - 5.0).Select(kv => kv.Key).ToList()) _speechUntil.Remove(id);
        }
        if (_now >= _forgetAt) { _forgetAt = _now + 2.0; ForgetIdleOneShots(); }
        // Continuous containers due now pick their next child.
        for (int i = _repeats.Count - 1; i >= 0; i--)
        {
            var (repeat, emitter, due, evt) = _repeats[i];
            if (due > _now) continue;
            _repeats.RemoveAt(i);
            Continuations++;
            ContByEvent[evt] = ContByEvent.GetValueOrDefault(evt) + 1;
            Schedule(_catalog.Continue(repeat, emitter.Switches), emitter, 0f, evt);
        }
        // Voices whose time has come and whose stream is ready.
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            if (p.Due > _now) continue;
            if (!_streams.TryGetValue(p.Voice.WemId, out var stream))
            {
                if (_now - p.Due > MaxLate) { Late++; _pending.RemoveAt(i--); FreeSpeech(p.Event, p.Emitter); }
                continue;
            }
            _pending.RemoveAt(i--);
            if (stream is null) continue;   // did not convert (counted by the catalogue)
            Start(p, stream);
        }
        UpdateActive((float)delta);
    }

    /// <summary>
    /// Starts a voice: positioned voices beyond their attenuation's end are virtual (a loop waits without a player until the
    /// listener comes in range; a one-shot is culled), as Wwise's "below threshold" handling does (interpretation). Otherwise
    /// it takes a player, stealing one when the cap is reached.
    /// </summary>
    void Start(Pending p, AudioStreamOggVorbis stream)
    {
        var v = p.Voice;
        if (v.LoopCount == 0)
        {
            stream = (AudioStreamOggVorbis)stream.Duplicate();   // the cached stream stays one-shot
            stream.Loop = true;
        }
        var a = new Active
        {
            Voice = v, Emitter = p.Emitter, Player = null, Started = _now, Event = p.Event, Stream = stream,
            Params = p.Emitter.Parameters.Count > 0 ? new Dictionary<uint, float>(p.Emitter.Parameters) : null,
            PostId = p.PostId, Handle = ++_handles, Footstep = p.Footstep, Limited = p.Limited, Offset = p.Offset,
        };
        if (!Audible(a))
        {
            if (v.LoopCount == 0) { _active.Add(a); Virtual++; } else { Culled++; if (!v.Is3D) ThresholdCulls++; FreeSpeech(p.Event, p.Emitter); }
            return;
        }
        // Only a voice that will really start takes part in the instance limits (an inaudible one must not evict an audible one).
        if (p.Limited && !ApplyInstanceLimits(a)) { FreeSpeech(p.Event, p.Emitter); return; }
        if (!Acquire(a))
        {
            if (v.LoopCount == 0) { _active.Add(a); Virtual++; } else { Culled++; FreeSpeech(p.Event, p.Emitter); }
            return;
        }
        _active.Add(a);
        VoicesStarted++;
        Tally(a);
        if (IsSpeech(p.Event)) _speechUntil[p.Emitter.Id] = _now + _catalog.Duration(v.WemId) / Math.Max(0.05f, v.PitchScale);   // the line is heard from now
        PeakActive = Math.Max(PeakActive, _active.Count(x => x.Player is not null));
    }

    /// <summary>Below this level (dB, not counting the master bus) a voice is inaudible (ours: <see cref="VoiceAudibility.VolumeThreshold"/>).</summary>
    public const float VolumeThreshold = VoiceAudibility.VolumeThreshold;

    /// <summary>
    /// The rule is <see cref="VoiceAudibility.IsAudible"/>: positioned voices as before the footsteps fix; non-positioned ones are also culled under the threshold only
    /// for posts that asked for it (<see cref="Active.Limited"/>: <c>--sound-limits footsteps</c> the footstep posts, <c>all</c> every post, <c>none</c> no 2D culling at all).
    /// </summary>
    bool Audible(Active a)
    {
        var v = a.Voice;
        float? distance = null;
        if (v.Is3D && v.Attenuation is not null && Distance(a.Emitter) is var (_, d)) distance = d;
        return VoiceAudibility.IsAudible(v.Is3D, v.Attenuation is not null, v.Attenuation?.MaxDistance ?? 0f, distance, MixOf(a).VolumeDb, _masterDb,
            v.Chain.Count > 0 ? v.Chain[0].BelowThreshold : (byte)1, v.Chain.Count > 0, a.Limited);
    }

    /// <summary>
    /// The nodes' instance limits for a starting voice (<see cref="InstanceLimiter"/>): kills the oldest instances (fading them out over <see cref="KillFade"/>)
    /// or, with kill-newest, drops the new voice (returns false).
    /// </summary>
    bool ApplyInstanceLimits(Active a)
    {
        if (a.Voice.Chain.All(n => n.Limit is null)) return true;
        var playing = _active.Where(x => x.FadeUntil <= 0).Select(x => new InstanceLimiter.Playing(x.Handle, x.Emitter.Id, x.PostId, x.Voice.Chain)).ToList();
        var decision = InstanceLimiter.Decide(a.Voice.Chain, a.Emitter.Id, a.PostId, playing);
        foreach (int handle in decision.Kill)
            if (_active.FirstOrDefault(x => x.Handle == handle) is { } victim)
            {
                victim.FadeUntil = _now + KillFade;
                LimitKills++;
                LimitLog?.Invoke($"LIMIT t {_now:F3} {a.Emitter.Label}: {decision.Reason}: wem {victim.Voice.WemId} (post {victim.PostId}, {_now - victim.Started:F2}s old) fades out; new wem {a.Voice.WemId} (post {a.PostId})");
            }
        // The killed instances' delayed voices (not started yet) are cancelled too.
        foreach (var (post, node) in decision.Instances)
            _pending.RemoveAll(q => q.PostId == post &&q.Voice.Chain.Any(n => n.Id == node));
        if (decision.DropNew)
        {
            LimitDrops++;
            LimitLog?.Invoke($"LIMIT t {_now:F3} {a.Emitter.Label}: {decision.Reason}: wem {a.Voice.WemId} (post {a.PostId})");
            return false;
        }
        return true;
    }

    /// <summary>Gives a voice a player (stealing the lowest priority, then quietest, then oldest one of its kind at the cap) and plays it.</summary>
    bool Acquire(Active a, bool revive = false)
    {
        bool positioned = a.Voice.Is3D;
        if (_active.Count(x => x.Player is not null && x.Voice.Is3D == positioned) >= (positioned ? Max3D : Max2D))
        {
            // A crowd's voice (SoundEmitter.Group) takes a player only from its own crowd: it never cuts a player's sounds.
            var victim = _active.Where(x => x.Player is not null && x.Voice.Is3D == positioned && (a.Emitter.Group is null || x.Emitter.Group == a.Emitter.Group))
                .OrderBy(x => x.Voice.Priority).ThenBy(x => x.Effective).ThenBy(x => x.Started).FirstOrDefault();
            if (victim is null || victim.Voice.Priority > a.Voice.Priority) return false;
            // A waiting loop takes over only from a voice at least 6 dB quieter than it would be (no swapping back and forth).
            if (revive && (victim.Voice.Priority == a.Voice.Priority && victim.Effective > EffectiveNow(a) - 6f)) return false;
            Steals++;
            if (victim.Voice.LoopCount == 0) Demote(victim); else Release(victim);
        }
        if (positioned)
        {
            var p3 = _free3D.Count > 0 ? _free3D.Pop() : New3D();
            p3.Stream = a.Stream;
            a.Player = p3;
        }
        else
        {
            var p2 = _free2D.Count > 0 ? _free2D.Pop() : New2D();
            p2.Stream = a.Stream;
            a.Player = p2;
        }
        a.AppliedPitch = -1f;       // Volume sets the pitch and the filter bus of the pooled player
        a.LpfLevel = -1;
        Volume(a);
        if (a.Player is AudioStreamPlayer3D a3) a3.Play(); else ((AudioStreamPlayer)a.Player).Play();
        return true;
    }

    /// <summary>A looping voice gives its player back and waits as a virtual voice.</summary>
    void Demote(Active a)
    {
        FreePlayer(a);
        Virtual++;
    }

    AudioStreamPlayer3D New3D()
    {
        _created3D++;
        var p = new AudioStreamPlayer3D
        {
            Name = $"Voice3D_{_created3D}", Bus = BusName, AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled, MaxDistance = 0f,
            AttenuationFilterCutoffHz = 20500f, AttenuationFilterDb = 0f, DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
        };
        AddChild(p);
        return p;
    }

    AudioStreamPlayer New2D()
    {
        _created2D++;
        var p = new AudioStreamPlayer { Name = $"Voice2D_{_created2D}", Bus = BusName };
        AddChild(p);
        return p;
    }

    /// <summary>The emitter's position and its distance from the listener (the current camera), or null without a camera.</summary>
    (Vector3 Position, float Distance)? Distance(SoundEmitter e)
    {
        var camera = GetViewport()?.GetCamera3D();
        var pos = e.Current;
        if (camera is null) return (pos, 0f);
        float d = camera.GlobalPosition.DistanceTo(pos);
        if (e.Own) d = Math.Min(d, OwnMinDistance);
        return (pos, d);
    }

    /// <summary>The voice's live mix: parent chain, buses, RTPCs (distance now, the emitter's parameters as at the start), attenuation, modifiers.</summary>
    VoiceMix MixOf(Active a)
    {
        float? distance = Distance(a.Emitter) is var (_, d) ? d : null;
        return VoiceMixer.Evaluate(a.Voice, _mods, a.Emitter.Id, _now, distance, a.Params, RtpcCap);
    }

    float EffectiveNow(Active a) => MixOf(a).VolumeDb + GlobalGainDb;

    void Volume(Active a)
    {
        var mix = MixOf(a).With(a.Offset);
        // ui/menu-rebuild: the Options menu's music, effects and voice volumes (0 dB at the default).
        float db = mix.VolumeDb + GlobalGainDb + (a.Footstep ? FootstepGainDb : 0f) + SmClient.Customizer.Model.LiveOptions.GainDb(SmClient.Customizer.Model.GameOptions.CategoryOf(a.Event));
        if (a.FadeUntil > 0) db += 20f * MathF.Log10(Math.Max(0.001f, (float)Math.Clamp((a.FadeUntil - _now) / KillFade, 0.0, 1.0)));   // killed by an instance limit: linear fade out
        if (a.Player is AudioStreamPlayer3D p3 && Distance(a.Emitter) is var (pos, _)) p3.GlobalPosition = pos;
        a.Effective = db;
        float clamped = Math.Clamp(db, -80f, 24f);
        float pitch = Math.Clamp(mix.PitchScale, 0.05f, 16f);
        int level = Math.Clamp((int)MathF.Round(mix.Lpf / LpfStep), 0, LpfBuses);
        bool pitchChanged = MathF.Abs(pitch - a.AppliedPitch) > 1e-3f, busChanged = level != a.LpfLevel;
        if (pitchChanged) a.AppliedPitch = pitch;
        if (busChanged)
        {
            if (a.LpfLevel < 0) { if (level > 0) FilteredStarts++; }
            else FilterChanges++;
            a.LpfLevel = level;
        }
        StringName? bus = busChanged ? new StringName(level == 0 ? BusName : LpfBusName(level)) : null;
        if (a.Player is AudioStreamPlayer3D q)
        {
            q.VolumeDb = clamped;
            if (pitchChanged) q.PitchScale = pitch;
            if (bus is not null) q.Bus = bus;
        }
        else if (a.Player is AudioStreamPlayer q2)
        {
            q2.VolumeDb = clamped;
            if (pitchChanged) q2.PitchScale = pitch;
            if (bus is not null) q2.Bus = bus;
        }
    }

    void UpdateActive(float delta)
    {
        if (_reverb is not null) _reverb.Wet = Mathf.MoveToward(_reverb.Wet, _reverbTarget, delta * 2f);
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            if (a.FadeUntil > 0 && _now >= a.FadeUntil) { Release(a); continue; }
            if (a.Player is null)
            {
                if (a.FadeUntil > 0) { Release(a); continue; }
                if (Audible(a) && Acquire(a, revive: true)) Revived++;
                continue;
            }
            bool playing = a.Player is AudioStreamPlayer3D p3 ? p3.Playing : ((AudioStreamPlayer)a.Player).Playing;
            // A finite loop count above 1 replays the stream (Wwise loop count; interpretation).
            if (!playing && a.Voice.LoopCount > 1 && _now - a.Started < 600)
            {
                a.Voice = a.Voice with { LoopCount = a.Voice.LoopCount - 1 };
                if (a.Player is AudioStreamPlayer3D r3) r3.Play(); else ((AudioStreamPlayer)a.Player).Play();
                playing = true;
            }
            if (!playing) { Release(a); continue; }
            if (a.Voice.LoopCount == 0 && !Audible(a)) { Demote(a); continue; }
            Volume(a);
        }
    }

    void FreePlayer(Active a)
    {
        if (a.Player is AudioStreamPlayer3D p3) { p3.Stop(); p3.Stream = null; _free3D.Push(p3); }
        else if (a.Player is AudioStreamPlayer p2) { p2.Stop(); p2.Stream = null; _free2D.Push(p2); }
        a.Player = null;
    }

    void Release(Active a)
    {
        _active.Remove(a);
        FreePlayer(a);
    }

    /// <summary>One log line: posts, voices, decode times, top events and misses.</summary>
    public string Summary(int top = 12)
    {
        string events = string.Join(", ", ByEvent.OrderByDescending(kv => kv.Value.Posts).Take(top).Select(kv => $"{kv.Key} {kv.Value.Posts}/{kv.Value.Voices}"));
        string misses = Misses.Count == 0 ? "none" : string.Join(", ", Misses.OrderByDescending(kv => kv.Value).Take(top).Select(kv => $"{kv.Key} x{kv.Value}"));
        return $"posts {Posts}, voices started {VoicesStarted}, playing {ActiveCount} (peak {PeakActive}; players 3D {_created3D}, 2D {_created2D}), virtual {_active.Count - ActiveCount} (made virtual {Virtual}, revived {Revived}), culled {Culled}, pending {_pending.Count}, steals {Steals}, late {Late}, " +
               $"continuations {Continuations} ({string.Join(", ", ContByEvent.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key + " " + kv.Value))}), decodes {DecodesDone} (avg {(DecodesDone > 0 ? DecodeMsTotal / DecodesDone : 0):F1} ms, max {DecodeMsMax:F1} ms; converted {_catalog?.Decoded}, disk hits {_catalog?.DiskHits}, failures {_catalog?.DecodeFailures}), " +
               $"streams {StreamsLoaded}, unsupported actions {UnsupportedActions}, sounds without media {MissingMedia}; " +
               $"M33: property actions {PropertyActions} (live {_mods.Count}), switch actions {SwitchActions}, voices on a filter bus {FilteredStarts} (bus changes {FilterChanges}), master {_masterDb:0.#} dB, " +
               $"barks {Barks} (dropped {BarksDropped}, no line {BarksSilent}), footsteps fix: instance-limit kills {LimitKills} drops {LimitDrops}, culled under the threshold (2D) {ThresholdCulls}; events (posts/voices): {events}; events not in the banks: {misses}";
    }
}
