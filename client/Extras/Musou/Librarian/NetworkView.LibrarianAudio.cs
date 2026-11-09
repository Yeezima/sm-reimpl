using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// The Librarian's chain lightning heard (librarian-lightning, owner 2026-11-10: "improve the chain lightning visual and audio effects. use real world lightning sound samples"): layers cut from real recordings
/// of lightning, thunder and high-voltage arcs (third-party, licences and sources in <c>THIRD_PARTY_NOTICES.md</c>; added content, embedded only when added content is built, from
/// <c>client/Extras/Musou/assets/audio/</c>), played on their own few players outside the data's voice pool (so a full battle never steals them), on the sound bus with the effects volume:
/// <list type="bullet">
/// <item><see cref="CastCrack"/>: the strike's crack at the release (the own Librarian's on the press, with his foreseen bolt);</item>
/// <item><see cref="ArcLoop"/>: the arc's sizzle while the lightning is held, faded in and out;</item>
/// <item><see cref="Snaps"/>: a high-voltage snap at each newly struck target (the zaps' budget: 3 a section, 8 a second);</item>
/// <item><see cref="ThunderTail"/>: a thunder roll at the release's end;</item>
/// <item><see cref="ShockwaveBoom"/>: a thunderclap under the Shockwave's own sound.</item>
/// </list>
/// Pitch varies a little per play (ours). Without the files (or with <c>--old-librarian-sound</c>) the original install events of pass 2 play instead (<see cref="SmNet.Extras.Musou.LibrarianSounds"/>).
/// </summary>
public partial class NetworkView
{
    public const string CastCrack = "librarian_cast_crack", ArcLoop = "librarian_arc_loop", ThunderTail = "librarian_thunder_tail", ShockwaveBoom = "librarian_shockwave_boom";
    public static readonly string[] Snaps = ["librarian_snap_1", "librarian_snap_2", "librarian_snap_3", "librarian_snap_4"];
    /// <summary>Each layer's level (dB, over the files' -1 dBFS peaks; ours, waiting for the owner's ear).</summary>
    const float CrackDb = -3f, LoopDb = -9f, SnapDb = -8f, TailDb = -9f, BoomDb = -2f;
    /// <summary>The 3D layers' full-volume radius and reach (m, ours: the lightning is heard across a battle).</summary>
    const float LayerUnitSize = 12f, LayerMaxDistance = 140f;
    const int SnapPlayers = 4;

    static readonly bool OldLibrarianSound = OS.GetCmdlineUserArgs().Contains("--old-librarian-sound");
    Dictionary<string, AudioStreamWav?>? _libraryStreams;
    readonly Dictionary<ushort, LibrarianVoice> _librarianVoices = new();
    readonly AudioStreamPlayer3D?[] _snapPlayers = new AudioStreamPlayer3D?[SnapPlayers];
    int _snapNext;
    readonly Random _librarianPitch = new(23);

    /// <summary>One Librarian's players: the loop (followed every frame, faded) and the one-shots at him.</summary>
    sealed class LibrarianVoice
    {
        public Node? Loop, Shot;   // AudioStreamPlayer (own) or AudioStreamPlayer3D
        public float LoopLevel, LoopWant;
    }

    /// <summary>Whether the real-world layers are here (embedded and decoded) and wanted; else the original events play.</summary>
    bool LibrarianLayers => !OldLibrarianSound && LibraryStream(CastCrack) is not null && LibraryStream(ArcLoop) is not null;

    AudioStreamWav? LibraryStream(string name)
    {
        if (_libraryStreams is null)
        {
            _libraryStreams = new Dictionary<string, AudioStreamWav?>(StringComparer.Ordinal);
            int loaded = 0;
            foreach (var n in new[] { CastCrack, ArcLoop, ThunderTail, ShockwaveBoom }.Concat(Snaps))
            {
                AudioStreamWav? stream = null;
                try
                {
                    using var res = typeof(NetworkView).Assembly.GetManifestResourceStream("Extras/" + n + ".wav");
                    if (res is not null)
                    {
                        var bytes = new byte[res.Length];
                        res.ReadExactly(bytes);
                        stream = AudioStreamWav.LoadFromBuffer(bytes);
                        if (stream is not null && n == ArcLoop) { stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward; stream.LoopBegin = 0; stream.LoopEnd = (int)(stream.GetLength() * stream.MixRate); }
                    }
                }
                catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException) { GD.Print($"librarian audio: {n} did not load ({ex.Message})"); }
                if (stream is not null) loaded++;
                _libraryStreams[n] = stream;
            }
            GD.Print($"librarian audio: {loaded} real-world layers loaded{(OldLibrarianSound ? " (--old-librarian-sound: the original events play)" : loaded == 0 ? " (none embedded: the original events play)" : "")}");
        }
        return _libraryStreams.GetValueOrDefault(name);
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

