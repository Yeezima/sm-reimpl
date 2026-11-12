using System;
using System.IO;
using System.Linq;
using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// sound/classic-soundscape (owner's interview, 2026-11-13; <see cref="ClassicSoundscape"/>): what a player hears of the OTHER players in Classic PvP (Annihilation, Seize Ground; not Musou Mode).
/// Posts for others carry a <see cref="ScapeClass"/> on their <see cref="VoiceOffset"/>; <see cref="SoundSystem"/> ranges, protects and pans them. <c>--sound-natural-position</c> keeps the ranges and
/// protection but Godot's own softer positioning; <c>--old-classic-mix</c> restores the whole old mix (the -3 dB footsteps for others included).
/// </summary>
public partial class NetworkView
{
    static ScapeMode ScapeFromArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        return args.Contains("--old-classic-mix") ? ScapeMode.Old : args.Contains("--sound-natural-position") ? ScapeMode.Natural : ScapeMode.Clear;
    }

    static readonly ScapeMode ScapeArg = ScapeFromArgs();

    /// <summary>The soundscape in force for this match: the launch options' in a Classic match, always Old (nothing changed) under added content's Musou Mode.</summary>
    ScapeMode ClassicScape => _config is { } c && c.Extras.HasFlag(ContentExtras.Musou) ? ScapeMode.Old : ScapeArg;

    /// <summary>What a post of another player's sound adds for the soundscape (its class by the post's kind); none for the own player and in the old mix.</summary>
    VoiceOffset ScapeOffset(string kind, bool own) => !own && ClassicScape != ScapeMode.Old ? new VoiceOffset(Scape: ClassicSoundscape.ClassOfKind(kind)) : default;

    /// <summary>
    /// Another player's evade roll: the install's own evade foley and armour thud (<c>play_evade_start</c>: two foley layers and the footstep-set armour thud) at the roll's start, heard like movement
    /// (a jump-pack dash has its own sound, <see cref="DashSound"/>). The clips of the rolls carry only foot tags, so nothing else sounded.
    /// </summary>
    void SoundRoll(SoundTrack t, PlayerState s, PlayerState p)
    {
        if (_sound is null || ClassicScape == ScapeMode.Old || !s.Alive || !p.Alive) return;
        if (!(Evade.Evading(s) && !Evade.Evading(p)) || !s.Grounded || Loadouts.Evade(_config!, s) is not { Dash: false }) return;
        SurfaceUnder(t, s);
        _sound.Post("play_evade_start", t.Emitter, offset: ScapeOffset("roll", false));
        CountSound("roll");
    }

    // ------------------------------------------------------------------ evidence: the mix recorder

    /// <summary><c>--record-mix &lt;file.wav&gt;</c> (test aid, any mode): the mix as it leaves Master, written as it plays (a real-time run with <c>--audio-driver Dummy</c> records without a sound from the speakers); the Classic soundscape's A/B evidence.</summary>
    static readonly string? RecordMixArg = CmdArg("record-mix");
    ClassicMixRecorder? _recorder;
    bool _recorderFailed;

    /// <summary>Once a frame: starts the recording on the first call and drains the capture tap.</summary>
    void ClassicRecordMixFrame()
    {
        if (RecordMixArg is not { } to || _sound is null || _recorderFailed) return;
        if (_recorder is null)
        {
            try { _recorder = new ClassicMixRecorder(to); GD.Print($"sound: recording the mix to {to} ({_recorder.Rate} Hz, float, after Master's effects and volume; --record-mix)"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { _recorderFailed = true; GD.PrintErr($"--record-mix: {ex.Message}"); return; }
        }
        _recorder.Drain();
    }

    /// <summary>A capture tap last on Master (after its limiter), drained once a frame into a 32-bit float stereo WAV times Master's volume. The header is brought up to date after each drain. Never inside the repository.</summary>
    sealed class ClassicMixRecorder
    {
        readonly FileStream _file;
        readonly AudioEffectCapture _tap = new() { BufferLength = 8f };
        public readonly int Rate;
        long _frames;

        public ClassicMixRecorder(string path)
        {
            string full = Path.GetFullPath(path);
            string repo = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
            if (full.StartsWith(repo.TrimEnd(Path.DirectorySeparatorChar, '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{full} is inside the repository");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            Rate = (int)AudioServer.GetMixRate();
            _file = new FileStream(full, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.Read);
            WriteHeader();
            AudioServer.AddBusEffect(0, _tap);
        }

        void WriteHeader()
        {
            long data = _frames * 8;
            Span<byte> h = stackalloc byte[44];
            "RIFF"u8.CopyTo(h);
            BitConverter.TryWriteBytes(h[4..], (uint)(36 + data));
            "WAVEfmt "u8.CopyTo(h[8..]);
            BitConverter.TryWriteBytes(h[16..], 16u);
            BitConverter.TryWriteBytes(h[20..], (ushort)3);
            BitConverter.TryWriteBytes(h[22..], (ushort)2);
            BitConverter.TryWriteBytes(h[24..], (uint)Rate);
            BitConverter.TryWriteBytes(h[28..], (uint)(Rate * 8));
            BitConverter.TryWriteBytes(h[32..], (ushort)8);
            BitConverter.TryWriteBytes(h[34..], (ushort)32);
            "data"u8.CopyTo(h[36..]);
            BitConverter.TryWriteBytes(h[40..], (uint)data);
            long end = _file.Length;
            _file.Position = 0;
            _file.Write(h);
            _file.Position = Math.Max(44, end);
        }

        public void Drain()
        {
            int n = _tap.GetFramesAvailable();
            if (n <= 0) return;
            var frames = _tap.GetBuffer(n);
            float gain = Mathf.DbToLinear(AudioServer.GetBusVolumeDb(0));
            var bytes = new byte[frames.Length * 8];
            for (int i = 0; i < frames.Length; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), frames[i].X * gain);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4), frames[i].Y * gain);
            }
            _file.Write(bytes);
            _frames += frames.Length;
            WriteHeader();
            _file.Flush();
        }
    }
}
