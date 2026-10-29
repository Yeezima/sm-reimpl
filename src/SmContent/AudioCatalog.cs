using System.Buffers.Binary;
using System.Diagnostics;
using SmFormats.Audio;

namespace SmContent;

/// <summary>
/// Switch values of one emitter (a player, a map emitter, a world position): switch group ID → value ID (FNV-1 of the
/// names). A group without a value plays the switch container's default.
/// </summary>
public sealed class SoundSwitches
{
    readonly Dictionary<uint, uint> _values = new();

    public static SoundSwitches None => new();

    public uint? Get(uint group) => _values.TryGetValue(group, out var v) ? v : null;

    public SoundSwitches Set(uint group, uint value) { _values[group] = value; return this; }

    public SoundSwitches Set(string group, string value) => Set(Fnv.Hash32(group), Fnv.Hash32(value));

    public SoundSwitches Clone()
    {
        var c = new SoundSwitches();
        foreach (var (k, v) in _values) c._values[k] = v;
        return c;
    }

    public IReadOnlyDictionary<uint, uint> Values => _values;
}

/// <summary>
/// One sound to play for a posted event: the .wem (<see cref="WemId"/>) of the Sound object <see cref="SoundId"/>, when
/// (<see cref="Delay"/>, seconds after the post: the Play action's delay), how loud and how high (<see cref="VolumeDb"/>,
/// <see cref="PitchCents"/>: the sums over the sound and its parents of each node's value plus a draw from its random range),
/// how often (<see cref="LoopCount"/>: 1 once, 0 forever), whether it is positioned (<see cref="Is3D"/>, with the
/// <see cref="Attenuation"/> of the nearest node up the parent chain that overrides positioning), its priority, and the play
/// path from the action's target down to the sound (<see cref="Path"/>; a Stop action on any of these stops it).
/// </summary>
public sealed record SoundVoice(uint WemId, uint SoundId, float Delay, float VolumeDb, float PitchCents, int LoopCount, bool Is3D,
    WwiseAttenuation? Attenuation, byte Priority, IReadOnlyList<uint> Path)
{
    /// <summary>The part of <see cref="VolumeDb"/> that comes from the output bus chain (<see cref="AudioCatalog.BusVolume"/>), master included.</summary>
    public float BusVolumeDb { get; init; }

    /// <summary>
    /// The voice's low-pass value at the post (0..100, Wwise's LPF units): the sum over the sound and its parents of each node's
    /// value plus a draw from its range, clamped (**interpretation**: additive like volume and pitch). The live value is
    /// <see cref="VoiceMixer.Evaluate"/>'s.
    /// </summary>
    public float LpfBase { get; init; }

    /// <summary>The sound first, then each parent up to the root: what a voice is made of, for <see cref="VoiceMixer"/>.</summary>
    public IReadOnlyList<VoiceNode> Chain { get; init; } = [];

    /// <summary>The output bus first, then its parents up to the master bus, with each bus's volume in dB.</summary>
    public IReadOnlyList<VoiceBus> Buses { get; init; } = [];

    /// <summary>Playback speed factor for the pitch: 2^(cents / 1200).</summary>
    public float PitchScale => MathF.Pow(2f, PitchCents / 1200f);
}

/// <summary>One node of a voice's parent chain: its ID, the volume (dB), pitch (cents) and LPF (0..100) it contributes at the post (base + draw), and its RTPC bindings.</summary>
public sealed record VoiceNode(uint Id, float Volume, float Pitch, float Lpf, IReadOnlyList<HircRtpc> Rtpcs)
{
    /// <summary>The node's "limit sound instances" setting (null: none), see <see cref="InstanceLimiter"/>.</summary>
    public InstanceLimit? Limit { get; init; }

    /// <summary>
    /// What happens to a voice of this node when its level is under the volume threshold (the advanced-settings byte; Wwise: 0 keep playing, 1 kill the voice,
    /// 2 keep it as a virtual voice). Read from the node itself (the sound), not inherited (interpretation).
    /// </summary>
    public byte BelowThreshold { get; init; }
}

/// <summary>An output bus of a voice's chain and its volume in dB (voice volume + bus volume, see <see cref="AudioCatalog.BusChain"/>).</summary>
public sealed record VoiceBus(uint Id, float Volume);

/// <summary>
/// A Set/Reset pitch, volume, LPF, bus volume or mute action resolved from a posted event (see <see cref="SoundModifiers"/>):
/// <see cref="Delay"/> and <see cref="Fade"/> in seconds; <see cref="Offset"/> adds to the target's own value instead of replacing
/// it; <see cref="EmitterScope"/>: only the posting emitter's voices (the action type's low bit; interpretation).
/// </summary>
public sealed record SoundPropertyAction(SoundProperty Property, uint Target, bool Reset, float Value, bool Offset, float Delay, float Fade, bool EmitterScope);

/// <summary>
/// A continuous random/sequence container that picks its next child later (ambience beds, trigger-rate one-shots): post
/// <see cref="AudioCatalog.Continue"/> <see cref="After"/> seconds after <see cref="Delay"/>. <see cref="PlaysLeft"/>: further
/// picks (-1: endless).
/// </summary>
public sealed record SoundRepeat(uint ContainerId, IReadOnlyList<uint> Path, float Delay, float After, int PlaysLeft, ulong Emitter);

