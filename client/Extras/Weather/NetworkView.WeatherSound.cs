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
/// <item>Wind: the Warfront battle bed already plays <c>play_wind_open</c> (Musou's bed); the squall's and the storm's gusts are procedural on top. Under cover (W4) the sheltered mix swaps in the install's <c>play_wind_closed</c>, or a tunnel wind (<c>play_wind_tunnels</c> and its likely spellings, the first the bank has) when the ceiling is low, at -10 dB over their own level, while the rig's own beds are cut and low-passed; the install's loop switches on when the shelter passes its midpoint and off when it falls back (a started loop has no gain control), the procedural beds crossfade smoothly. When the bank has none (or the post sounds nothing) the procedural fallback alone is the sheltered mix.</item>
/// </list>
/// <c>--log-sound weather</c> prints a line per install post.
/// </summary>
public partial class NetworkView
{
    public const string WxThunderEvent = "play_distant_explosions", WxGritEvent = "play_dust_loop";
    public const float WxFarThunderGainDb = -6f, WxWarningGainDb = -8f, WxGritGainDb = -10f;

    SoundEmitter? _wxGrit, _wxClosed;
    string? _wxClosedPlaying;
    double _wxClosedUntil, _wxClosedFailedUntil;
    bool _wxClosedLoops;
    /// <summary>The sheltered-wind stop's 1 s fade, on real frame time.</summary>
    readonly WeatherClosedWindFade _wxClosedFade = new();
    /// <summary>Verified in the installed In_Game_Default bank (weather W5): <c>play_wind_tunnels</c> is a real looping sound (one 21 KB wem, loop endless, -7 dB, +910 cents). The other spellings W4 guessed are not in the bank.</summary>
    static readonly string[] WxTunnelEvents = ["play_wind_tunnels"];
    /// <summary>Verified (weather W5): <c>play_wind_closed</c> is not a sound but a SetLpf action on the open wind's own node (<c>stop_wind_closed</c> resets it), so posting it on the weather's emitter sounds nothing. The Warfront bed's wind gets the same job from the shelter level (<see cref="WeatherShelterMix.BedWindLpf"/>).</summary>
    public const string WxClosedEvent = "play_wind_closed";
    bool _wxGritOn, _wxWarned, _wxGritLoops;
    double _wxGritUntil;
    WeatherThunderTracker _wxThunder = new();
    WeatherId _wxSoundId = WeatherId.None;
    /// <summary>A grit sound that is not an endless loop is posted again this many seconds after its last post (the data's one-shots are a few seconds long).</summary>
    const double WxGritRepostSeconds = 6.0;

