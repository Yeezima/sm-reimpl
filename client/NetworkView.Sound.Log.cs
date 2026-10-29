using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmFormats.Audio;
using SmSim;

namespace SmClient;

/// <summary>
/// Test aid <c>--log-sound &lt;kinds&gt;</c> (comma list; today: <c>footstep</c>): prints every post of the named sound kind with what the engine chose, so a
/// run can be compared with the data without listening: the tag and event, which player and whether the local variant was set, the surface switch the
/// foot read (name and Wwise value), the <c>speed</c> game parameter, the distance to the listener, and for every voice the .wem, its length, whether it is
/// positioned, and its mix (volume dB, pitch cents, low-pass 0..100) as <see cref="VoiceMixer"/> evaluates it at the start (RTPCs read once, as the voices do).
/// </summary>
public partial class NetworkView
{
    internal static readonly HashSet<string> LogSoundKinds = ParseLogSound();

    static HashSet<string> ParseLogSound()
    {
        var args = OS.GetCmdlineUserArgs();
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--log-sound")
                foreach (var k in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) kinds.Add(k);
        return kinds;
    }

    static bool LogSound(string kind) => LogSoundKinds.Count > 0 && LogSoundKinds.Contains(kind);

    /// <summary>
    /// <c>--sound-limits none|footsteps|all</c> (footsteps fix): which posts get the banks' "limit sound instances" and the 2D volume threshold: none = exactly the behaviour before the fix, footsteps (default) = footstep and landing posts only, all = every post (see <see cref="SoundSystem.Limits"/>).
    /// </summary>
    static readonly InstanceLimits SoundLimitsMode = ParseSoundLimits();

    /// <summary><c>--footstep-gain &lt;dB&gt;</c> (ours, default 0): see <see cref="SoundSystem.FootstepGainDb"/>.</summary>
    static readonly float FootstepGainDbArg = ParseFootstepGain();

    /// <summary><c>--rtpc-volume-cap peak|node|voice|off</c> (sound/footsteps-2; default peak): see <see cref="SoundSystem.RtpcCap"/>.</summary>
    static readonly RtpcVolumeCap RtpcCapArg = ParseRtpcCap();