/// <summary>What one posted event asks for: the voices to start, the containers to continue later, the nodes to stop.</summary>
public sealed class SoundPlay
{
    public uint EventId { get; init; }
    /// <summary>False when no loaded bank has the event.</summary>
    public bool Found { get; init; }
    public List<SoundVoice> Voices { get; } = new();
    public List<SoundRepeat> Repeats { get; } = new();
    /// <summary>Stop actions: the target node and when (seconds after the post).</summary>
    public List<(uint Target, float Delay)> Stops { get; } = new();
    /// <summary>SetSwitch actions: switch group and value, already applied to the posting emitter's <see cref="SoundSwitches"/>.</summary>
    public List<(uint Group, uint Value)> SwitchSets { get; } = new();
    /// <summary>Set/Reset pitch, volume, LPF and mute actions, with their delays (M33): the client applies them to <see cref="SoundModifiers"/>.</summary>
    public List<SoundPropertyAction> Properties { get; } = new();
    /// <summary>Actions this player does not do (pause, resume, game parameters, ...): counted, not played.</summary>
    public int UnsupportedActions { get; set; }
    /// <summary>Sounds reached whose media no loaded package holds.</summary>
    public int MissingMedia { get; set; }
    /// <summary>Sounds that are not Wwise Vorbis (generator plug-ins).</summary>
    public int NotVorbis { get; set; }
}

/// <summary>
/// The multiplayer sound content (M26b): the Wwise packages a match needs, opened read-only from the install, with event
/// resolution into playable voices and on-demand decoding to Ogg Vorbis (memory cache, optional disk cache in the private
/// cache; never the repository). Behaviour emulated (rules **interpretation** of Wwise's documented model, values from the
/// banks): Play actions with their delays; Stop actions on nodes; SetState actions (state groups, global); random containers
/// (uniform or weighted, avoiding the last <c>avoidRepeat</c> picks; shuffle mode as a bag), sequence containers (in order),
/// continuous containers (trigger rate, cross-fade, delay or back-to-back to the next pick); switch containers by the
/// emitter's switch values or the global states, else their default; layer containers (all children); volume and pitch
/// summed up the parent chain with each node's random range; positioning and attenuation from the nearest node that
/// overrides it. M33 adds: output buses with the master, node and attenuation low-pass, RTPC
/// curves (<see cref="VoiceMixer"/>), Set/Reset pitch, volume, LPF and mute actions, SetSwitch, character voices (<see cref="PostSpeech"/>). Not done:
/// HDR, voice limits per node, state-driven properties, fade-in and fade-out of Play and Stop, pause and resume, game parameter actions, music.
/// Thread-safety: <see cref="Post"/>/<see cref="Continue"/> are for one thread (the game loop); <see cref="Ogg"/> and
/// <see cref="Duration"/> may be called from worker threads.
/// </summary>
public sealed class AudioCatalog : IDisposable
{
    /// <summary>The surface switch group (<c>material_type</c>).</summary>
    public static readonly uint MaterialType = Fnv.Hash32("material_type");

    /// <summary>
    /// The unnamed switch group 0x0d500bea, whose cases are 0x0d500bea and <c>default</c>. **Interpretation** (M26b,
    /// docs/discovery.md): the 0x0d500bea case is the local player's (non-positioned, richer) variant, <c>default</c>
    /// everyone else's (positioned, with distance attenuation).
    /// </summary>
    public const uint PlayerGroup = 0x0d500bea, LocalPlayerValue = 0x0d500bea;

    /// <summary>Packages loaded before the weapons and the map's region bank (docs/discovery.md, "Audio (Wwise)").</summary>
    public static readonly IReadOnlyList<string> BasePackages =
    [
        "init", "in_game_default", "player1_vanguard", "player1_chaos", "abilities", "jump_pack", "explosions", "foley",
        "destruction", "world_objects", "ambiences", "ambiences_ram", "ui_in_game",
        // The character voices (M33): the speech bank's dialogue events (speech_exertion, speech_action) and their lines.
        "speech_01",
    ];

    const int MaxDepth = 32;
    const int MaxVoicesPerPost = 64;
    /// <summary>Shortest time between two picks of a continuous container (s; ours, a guard against zero transitions).</summary>
    public const float MinContinueSeconds = 0.1f;

    readonly WwiseLibrary _lib;
    readonly VorbisCodebookLibrary? _codebooks;
    readonly string? _diskCache;
    /// <summary>Folder version of the decoded Ogg cache: raise it when the Wwise Vorbis rebuild changes, so old files are not served.</summary>
    public const int OggCacheVersion = 1;
    readonly object _mediaLock = new();
    readonly Dictionary<uint, bool> _hasMedia = new();
    readonly Dictionary<uint, WwiseAttenuation?> _attenuations = new();
    readonly Dictionary<(uint Container, ulong Emitter), ContainerState> _containers = new();
    readonly Dictionary<uint, uint> _states = new();
    readonly Dictionary<uint, float> _durations = new();
    readonly Dictionary<uint, IReadOnlyList<VoiceBus>> _busChains = new();
    readonly object _durationLock = new();
    readonly Random _random;
    // Decoded Ogg: a bounded in-memory cache (least recently used out first).
    readonly Dictionary<uint, LinkedListNode<(uint Wem, byte[] Ogg)>> _ogg = new();
    readonly LinkedList<(uint Wem, byte[] Ogg)> _oggOrder = new();
    long _oggBytes;
    readonly object _oggLock = new();

