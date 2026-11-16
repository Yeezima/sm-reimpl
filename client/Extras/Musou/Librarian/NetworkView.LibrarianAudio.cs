using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SmNet.Extras.Musou;

namespace SmClient;

/// <summary>
/// The Librarian's chain lightning heard (librarian-lightning, owner 2026-11-10: "improve the chain lightning visual and audio effects"; his answer to the download question: none, "build the lightning
/// sound from the game's own audio, read from the owner's install at runtime ... plus procedural synthesis where it helps"). Three kinds of layer, nothing stored:
/// <list type="bullet">
/// <item><b>The install's events</b>, posted through the sound system as before (the data's mix, curves and limits): the power conduit's crackle loop while the lightning is held and its discharge at the
/// release, the psyker bolt's hit per newly struck target (budgeted), the reactive blast for the Shockwave, the overheat vent at the lock (<see cref="LibrarianSounds"/>).</item>
/// <item><b>The install's audio, decoded at run time</b> and played on this client's own players (<see cref="InstallLayers"/>; found by name in the shared bank, docs/discovery.md "Librarian lightning
/// sounds"): the campaign's lightning strike (<c>play_capacitor_lightning</c>, the recordings the chaos portal and tower lightning share) with the tainted psyker's spell cast at the release; the Weirdboy's
/// tiny arc crackles (<c>play_weird_boy_teleport_small_arcs</c>) retriggered at random over the hands while the lightning is held; his large arcs (<c>play_weird_boy_large_arcs</c>) at each newly struck
/// target; the chaos whip's lightning at the release's end; his staff's shockwave and the thunder hammer's displacement under the Shockwave.</item>
/// <item><b>Synthesized layers</b> (<see cref="LibrarianSynth"/>, generated once from fixed seeds) for what the install lacks: a low thump under the strike, a seamless arc sizzle and hum under the held
/// crackle, a soft snap under the large arcs, a thunder roll at the release's end, a thunderclap under the Shockwave.</item>
/// </list>
/// The decoded and synthesized layers play on their own few players, outside the data's voice pool (a full battle never steals them), on the sound bus with the effects volume; the own Librarian's are
/// not positioned, everyone else's are. Pitch varies a little per play. Both are prepared on a worker the first time a Librarian is seen; until then (and with <c>--old-librarian-sound</c>) only the
/// install's events play, as in pass 2. Levels are ours, waiting for the owner's ear.
/// </summary>
public partial class NetworkView
{
    /// <summary>The install events whose audio is decoded at run time and played on the client's own players.</summary>
    public const string StrikeEvent = "play_capacitor_lightning", SpellEvent = "play_tainted_psyker_spell_cast", CrackleEvent = "play_weird_boy_teleport_small_arcs",
        ArcEvent = "play_weird_boy_large_arcs", WhipEvent = "play_chaos_whip_lightning", StaffShockEvent = "play_weird_boy_staff_hit_shockwave", HammerEvent = "play_sm_thunderhammer_displace";
    static readonly string[] InstallLayers = [StrikeEvent, SpellEvent, CrackleEvent, ArcEvent, WhipEvent, StaffShockEvent, HammerEvent];
    /// <summary>Each layer's level (dB over the recordings' and the synthesized layers' own peaks, plus the bus and the effects volume; ours, waiting for the owner's ear).</summary>
    const float StrikeDb = -2f, SpellDb = -9f, ThumpDb = -6f, CrackleDb = -9f, SizzleDb = -15f, ArcDb = -6f, SnapDb = -14f, WhipDb = -7f, ThunderDb = -9f, StaffDb = -3f, HammerDb = -5f, BoomDb = -4f;
    /// <summary>The held crackle's retrigger gap (s, random in the range; ours).</summary>
    const float CrackleMin = 0.04f, CrackleMax = 0.12f;
    /// <summary>The 3D layers' full-volume radius and reach (m, ours: the lightning is heard across a battle).</summary>
    const float LayerUnitSize = 12f, LayerMaxDistance = 140f;
    const int SnapPlayers = 6, SnapVariants = 4;

    static readonly bool OldLibrarianSound = OS.GetCmdlineUserArgs().Contains("--old-librarian-sound");
    readonly Dictionary<ushort, LibrarianVoice> _librarianVoices = new();
    readonly AudioStreamPlayer3D?[] _snapPlayers = new AudioStreamPlayer3D?[SnapPlayers];
    int _snapNext;
    readonly Random _librarianPitch = new(23);
    Task<LibrarianLayerData>? _librarianPrep;
    LibrarianLayerStreams? _librarianStreams;