    static RtpcVolumeCap ParseRtpcCap()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--rtpc-volume-cap")
            {
                if (RtpcVolumeCaps.TryParse(i + 1 < args.Length ? args[i + 1] : null, out var cap, out string? error)) return cap;
                GD.PrintErr($"--rtpc-volume-cap: {error}; using {RtpcVolumeCaps.Default.ToString().ToLowerInvariant()}");
            }
        return RtpcVolumeCaps.Default;
    }

    /// <summary><c>--impact-treatment bolters|ballistic|off</c> (sound/impacts; default bolters): see <see cref="ImpactTreatmentMode"/>.</summary>
    static readonly ImpactTreatmentMode ImpactTreatmentArg = ParseMode("--impact-treatment", ImpactTreatmentMode.Bolters);

    /// <summary><c>--hit-confirm soft|data|off</c> (sound/impacts; default soft): see <see cref="HitConfirmMode"/>.</summary>
    static readonly HitConfirmMode HitConfirmArg = ParseMode("--hit-confirm", HitConfirmMode.Soft);

    static T ParseMode<T>(string flag, T fallback) where T : struct, Enum
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
            if (args[i] == flag)
            {
                if (ImpactSounds.TryParse<T>(i + 1 < args.Length ? args[i + 1] : null, out var value, out string? error)) return value;
                GD.PrintErr($"{flag}: {error}; using {fallback.ToString().ToLowerInvariant()}");
            }
        return fallback;
    }

    /// <summary>A post's voices with their mix at the listener (distance from the camera, as the voices start; the offset included), for the logs.</summary>
    string VoiceMixText(SoundPlay? play, SoundEmitter e, VoiceOffset offset)
    {
        if (play is null) return "not posted";
        if (!play.Found) return "event not in the banks";
        var camera = GetViewport()?.GetCamera3D();
        float distance = camera is null ? 0f : camera.GlobalPosition.DistanceTo(e.Current);
        if (e.Own) distance = Math.Min(distance, SoundSystem.OwnMinDistance);
        var voices = play.Voices.Select(v =>
        {
            var mix = VoiceMixer.Evaluate(v, null, e.Id, 0, v.Is3D ? distance : null, e.Parameters, RtpcCapArg).With(offset);
            return $"wem {v.WemId} {(v.Is3D ? "3D" : "2D")} {mix.VolumeDb:F1}dB {mix.PitchCents:F0}c lpf{mix.Lpf:F0}";
        });
        return $"dist {distance:F1} m, {play.Voices.Count} voices: " + string.Join("; ", voices);
    }

    /// <summary><c>--log-sound impact</c>: an impact post (a shot's destination) with its place, distance, offset and voices.</summary>
    void LogImpact(string kind, string hitSound, SoundEmitter at, VoiceOffset offset, SoundPlay? play)
    {
        if (!LogSound("impact")) return;
        var p = at.Current;
        uint? surface = at.Switches.Get(AudioCatalog.MaterialType);
        GD.Print($"IMPACT t {_connectedFor:F3} {at.Label} {kind} event {hitSound} surface {(surface is uint sv ? SurfaceNameOfSwitch(sv) : "-")} at {p.X:F1},{p.Y:F1},{p.Z:F1} " +
                 $"treatment {ImpactTreatmentArg} offset {offset} | {VoiceMixText(play, at, offset)}");
    }

    static float ParseFootstepGain()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--footstep-gain" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db) && float.IsFinite(db)) return db;
        return 0f;
    }

    static InstanceLimits ParseSoundLimits()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--sound-limits")
            {
                // Names only: Enum.TryParse would take "1" or "2" too.
                var name = Enum.GetNames<InstanceLimits>().FirstOrDefault(n => n.Equals(args[i + 1], StringComparison.OrdinalIgnoreCase));
                if (name is not null) return Enum.Parse<InstanceLimits>(name);
                GD.PrintErr($"--sound-limits {args[i + 1]}: not one of none, footsteps, all; using footsteps");
            }
        return InstanceLimits.Footsteps;
    }

    /// <summary>The name of a surface switch value (the surfaces' hashes), or its hex.</summary>
    static string SurfaceNameOfSwitch(uint value) =>
        Surfaces.Default.FirstOrDefault(n => Fnv.Hash32(n) == value) ?? $"0x{value:x8}";

    /// <summary>One footstep post (<c>--log-sound footstep</c>).</summary>
    void LogFootstep(string tag, SoundBinding b, SoundTrack t, PlayerState s, bool own, SoundPlay? play)
    {
        uint? surface = t.Emitter.Switches.Get(AudioCatalog.MaterialType);
        bool local = t.Emitter.Switches.Get(AudioCatalog.PlayerGroup) == AudioCatalog.LocalPlayerValue;
        t.Emitter.Parameters.TryGetValue(VoiceMixer.Speed, out float speed);
        var camera = GetViewport()?.GetCamera3D();
        float distance = camera is null ? 0f : camera.GlobalPosition.DistanceTo(t.Emitter.Current);
        if (t.Emitter.Own) distance = Math.Min(distance, SoundSystem.OwnMinDistance);
        string head = $"FOOTSTEP t {_connectedFor:F3} P{s.PlayerId}{(own ? " own" : "")} tag {tag} event {b.StartEvent} variant {(local ? "local" : "other")} " +
                      $"surface {(surface is uint sv ? SurfaceNameOfSwitch(sv) : "(none: container default)")} speed {speed:F2} m/s grounded {s.Grounded} dist {distance:F1} m";
        // The voices' dB below include the owner's footstep level and --footstep-gain (what the player gets), not --sound-gain.
        float gain = FootstepLevel.GainDb(b) + FootstepGainDbArg;
        head += $" gain {gain:F1} dB (owner {FootstepLevel.GainDb(b):F1}, --footstep-gain {FootstepGainDbArg:F1}) rtpc-cap {RtpcCapArg}";
        if (play is null) { GD.Print(head + " | not posted"); return; }
        if (!play.Found) { GD.Print(head + " | event not in the banks"); return; }
        var voices = new List<string>();
        foreach (var v in play.Voices)
        {
            var mix = VoiceMixer.Evaluate(v, null, t.Emitter.Id, 0, v.Is3D ? distance : null, t.Emitter.Parameters, RtpcCapArg);
            float duration = _audio?.Duration(v.WemId) ?? 0f;
            voices.Add($"wem {v.WemId} {duration:F2}s {(v.Is3D ? "3D" : "2D")} {mix.VolumeDb + gain:F1}dB {mix.PitchCents:F0}c lpf{mix.Lpf:F0}");
        }
        GD.Print(head + $" | {play.Voices.Count} voices: " + string.Join("; ", voices));
    }
}