    /// <summary>Most decoded bytes kept in memory (the disk cache holds the rest).</summary>
    public long MemoryBudget { get; init; } = 96L * 1024 * 1024;

    public WwiseLibrary Library => _lib;
    /// <summary>Packages asked for but not in the install.</summary>
    public IReadOnlyList<string> MissingPackages { get; }
    public IReadOnlyList<string> LoadedPackages { get; }
    public double LoadMilliseconds { get; init; }

    // Statistics (decode times in milliseconds).
    public int Decoded { get; private set; }
    public int DiskHits { get; private set; }
    public int MemoryHits { get; private set; }
    public int DecodeFailures { get; private set; }
    public double DecodeMillisecondsTotal { get; private set; }
    public double DecodeMillisecondsMax { get; private set; }

    public AudioCatalog(WwiseLibrary library, VorbisCodebookLibrary? codebooks, string? diskCache = null, int? seed = null,
        IReadOnlyList<string>? loaded = null, IReadOnlyList<string>? missing = null)
    {
        _lib = library;
        _codebooks = codebooks;
        _diskCache = diskCache;
        _random = seed is int s ? new Random(s) : new Random();
        LoadedPackages = loaded ?? library.Banks.Select(b => b.PackageName).Distinct().ToList();
        MissingPackages = missing ?? [];
    }