    static void PlayOn(Node player, AudioStream stream, float db, float pitch)
    {
        switch (player)
        {
            case AudioStreamPlayer p2: p2.Stream = stream; p2.VolumeDb = db; p2.PitchScale = pitch; p2.Play(); break;
            case AudioStreamPlayer3D p3: p3.Stream = stream; p3.VolumeDb = db; p3.PitchScale = pitch; p3.Play(); break;
        }
    }

    float Vary(float spread) => 1f + spread * (_librarianPitch.NextSingle() * 2f - 1f);

    LibrarianVoice VoiceOf(ushort player, bool own)
    {
        if (_librarianVoices.TryGetValue(player, out var v) && v.Loop is { } l && IsInstanceValid(l)) return v;
        v = new LibrarianVoice { Loop = NewLayerPlayer($"LibrarianLoop{player}", own), Shot = NewLayerPlayer($"LibrarianShot{player}", own) };
        _librarianVoices[player] = v;
        return v;
    }

    /// <summary>A cast began: the crack, and the loop fades in.</summary>
    void LibrarianLayerStart(ushort player, bool own, Vector3 at)
    {
        var v = VoiceOf(player, own);
        if (v.Shot is AudioStreamPlayer3D s3) s3.GlobalPosition = at;
        if (LibraryStream(CastCrack) is { } crack) PlayOn(v.Shot!, crack, LayerDb(CrackDb), Vary(0.06f));
        if (LibraryStream(ArcLoop) is { } loop && v.Loop is { } l)
        {
            if (l is AudioStreamPlayer3D l3) l3.GlobalPosition = at;
            v.LoopLevel = 0f; v.LoopWant = 1f;
            PlayOn(l, loop, LayerDb(LoopDb) - 40f, Vary(0.04f));
        }
    }

    /// <summary>A cast ended: the loop fades out and the thunder rolls.</summary>
    void LibrarianLayerStop(ushort player, bool own, Vector3 at)
    {
        var v = VoiceOf(player, own);
        v.LoopWant = 0f;
        if (v.Shot is AudioStreamPlayer3D s3) s3.GlobalPosition = at;
        if (LibraryStream(ThunderTail) is { } tail) PlayOn(v.Shot!, tail, LayerDb(TailDb), Vary(0.08f));
    }

    /// <summary>A newly struck target: a snap there (a pooled 3D player, round robin).</summary>
    void LibrarianLayerSnap(Vector3 at)
    {
        int k = _snapNext++ % SnapPlayers;
        if (_snapPlayers[k] is not { } p || !IsInstanceValid(p)) _snapPlayers[k] = p = (AudioStreamPlayer3D)NewLayerPlayer($"LibrarianSnap{k}", own: false);
        p.GlobalPosition = at;
        if (LibraryStream(Snaps[_librarianPitch.Next(Snaps.Length)]) is { } snap) PlayOn(p, snap, LayerDb(SnapDb), Vary(0.1f));
    }

    /// <summary>The Shockwave: the boom at him.</summary>
    void LibrarianLayerShockwave(ushort player, bool own, Vector3 at)
    {
        var v = VoiceOf(player, own);
        if (v.Shot is AudioStreamPlayer3D s3) s3.GlobalPosition = at;
        if (LibraryStream(ShockwaveBoom) is { } boom) PlayOn(v.Shot!, boom, LayerDb(BoomDb), Vary(0.05f));
    }

    /// <summary>Every frame: each loop follows its Librarian and moves toward its wanted level (in 40 ms, out 140 ms), stopping when silent.</summary>
    void LibrarianLayersFrame(float delta)
    {
        foreach (var (id, v) in _librarianVoices)
        {
            if (v.Loop is not { } l || !IsInstanceValid(l)) continue;
            float rate = v.LoopWant > v.LoopLevel ? delta / 0.04f : delta / 0.14f;
            v.LoopLevel = Mathf.MoveToward(v.LoopLevel, v.LoopWant, rate);
            float db = v.LoopLevel <= 0.001f ? -80f : LayerDb(LoopDb) + Mathf.LinearToDb(v.LoopLevel);
            if (l is AudioStreamPlayer3D l3 && _librarianSounds.TryGetValue(id, out var s)) l3.GlobalPosition = s.Emitter.Position;
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
        if (v.Loop is { } l && IsInstanceValid(l)) l.QueueFree();
        if (v.Shot is { } s && IsInstanceValid(s)) s.QueueFree();
    }
}
