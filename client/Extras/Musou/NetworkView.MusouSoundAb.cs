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
        if (!LogSound("mixab")) return;
        if (!_musouSoundAbMeters)
        {
            _musouSoundAbMeters = true;
            sound.AddMeters();
            sound.MixLog = GD.Print;
        }
        if (_connectedFor < _musouMixLogAt) return;
        _musouMixLogAt = _connectedFor + 0.5;
        var now = (sound.Steals, sound.Culled, sound.Virtual, sound.LimitKills, sound.LimitDrops, sound.BusActions, sound.BusActionsDropped, sound.VoicesStarted);
        var last = _musouMixLast;
        _musouMixLast = now;
        GD.Print($"SOUND MIX t {_connectedFor:F2} frame {Engine.GetProcessFrames()} off {MusouSoundAb.Describe(off)}: started +{now.VoicesStarted - last.Started}, steals +{now.Steals - last.Steals}, culled +{now.Culled - last.Culled}, made virtual +{now.Virtual - last.Virtual}, " +
                 $"instance-limit kills +{now.LimitKills - last.Kills} drops +{now.LimitDrops - last.Drops}, bus actions +{now.BusActions - last.Bus} dropped +{now.BusActionsDropped - last.BusDropped}; {sound.MixLine()}; {sound.MeterLine()}");
    }
}
