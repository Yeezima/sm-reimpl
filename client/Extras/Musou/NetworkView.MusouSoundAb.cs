using System;
using System.IO;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// musou/sound-ab (owner, 2026-11-10: "remove the 'drowned out sound overload' effect that happens in heavy combat"; the A/B comes first, the default changes only after he picks). Test aids,
/// Musou Mode only (Annihilation and Seize Ground never set any of it: <see cref="MusouSoundAbFrame"/> puts every switch back to the behaviour as it was when the match is not Musou):
/// <list type="bullet">
/// <item><c>--musou-sound-off a,b,...</c> (<see cref="MusouSoundAb"/>: duck, lowpass, hitfilter, limiter, steal, instlimit, ambience, crowdvoices, blast, or all): each named system off.</item>
/// <item><c>--log-sound mixab</c>: what engages, measured: every 0.5 s a SOUND MIX line (voices against the caps, low-passed and hit-filtered voices, the live bus ducking, the voices by source
/// with their summed level, the peak before each hard limiter and how much it had to take off), every 0.5 s the counters' change (steals, culls, instance-limit kills and drops, bus actions),
/// and a line for each bus Set/Reset action (DUCK), voice steal (STEAL) and instance-limit kill or drop (LIMIT). The limiter taps exist only with this flag.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    static readonly MusouSoundOff MusouSoundOffArg = ParseMusouSoundOff();
    /// <summary><c>--musou-record-mix &lt;file.wav&gt;</c>: the mix as it leaves Master, written as it plays (a real-time run with <c>--audio-driver Dummy</c> records without a sound from the speakers).</summary>
    static readonly string? MusouRecordMixArg = CmdArg("musou-record-mix");
    MixRecorder? _musouRecorder;
    bool _musouRecorderFailed;
    bool _musouSoundAbSaid, _musouSoundAbMeters;
    double _musouMixLogAt;
    (int Steals, int Culled, int Virtual, int Kills, int Drops, int Bus, int BusDropped, int Started) _musouMixLast;

    static MusouSoundOff ParseMusouSoundOff()
    {
        string? text = CmdArg("musou-sound-off");
        if (MusouSoundAb.TryParse(text, out var off, out string? error)) return off;
        GD.PrintErr($"--musou-sound-off: {error}; nothing is turned off");
        return MusouSoundOff.None;
    }

    /// <summary>Whether a system is off in this match (only ever in Musou Mode).</summary>
    bool MusouSoundIsOff(MusouSoundOff system) => MusouActive && (MusouSoundOffArg & system) != 0;

    /// <summary>Once a frame (the dispatcher, every mode): the switches for this match, and the measurement.</summary>
    void MusouSoundAbFrame()
    {
        if (_sound is not { } sound) return;
        var off = MusouActive ? MusouSoundOffArg : MusouSoundOff.None;
        sound.IgnoreBusActions = (off & MusouSoundOff.Duck) != 0;
        sound.NoLowPass = (off & MusouSoundOff.LowPass) != 0;
        sound.NoHitFilter = (off & MusouSoundOff.HitFilter) != 0;
        sound.NoInstanceLimits = (off & MusouSoundOff.InstLimit) != 0;
        bool lifted = (off & MusouSoundOff.Steal) != 0;
        sound.Cap3D = lifted ? MusouSoundAb.LiftedCap3D : SoundSystem.Max3D;
        sound.Cap2D = lifted ? MusouSoundAb.LiftedCap2D : SoundSystem.Max2D;
        sound.SetLimitersBypassed((off & MusouSoundOff.Limiter) != 0);
        if (!MusouActive) return;
        if (!_musouSoundAbSaid)
        {
            _musouSoundAbSaid = true;
            GD.Print($"musou sound-ab: off {MusouSoundAb.Describe(off)} (--musou-sound-off; test aid, Musou Mode only)");
        }
        if (MusouRecordMixArg is { } recordTo && _musouRecorder is null && !_musouRecorderFailed)
        {
            try { _musouRecorder = new MixRecorder(recordTo); GD.Print($"musou sound-ab: recording the mix to {recordTo} ({_musouRecorder.Rate} Hz, float, after Master's effects and volume)"); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { _musouRecorderFailed = true; GD.PrintErr($"--musou-record-mix: {ex.Message}"); }
        }
        _musouRecorder?.Drain();
        if (!LogSound("mixab")) return;
        if (!_musouSoundAbMeters)
        {
            _musouSoundAbMeters = true;
            sound.AddMeters();
            sound.MixStats = true;
            if (LogSound("mixab-lines")) sound.MixLog = GD.Print;   // a line per bus action, steal and instance-limit kill (heavy: a Musou fight steals hundreds a second)
            else sound.MixLog = line => { if (line.StartsWith("DUCK", StringComparison.Ordinal)) GD.Print(line); };   // the bus actions are few
        }
        if (_connectedFor < _musouMixLogAt) return;
        _musouMixLogAt = _connectedFor + 0.5;
        var now = (sound.Steals, sound.Culled, sound.Virtual, sound.LimitKills, sound.LimitDrops, sound.BusActions, sound.BusActionsDropped, sound.VoicesStarted);
        var last = _musouMixLast;
        _musouMixLast = now;
        GD.Print($"SOUND MIX t {_connectedFor:F2} frame {Engine.GetProcessFrames()} off {MusouSoundAb.Describe(off)}: started +{now.VoicesStarted - last.Started}, steals +{now.Steals - last.Steals}, culled +{now.Culled - last.Culled}, made virtual +{now.Virtual - last.Virtual}, " +
                 $"instance-limit kills +{now.LimitKills - last.Kills} drops +{now.LimitDrops - last.Drops}, bus actions +{now.BusActions - last.Bus} dropped +{now.BusActionsDropped - last.BusDropped}; {sound.MixLine()}; {sound.MeterLine()}");
    }

    /// <summary>
    /// The test aid's recorder: a capture tap last on Master (after its limiter), drained once a frame into a 32-bit float stereo WAV at the mix rate, times Master's volume (Godot applies a bus's
    /// volume after its effects). The header is brought up to date after each drain, so a run that is stopped still leaves a whole file. Never inside the repository.
    /// </summary>
    sealed class MixRecorder
    {
        readonly FileStream _file;
        readonly AudioEffectCapture _tap = new() { BufferLength = 8f };
        public readonly int Rate;
        /// <summary>Stereo frames written so far (the SOUNDCAP marks name it: the clips are cut at these).</summary>
        public long Frames { get; private set; }

        public MixRecorder(string path)
        {
            string full = Path.GetFullPath(path);
            string repo = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
            if (full.StartsWith(repo.TrimEnd(Path.DirectorySeparatorChar, '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{full} is inside the repository");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            Rate = (int)AudioServer.GetMixRate();
            _file = new FileStream(full, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.Read);
            WriteHeader();
            AudioServer.AddBusEffect(0, _tap);   // last on Master: after the limiter
        }

        void WriteHeader()
        {
            long data = Frames * 8;
            Span<byte> h = stackalloc byte[44];
            "RIFF"u8.CopyTo(h);
            BitConverter.TryWriteBytes(h[4..], (uint)(36 + data));
            "WAVEfmt "u8.CopyTo(h[8..]);
            BitConverter.TryWriteBytes(h[16..], 16u);
            BitConverter.TryWriteBytes(h[20..], (ushort)3);   // IEEE float
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
            Frames += frames.Length;
            WriteHeader();
            _file.Flush();
        }
    }
}