    /// <summary>What the worker makes: each synthesized layer as WAV bytes, each install layer's decoded Ogg files.</summary>
    sealed class LibrarianLayerData
    {
        public byte[][] Cracks = [], Snaps = [], Thunders = [];
        public byte[] Sizzle = [], Boom = [];
        public readonly Dictionary<string, List<byte[]>> Install = new(StringComparer.Ordinal);
        public double Milliseconds;
    }

    /// <summary>The streams, made on the main thread from <see cref="LibrarianLayerData"/>.</summary>
    sealed class LibrarianLayerStreams
    {
        public AudioStreamWav[] Cracks = [], Snaps = [], Thunders = [];
        public AudioStreamWav? Sizzle, Boom;
        public readonly Dictionary<string, AudioStream[]> Install = new(StringComparer.Ordinal);
    }

    /// <summary>One Librarian's players: the sizzle loop (followed every frame, faded), the crackle's two, and three for one-shots at him.</summary>
    sealed class LibrarianVoice
    {
        public Node? Loop, Shot, Deep, Spell, CrackA, CrackB;   // AudioStreamPlayer (own) or AudioStreamPlayer3D
        public float LoopLevel, LoopWant;
        public double NextCrackle;
        public bool Held;
    }

    /// <summary>Whether the decoded and synthesized layers are ready and wanted (else only the install's events play).</summary>
    bool LibrarianLayers => !OldLibrarianSound && LibrarianStreams() is not null;

    /// <summary>The layers once the worker has made them (it is started on the first call); null until then.</summary>
    LibrarianLayerStreams? LibrarianStreams()
    {
        if (_librarianStreams is not null || OldLibrarianSound) return _librarianStreams;
        if (_librarianPrep is null)
        {
            var catalog = _audio;
            _librarianPrep = Task.Run(() => PrepareLibrarianLayers(catalog));
            return null;
        }
        if (!_librarianPrep.IsCompleted) return null;
        var s = new LibrarianLayerStreams();
        if (_librarianPrep.IsCompletedSuccessfully)
        {
            var d = _librarianPrep.Result;
            AudioStreamWav Load(byte[] wav) => AudioStreamWav.LoadFromBuffer(wav);
            s.Cracks = Array.ConvertAll(d.Cracks, Load); s.Snaps = Array.ConvertAll(d.Snaps, Load); s.Thunders = Array.ConvertAll(d.Thunders, Load);
            s.Boom = Load(d.Boom);
            s.Sizzle = Load(d.Sizzle);
            s.Sizzle.LoopMode = AudioStreamWav.LoopModeEnum.Forward; s.Sizzle.LoopBegin = 0; s.Sizzle.LoopEnd = d.Sizzle.Length / 2 - 22;
            int install = 0;
            foreach (var (name, oggs) in d.Install)
            {
                var list = new List<AudioStream>();
                foreach (var ogg in oggs) if (AudioStreamOggVorbis.LoadFromBuffer(ogg) is { } st) list.Add(st);
                if (list.Count > 0) { s.Install[name] = list.ToArray(); install += list.Count; }
            }
            GD.Print($"librarian audio: {s.Cracks.Length + s.Snaps.Length + s.Thunders.Length + 2} synthesized layers and {install} decoded install sounds from {s.Install.Count} events ready ({d.Milliseconds:F0} ms on a worker)");
        }
        else GD.Print($"librarian audio: the layers could not be prepared ({_librarianPrep.Exception?.GetBaseException().Message}); the install's events play alone");
        _librarianStreams = s;
        return s;
    }