    /// <summary>The packages for a map: <see cref="BasePackages"/>, every <c>weapon_*.pck</c> in the install, then the map's region bank.</summary>
    public static IReadOnlyList<string> PackagesFor(string streamingAudioDir, string? map)
    {
        var list = new List<string>(BasePackages);
        if (Directory.Exists(streamingAudioDir))
            list.AddRange(Directory.EnumerateFiles(streamingAudioDir, "weapon_*.pck").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal));
        if (!string.IsNullOrEmpty(map) && map.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) list.Add(map.ToLowerInvariant());
        return list;
    }

    /// <summary>
    /// Opens the match's packages from the install (read-only) and the codebook library (downloaded into the private cache on
    /// first use; see <see cref="AudioCache"/>). <paramref name="diskCache"/>: where decoded Ogg files are kept (default
    /// <c>%LOCALAPPDATA%\sm-reimpl\cache\audio\ogg</c>; refused inside the install or <paramref name="repoRoot"/>).
    /// </summary>
    public static AudioCatalog Open(string gameDir, string? map, string? diskCache = null, string? repoRoot = null, TextWriter? log = null)
    {
        var sw = Stopwatch.StartNew();
        string dir = Path.Combine(gameDir, "streamingaudio");
        diskCache ??= Path.Combine(AudioCache.DecodedDir, "ogg", $"v{OggCacheVersion}");
        if (AudioCache.RefuseOutputDir(diskCache, gameDir, repoRoot) is { } refusal) throw new IOException($"Audio cache refused: {refusal}.");
        var codebooks = AudioCache.EnsureCodebooks(log: log);
        var names = PackagesFor(dir, map);
        var lib = WwiseLibrary.Open(dir, names, out var missing);
        return new AudioCatalog(lib, codebooks, diskCache, loaded: names.Except(missing).ToList(), missing: missing) { LoadMilliseconds = sw.Elapsed.TotalMilliseconds };
    }

    /// <summary>The packages the menus' sounds need (fidelity gap 9): the global banks and the one holding <c>Play_Menu_Move</c> / <c>_Select</c> / <c>_Back</c> (embedded media).</summary>
    public static readonly IReadOnlyList<string> UiPackages = ["init", "in_game_default"];

    /// <summary>
    /// Opens only <see cref="UiPackages"/> (the front end and customizer, before a match loads the full set); same cache and codebook rules as <see cref="Open"/>.
    /// </summary>
    public static AudioCatalog OpenUi(string gameDir, string? diskCache = null, string? repoRoot = null, TextWriter? log = null)
    {
        var sw = Stopwatch.StartNew();
        diskCache ??= Path.Combine(AudioCache.DecodedDir, "ogg", $"v{OggCacheVersion}");
        if (AudioCache.RefuseOutputDir(diskCache, gameDir, repoRoot) is { } refusal) throw new IOException($"Audio cache refused: {refusal}.");
        var codebooks = AudioCache.EnsureCodebooks(log: log);
        var lib = WwiseLibrary.Open(Path.Combine(gameDir, "streamingaudio"), UiPackages, out var missing);
        return new AudioCatalog(lib, codebooks, diskCache, loaded: UiPackages.Except(missing).ToList(), missing: missing) { LoadMilliseconds = sw.Elapsed.TotalMilliseconds };
    }

    /// <summary>
    /// Adds a package later (the map's region bank once the map is known); objects already loaded keep precedence. Call from
    /// the thread that posts; media reads on other threads wait for it. Returns false when the install has no such package.
    /// </summary>
    public bool AddPackage(string gameDir, string name)
    {
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        string path = Path.Combine(gameDir, "streamingaudio", name.ToLowerInvariant() + ".pck");
        if (!File.Exists(path)) return false;
        lock (_mediaLock)
        {
            _lib.AddPackage(path);
            // Caches filled before this bank loaded may have missed its objects (buses, attenuations).
            _busChains.Clear();
            _master = null;
            foreach (var none in _attenuations.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) _attenuations.Remove(none);
            foreach (var miss in _hasMedia.Where(kv => !kv.Value).Select(kv => kv.Key).ToList()) _hasMedia.Remove(miss);
        }
        return true;
    }

    public bool HasEvent(uint eventId) => _lib.Find(eventId) is HircEvent;
    public bool HasEvent(string name) => name.Length > 0 && HasEvent(Fnv.Hash32(name));

    /// <summary>
    /// The value an unset state group has: Wwise's "None" state (FNV-1 of <c>none</c>, the name the install's state switch cases carry). **Interpretation**
    /// (sound/impacts): before, an unset group fell to the container's default case; the confirm event <c>play_bolter_smack</c>'s kill layer (group
    /// <c>0x47e2171c</c>, cases none / alive / death_flesh / death_armored = the default) then played on the first hit of every session.
    /// </summary>
    public static readonly uint NoneState = Fnv.Hash32("none");

    /// <summary>The current value of a state group (set by SetState actions or <see cref="SetState"/>), or null.</summary>
    public uint? State(uint group) => _states.TryGetValue(group, out var v) ? v : null;
    public void SetState(uint group, uint state) => _states[group] = state;

    // ------------------------------------------------------------------ posting

    public SoundPlay Post(string eventName, SoundSwitches switches, ulong emitter = 0) => Post(Fnv.Hash32(eventName), switches, emitter);

    /// <summary>
    /// Resolves one post of an event for an emitter: picks the variants (random, sequence, switch values) and returns the
    /// voices to start, stops and continuations. Pure apart from the container histories and global states it updates.
    /// </summary>
    public SoundPlay Post(uint eventId, SoundSwitches switches, ulong emitter = 0)
    {
        if (_lib.Find(eventId) is not HircEvent ev) return new SoundPlay { EventId = eventId, Found = false };
        var play = new SoundPlay { EventId = eventId, Found = true };
        var draws = new Dictionary<uint, NodeDraw>();
        foreach (var actionId in ev.ActionIds)
        {
            if (_lib.Find(actionId) is not HircAction a) continue;
            float delay = Math.Max(0, a.Delay + Uniform(a.DelayMin, a.DelayMax)) / 1000f;
            switch (a.Kind)
            {
                case HircActionKind.Play:
                    Visit(a.TargetId, delay, switches, emitter, new List<uint>(), play, draws, 0);
                    break;
                case HircActionKind.Stop:
                    play.Stops.Add((a.TargetId, delay));
                    break;
                case HircActionKind.SetState when a.Parameters.Length >= 8:
                    _states[BinaryPrimitives.ReadUInt32LittleEndian(a.Parameters.Span)] = BinaryPrimitives.ReadUInt32LittleEndian(a.Parameters.Span[4..]);
                    break;
                case HircActionKind.SetSwitch when a.SwitchSetting is var (group, value):
                    switches.Set(group, value);                         // the emitter's own value, for its later posts
                    play.SwitchSets.Add((group, value));
                    break;
                case var kind when PropertyOf(kind) is { } property:
                    play.Properties.Add(new SoundPropertyAction(property, a.TargetId, IsReset(kind), a.Value, a.ValueMeaning == 2,
                        delay, a.TransitionMilliseconds / 1000f, (a.ActionType & 1) != 0));
                    break;
                default:
                    play.UnsupportedActions++;
                    break;
            }
        }
        return play;
    }

    /// <summary>The property a Set/Reset action kind changes, or null for other kinds.</summary>
    static SoundProperty? PropertyOf(HircActionKind kind) => kind switch
    {
        HircActionKind.SetVolume or HircActionKind.ResetVolume or HircActionKind.SetBusVolume or HircActionKind.ResetBusVolume => SoundProperty.Volume,
        HircActionKind.SetPitch or HircActionKind.ResetPitch => SoundProperty.Pitch,
        HircActionKind.SetLpf or HircActionKind.ResetLpf => SoundProperty.Lpf,
        HircActionKind.Mute or HircActionKind.Unmute => SoundProperty.Mute,
        _ => null,
    };

    static bool IsReset(HircActionKind kind) => kind is HircActionKind.ResetVolume or HircActionKind.ResetBusVolume or HircActionKind.ResetPitch
        or HircActionKind.ResetLpf or HircActionKind.Unmute;

    /// <summary>The speech bank's dialogue events (found by hashing the names): character + exertion type, and character + action type + state + threat.</summary>
    public static readonly uint SpeechExertionEvent = Fnv.Hash32("speech_exertion"), SpeechActionEvent = Fnv.Hash32("speech_action");

    /// <summary>Whether the speech bank is loaded (the dialogue event for exertions is there).</summary>
    public bool HasSpeech => _lib.Find(SpeechExertionEvent) is HircDialogueEvent;

    /// <summary>
    /// Resolves a character voice line (M33): the dialogue event of <paramref name="kind"/> walks its decision tree with the hashed
    /// values (character, then the exertion type, or the action type, state and threat; empty values and values the tree lacks fall
    /// to its "any" case), and the audio node it names plays like any container. <see cref="SoundPlay.Found"/> is false when the
    /// speech bank is not loaded; the play has no voices when the tree has no line for the values.
    /// </summary>
    public SoundPlay PostSpeech(SpeechKind kind, string character, string type, string state, string threat, SoundSwitches switches, ulong emitter = 0)
    {
        uint dialogue = kind == SpeechKind.Exertion ? SpeechExertionEvent : SpeechActionEvent;
        if (_lib.Find(dialogue) is not HircDialogueEvent d) return new SoundPlay { EventId = dialogue, Found = false };
        var play = new SoundPlay { EventId = dialogue, Found = true };
        uint[] values = kind == SpeechKind.Exertion
            ? [Fnv.Hash32(character), Fnv.Hash32(type)]
            : [Fnv.Hash32(character), Fnv.Hash32(type), Fnv.Hash32(state), Fnv.Hash32(threat)];
        if (d.Resolve(values) is uint node)
            Visit(node, 0f, switches, emitter, new List<uint>(), play, new Dictionary<uint, NodeDraw>(), 0);
        return play;
    }

    /// <summary>The next pick of a continuous container (<see cref="SoundRepeat"/>); its voices' delays count from now.</summary>
    public SoundPlay Continue(SoundRepeat repeat, SoundSwitches switches)
    {
        var play = new SoundPlay { EventId = 0, Found = true };
        if (_lib.Find(repeat.ContainerId) is not HircRandomSequence r) return play;
        var path = new List<uint>(repeat.Path.Take(Math.Max(0, repeat.Path.Count - 1)));
        PickContinuous(r, 0f, switches, repeat.Emitter, path, play, new Dictionary<uint, NodeDraw>(), 0, repeat.PlaysLeft);
        return play;
    }

    void Visit(uint id, float delay, SoundSwitches switches, ulong emitter, List<uint> path, SoundPlay play,
        Dictionary<uint, NodeDraw> draws, int depth)
    {
        if (depth >= MaxDepth || path.Contains(id) || play.Voices.Count >= MaxVoicesPerPost) return;
        var obj = _lib.Find(id);
        path.Add(id);
        try
        {
            switch (obj)
            {
                case HircSound s:
                    AddVoice(s, delay, path, play, draws, emitter);
                    break;
                case HircMusicSegment music:
                    AddMusic(music, delay, path, play, draws);
                    break;
                case HircLayer l:
                    foreach (var c in l.Children) Visit(c, delay, switches, emitter, path, play, draws, depth + 1);
                    break;
                case HircSwitch sw:
                    // A state group nobody has set is in Wwise's "None" state (sound/impacts): a container with a "none" case plays that case, not its default.
                    uint? value = sw.GroupType == 1 ? State(sw.GroupId) ?? NoneState : switches.Get(sw.GroupId);
                    foreach (var c in sw.NodesFor(value)) Visit(c, delay, switches, emitter, path, play, draws, depth + 1);
                    break;
                case HircRandomSequence r when r.IsContinuous:
                    PickContinuous(r, delay, switches, emitter, path, play, draws, depth, r.LoopCount == 0 ? -1 : Math.Max(0, r.LoopCount * Math.Max(1, r.Children.Count) - 1));
                    break;
                case HircRandomSequence r:
                    if (Pick(r, emitter) is uint child) Visit(child, delay, switches, emitter, path, play, draws, depth + 1);
                    break;
            }
        }
        finally { path.RemoveAt(path.Count - 1); }
    }

    /// <summary>
    /// A continuous container: plays one pick now and, unless the plays are used up, asks to be continued: after the
    /// transition time for trigger rate (mode 5), after the pick's length minus the transition for cross-fades (1, 2), plus
    /// it for a delay (3), else after the pick's length. Transition times are milliseconds (interpretation; ambience beds hold
    /// 4500 ± 3000).
    /// </summary>
    void PickContinuous(HircRandomSequence r, float delay, SoundSwitches switches, ulong emitter, List<uint> path, SoundPlay play,
        Dictionary<uint, NodeDraw> draws, int depth, int playsLeft)
    {
        if (Pick(r, emitter) is not uint child) return;
        path.Add(r.Id);
        int before = play.Voices.Count;
        try { Visit(child, delay, switches, emitter, path, play, draws, depth + 1); }
        finally { path.RemoveAt(path.Count - 1); }
        if (playsLeft == 0) return;
        float transition = Math.Max(0f, r.TransitionTime.Base + Uniform(r.TransitionTime.Min, r.TransitionTime.Max)) / 1000f;
        float length = 0f;
        bool endless = false;
        for (int i = before; i < play.Voices.Count; i++)
        {
            var v = play.Voices[i];
            if (v.LoopCount == 0) { endless = true; break; }
            float d = Duration(v.WemId) * Math.Max(1, (int)v.LoopCount) / Math.Max(0.01f, v.PitchScale);
            length = Math.Max(length, v.Delay - delay + d);
        }
        if (endless) return;
        // A cross-fade never starts the next pick before half of this one (our bound: a transition longer than the pick).
        float after = r.TransitionMode switch
        {
            5 => transition,
            1 or 2 => Math.Max(length - transition, length * 0.5f),
            3 => length + transition,
            _ => length,
        };
        // Nothing playable, or no length known (and no trigger rate): stop instead of spinning.
        if (r.TransitionMode != 5 && (play.Voices.Count == before || length <= 0.01f)) return;
        var repeatPath = new List<uint>(path) { r.Id };
        play.Repeats.Add(new SoundRepeat(r.Id, repeatPath, delay, Math.Max(MinContinueSeconds, after), playsLeft < 0 ? -1 : playsLeft - 1, emitter));
    }

    /// <summary>One node's random draws for one post (volume dB, pitch cents, LPF), shared by every voice of the post.</summary>
    readonly record struct NodeDraw(float Volume, float Pitch, float Lpf);

    sealed class ContainerState
    {
        public readonly List<int> Recent = new();
        public readonly List<int> Bag = new();
        public int Next;
    }

    /// <summary>
    /// The child a random/sequence container plays. Random: uniform (or by playlist weight when the container uses weights)
    /// over the children not among the last <c>min(avoidRepeat, n − 1)</c> picks; shuffle mode (random mode 1): a bag
    /// emptied without replacement, refilled when empty (not starting with the last pick). Sequence: the next child in order,
    /// from the first again after the last. Histories are per container, per emitter unless the container is global.
    /// </summary>
    /// <summary>
    /// Drops an emitter's container histories (avoid-repeat, shuffle bags, sequence positions); called when a one-shot emitter
    /// (an impact, a launch, a detonation) has no voice left, so the histories do not grow with every post.
    /// </summary>
    public void ForgetEmitter(ulong emitter)
    {
        if (emitter == 0) return;
        foreach (var key in _containers.Keys.Where(k => k.Emitter == emitter).ToList()) _containers.Remove(key);
    }

    /// <summary>Containers with a history (diagnostics, tests).</summary>
    public int ContainerHistories => _containers.Count;

    /// <summary>Forgets a decoded file that the player could not load (memory and disk), so the next request decodes it again.</summary>
    public void Discard(uint wemId)
    {
        lock (_oggLock)
            if (_ogg.Remove(wemId, out var node)) { _oggOrder.Remove(node); _oggBytes -= node.Value.Ogg.Length; }
        if (_diskCache is null) return;
        try { File.Delete(Path.Combine(_diskCache, $"{wemId}.ogg")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    uint? Pick(HircRandomSequence r, ulong emitter)
    {
        var children = r.Playlist.Count > 0 ? r.Playlist.Select(p => p.Id).ToList() : r.Children.ToList();
        if (children.Count == 0) return null;
        var key = (r.Id, r.IsGlobal ? 0UL : emitter);
        if (!_containers.TryGetValue(key, out var st)) _containers[key] = st = new ContainerState();
        int n = children.Count;
        int index;
        if (!r.IsRandom)
        {
            index = st.Next % n;
            st.Next = (index + 1) % n;
            return children[index];
        }
        int avoid = Math.Clamp((int)r.AvoidRepeatCount, 0, n - 1);
        if (r.RandomMode == 1)
        {
            bool fresh = st.Bag.Count == 0;
            if (fresh) st.Bag.AddRange(Enumerable.Range(0, n));
            // A new round does not start with the previous round's last pick.
            var choices = fresh && n > 1 && st.Recent.Count > 0 ? st.Bag.Where(i => i != st.Recent[^1]).ToList() : st.Bag;
            index = choices[_random.Next(choices.Count)];
            st.Bag.Remove(index);
        }
        else
        {
            var allowed = Enumerable.Range(0, n).Where(i => !st.Recent.TakeLast(avoid).Contains(i)).ToList();
            if (allowed.Count == 0) allowed = Enumerable.Range(0, n).ToList();
            if (r.IsUsingWeight && r.Playlist.Count == n)
            {
                double total = allowed.Sum(i => (double)r.Playlist[i].Weight);
                if (total > 0)
                {
                    double x = _random.NextDouble() * total;
                    index = allowed[^1];
                    foreach (int i in allowed)
                    {
                        x -= r.Playlist[i].Weight;
                        if (x < 0) { index = i; break; }
                    }
                }
                else index = allowed[_random.Next(allowed.Count)];
            }
            else index = allowed[_random.Next(allowed.Count)];
        }
        st.Recent.Add(index);
        if (st.Recent.Count > 16) st.Recent.RemoveAt(0);
        return children[index];
    }

    void AddVoice(HircSound s, float delay, List<uint> path, SoundPlay play, Dictionary<uint, NodeDraw> draws, ulong emitter)
        => AddVoice(s, s.SourceId, s.Id, s.IsVorbis, s.LoopCount, delay, path, play, draws);

    /// <summary>
    /// A music segment (match flow: the victory and defeat stingers): each of its tracks' sources plays once as a 2D voice with the
    /// segment's volume, bus and pitch (the track's own node base is not read; interpretation). Tempo, markers, transitions and the
    /// music state machine are not reproduced.
    /// </summary>
    void AddMusic(HircMusicSegment segment, float delay, List<uint> path, SoundPlay play, Dictionary<uint, NodeDraw> draws)
    {
        foreach (var trackId in segment.Children)
        {
            if (_lib.Find(trackId) is not HircMusicTrack track || play.Voices.Count >= MaxVoicesPerPost) continue;
            foreach (var source in track.Sources)
                AddVoice(segment, source.SourceId, track.Id, source.IsVorbis, 1, delay, path, play, draws);
        }
    }

    void AddVoice(HircNode start, uint sourceId, uint soundId, bool vorbis, short loopCount, float delay, List<uint> path, SoundPlay play, Dictionary<uint, NodeDraw> draws)
    {
        if (!vorbis) { play.NotVorbis++; return; }
        if (!HasMedia(sourceId)) { play.MissingMedia++; return; }
        float volume = 0f, pitch = 0f, lpf = 0f;
        uint bus = 0;
        bool is3D = false;
        WwiseAttenuation? attenuation = null;
        bool positioned = false;
        HircNode? node = start;
        var seen = new HashSet<uint>();
        var chain = new List<VoiceNode>();
        for (int depth = 0; node is not null && depth < MaxDepth && seen.Add(node.Id); depth++)
        {
            if (!draws.TryGetValue(node.Id, out var d))
                draws[node.Id] = d = new NodeDraw(node.Base.Volume.Base + Uniform(node.Base.Volume.Min, node.Base.Volume.Max),
                                                  node.Base.Pitch.Base + Uniform(node.Base.Pitch.Min, node.Base.Pitch.Max),
                                                  node.Base.Lpf.Base + Uniform(node.Base.Lpf.Min, node.Base.Lpf.Max));
            volume += d.Volume;
            pitch += d.Pitch;
            lpf += d.Lpf;
            chain.Add(new VoiceNode(node.Id, d.Volume, d.Pitch, d.Lpf, node.Base.Rtpcs)
            {
                Limit = node.Base.MaxInstances > 0 || node.Base.IgnoreParentMaxInstances
                    ? new InstanceLimit(node.Base.MaxInstances, node.Base.KillNewest, node.Base.GlobalLimit, node.Base.IgnoreParentMaxInstances) : null,
                BelowThreshold = node.Base.BelowThreshold,
            });
            if (bus == 0) bus = node.Base.OutputBusId;
            if (!positioned && node.Base.PositioningOverrideParent)
            {
                positioned = true;
                is3D = node.Base.Is3D;
                if (is3D && node.Base.AttenuationId != 0) attenuation = Attenuation(node.Base.AttenuationId);
            }
            node = node.Base.ParentId != 0 ? _lib.Find(node.Base.ParentId) as HircNode : null;
        }
        var buses = BusChain(bus);
        float busVolume = buses.Sum(b => b.Volume);
        play.Voices.Add(new SoundVoice(sourceId, soundId, delay, volume + busVolume, pitch, loopCount, is3D, attenuation, start.Base.Priority, path.ToArray())
            { BusVolumeDb = busVolume, LpfBase = Math.Clamp(lpf, 0f, 100f), Chain = chain, Buses = buses });
    }

    /// <summary>
    /// The volume (dB) of an output bus and its parents up to and including the master bus (0 for an unknown bus; a voice without
    /// an output bus goes to the master). **Interpretation** of the v56 bus layout (objects kept raw by the reader):
    /// <c>u32 parentBusId, f32 a, f32 b, ...</c> where <c>a</c> and <c>b</c> are two volume properties (never both non-zero on the
    /// install: 84 buses use <c>a</c>, 3 use <c>b</c> including the master, -7 dB; read as the voice volume and the bus volume,
    /// both gains), added. The earlier reading used <c>a</c> only and left the master out. See <see cref="BusChain"/>.
    /// </summary>
    public float BusVolume(uint busId) => BusChain(busId).Sum(b => b.Volume);

    /// <summary>
    /// The chain of output buses of a voice, output bus first up to the master, each with its volume (<see cref="BusVolume"/>).
    /// An empty list for an unknown bus ID; <paramref name="busId"/> 0 (no bus named up the voice's parents) means the master alone.
    /// </summary>
    public IReadOnlyList<VoiceBus> BusChain(uint busId)
    {
        if (_busChains.TryGetValue(busId, out var cached)) return cached;
        var list = new List<VoiceBus>();
        uint id = busId == 0 ? MasterBusId() : busId;
        var seen = new HashSet<uint>();
        for (int depth = 0; id != 0 && depth < MaxDepth && seen.Add(id); depth++)
        {
            if (_lib.Find(id) is not { Type: HircType.Bus } bus || bus.Payload.Length < 12) break;
            uint parent = BinaryPrimitives.ReadUInt32LittleEndian(bus.Payload.Span);
            float a = BinaryPrimitives.ReadSingleLittleEndian(bus.Payload.Span[4..]), b = BinaryPrimitives.ReadSingleLittleEndian(bus.Payload.Span[8..]);
            float volume = (float.IsFinite(a) && a is >= -96f and <= 24f ? a : 0f) + (float.IsFinite(b) && b is >= -96f and <= 24f ? b : 0f);
            list.Add(new VoiceBus(id, volume));
            id = parent;
        }
        // An unknown bus ID (not in any loaded bank) sends the voice to the master, like a voice with no bus; not cached, a later bank may define it.
        if (list.Count == 0 && busId != 0 && MasterBusId() is var master && master != 0 && master != busId) return BusChain(master);
        _busChains[busId] = list;
        return list;
    }

    uint? _master;

    /// <summary>The master bus: the bus object without a parent (the install has one), 0 when no bus is loaded.</summary>
    public uint MasterBusId()
    {
        if (_master is uint m) return m;
        uint found = 0;
        foreach (var o in _lib.Banks.SelectMany(b => b.Bank.Objects))
            if (o.Type == HircType.Bus && o.Payload.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(o.Payload.Span) == 0) { found = o.Id; break; }
        if (found != 0) _master = found;   // not cached while no bus is loaded: a later bank may bring it
        return found;
    }

    /// <summary>The master bus's own volume in dB (-7 on the install), 0 without one.</summary>
    public float MasterBusVolume
    {
        get
        {
            uint id = MasterBusId();
            return id == 0 ? 0f : BusChain(id).FirstOrDefault()?.Volume ?? 0f;
        }
    }

    /// <summary>An attenuation object by ID (parsed once), or null when no loaded bank has it or it does not parse.</summary>
    public WwiseAttenuation? Attenuation(uint id)
    {
        if (_attenuations.TryGetValue(id, out var a)) return a;
        a = _lib.Find(id) is { } o ? WwiseAttenuation.TryRead(o, out _) : null;
        _attenuations[id] = a;
        return a;
    }

    float Uniform(float min, float max) => min == 0 && max == 0 ? 0f : max <= min ? min : min + (float)_random.NextDouble() * (max - min);

    // ------------------------------------------------------------------ media

    /// <summary>Whether a loaded package holds the .wem (cached).</summary>
    public bool HasMedia(uint wemId)
    {
        lock (_mediaLock)
        {
            if (_hasMedia.TryGetValue(wemId, out bool has)) return has;
            has = _lib.FindMedia(wemId) is not null;
            _hasMedia[wemId] = has;
            return has;
        }
    }

    /// <summary>Every .wem an event can reach (all switch values), for prefetching.</summary>
    public IReadOnlyList<uint> WemsOf(uint eventId) => _lib.ResolveEvent(eventId).WemIds();
    public IReadOnlyList<uint> WemsOf(string eventName) => WemsOf(Fnv.Hash32(eventName));

    /// <summary>A .wem's length in seconds (from its header; 0 if unknown). Thread-safe.</summary>
    public float Duration(uint wemId)
    {
        lock (_durationLock) if (_durations.TryGetValue(wemId, out float d)) return d;
        float seconds = 0f;
        try
        {
            byte[]? bytes;
            lock (_mediaLock) bytes = _lib.ReadMedia(wemId);
            if (bytes is not null && WwiseVorbis.IsWwiseVorbis(bytes)) seconds = (float)WwiseVorbis.ReadInfo(bytes).Duration;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException) { }
        lock (_durationLock) _durations[wemId] = seconds;
        return seconds;
    }

    /// <summary>
    /// The .wem as Ogg Vorbis (memory cache, then the disk cache, else read from the package and converted), or null when it
    /// is missing or does not convert. Thread-safe. <paramref name="milliseconds"/>: the time this call took.
    /// </summary>
    public byte[]? Ogg(uint wemId, out double milliseconds)
    {
        var sw = Stopwatch.StartNew();
        lock (_oggLock)
            if (_ogg.TryGetValue(wemId, out var hit))
            {
                _oggOrder.Remove(hit);
                _oggOrder.AddFirst(hit);
                MemoryHits++;
                milliseconds = sw.Elapsed.TotalMilliseconds;
                return hit.Value.Ogg;
            }
        byte[]? ogg = null;
        string? file = _diskCache is null ? null : Path.Combine(_diskCache, $"{wemId}.ogg");
        bool fromDisk = false;
        if (file is not null && File.Exists(file))
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length > 58 && bytes.AsSpan().StartsWith("OggS"u8)) { ogg = bytes; fromDisk = true; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (ogg is null)
        {
            try
            {
                byte[]? wem;
                lock (_mediaLock) wem = _lib.ReadMedia(wemId);
                if (wem is not null && _codebooks is not null)
                {
                    var stream = WwiseVorbis.Convert(wem, _codebooks);
                    using var ms = new MemoryStream();
                    OggWriter.Write(stream, ms);
                    ogg = ms.ToArray();
                    if (file is not null)
                    {
                        try
                        {
                            Directory.CreateDirectory(_diskCache!);
                            string temp = file + $".{Environment.CurrentManagedThreadId}.part";
                            File.WriteAllBytes(temp, ogg);
                            File.Move(temp, file, overwrite: true);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException) { ogg = null; }
        }
        milliseconds = sw.Elapsed.TotalMilliseconds;
        lock (_oggLock)
        {
            if (ogg is null) { DecodeFailures++; return null; }
            if (fromDisk) DiskHits++;
            else
            {
                Decoded++;
                DecodeMillisecondsTotal += milliseconds;
                DecodeMillisecondsMax = Math.Max(DecodeMillisecondsMax, milliseconds);
            }
            if (!_ogg.ContainsKey(wemId))
            {
                _ogg[wemId] = _oggOrder.AddFirst((wemId, ogg));
                _oggBytes += ogg.Length;
                while (_oggBytes > MemoryBudget && _oggOrder.Last is { } old && old.Value.Wem != wemId)
                {
                    _oggOrder.RemoveLast();
                    _ogg.Remove(old.Value.Wem);
                    _oggBytes -= old.Value.Ogg.Length;
                }
            }
        }
        return ogg;
    }

    public void Dispose() => _lib.Dispose();
}