    /// <summary>Once a frame while the rig is up: the thunder that arrives now, the squall's warning and grit.</summary>
    void WeatherInstallSounds(WeatherState st, float intensity)
    {
        if (_wxSoundId != st.Id) { WeatherInstallStop(fade: true); _wxSoundId = st.Id; _wxThunder = new WeatherThunderTracker(); _wxWarned = false; }

        WeatherClosedWind(st, intensity);

        // thunder: each strike's sound once, strictly in time order (the tracker ignores a step back of the 2 s state snap and a jump forward)
        if (st.Id == WeatherId.Thunderstorm)
        {
            foreach (var strike in _wxThunder.Step(st.Elapsed, st.Span))
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
            float ashShare = WeatherPlan.AshShare(st);
            bool grit = ashShare > 0.02f && _wxShelter.Level < WeatherShelterMix.GritStopAbove;   // under cover the grit stops (W4); the weather finish: the grit runs through the whole active squall
            if (_wxGritOn && _wxGrit is not null) _wxGrit.LiveDb = WeatherPlan.FollowDb(ashShare, 0f, -60f);   // and its level follows the ash (amplitude with the share: -5 dB at the squall's lull)
            double now = st.Elapsed;
            if (grit && (!_wxGritOn || !_wxGritLoops) && (now >= _wxGritUntil || now < _wxGritUntil - WxGritRepostSeconds) && _sound is not null && _audio is { } audio && audio.HasEvent(WxGritEvent) && _camera is not null)
            {
                _wxGrit ??= new SoundEmitter { Label = "weather grit", Group = "weather", Background = false, Follow = () => _camera?.GlobalPosition };
                if (_wxGritOn) _sound.StopEmitter(_wxGrit);   // a one-shot's repost ends the last one first, so a sample longer than the repost interval never stacks
                float gritDb = WxGritGainDb + WeatherShelterMix.InstallGritDb(_wxShelter.Level);
                var play = _sound.Post(WxGritEvent, _wxGrit, offset: new VoiceOffset(gritDb));
                _wxGritOn = play is { Voices.Count: > 0 };
                // an endless loop (a voice with loop count 0, or a continuing container) is posted once; anything else is a one-shot: posted again after a few seconds while the grit is wanted
                _wxGritLoops = play is { } p && (p.Repeats.Count > 0 || p.Voices.Exists(v => v.LoopCount == 0));
                _wxGritUntil = now + WxGritRepostSeconds;
                if (LogSound("weather")) GD.Print($"weather grit: {WxGritEvent} voices {play?.Voices.Count ?? 0} {(_wxGritLoops ? "looping" : "one-shot, reposted every " + WxGritRepostSeconds + " s")} at {gritDb:0} dB");
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
        _wxOneShots.RemoveAll(e => !_sound.HasVoices(e));   // the ones whose voices ended are forgotten
        _wxOneShots.Add(emitter);
        gainDb += WeatherShelterMix.InstallThunderDb(_wxShelter.Level);   // W4: duller and quieter under cover
        var play = _sound.Post(eventName, emitter, offset: new VoiceOffset(gainDb), once: true);
        bool ok = play is { Voices.Count: > 0 };
        if (LogSound("weather")) GD.Print($"weather sound: {what} via {eventName} voices {play?.Voices.Count ?? 0} at {gainDb:0} dB");
        return ok;
    }

    /// <summary>The sheltered wind bed from the install (see the class notes). Posted once per cover; stopped when the cover ends or the wind dies down; a post that sounded nothing is retried after 10 s while it is still wanted; a one-shot is posted again like the grit.</summary>
    void WeatherClosedWind(WeatherState st, float intensity)
    {
        double now = st.Elapsed;   // (a stop in progress fades in WeatherInstallFades, every connected frame, on real frame time)
        if (_sound is null || _audio is not { } audio || _camera is null) return;
        float wind = WeatherPlan.Wind(st.Id, intensity, now);
        var action = WeatherClosedWindRule.Decide(_wxShelter.Covered, _wxShelter.Level, _wxShelter.Tunnel, wind, _wxClosedPlaying, WxTunnelEvents, audio.HasEvent, out var name);
        if (action == ClosedWindAction.Stop)
        {
            if (LogSound("weather")) GD.Print($"weather sound: closed wind ({_wxClosedPlaying}) fading out");
            _wxClosedPlaying = null; _wxClosedLoops = false; _wxClosedUntil = 0;
            _wxClosedFade.Start();   // the emitter keeps playing while its gain falls to the floor
            return;
        }
        if (action == ClosedWindAction.None || name is null) return;
        if (_wxClosedFade.Active) WeatherClosedFadeEnd();   // re-entering a tunnel: the fading loop ends at once and posts again
        if (action == ClosedWindAction.Keep && (_wxClosedLoops || now < _wxClosedUntil && now > _wxClosedUntil - WxGritRepostSeconds)) return;   // an endless loop is posted once; a one-shot is posted again after its few seconds
        if (_wxClosedPlaying is null && _wxClosedFailedUntil > now && _wxClosedFailedUntil - 10.0 < now) return;   // the 10 s wait applies only after a post that sounded nothing
        _wxClosed ??= new SoundEmitter { Label = "weather closed wind", Group = "weather", Background = false, Follow = () => _camera?.GlobalPosition };
        if (_wxClosedPlaying is not null) _sound.StopEmitter(_wxClosed);   // a one-shot's repost ends the last one first (no stacked voices)
        var play = _sound.Post(name, _wxClosed, offset: new VoiceOffset(WeatherShelterMix.InstallClosedWindDb));
        bool ok = play is { Voices.Count: > 0 };
        _wxClosedLoops = play is { } p && (p.Repeats.Count > 0 || p.Voices.Exists(v => v.LoopCount == 0));
        _wxClosedUntil = now + WxGritRepostSeconds;
        _wxClosedFailedUntil = ok ? 0 : now + 10.0;
        if (ok) _wxClosedPlaying = name;
        if (LogSound("weather")) GD.Print($"weather sound: closed wind via {name} voices {play?.Voices.Count ?? 0} {(ok ? (_wxClosedLoops ? "looping" : "one-shot, reposted every " + WxGritRepostSeconds + " s") : "nothing sounded: the procedural bed alone, retry in 10 s")} at {WeatherShelterMix.InstallClosedWindDb:0} dB");
    }

    /// <summary>The one-shot emitters the weather code posted (far thunder, the squall's warning) that may still sound; combat sounds are never here, even with the same event name.</summary>
    readonly System.Collections.Generic.List<SoundEmitter> _wxOneShots = new();
    readonly WeatherThunderFade _wxThunderFade = new();
    WeatherId _wxFadePrev = WeatherId.None;
    double _wxThunderLastTicks = -1;

    /// <summary>Every connected frame (rig or not): a switch of the effective weather to None starts the 0.5 s fade of the weather's thunder voices, which then stop; any other switch leaves the rolls to finish.</summary>
    void WeatherThunderFadeFrame(WeatherId id)
    {
        double ticks = Time.GetTicksMsec() / 1000.0, dt = _wxThunderLastTicks < 0 ? 0 : ticks - _wxThunderLastTicks;
        _wxThunderLastTicks = ticks;
        if (WeatherThunderFade.Fades(_wxFadePrev, id)) _wxThunderFade.Start();
        else if (!WeatherThunderFade.Silent(id) && _wxThunderFade.Active) { _wxThunderFade.Cancel(); WeatherThunderFadeApply(0f); }   // the weather is back: nothing is fading
        _wxFadePrev = id;
        if (!_wxThunderFade.Active) return;
        float db = _wxThunderFade.Step(dt);
        if (_wxThunderFade.Done) WeatherThunderStopAll(); else WeatherThunderFadeApply(db);
    }

    void WeatherThunderFadeApply(float db)
    {
        _wxRig?.FadeThunder(db);
        foreach (var e in _wxOneShots) e.LiveDb = db;
    }

    /// <summary>The weather's own thunder voices (rumbles and installed one-shots) stop at once: the end of the fade, a disconnect, the view leaving.</summary>
    void WeatherThunderStopAll()
    {
        _wxThunderFade.Cancel();
        _wxRig?.StopThunder();
        foreach (var e in _wxOneShots) { _sound?.StopEmitter(e); e.LiveDb = 0f; }
        _wxOneShots.Clear();
    }

    void WeatherClosedFadeEnd()
    {
        if (_wxClosed is not null) { _sound?.StopEmitter(_wxClosed); _wxClosed.LiveDb = 0f; }
        _wxClosedFade.Cancel();
    }

    /// <summary>
    /// The grit loop ends. Repair 3 (review): with <paramref name="fade"/> (a switch of preset, the squall easing below the grit, cover) the playing emitter is handed to a 0.5 s fade (<see cref="WeatherBedFade"/>, the beds' and the
    /// thunder's length) and stopped at its end; a new grit post uses a fresh emitter meanwhile. Without it (a disconnect, the view leaving) everything stops at once.
    /// </summary>
    void WeatherGritStop(bool fade = true)
    {
        if (_wxGrit is not null && _wxGritOn && fade && _sound is not null)
        {
            if (_wxGritFading is not null) _sound.StopEmitter(_wxGritFading);   // an older fade ends at once
            _wxGritFading = _wxGrit; _wxGrit = null;
            _wxGritFade.Cancel(); _wxGritFade.Start(_wxGritFading.LiveDb);
        }
        else if (_wxGrit is not null) _sound?.StopEmitter(_wxGrit);
        if (!fade && _wxGritFading is not null) { _sound?.StopEmitter(_wxGritFading); _wxGritFading = null; _wxGritFade.Cancel(); }
        _wxGritOn = false;
        _wxGritLoops = false;
        _wxGritUntil = double.NegativeInfinity;   // the next peak posts at once; a failed post (no voices) still waits its 6 s before retrying
    }

    SoundEmitter? _wxGritFading;
    readonly WeatherBedFade _wxGritFade = new();
    double _wxInstallLastTicks = -1;

    /// <summary>Every connected frame (rig or not; repair 3): the grit's and the sheltered wind's stop fades run on real frame time and end their loops, even after the rig has gone.</summary>
    void WeatherInstallFades()
    {
        double ticks = Time.GetTicksMsec() / 1000.0, dt = _wxInstallLastTicks < 0 ? 0 : Math.Min(0.25, ticks - _wxInstallLastTicks);
        _wxInstallLastTicks = ticks;
        if (_wxClosedFade.Active && _wxClosed is not null)
        {
            float db = _wxClosedFade.Step(dt);
            if (_wxClosedFade.Done) WeatherClosedFadeEnd(); else _wxClosed.LiveDb = db;
        }
        if (_wxGritFade.Active && _wxGritFading is not null)
        {
            float db = _wxGritFade.Step(dt, out bool done);
            if (done) { _sound?.StopEmitter(_wxGritFading); _wxGritFading = null; } else _wxGritFading.LiveDb = db;
        }
    }

    /// <summary>
    /// The rig ends (or the preset changes): the loops the weather started end. With <paramref name="fade"/> (repair 3: a preset switch, the rig's own end) the grit fades over 0.5 s and the sheltered wind over its 1 s
    /// (weather fix 4) before they stop; without it (a disconnect, the view leaving) they stop at once.
    /// </summary>
    void WeatherInstallStop(bool fade = false)
    {
        WeatherGritStop(fade);
        if (fade && _wxClosedPlaying is not null && _wxClosed is not null) { _wxClosedPlaying = null; _wxClosedFade.Start(); }   // the emitter keeps playing while its gain falls (WeatherInstallFades)
        else
        {
            if (_wxClosedPlaying is not null && _wxClosed is not null) _sound?.StopEmitter(_wxClosed);
            _wxClosedPlaying = null;
            if (!fade) WeatherClosedFadeEnd();
        }
        _wxClosedUntil = 0; _wxClosedFailedUntil = 0; _wxClosedLoops = false;
        _wxSoundId = WeatherId.None;
        _wxWarned = false;
    }
}