    /// <summary>On a worker: the synthesized layers (fixed seeds) and the install layers' audio, decoded from the install by event name (the catalogue's decode is thread-safe; read-only).</summary>
    static LibrarianLayerData PrepareLibrarianLayers(SmContent.AudioCatalog? catalog)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = new LibrarianLayerData
        {
            Cracks = [LibrarianSynth.Wav(LibrarianSynth.Crack(11)), LibrarianSynth.Wav(LibrarianSynth.Crack(12))],
            Thunders = [LibrarianSynth.Wav(LibrarianSynth.Thunder(21)), LibrarianSynth.Wav(LibrarianSynth.Thunder(22))],
            Sizzle = LibrarianSynth.Wav(LibrarianSynth.Sizzle(31)),
            Boom = LibrarianSynth.Wav(LibrarianSynth.Boom(41)),
        };
        var snaps = new byte[SnapVariants][];
        for (int i = 0; i < snaps.Length; i++) snaps[i] = LibrarianSynth.Wav(LibrarianSynth.Snap(51u + (uint)i));
        d.Snaps = snaps;
        if (catalog is not null)
            foreach (var name in InstallLayers)
            {
                var oggs = new List<byte[]>();
                foreach (uint wem in catalog.WemsOf(name))
                    if (catalog.Ogg(wem, out _) is { } ogg) oggs.Add(ogg);
                if (oggs.Count > 0) d.Install[name] = oggs;
            }
        d.Milliseconds = sw.Elapsed.TotalMilliseconds;
        return d;
    }

    float LayerDb(float layer) => layer + SoundGainDb + (_audio?.MasterBusVolume ?? 0f) + SmClient.Customizer.Model.LiveOptions.GainDb(SmClient.Customizer.Model.SoundCategory.Effects);

    string LayerBus => AudioServer.GetBusIndex(SoundSystem.BusName) >= 0 ? SoundSystem.BusName : "Master";

    Node NewLayerPlayer(string name, bool own)
    {
        Node p = own
            ? new AudioStreamPlayer { Name = name, Bus = LayerBus }
            : new AudioStreamPlayer3D { Name = name, Bus = LayerBus, UnitSize = LayerUnitSize, MaxDistance = LayerMaxDistance, AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance, DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled };
        AddChild(p);
        return p;
    }

    static void PlayOn(Node? player, AudioStream? stream, float db, float pitch)
    {
        if (stream is null) return;
        switch (player)
        {
            case AudioStreamPlayer p2: p2.Stream = stream; p2.VolumeDb = db; p2.PitchScale = pitch; p2.Play(); break;
            case AudioStreamPlayer3D p3: p3.Stream = stream; p3.VolumeDb = db; p3.PitchScale = pitch; p3.Play(); break;
        }
    }

    static void Place(Node? player, Vector3 at) { if (player is AudioStreamPlayer3D p3) p3.GlobalPosition = at; }

    float Vary(float spread) => 1f + spread * (_librarianPitch.NextSingle() * 2f - 1f);

    T? Pick<T>(T[] options) where T : class => options.Length == 0 ? null : options[_librarianPitch.Next(options.Length)];

    AudioStream? InstallSound(string name) => _librarianStreams is { } s && s.Install.TryGetValue(name, out var list) ? Pick(list) : null;

    LibrarianVoice VoiceOf(ushort player, bool own)
    {
        if (_librarianVoices.TryGetValue(player, out var v) && v.Loop is { } l && IsInstanceValid(l)) return v;
        v = new LibrarianVoice
        {
            Loop = NewLayerPlayer($"LibrarianLoop{player}", own), Shot = NewLayerPlayer($"LibrarianShot{player}", own), Deep = NewLayerPlayer($"LibrarianDeep{player}", own),
            Spell = NewLayerPlayer($"LibrarianSpell{player}", own), CrackA = NewLayerPlayer($"LibrarianCrackA{player}", own), CrackB = NewLayerPlayer($"LibrarianCrackB{player}", own),
        };
        _librarianVoices[player] = v;
        return v;
    }

    /// <summary>A cast began: the lightning strike, the psyker's spell and a low thump; the sizzle fades in and the crackle starts.</summary>
    void LibrarianLayerStart(ushort player, bool own, Vector3 at)
    {
        if (LibrarianStreams() is not { } s) return;
        var v = VoiceOf(player, own);
        Place(v.Shot, at); Place(v.Deep, at); Place(v.Spell, at); Place(v.Loop, at);
        PlayOn(v.Shot, InstallSound(StrikeEvent) ?? Pick(s.Cracks), LayerDb(StrikeDb), Vary(0.05f));
        PlayOn(v.Spell, InstallSound(SpellEvent), LayerDb(SpellDb), Vary(0.04f));
        PlayOn(v.Deep, Pick(s.Cracks), LayerDb(ThumpDb), 0.8f * Vary(0.04f));   // the synthesized crack slowed: the strike's weight
        v.LoopLevel = 0f; v.LoopWant = 1f; v.Held = true; v.NextCrackle = LibrarianClock.Now + 0.05;
        PlayOn(v.Loop, s.Sizzle, -80f, Vary(0.04f));
    }

    /// <summary>A cast ended: the sizzle fades out, the crackle stops, the whip's lightning cracks and the thunder rolls.</summary>
    void LibrarianLayerStop(ushort player, bool own, Vector3 at)
    {
        if (LibrarianStreams() is not { } s) return;
        var v = VoiceOf(player, own);
        v.LoopWant = 0f; v.Held = false;
        Place(v.Shot, at); Place(v.Deep, at);
        PlayOn(v.Shot, InstallSound(WhipEvent), LayerDb(WhipDb), Vary(0.06f));
        PlayOn(v.Deep, Pick(s.Thunders), LayerDb(ThunderDb), Vary(0.08f));
    }

    /// <summary>A newly struck target: the Weirdboy's large arc there with a soft synthesized snap under it (pooled 3D players, round robin).</summary>
    void LibrarianLayerSnap(Vector3 at)
    {
        if (LibrarianStreams() is not { } s) return;
        for (int layer = 0; layer < 2; layer++)
        {
            int k = _snapNext++ % SnapPlayers;
            if (_snapPlayers[k] is not { } p || !IsInstanceValid(p)) _snapPlayers[k] = p = (AudioStreamPlayer3D)NewLayerPlayer($"LibrarianSnap{k}", own: false);
            p.GlobalPosition = at;
            if (layer == 0) PlayOn(p, InstallSound(ArcEvent) ?? Pick(s.Snaps), LayerDb(ArcDb), Vary(0.1f));
            else PlayOn(p, Pick(s.Snaps), LayerDb(SnapDb), Vary(0.12f));
        }
    }

    /// <summary>The Shockwave: the Weirdboy staff's shockwave, the thunder hammer's displacement and a synthesized thunderclap, at him.</summary>
    void LibrarianLayerShockwave(ushort player, bool own, Vector3 at)
    {
        if (LibrarianStreams() is not { } s) return;
        var v = VoiceOf(player, own);
        Place(v.Shot, at); Place(v.Deep, at); Place(v.Spell, at);
        PlayOn(v.Shot, InstallSound(StaffShockEvent) ?? s.Boom, LayerDb(StaffDb), Vary(0.04f));
        PlayOn(v.Spell, InstallSound(HammerEvent), LayerDb(HammerDb), 0.85f * Vary(0.04f));
        PlayOn(v.Deep, s.Boom, LayerDb(BoomDb), Vary(0.04f));
    }

    /// <summary>Every frame: each loop follows its Librarian and moves toward its wanted level (in 40 ms, out 140 ms), stopping when silent; the layers' worker is checked.</summary>
    void LibrarianLayersFrame(float delta)
    {
        if (_librarianPrep is not null && _librarianStreams is null) LibrarianStreams();
        foreach (var (id, v) in _librarianVoices)
        {
            if (v.Loop is not { } l || !IsInstanceValid(l)) continue;
            float rate = v.LoopWant > v.LoopLevel ? delta / 0.04f : delta / 0.14f;
            v.LoopLevel = Mathf.MoveToward(v.LoopLevel, v.LoopWant, rate);
            float db = v.LoopLevel <= 0.001f ? -80f : LayerDb(SizzleDb) + Mathf.LinearToDb(v.LoopLevel);
            Vector3? here = _librarianSounds.TryGetValue(id, out var s) ? s.Emitter.Position : null;
            if (here is { } h) { Place(l, h); Place(v.CrackA, h); Place(v.CrackB, h); }
            // The held crackle: one of the Weirdboy's tiny arcs every 40 to 120 ms over the hands, alternating two players, random pitch and level.
            if (v.Held && LibrarianClock.Now >= v.NextCrackle && InstallSound(CrackleEvent) is { } crackle)
            {
                v.NextCrackle = LibrarianClock.Now + CrackleMin + (CrackleMax - CrackleMin) * _librarianPitch.NextSingle();
                var player = (v.NextCrackle * 1000.0) % 2 < 1 ? v.CrackA : v.CrackB;
                PlayOn(player, crackle, LayerDb(CrackleDb) - 8f * _librarianPitch.NextSingle(), 0.75f + 0.6f * _librarianPitch.NextSingle());
            }
            switch (l)
            {
                case AudioStreamPlayer p2: p2.VolumeDb = db; if (v.LoopLevel <= 0.001f && v.LoopWant <= 0f && p2.Playing) p2.Stop(); break;
                case AudioStreamPlayer3D p3: p3.VolumeDb = db; if (v.LoopLevel <= 0.001f && v.LoopWant <= 0f && p3.Playing) p3.Stop(); break;
            }
        }
    }

    /// <summary>A Librarian gone: his players are freed.</summary>
    void LibrarianLayersGone(ushort player)
    {
        if (!_librarianVoices.Remove(player, out var v)) return;
        foreach (var n in new[] { v.Loop, v.Shot, v.Deep, v.Spell, v.CrackA, v.CrackB }) if (n is not null && IsInstanceValid(n)) n.QueueFree();
    }
}
