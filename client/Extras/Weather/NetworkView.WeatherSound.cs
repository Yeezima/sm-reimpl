using System;
using Godot;
using SmContent;
using SmSim.Extras.Weather;

namespace SmClient;

/// <summary>
/// The install's own sounds for the W2 weather (weather W2; client only, presentation only, **ours**: the choice of events and their levels). Read from the owner's installed banks through the existing sound path
/// (<see cref="SoundSystem.Post"/>); each one has a procedural fallback in <see cref="WeatherRig"/> when the bank has no such event, so a missing sound is a quieter storm, never a silent one:
/// <list type="bullet">
/// <item>Far thunder: <c>play_distant_explosions</c> (the stand-in for distant thunder) from a point 90 m away toward the wind's side, once per strike after its delay (<see cref="WeatherPlan.ThunderIn"/>), at -6 dB over its own level. A near strike is the rig's procedural crack.</item>
/// <item>Ash Squall: one <c>play_distant_explosions</c> at the start of the warning (the audible warning, -8 dB), and the grit loop <c>play_dust_loop</c> on the listener through the peak, at -10 dB (stopped as it clears).</item>
/// <item>Wind: the Warfront battle bed already plays <c>play_wind_open</c> (Musou's bed); the squall's and the storm's gusts are procedural on top. <c>play_wind_closed</c> and <c>play_wind_tunnels*</c> are held for the sheltered mix (W4).</item>
/// </list>
/// <c>--log-sound weather</c> prints a line per install post.
/// </summary>
public partial class NetworkView
{
    public const string WxThunderEvent = "play_distant_explosions", WxGritEvent = "play_dust_loop";
    public const float WxFarThunderGainDb = -6f, WxWarningGainDb = -8f, WxGritGainDb = -10f;

    SoundEmitter? _wxGrit;
    bool _wxGritOn, _wxWarned, _wxGritLoops;
    double _wxGritUntil;
    WeatherThunderTracker _wxThunder = new();
    WeatherId _wxSoundId = WeatherId.None;
    /// <summary>A grit sound that is not an endless loop is posted again this many seconds after its last post (the data's one-shots are a few seconds long).</summary>
    const double WxGritRepostSeconds = 6.0;

    /// <summary>Once a frame while the rig is up: the thunder that arrives now, the squall's warning and grit.</summary>
    void WeatherInstallSounds(WeatherState st, float intensity)
    {
        if (_wxSoundId != st.Id) { WeatherInstallStop(); _wxSoundId = st.Id; _wxThunder = new WeatherThunderTracker(); _wxWarned = false; }

        // thunder: each strike's sound once, strictly in time order (the tracker ignores a step back of the 2 s state snap and a jump forward)
        if (st.Id == WeatherId.Thunderstorm)
        {
            foreach (var strike in _wxThunder.Step(st.Elapsed))
            {
                bool played = false;
                if (!strike.Near) played = WeatherPostInstall(WxThunderEvent, WxFarThunderGainDb, ThunderPoint(strike.Seed), "far thunder");
                if (!played) _wxRig?.PlayRumble(strike.Seed, strike.Near);
                else _wxRig?.PlayRumble(strike.Seed, false, -12f);   // a quiet low layer under the install's own, so the roll never thins out
                if (LogSound("weather")) GD.Print($"weather thunder: {(strike.Near ? "near (procedural)" : played ? "far via " + WxThunderEvent : "far (procedural)")} at {st.Elapsed:F1}s");
            }
        }

        // the ash squall: the audible warning once, the grit through the peak
        if (st.Id == WeatherId.AshSquall)
        {
            if (!_wxWarned && WeatherPlan.Warning(st))
            {
                _wxWarned = true;
                if (!WeatherPostInstall(WxThunderEvent, WxWarningGainDb, ThunderPoint(7), "squall warning")) _wxRig?.PlayRumble(7, false, -4f);
            }
            if (st.Phase == WeatherPhase.Buildup && !WeatherPlan.Warning(st)) _wxWarned = false;
            bool grit = WeatherPlan.AshShare(st) > 0.5f;
            double now = st.Elapsed;
            if (grit && (!_wxGritOn || (!_wxGritLoops && now >= _wxGritUntil)) && _sound is not null && _audio is { } audio && audio.HasEvent(WxGritEvent) && _camera is not null)
            {
                _wxGrit ??= new SoundEmitter { Label = "weather grit", Group = "weather", Background = false, Follow = () => _camera?.GlobalPosition };
                var play = _sound.Post(WxGritEvent, _wxGrit, offset: new VoiceOffset(WxGritGainDb));
                _wxGritOn = play is { Voices.Count: > 0 };
                // an endless loop (a voice with loop count 0, or a continuing container) is posted once; anything else is a one-shot: posted again after a few seconds while the grit is wanted
                _wxGritLoops = play is { } p && (p.Repeats.Count > 0 || p.Voices.Exists(v => v.LoopCount == 0));
                _wxGritUntil = now + WxGritRepostSeconds;
                if (LogSound("weather")) GD.Print($"weather grit: {WxGritEvent} voices {play?.Voices.Count ?? 0} {(_wxGritLoops ? "looping" : "one-shot, reposted every " + WxGritRepostSeconds + " s")} at {WxGritGainDb:0} dB");
            }
            else if (!grit && _wxGritOn) WeatherGritStop();
        }
        else if (_wxGritOn) WeatherGritStop();
    }

    Vector3 ThunderPoint(uint seed)
    {
        float angle = (seed % 628) / 100f;
        var c = _camera?.GlobalPosition ?? Vector3.Zero;
        return c + new Vector3(MathF.Cos(angle) * 90f, 25f, MathF.Sin(angle) * 90f);
    }

    /// <summary>Posts an install event once on a one-shot emitter at <paramref name="at"/>; false when the bank has no such event or nothing sounded (the caller falls back to the procedural sound).</summary>
    bool WeatherPostInstall(string eventName, float gainDb, Vector3 at, string what)
    {
        if (_sound is null || _audio is not { } audio || !audio.HasEvent(eventName)) return false;
        var emitter = new SoundEmitter { Position = at, OneShot = true, Label = "weather " + what };
        var play = _sound.Post(eventName, emitter, offset: new VoiceOffset(gainDb), once: true);
        bool ok = play is { Voices.Count: > 0 };
        if (LogSound("weather")) GD.Print($"weather sound: {what} via {eventName} voices {play?.Voices.Count ?? 0} at {gainDb:0} dB");
        return ok;
    }

    void WeatherGritStop()
    {
        if (_wxGrit is not null) _sound?.StopEmitter(_wxGrit);
        _wxGritOn = false;
        _wxGritLoops = false;
    }

    /// <summary>The rig ends (or the preset changes): the loops the weather started are stopped.</summary>
    void WeatherInstallStop()
    {
        WeatherGritStop();
        _wxSoundId = WeatherId.None;
        _wxWarned = false;
    }
}
