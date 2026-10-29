using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Audio;
using SmFormats.Havok;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// M26b: the original sounds in the networked view. The catalogue (<see cref="AudioCatalog"/>: the MP Wwise banks of the install)
/// and the triggers (<see cref="SoundTriggers"/>: <c>post_sound_event</c> commands of the players' blueprints, the wargear and
/// projectiles, the hit containers, the rig's behaviour tags) load in the background; sounds start once both are ready and the
/// map is known (its region bank and <c>a_audio</c> emitters are added then). Triggers (data from the files; **when** they fire
/// is our reading of the simulation's state, labelled where it is an interpretation):
/// <list type="bullet">
/// <item>fire: own shots from the prediction (<see cref="GameClient.TakeOwnEvents"/>), others' ray and cone shots from the
/// snapshots' shot records, others' projectile shots when their projectile first shows (the snapshot does not say whose);</item>
/// <item>impacts: the weapon's hit container's sound with <c>material_type</c> = the surface (world) or <c>ceramite_armour</c>
/// (a player; interpretation), own impacts from a ray along the own aim (interpretation: players are not tested);</item>
/// <item>behaviour tags crossed by the clip a character shows (footsteps with the surface under the feet, melee swings,
/// evade rolls, ...): the <c>triggered_actions</c> of its blueprint and its weapons; reloads (own) at the reload clip's
/// tags spread over the reload time (interpretation);</item>
/// <item>state changes: spawn, death, weapon switch (deploy), charge start and end, throw, overheat, jump-pack launch, apex,
/// dive, landing (by drop height), falls, own armour break / regeneration / hits and health stages;</item>
/// <item>projectile trails and detonations, own hit confirmations, the map's emitters and trigger regions, reverb in its
/// environment regions (reverb settings provisional).</item>
/// </list>
/// The local player's sounds play with the switch group 0x0d500bea set to its own value (**interpretation**: the local-player
/// variant; see <see cref="AudioCatalog.PlayerGroup"/>). <c>--no-sound</c> switches all of it off.
/// M33: players provide the <c>speed</c> game parameter (the larger of this and the last frame's speed, m/s), and the characters speak:
/// the exertion lines the multiplayer files bind (<see cref="SpeechBinding"/>) at melee swings and charges and at the behaviour tags
/// that name them, resolved through the speech bank's dialogue events (<see cref="AudioCatalog.PostSpeech"/>).
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-sound</c>: no catalogue, no sounds.</summary>
    public bool NoSound { get; init; }

    /// <summary>
    /// <c>--map-sounds</c> (M38; test aid): plays the map's own sounds again. **Deliberate change from the original (owner's decision 2026-10-28)**: by default the map
    /// makes no sound (its emitters, the ambience bed, the spawn-room shield hum and the region events); only players do. The map's reverb regions still shape the players' sounds.
    /// </summary>
    public bool MapSounds { get; init; }

    /// <summary><c>--sound-gain &lt;dB&gt;</c>: added to every voice (ours; 0 by default). The data's master bus is -7 dB; <c>--sound-gain 7</c> undoes it.</summary>
    public float SoundGainDb { get; init; }

    Task<(AudioCatalog Catalog, SoundTriggers Triggers, double Ms)>? _soundLoad;
    AudioCatalog? _audio;
    SoundTriggers? _soundTriggers;
    SoundSystem? _sound;
    MapAudio? _mapAudio;
    string _soundNote = "not loaded";
    string? _soundMap;
    readonly Dictionary<int, SoundTrack> _soundTracks = new();
    readonly Dictionary<int, (SoundEmitter Emitter, IReadOnlyList<SoundBinding> Trail)> _projectileSounds = new();
    readonly Dictionary<int, Vector3> _projectilePositions = new();
    readonly HashSet<string> _regionsInside = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _soundKinds = new(StringComparer.Ordinal);
    /// <summary>Tags crossed that no file of the player names under <c>triggered_actions</c>.</summary>
    readonly Dictionary<string, int> _unboundTags = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tags a file names under <c>triggered_actions</c> whose default commands hold no sound (effects, rumble, area effects: as in the data).</summary>
    readonly Dictionary<string, int> _silentTags = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Map emitters whose event name is no event of the banks (Manufactorum: the layer's own strings "er", "rer").</summary>
    readonly Dictionary<string, int> _mapEmitterMisses = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Speech commands posted by trigger label (M33), for the log.</summary>
    readonly Dictionary<string, int> _barkKinds = new(StringComparer.Ordinal);
    readonly List<(double Due, Action Act)> _soundSchedule = new();
    SoundEmitter? _worldEmitter;
    double _soundLogAt, _lastOwnFireAt = double.NegativeInfinity, _regionCheckAt;

    sealed class SoundTrack
    {
        public PlayerState Prev;
        public bool Has;
        public string? Clip;
        public float Fraction;
        // M40-T4: tags of a blend layer fire on the crossing of the shared gait phase (SoundBlendTags), not on a clip's own time.
        public bool PhaseValid;
        public float PhasePrev, Cycles;
        public readonly Dictionary<string, float> TagFiredAt = new();   // tag name -> the cycle count when it last fired
        public readonly Dictionary<string, double> FootPostedAt = new();
        public readonly ShieldSoundTracker Shield = new();   // sound/shield-regen: the own shield's triggers   // foot -> connection time of its last post (FootstepLevel.AdmitFootfall)
        public required SoundEmitter Emitter;
        public float AirTime;
        public float TopZ;
        public double MaterialAt = double.NegativeInfinity;
        /// <summary>The ground pound's impact sound has played for this pound (M41: at the impact, not at the end of the recovery).</summary>
        public bool PoundSounded;
    }

    /// <summary>Starts loading the catalogue and the triggers on a worker thread (the behaviour graphs are read here, on the main thread).</summary>
    void SetUpSound()
    {
        if (NoSound || Lean) { _soundNote = Lean ? "not loaded (lean bot)" : "switched off (--no-sound)"; return; }   // perf/lean-bots
        if (_config is null || _assets is null) return;
        try
        {
            var graphs = HavokBehavior.Read(_assets.Model.Rig.Pack);
            var wargear = SoundTriggers.WargearNames(_config);
            bool orkClass = Enumerable.Range(0, _config.Classes.Count).Any(i => Races.Of(_config, i, Team.None) == Race.Orks);   // Ork O8: the Ork rig's clip tags too
            string gameDir = _gameDir;
            string repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
            _soundNote = "loading";
            _soundLoad = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var catalog = AudioCatalog.Open(gameDir, null, repoRoot: repo);
                var orkGraphs = orkClass ? OrkGraphsOrNull(gameDir) : null;   // read here, on the worker: its own rig object
                var triggers = SoundTriggers.Load(gameDir, wargear, SoundTriggers.PlayerBlueprints, graphs, orkGraphs);
                return (catalog, triggers, sw.Elapsed.TotalMilliseconds);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _soundNote = "not available: " + ex.Message;
            GD.Print("sound: " + _soundNote);
        }
    }

    /// <summary>Takes the finished load (once), adds the map's bank and emitters once the map is known.</summary>
    bool SoundReady()
    {
        if (_sound is null)
        {
            if (_soundLoad is null || !_soundLoad.IsCompleted) return false;
            var load = _soundLoad;
            _soundLoad = null;
            if (load.IsFaulted || load.IsCanceled)
            {
                _soundNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
                GD.PrintErr("sound: " + _soundNote);
                return false;
            }
            (_audio, _soundTriggers, double ms) = load.Result;
            _sound = new SoundSystem(_audio) { GlobalGainDb = SoundGainDb, Limits = SoundLimitsMode, FootstepGainDb = FootstepGainDbArg, RtpcCap = RtpcCapArg, LimitLog = LogSound("footstep") ? GD.Print : null };
            AddChild(_sound);
            _worldEmitter = new SoundEmitter { Label = "world" };
            _soundNote = $"{_audio.LoadedPackages.Count} packages, {_audio.Library.ObjectCount:N0} objects, {_soundTriggers.Files.Count} files, " +
                         $"{_soundTriggers.Files.Values.Sum(f => f.Bindings.Count)} commands, {_soundTriggers.ClipTags.Count} tagged clips; loaded in {ms:F0} ms";
            GD.Print("sound: " + _soundNote + (_audio.MissingPackages.Count > 0 ? $"; packages not in the install: {string.Join(", ", _audio.MissingPackages)}" : ""));
            foreach (var n in _soundTriggers.Notes) GD.Print("  sound note: " + n);
        }
        if (_soundMap is null && _client is { Status: ClientStatus.Connected, MapName.Length: > 0 } c)
        {
            _soundMap = c.MapName;
            SetUpMapSound(c.MapName);
        }
        return true;
    }

    void SetUpMapSound(string map)
    {
        if (_audio is null || _sound is null || _gameSpace is null) return;
        var sw = Stopwatch.StartNew();
        bool bank = map != "none" && MapSounds && _audio.AddPackage(_gameDir, map);
        int started = 0, missing = 0;
        try
        {
            if (map != "none")
            {
                var notes = new List<string>();
                _mapAudio = MapAudio.Load(_gameDir, map, notes);
                foreach (var n in notes) GD.Print("  sound note: " + n);
                foreach (var e in _mapAudio.Emitters)
                {
                    if (!MapSounds) break;   // M38: the map is silent unless --map-sounds
                    if (!_audio.HasEvent(e.Event)) { missing++; _mapEmitterMisses[e.Event] = _mapEmitterMisses.GetValueOrDefault(e.Event) + 1; continue; }
                    var emitter = new SoundEmitter { Position = ToGodot(e.Position), Label = e.Name };
                    _sound.Post(e.Event, emitter);
                    started++;
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or ArgumentException or NotSupportedException)
        {
            GD.Print($"sound: map audio of {map} not read: {ex.Message}");
        }
        PrefetchSounds();
        if (!MapSounds) GD.Print($"sound: map {map}: the map's own sounds are off (M38; --map-sounds plays its {_mapAudio?.Emitters.Count ?? 0} emitters and trigger regions); {_mapAudio?.Regions.Count ?? 0} regions kept for reverb");
        else GD.Print($"sound: map {map}: region bank {(bank ? "added" : "not found")}, {started} emitters started, {missing} emitters name events not in the banks, " +
                 $"{_mapAudio?.Regions.Count ?? 0} regions; {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Decodes ahead: the configuration's fire sounds, hit sounds, footsteps and the players' common sounds.</summary>
    void PrefetchSounds()
    {
        if (_audio is null || _soundTriggers is null || _sound is null || _config is null) return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var me = _client?.Predicted;
        var own = me is { } m ? new[] { Loadouts.InHand(_config, m)?.Name, Loadouts.Holstered(_config, m)?.Name } : [];
        foreach (var w in own.OfType<string>())
        {
            if (_soundTriggers.File(w) is { } f) foreach (var b in f.Fire(null)) names.Add(b.StartEvent);
            if (_soundTriggers.HitSound(w) is { } hit) names.Add(hit);
        }
        foreach (var f in SoundTriggers.PlayerBlueprints.Select(_soundTriggers.File).OfType<SoundFile>())
            foreach (var tag in new[] { "evt_left_foot", "evt_left_foot_rush" }) foreach (var b in f.Tag(tag)) names.Add(b.StartEvent);
        var wems = names.Where(n => n.Length > 0).SelectMany(_audio.WemsOf).Distinct().ToList();
        _sound.Prefetch(wems);
        GD.Print($"sound: prefetching {wems.Count} wems of {names.Count} events ({string.Join(", ", names.Take(8))})");
        PrefetchMatchSounds();   // the Ork's lines are decoded when the first Ork shows (SoundOrk)
    }

    Vector3 ToGodot(System.Numerics.Vector3 v) =>
        _gameSpace is null ? new Vector3(v.X, v.Z, -v.Y) : _gameSpace.GlobalTransform * new Vector3(v.X, v.Y, v.Z);

    void CountSound(string kind) => _soundKinds[kind] = _soundKinds.GetValueOrDefault(kind) + 1;

    /// <summary>Posts a binding's start event (unless it is for the local player only and this is not it) after its delay.</summary>
    SoundPlay? PostBinding(SoundBinding b, SoundEmitter e, bool own, string kind, float extraDelay = 0f)
    {
        if (_sound is null || b.StartEvent.Length == 0 || (b.OnlyIfOnPlayer && !own)) return null;
        var play = _sound.Post(b.StartEvent, e, extraDelay + b.DelaySeconds, footstep: b.IsFootstep, offset: new VoiceOffset(FootstepLevel.GainDb(b)));
        CountSound(kind);
        if (LogSoundKinds.Count > 0) LogBindingPost(b, e, kind, play);
        if (kind.StartsWith("armour", StringComparison.Ordinal) && LogSound("armour"))
            GD.Print($"ARMOUR POST t {_connectedFor:F3} {e.Label} {kind} [{b.Trigger}] event {b.StartEvent} stop '{b.StopEvent}' | {play?.Voices.Count ?? 0} voices, {play?.Stops.Count ?? 0} stops");
        return play;
    }

    void PostAll(IEnumerable<SoundBinding> bindings, SoundEmitter e, bool own, string kind, float extraDelay = 0f)
    {
        foreach (var b in bindings) PostBinding(b, e, own, kind, extraDelay);
    }

    // ------------------------------------------------------------------ per frame

    /// <summary>Called once per frame after the characters are placed.</summary>
    void UpdateSound(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        if (_client is null || _config is null || !SoundReady() || _sound is null || _soundTriggers is null) return;
        int ownId = _client.PlayerId;
        foreach (var (_, ev) in _frameOwnEvents) OwnSoundEvents(me, ev);   // drained once per frame in HipFireBegin (M27)
        foreach (var shot in _frameShots) SoundShot(shot);
        if (_local is not null) SoundPlayer(ownId, _local, me, own: true, (float)delta);
        foreach (var s in others)
            if (_remotes.TryGetValue(s.PlayerId, out var c)) SoundPlayer(s.PlayerId, c, s, own: false, (float)delta);
        ConnectUiSounds();
        SoundSeize(me);   // fidelity gap 9: the Seize Ground capture tick, objective noise and score tick
        foreach (var gone in _soundTracks.Keys.Where(id => id != ownId && !_remotes.ContainsKey(id)).ToList())
        {
            _sound.StopEmitter(_soundTracks[gone].Emitter);
            _soundTracks.Remove(gone);
        }
        SoundProjectiles(me);
        if (_connectedFor >= _regionCheckAt) { _regionCheckAt = _connectedFor + 0.25; SoundRegions(me); }
        for (int i = _soundSchedule.Count - 1; i >= 0; i--)
            if (_soundSchedule[i].Due <= _connectedFor) { var act = _soundSchedule[i].Act; _soundSchedule.RemoveAt(i); act(); }
        if (_bot && _connectedFor - _soundLogAt >= 10.0)
        {
            _soundLogAt = _connectedFor;
            GD.Print($"t {_connectedFor:F0} s: id {ownId} sound: {_sound.Summary()}; triggers {string.Join(", ", _soundKinds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}; " +
                     $"tags whose actions hold no sound (as in the data) {_silentTags.Count} ({Top(_silentTags)}); tags no file names {_unboundTags.Count} ({Top(_unboundTags)}); " +
                     $"map emitters naming no event: {(_mapEmitterMisses.Count == 0 ? "none" : string.Join(", ", _mapEmitterMisses.Keys))}; " +
                     $"speech commands: {(_barkKinds.Count == 0 ? "none" : string.Join(", ", _barkKinds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")))}");
            static string Top(Dictionary<string, int> d) => string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} x{kv.Value}"));
        }
    }

    SoundTrack Track(int id, bool own)
    {
        if (_soundTracks.TryGetValue(id, out var t)) return t;
        var emitter = new SoundEmitter { Own = own, Label = $"P{id}", Follow = () => PlayerSoundPoint(id) };
        if (own) emitter.Switches.Set(AudioCatalog.PlayerGroup, AudioCatalog.LocalPlayerValue);
        _soundTracks[id] = t = new SoundTrack { Emitter = emitter };
        return t;
    }

    /// <summary>Where a player's sounds come from: its character's position 1 m up (Godot space), or null when it is not shown.</summary>
    Vector3? PlayerSoundPoint(int id)
    {
        CharacterViewer? c = _client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);
        return c is not null && c.IsInsideTree() ? c.GlobalPosition + new Vector3(0, 1f, 0) : null;
    }

    /// <summary>The files a player's tags and actions are looked up in: its blueprint, the weapon in hand, its melee weapon, jump pack, evade, equipment.</summary>
    IEnumerable<SoundFile> PlayerFiles(PlayerState s)
    {
        if (_soundTriggers is null || _config is null) yield break;
        var names = new[]
        {
            BlueprintName(s), Loadouts.InHand(_config, s)?.Name, MeleeWargear(s),
            Loadouts.JumpPack(_config, s)?.Name, Loadouts.Evade(_config, s)?.Name, ItemOf(s)?.Name,   // perks/inert-2: remotes' item from the scoreboard
        };
        foreach (var n in names.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            if (_soundTriggers.File(n) is { } f) yield return f;
    }

    SoundFile? BlueprintFile(PlayerState s) => _soundTriggers?.File(BlueprintName(s));

    /// <summary>The player blueprint whose sounds a player makes: the Ork Boy's for an Ork (Ork O8), else its side's.</summary>
    string BlueprintName(PlayerState s) => IsOrkVoice(s) ? OrkSounds.Blueprint : s.Team == Team.Chaos ? GameplayTuning.ChaosBlueprint : GameplayTuning.MarineBlueprint;

    /// <summary>Sets the emitter's surface switch from the level under a player's feet (refreshed at most every 0.25 s).</summary>
    void SurfaceUnder(SoundTrack t, PlayerState s)
    {
        if (_connectedFor - t.MaterialAt < 0.25) return;
        t.MaterialAt = _connectedFor;
        string name = MaterialEffectTable.DefaultKey;
        if (_client?.Collision is { } world && world.Weapons.Raycast(s.Position + new System.Numerics.Vector3(0, 0, 0.5f), new System.Numerics.Vector3(0, 0, -1), 2.5f) is { } hit)
            name = Surfaces.NameOf(world.Weapons.MaterialOf(hit.Triangle));
        t.Emitter.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(name));
    }

    void SoundPlayer(int id, CharacterViewer character, PlayerState s, bool own, float delta)
    {
        var t = Track(id, own);
        if (!t.Has) { t.Prev = s; t.Has = true; return; }
        var p = t.Prev;
        // The speed game parameter: the larger of this and the last frame's speed, so a landing is heard at the speed it hit with (interpretation).
        t.Emitter.Parameters[VoiceMixer.Speed] = Math.Max(s.Velocity.Length(), p.Velocity.Length());
        var blueprint = BlueprintFile(s);
        // Spawn and death. A life's looping sounds end with it (sound/shield-regen: the broken-shield warning of a player who died with the shield down
        // pulsed every 0.7 s for the rest of the session).
        if (s.Alive != p.Alive && _sound is not null)
        {
            int ended = _sound.StopLoops(t.Emitter);
            if (ended > 0 && LogSound("armour")) GD.Print($"ARMOUR t {_connectedFor:F3} P{id} {(s.Alive ? "spawn" : "death")}: {ended} looping sound(s) of the last life ended");
        }
        if (s.Alive && !p.Alive) { PostAll(blueprint?.Of("lifetime_action") ?? [], t.Emitter, own, "spawn"); t.Clip = null; }
        if (!s.Alive && p.Alive)
        {
            var at = new SoundEmitter { Position = ToGodot(s.Position) + new Vector3(0, 1f, 0), Own = own, Switches = t.Emitter.Switches.Clone(), Label = $"P{id} death", OneShot = true };
            PostAll(blueprint?.Of("death_action") ?? [], at, own, "death");
        }
        if (own) SoundOwnShield(t, s, p);
        if (s.Alive && p.Alive)
        {
            // Weapon switch: the new weapon's deploy sound.
            if (s.Slot != p.Slot && s.Class == p.Class && Loadouts.InHand(_config!, s) is { } now && _soundTriggers!.File(now.Name) is { } wf)
                PostAll(wf.Of("on_deploy_actions"), t.Emitter, own, "switch");
            SoundCharge(t, s, p, own);
            SoundMeleeBarks(t, s, p);
            SoundJump(t, s, p, own, delta);
            if (own) SoundOwnState(t, s, p);
            // Others' throws: the toss sound at the release time (own throws come from the prediction's Threw event).
            // perks/inert-2: only a known thrown item (a stim's sound comes at its release, NetworkView.Stims.cs; an unknown remote item: none).
            if (!own && s.Throwing && !p.Throwing && ItemOf(s) is { Stim: null } item && _soundTriggers!.File(item.Name) is { } tf)
                PostAll(tf.Fire(null), t.Emitter, own, "throw", item.ReleaseAt(_config!));
        }
        if (s.Alive) SoundTags(t, character, s, own);
        if (IsOrkVoice(s)) SoundOrk(t, s, p, own);   // Ork O8: the boy's grunts, roars and yells for what the simulation shows
        else _orkEmitterIds.Remove(t.Emitter.Id);
        t.Prev = s;
    }

    /// <summary>The behaviour tags the character's clip crossed since the last frame → the files' <c>triggered_actions</c>.</summary>
    void SoundTags(SoundTrack t, CharacterViewer character, PlayerState s, bool own)
    {
        if (_soundTriggers is not null && character.BlendState(_blendTagClips, out float blendPhase))
        {
            SoundBlendTags(t, s, own, blendPhase);
            SoundLayerTags(t, character, s, own);   // the action clip over the upper body keeps its own tags (M40-T4 follow-up)
            return;
        }
        t.PhaseValid = false;
        if (_soundTriggers is null || character.ClipTime is not { } ct || ct.Duration <= 1e-3f) { t.Clip = null; return; }
        float f = Math.Clamp(ct.Time / ct.Duration, 0f, 1f);
        if (t.Clip is null) { t.Clip = ct.Clip; t.Fraction = f; return; }
        if (_soundTriggers.ClipTags.TryGetValue(ct.Clip, out var tags))
        {
            bool same = string.Equals(ct.Clip, t.Clip, StringComparison.OrdinalIgnoreCase);
            foreach (var (tag, at) in tags)
            {
                bool crossed = same
                    ? (f >= t.Fraction ? at > t.Fraction && at <= f : ct.Looping ? at > t.Fraction || at <= f : at <= f)
                    : (f < 0.25f ? at <= f : at > t.Fraction && at <= f);
                if (crossed) TagSound(tag, t, s, own);
            }
        }
        t.Clip = ct.Clip;
        t.Fraction = f;
    }

    /// <summary>Test aid <c>--log-footsteps</c>: prints every footstep tag that fires (with the clip and the gait phase), for the footstep-per-step check of the blend space (M40-T4).</summary>
    static readonly bool LogFootsteps = OS.GetCmdlineUserArgs().Contains("--log-footsteps");
    readonly List<CharacterViewer.BlendClipState> _blendTagClips = new();
    /// <summary>A blend layer's clips with at least this weight contribute their tags (the union, deduplicated by <see cref="BlendTagSpacing"/>).</summary>
    const float BlendTagWeight = 0.2f;
    /// <summary>A tag does not fire again within this many gait cycles of its last firing (the blended clips' versions of one footstep sit at slightly different phases).</summary>
    const float BlendTagSpacing = 0.35f;

    /// <summary>
    /// M40-T4: the tags of a marine in a blend layer (the relaxed, ready or sprint blend). The reported clip changes where two clips weigh the same, and a
    /// change of clip resets the clip-time logic above (a tag could fire twice or be dropped); here the shared gait phase (<see cref="GaitPhase"/>) is the clock:
    /// the tags of every clip with weight of at least <see cref="BlendTagWeight"/> are placed on the phase through their clip's sync points, fire when the phase
    /// crosses them, and not twice within <see cref="BlendTagSpacing"/> cycles.
    /// </summary>
    void SoundBlendTags(SoundTrack t, PlayerState s, bool own, float phase)
    {
        if (!t.PhaseValid) { t.PhaseValid = true; t.PhasePrev = phase; return; }
        float delta = GaitPhase.Wrap(phase - t.PhasePrev, 1f);
        if (delta > 0.5f) delta = 0f;   // the phase does not run backward: a jump of the layer, not a step
        float from = t.PhasePrev;
        t.PhasePrev = phase;
        t.Cycles += delta;
        if (delta <= 0f) return;
        for (int i = 0; i < _blendTagClips.Count; i++)
        {
            var c = _blendTagClips[i];
            if (c.Weight < BlendTagWeight || !_soundTriggers!.ClipTags.TryGetValue(c.Clip, out var tags)) continue;
            foreach (var (tag, at) in tags)
            {
                float ahead = GaitPhase.Wrap(GaitPhase.PhaseAt(c.Gait, at * c.Duration) - from, 1f);
                if (!(ahead > 0f && ahead <= delta)) continue;
                // One footfall, one post: the run's EVT_Left_Foot and the sprint's EVT_Left_Foot_Rush share a key (sound/footsteps-2: at a run-to-sprint change both blends fired the same foot 0.04 s apart).
                string key = FootstepLevel.SpacingKey(tag);
                if (t.TagFiredAt.TryGetValue(key, out float last) && t.Cycles - last < BlendTagSpacing) continue;
                t.TagFiredAt[key] = t.Cycles;
                if (LogFootsteps) GD.Print($"TAG P{s.PlayerId} t {_connectedFor:F3} {tag} via {c.Clip} phase {phase:F3} cycles {t.Cycles:F2}");
                TagSound(tag, t, s, own);
            }
        }
    }

    /// <summary>
    /// The action clip laid over the upper body (a melee attack's tail, a toss: <c>NetworkView.Handoff.cs</c>) while a blend layer has the legs: its tags (the files' toss
    /// and attack actions) fire from the clip's own time as they did when it was the whole-body pose, the clip and fraction carried across the hand-off in
    /// <see cref="SoundTrack.Clip"/> / <see cref="SoundTrack.Fraction"/>. No layer: those are reset.
    /// </summary>
    void SoundLayerTags(SoundTrack t, CharacterViewer character, PlayerState s, bool own)
    {
        if (character.PoseLayerState is not { } layer || layer.Duration <= 1e-3f) { t.Clip = null; return; }
        float f = Math.Clamp(layer.Time / layer.Duration, 0f, 1f);
        if (t.Clip is null) { t.Clip = layer.Clip; t.Fraction = f; return; }
        if (string.Equals(layer.Clip, t.Clip, StringComparison.OrdinalIgnoreCase) && _soundTriggers!.ClipTags.TryGetValue(layer.Clip, out var tags))
            foreach (var (tag, at) in tags)
                if (f >= t.Fraction ? at > t.Fraction && at <= f : at <= f) TagSound(tag, t, s, own);
        t.Clip = layer.Clip;
        t.Fraction = f;
    }

    void TagSound(string tag, SoundTrack t, PlayerState s, bool own)
    {
        // One footfall, one post, whichever clip (run blend, sprint clip) fires it (sound/footsteps-2).
        if (!FootstepLevel.AdmitFootfall(t.FootPostedAt, tag, _connectedFor))
        {
            if (LogFootsteps) GD.Print($"TAG P{s.PlayerId} t {_connectedFor:F3} {tag} dropped: the same foot posted under {FootstepLevel.SameFootMinSeconds:F2} s ago");
            return;
        }
        bool any = false;
        foreach (var file in PlayerFiles(s))
        {
            foreach (var b in file.Tag(tag))
            {
                if (b.StartEvent.Length > 0 && _recentPosts.Recent(t.Emitter.Id, b.StartEvent, _connectedFor, OrkSounds.DuplicateWindow)) continue;   // Ork O8: the swing just posted at the ability's start
                if (LogFootsteps && b.IsFootstep && !t.PhaseValid) GD.Print($"TAG P{s.PlayerId} t {_connectedFor:F3} {tag} via {t.Clip} (clip time)");
                if (b.UseTerrainMaterial || b.IsFootstep) SurfaceUnder(t, s);
                var play = PostBinding(b, t.Emitter, own, b.IsFootstep ? "footstep" : "tag");
                if (b.IsFootstep && LogSound("footstep")) LogFootstep(tag, b, t, s, own, play);
                any = true;
            }
            foreach (var b in file.SpeechTag(tag)) { Bark(b, t, s, "tag"); any = true; }
        }
        if (any) return;
        var tally = PlayerFiles(s).Any(f => f.TriggeredActions.Contains(tag)) ? _silentTags : _unboundTags;
        tally[tag] = tally.GetValueOrDefault(tag) + 1;
    }

    /// <summary>The speech character of a player: its class's <c>speech_arg_character</c> (what <c>autodetect</c> reads as), or null.</summary>
    string? SpeechCharacterOf(PlayerState s) =>
        _soundTriggers is not null && _config is not null && Loadouts.ClassOf(_config, s.Class) is { } k ? _soundTriggers.SpeechCharacter(k.Name) : null;

    /// <summary>Posts a speech command for a player (M33): the exertion or action line of its character; counted by trigger.</summary>
    void Bark(SpeechBinding b, SoundTrack t, PlayerState s, string kind)
    {
        if (_sound is null || SpeechCharacterOf(s) is not { } character) return;
        var result = _sound.PostSpeech(b, character, t.Emitter);
        if (LogSoundKinds.Count > 0) LogBark(b, t, s, kind, character, result);
        string key = $"{kind} {b.Type}" + (result == SpeechResult.Started ? "" : result == SpeechResult.Busy ? " (busy: dropped)" : " (no line)");
        _barkKinds[key] = _barkKinds.GetValueOrDefault(key) + 1;
        CountSound("bark");
    }

    /// <summary>
    /// A melee swing or the charge starts: the melee wargear's <c>start_audio</c> exertion of that ability (<c>ability_melee_x1</c>...
    /// for the combo step, <c>ability_melee_y1</c>... for a heavy strike, <c>ability_bullrush_01</c> for the charge; **interpretation**
    /// of which ability a step is: the sim keeps no ability names), else the file's first one.
    /// </summary>
    void SoundMeleeBarks(SoundTrack t, PlayerState s, PlayerState p)
    {
        if (_soundTriggers is null || MeleeWargear(s) is not { } wargear || _soundTriggers.File(wargear) is not { } file) return;
        bool swing = s.MeleeStep != 0 && (p.MeleeStep != s.MeleeStep || s.MeleeTime + 1e-4f < p.MeleeTime || p.MeleeHeavy != s.MeleeHeavy);
        if (swing)
        {
            string ability = (s.MeleeHeavy ? "ability_melee_y" : "ability_melee_x") + s.MeleeStep;
            var b = file.AbilityStart(ability) ?? file.Speech.FirstOrDefault(x => x.IsDefault && x.Kind == SpeechKind.Exertion && x.Trigger.Equals("start_audio", StringComparison.OrdinalIgnoreCase));
            if (b is not null) Bark(b, t, s, "melee");
        }
        if (s.Rush == RushPhase.Rush && p.Rush != RushPhase.Rush && file.AbilityStart("ability_bullrush_01") is { } rush) Bark(rush, t, s, "charge");
    }

    void SoundCharge(SoundTrack t, PlayerState s, PlayerState p, bool own)
    {
        if (s.Charging == p.Charging || Loadouts.InHand(_config!, s) is not { } weapon || _soundTriggers!.File(weapon.Name) is not { } wf) return;
        var charging = wf.Of("charging_action").Where(b => b.StartEvent.Length > 0).OrderBy(b => b.ChargeStage, StringComparer.Ordinal).Take(1).ToList();
        if (s.Charging) PostAll(charging, t.Emitter, own, "charge");
        else
        {
            // End of a charge (fired or not): the abort action's stop and the charge sound's own stop event.
            PostAll(wf.Of("charge_abort_action"), t.Emitter, own, "charge end");
            foreach (var b in charging.Where(b => b.StopEvent.Length > 0)) { _sound!.Post(b.StopEvent, t.Emitter); CountSound("charge end"); }
        }
    }

    void SoundJump(SoundTrack t, PlayerState s, PlayerState p, bool own, float delta)
    {
        bool wasUp = JumpPack.Jumping(p.Jump) || JumpPack.Pounding(p.Jump), up = JumpPack.Jumping(s.Jump) || JumpPack.Pounding(s.Jump);
        t.AirTime = s.Grounded ? 0f : t.AirTime + delta;
        if (!p.Grounded || up) t.TopZ = Math.Max(t.TopZ, s.Position.Z); else t.TopZ = s.Position.Z;
        var pack = Loadouts.JumpPack(_config!, s) is { } j ? _soundTriggers!.File(j.Name) : null;
        if (pack is not null)
        {
            if (JumpPack.Jumping(s.Jump) && !wasUp) { t.TopZ = s.Position.Z; PostAll(pack.Of("action_ascending"), t.Emitter, own, "jump launch"); }
            // M45: the own player's thrust ends at the release that cuts the rise (SmContent.ThrustEndRules), as its effect does; others' at the apex.
            if (SmContent.ThrustEndRules.EndsNow(JumpPack.Jumping(s.Jump), p.Velocity.Z, s.Velocity.Z, own && p.JumpCut, own && s.JumpCut))
                PostAll(pack.Of("action_apex"), t.Emitter, own, s.JumpCut ? "jump thrust cut" : "jump apex");
            if (JumpPack.Pounding(s.Jump) && !JumpPack.Pounding(p.Jump)) PostAll(pack.Of("action_descending_smash"), t.Emitter, own, "jump dive");
            // M41: the impact sound plays once per pound, when it reaches the ground (own: the dive turns into the recovery; others: the pound flag with the player on the ground), not at
            // the end of the recovery 0.36 s later; the end of the pound plays it only when the impact was never seen (a snapshot gap). The rule is SmContent.PoundSoundRules (tested).
            var (poundPost, poundSounded) = PoundSoundRules.Step(JumpPack.Pounding(s.Jump), s.Grounded, JumpPack.Pounding(p.Jump), p.Grounded, t.PoundSounded);
            t.PoundSounded = poundSounded;
            if (poundPost)
            {
                SurfaceUnder(t, s);
                // feel/jump-pack (ours): the sword slash lands with the 15 m landing's sound (the stand-in the dash attack's hit uses too), not the pound's smash and bark.
                if (s.JumpSlash) PostAll(LandingSounds(pack, float.MaxValue), t.Emitter, own, "slash land");
                else PoundImpactSound(pack, t, s, own);
            }
            if (wasUp && !up)
            {
                SurfaceUnder(t, s);
                if (!JumpPack.Pounding(p.Jump))
                {
                    // The landing entry for the drop from the apex: the largest distance_from_apex_to_land not above it.
                    PostAll(LandingSounds(pack, t.TopZ - s.Position.Z), t.Emitter, own, "jump land");
                }
                t.TopZ = s.Position.Z;
                return;
            }
        }
        // A fall without the pack: the blueprint's landing (after 0.4 s in the air; our threshold).
        if (!up && !wasUp && s.Grounded && !p.Grounded && t.AirTime == 0f && _fallTimes.GetValueOrDefault(s.PlayerId) > SmContent.FallRules.LandingFeelSeconds)   // the same threshold as the landing feel (NetworkView.Fall.cs); the sound counts from the first unsupported frame of any kind, the feel after the debounce and only for an eligible fall
        {
            SurfaceUnder(t, s);
            PostAll(BlueprintFile(s)?.Of("finish_falling_actions") ?? [], t.Emitter, own, "land");
        }
        _fallTimes[s.PlayerId] = s.Grounded ? 0f : _fallTimes.GetValueOrDefault(s.PlayerId) + delta;
    }

    /// <summary>The landing entry for a drop from the apex: the largest <c>distance_from_apex_to_land</c> not above it.</summary>
    static IEnumerable<SoundBinding> LandingSounds(SoundFile pack, float drop) =>
        pack.Of("action_land_regular_by_height")
            .GroupBy(b => b.Parameters.GetValueOrDefault("distance_from_apex_to_land"))
            .Where(g => g.Key <= drop).OrderByDescending(g => g.Key).FirstOrDefault() ?? Enumerable.Empty<SoundBinding>();

    /// <summary>The ground pound's impact: the <c>actn_gp_hit</c> tag's sound (<c>play_sm_jump_pack_smash</c>, trigger point self) and bark.</summary>
    void PoundImpactSound(SoundFile pack, SoundTrack t, PlayerState s, bool own)
    {
        PostAll(pack.Tag("actn_gp_hit"), t.Emitter, own, "ground pound");
        foreach (var b in pack.SpeechTag("actn_gp_hit")) Bark(b, t, s, "ground pound");
    }

    readonly Dictionary<int, float> _fallTimes = new();

    /// <summary>The local player's own state sounds: overheat, reload (at the reload clip's tags), armour and health stages.</summary>
    void SoundOwnState(SoundTrack t, PlayerState s, PlayerState p)
    {
        var c = _config!;
        var weapon = Loadouts.InHand(c, s);
        var wf = weapon is null ? null : _soundTriggers!.File(weapon.Name);
        if (wf is not null && s.Overheated && !p.Overheated && s.Slot == p.Slot) PostAll(wf.Of("overheat"), t.Emitter, true, "overheat");
        if (wf is not null && weapon is not null && s.ReloadLeft > 0f && p.ReloadLeft <= 0f && s.Slot == p.Slot)
        {
            float length = Math.Max(0.1f, weapon.ReloadTime);
            foreach (var (tag, at) in _soundTriggers!.ReloadTags(wf, IsOrkVoice(s)))
            {
                var file = wf;
                _soundSchedule.Add((_connectedFor + at * length, () =>
                {
                    if (_client is not { } cl || !cl.Predicted.Alive || cl.Predicted.ReloadLeft <= 0f) return;   // cancelled by a switch, melee or death
                    PostAll(file.Tag(tag), t.Emitter, true, "reload");
                }));
            }
        }
        var bp = BlueprintFile(s);
        if (bp is null) return;
        var body = Loadouts.Body(c, s);   // M43: the perks' maxima
        float before = p.Health / Math.Max(1f, body.HealthMax) * 100f, now = s.Health / Math.Max(1f, body.HealthMax) * 100f;
        foreach (var trigger in bp.Triggers)
        {
            if (!trigger.EndsWith("% health", StringComparison.Ordinal) || !float.TryParse(trigger.AsSpan(0, trigger.IndexOf('%')), System.Globalization.CultureInfo.InvariantCulture, out float level)) continue;
            if (before > level && now <= level) PostAll(bp.Of(trigger), t.Emitter, true, "health stage");
        }
        if (before <= 89f && now > 89f) PostAll(bp.Of("up_out_of_action"), t.Emitter, true, "health stage");
    }

    /// <summary>
    /// The own player's armour shield sounds (the blueprint's <c>armourshield_info</c> actions; the regen and its warning loop are the local player's only:
    /// <c>apply_only_if_on_player</c>, and <c>play_armor_break</c>'s warning has no case for other players). <see cref="ShieldSoundTracker"/> decides: a break, a hit,
    /// the start of a regeneration (once per regeneration, any level), an interrupted regeneration (the binding's stop event), the shield full again (M43: the
    /// perks' maximum, e.g. Iron Halo's 150).
    /// </summary>
    void SoundOwnShield(SoundTrack t, PlayerState s, PlayerState p)
    {
        if (_config is null || BlueprintFile(s) is not { } bp) return;
        var events = t.Shield.Step(p.Shield, s.Shield, Loadouts.Body(_config, s).ShieldMax, p.Alive, s.Alive);
        if (LogSound("armour") && (events != ShieldSoundEvents.None || MathF.Abs(s.Shield - p.Shield) > 1e-4f))
            GD.Print($"ARMOUR t {_connectedFor:F3} P{s.PlayerId} shield {p.Shield:F2} -> {s.Shield:F2} since damage {s.SinceDamage:F2} regenerating {t.Shield.Regenerating} events {events}");
        if (events.HasFlag(ShieldSoundEvents.Broken)) PostAll(bp.Of("armourshield_broken"), t.Emitter, true, "armour break");
        if (events.HasFlag(ShieldSoundEvents.Hit)) PostAll(bp.Of("armourshield_hit"), t.Emitter, true, "armour hit");
        if (events.HasFlag(ShieldSoundEvents.RegenInterrupted) && _sound is not null)
            foreach (var b in bp.Of("armourshield_regen").Where(b => b.StopEvent.Length > 0)) _sound.Post(b.StopEvent, t.Emitter);
        if (events.HasFlag(ShieldSoundEvents.RegenStart)) PostAll(bp.Of("armourshield_regen"), t.Emitter, true, "armour regen");
        if (events.HasFlag(ShieldSoundEvents.Restored)) PostAll(bp.Of("armourshield_restored"), t.Emitter, true, "armour restored");
    }

    // ------------------------------------------------------------------ shots, impacts, projectiles

    /// <summary>The local player's predicted events: shots (fire and an impact along the aim), throws.</summary>
    void OwnSoundEvents(PlayerState me, SelfEvents ev)
    {
        if (_config is null || _soundTriggers is null || _client is null) return;
        var t = Track(_client.PlayerId, own: true);
        if (ev.Fired && Loadouts.InHand(_config, me) is { } weapon)
        {
            int index = Loadouts.WeaponIndex(_config, me, me.Slot);
            _lastOwnFireAt = _connectedFor;
            PostFire(index, ev.ShotStage, t.Emitter, own: true, braced: me.Brace == BracePhase.Braced);   // fidelity gap 7: the braced shot's sound
            // The impact of an own hit-scan shot: where the shot goes (the level, or since M38 a player's body, as the effects: TraceOwn), armour or flesh by what is known of the victim.
            if (weapon.Delivery == DeliveryKind.Raycast && _soundTriggers.HitSound(weapon.Name) is { } hitSound)
            {
                var (end, hit, surface, _, _, victim) = TraceOwn(me, weapon);
                if (hit != ShotHit.None)
                {
                    var at = new SoundEmitter { Position = ToGodot(end), Label = $"impact of P{_client.PlayerId}", OneShot = true };
                    var offset = ImpactSounds.ImpactOffset(ImpactTreatmentArg, hitSound, hitScan: weapon.Delivery == DeliveryKind.Raycast);   // sound/impacts: the owner's deeper, muddier impacts
                    if (hit == ShotHit.World) { at.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(Surfaces.NameOf(surface))); PostLevelImpact(hitSound, at, offset); }
                    else { PostPlayerHitSound(hitSound, at, offset); OrkShotHit(victim, hit, index); }   // M38: armour and flesh layers together; Ork O8: a remote Ork grunts
                    CountSound(hit == ShotHit.World ? "impact" : "body hit");
                }
            }
        }
        if (ev.Threw && Loadouts.Equipment(_config, me) is { } item && _soundTriggers.File(item.Name) is { } f) PostAll(f.Fire(null), t.Emitter, true, "throw");
    }

    void PostFire(int weaponIndex, int stage, SoundEmitter e, bool own, bool braced = false)
    {
        if (_config is null || _soundTriggers is null || weaponIndex < 0 || weaponIndex >= _config.Weapons.Count) return;
        var spec = _config.Weapons[weaponIndex];
        if (_soundTriggers.File(spec.Name) is not { } file) return;
        string? stageName = stage > 0 && spec.Charge is { } ch && stage - 1 < ch.Stages.Count ? ch.Stages[stage - 1].Name : null;
        // Fidelity gap 7: a braced shot posts the braced ability's fire sound (NetworkView.Brace.cs).
        var bindings = braced && stage == 0 && BracedFireSounds(spec.Name) is { } braceFire ? braceFire : file.Fire(stageName);
        PostAll(bindings, e, own, own ? "fire own" : "fire other");
    }

    /// <summary>Another player's ray or cone shot (snapshot shot record): fire at the shooter, impact at the end point.</summary>
    void SoundShot(ShotView shot)
    {
        if (_config is null || _soundTriggers is null || _client is null || shot.Shooter == _client.PlayerId) return;
        var t = Track(shot.Shooter, own: false);
        PostFire(shot.Weapon, shot.Stage, t.Emitter, own: false, braced: BracedNow(shot.Shooter));
        if (shot.Hit == ShotHit.None || shot.Weapon < 0 || shot.Weapon >= _config.Weapons.Count) return;
        if (_soundTriggers.HitSound(_config.Weapons[shot.Weapon].Name) is not { } hitSound) return;
        if (shot.Hit is ShotHit.Body or ShotHit.Head) OrkShotHit(VictimAt(shot.End, shot.Shooter), shot.Hit, shot.Weapon);   // Ork O8: a remote Ork grunts at a hit
        var at = new SoundEmitter { Position = ToGodot(shot.End), Label = $"impact of P{shot.Shooter}", OneShot = true };
        // sound/impacts: the owner's deeper, muddier impacts; a cone shot (the melta) is not a ballistic one.
        var offset = ImpactSounds.ImpactOffset(ImpactTreatmentArg, hitSound, hitScan: _config.Weapons[shot.Weapon].Delivery == DeliveryKind.Raycast);
        // A world hit by its surface; a player hit plays the armour impact and the flesh layer together (M38, owner's direction: PostPlayerHitSound).
        if (shot.Hit == ShotHit.World) { at.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(Surfaces.NameOf(shot.Surface))); PostLevelImpact(hitSound, at, offset); }
        else PostPlayerHitSound(hitSound, at, offset);
        CountSound(shot.Hit == ShotHit.World ? "impact" : "body hit");
    }

    /// <summary>Own hit reports (from <see cref="UpdateHits"/>): the weapon's hit confirmation, a melee hit.</summary>
    void SoundHits(HitReport hits)
    {
        if (_client is null || _config is null || _soundTriggers is null || _sound is null) return;
        var me = _client.Predicted;
        var t = Track(_client.PlayerId, own: true);
        if (hits.LastMelee)
        {
            if (MeleeWargear(me) is { } m && _soundTriggers.File(m) is { } mf) PostAll(mf.Of("hit_audio").Take(1), t.Emitter, true, "melee hit");
        }
        else if (!hits.LastSplash && Loadouts.InHand(_config, me) is { } w && _soundTriggers.File(w.Name) is { } wf && wf.Of("player_hit_something_action").FirstOrDefault() is { } confirm)
            HitConfirmSound(confirm, hits);
    }

    readonly HitConfirmGate _hitConfirmGate = new();

    /// <summary>
    /// The shooter's hit confirmation for a ranged hit on a player (sound/impacts). The data binds the weapon's <c>player_hit_something_action</c> (bolters:
    /// <c>play_bolter_smack</c>, trigger point self): a non-positioned smack layer, a layer switched on <c>material_type</c> (the victim's: ceramite_armour or flesh; before this
    /// branch the shooter's own emitter gave it the floor under the shooter's feet, so a hit while standing on metal played a metal ricochet at +250 cents "on top of" the
    /// shooter) and a kill layer on the death state. <see cref="HitConfirmMode.Data"/> plays that with the victim's material; <see cref="HitConfirmMode.Soft"/> (the owner's
    /// change, default) plays only the smack layer (no material) pitched down, low-passed and 10 dB quieter, once per <see cref="ImpactSounds.HitConfirmCooldown"/>, and on a
    /// kill the data's kill layer as well (the death state set as the data's own <c>play_set_state_death_*</c> events do).
    /// </summary>
    void HitConfirmSound(SoundBinding confirm, HitReport hits)
    {
        if (_client is null || _audio is null || _sound is null || HitConfirmArg == HitConfirmMode.Off || confirm.StartEvent.Length == 0) return;
        bool kill = hits.LastKilled || hits.Kills > 0, armoured = hits.LastArmoured;
        int ownId = _client.PlayerId;
        var at = new SoundEmitter { Own = true, Follow = () => PlayerSoundPoint(ownId), Label = "hit confirm", OneShot = true };
        at.Switches.Set(AudioCatalog.PlayerGroup, AudioCatalog.LocalPlayerValue);
        var offset = VoiceOffset.None;
        // Data mode: the data's event as it is (no cooldown; the kill layer plays only when the data itself set a death state, i.e. the Ork Boy's death_action).
        if (HitConfirmArg == HitConfirmMode.Data) at.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(ImpactSounds.ConfirmMaterial(armoured)));
        else
        {
            if (!_hitConfirmGate.Admit(_connectedFor, kill)) { if (LogSound("hitconfirm")) GD.Print($"HIT CONFIRM t {_connectedFor:F3} held back (cooldown {ImpactSounds.HitConfirmCooldown:F2} s)"); return; }
            offset = ImpactSounds.HitConfirmOffset(armoured, kill);
        }
        // The death state (group 0x47e2171c) is global: set for this post only and put back after it (review fix), so no smack event without the reset and no Ork death leaves a death_* state behind.
        uint? stateBefore = _audio.State(ImpactSounds.DeathStateGroup);
        _audio.SetState(ImpactSounds.DeathStateGroup, ImpactSounds.StateBeforeConfirm(HitConfirmArg, stateBefore, kill, armoured));
        var play = _sound.Post(confirm.StartEvent, at, offset: offset);
        _audio.SetState(ImpactSounds.DeathStateGroup, ImpactSounds.StateAfterConfirm(HitConfirmArg, stateBefore, _audio.State(ImpactSounds.DeathStateGroup)));
        CountSound("hit confirm");
        if (LogSound("hitconfirm"))
            GD.Print($"HIT CONFIRM t {_connectedFor:F3} mode {HitConfirmArg} event {confirm.StartEvent} victim P{hits.LastVictim} {(armoured ? "armour" : "flesh")}{(kill ? " kill" : "")} offset {offset} | {VoiceMixText(play, at, offset)}");
    }

    string? ProjectileFile(int weapon, int stage, bool thrown)
    {
        if (_config is null) return null;
        var spec = Shots.ProjectileOf(_config, weapon, stage, thrown);
        return spec?.Blueprint is { Length: > 0 } bp ? bp.Split('\\', '/')[^1] : null;
    }

    /// <summary>Projectiles: others' launches (fire sound where the projectile first shows), trails while they fly.</summary>
    void SoundProjectiles(PlayerState me)
    {
        if (_client is null || _soundTriggers is null || _sound is null || _config is null) return;
        var live = new HashSet<int>();
        foreach (var q in _client.Projectiles())
        {
            live.Add(q.Id);
            var pos = ToGodot(q.Position);
            _projectilePositions[q.Id] = pos;
            if (_projectileSounds.ContainsKey(q.Id)) continue;
            int id = q.Id;
            var emitter = new SoundEmitter { Position = pos, Follow = () => _projectilePositions.TryGetValue(id, out var x) ? x : null, Label = $"projectile {id}" };
            var file = ProjectileFile(q.Weapon, q.Stage, q.Thrown) is { } name ? _soundTriggers.File(name) : null;
            var trail = file?.Of("lifetime_action") ?? [];
            _projectileSounds[q.Id] = (emitter, trail);
            PostAll(trail, emitter, false, "trail");
            // Someone else's projectile shot: its fire sound from where it first shows (not when it is near the own player
            // just after an own shot: the prediction already played that one).
            bool mine = _connectedFor - _lastOwnFireAt < 0.5 && System.Numerics.Vector3.Distance(q.Position, Combat.EyePosition(me, _config)) < 4f;
            if (!q.Thrown && !mine)
                PostFire(q.Weapon, q.Stage, new SoundEmitter { Position = pos, Label = "launch", OneShot = true }, own: false);
        }
        foreach (var id in _projectileSounds.Keys.Where(id => !live.Contains(id)).ToList())
        {
            var (emitter, trail) = _projectileSounds[id];
            foreach (var b in trail.Where(b => b.StopEvent.Length > 0)) _sound.Post(b.StopEvent, emitter);
            // Loops the stop event did not name end with the projectile; one-shots finish.
            var e = emitter;
            _soundSchedule.Add((_connectedFor + 0.05, () => { if (_sound is { } snd) snd.StopEmitter(e); }));
            _projectileSounds.Remove(id);
            _projectilePositions.Remove(id);
        }
    }

    /// <summary>A detonation (from <see cref="UpdateProjectiles"/>): the projectile blueprint's detonate sounds and its impact container's.</summary>
    void SoundDetonation(ProjectileView d)
    {
        if (_soundTriggers is null || _sound is null) return;
        var name = ProjectileFile(d.Weapon, d.Stage, d.Thrown);
        var file = name is null ? null : _soundTriggers.File(name);
        var at = new SoundEmitter { Position = ToGodot(d.Position), Label = "detonation", OneShot = true };
        string surface = MaterialEffectTable.DefaultKey;
        if (_client?.Collision is { } world && world.Weapons.Raycast(d.Position + new System.Numerics.Vector3(0, 0, 0.5f), new System.Numerics.Vector3(0, 0, -1), 3f) is { } hit)
            surface = Surfaces.NameOf(world.Weapons.MaterialOf(hit.Triangle));
        at.Switches.Set(AudioCatalog.MaterialType, Fnv.Hash32(surface));
        // M38: a detonation is a level impact: the armour impact state (no flesh layer) for all of its posts, whatever the own armour events left behind; restored afterwards.
        var stateBefore = BeginImpactState(flesh: false);
        if (file is not null) PostAll(file.Of("detonate_action"), at, false, "detonation");
        if (name is not null && _soundTriggers.HitSound(name) is { } hitSound && file?.Of("detonate_action").Any(b => b.StartEvent == hitSound) != true)
        {
            _sound.Post(hitSound, at);
            CountSound("detonation impact");
        }
        EndImpactState(stateBefore);
    }

    /// <summary>The map's trigger regions (enter / exit events for the local player) and reverb regions (provisional reverb).</summary>
    void SoundRegions(PlayerState me)
    {
        if (_mapAudio is null || _sound is null || _client is null) return;
        var t = Track(_client.PlayerId, own: true);
        float reverb = 0f, room = 0.5f;
        foreach (var r in _mapAudio.Regions)
        {
            bool environment = r.Kind.Equals("AudioEnvironmentRegion", StringComparison.OrdinalIgnoreCase);
            if (!MapSounds && !environment) continue;   // M38: trigger regions only move the silent bed
            bool inside = r.Contains(me.Position + new System.Numerics.Vector3(0, 0, 1f));
            if (environment)
            {
                if (inside && r.EnvironmentAmount > reverb)
                {
                    reverb = r.EnvironmentAmount;
                    room = r.Environment.Contains("large", StringComparison.OrdinalIgnoreCase) ? 0.8f : 0.5f;
                }
                continue;
            }
            string key = r.Name + "@" + r.Position;
            if (inside && _regionsInside.Add(key)) { _sound.Post(r.EnterEvent, t.Emitter); CountSound("region enter"); }
            else if (!inside && _regionsInside.Remove(key)) { _sound.Post(r.ExitEvent, t.Emitter); CountSound("region exit"); }
        }
        _sound.SetReverb(reverb, room);
    }

    /// <summary>The F1 line about the sounds.</summary>
    string SoundInfoLine() =>
        $"Sound (M26b; original banks, triggers from the files; mixing provisional): {_soundNote}" +
        (_sound is null ? "" : $"; posts {_sound.Posts}, voices {_sound.VoicesStarted}, active {_sound.ActiveCount}");
}
