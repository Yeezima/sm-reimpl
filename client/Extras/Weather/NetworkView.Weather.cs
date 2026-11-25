using System;
using Godot;
using SmClient.Extras.Weather;
using SmNet;
using SmNet.Extras.Weather;
using SmSim;
using SmSim.Extras.Weather;

namespace SmClient;

/// <summary>
/// Weather on the client (weather W1; docs/added-content.md "Weather"): the server's state section (tag 48) feeds a <see cref="WeatherClientModel"/>; each frame the model's clock runs, the look reaches the screen through
/// <c>LightingGradeView</c>'s hook (<see cref="WeatherGrade"/>), and <see cref="WeatherRig"/> draws rain, ash, embers, the sky glow and their sounds around the camera. Warfront only today. No weather or None leaves the map exactly as it
/// was drawn: the model's grade is the identity, the rig is freed. Test aid: every change of the shown preset or phase is logged as <c>weather: ...</c>.
/// W2 adds the install's own sounds (<c>NetworkView.WeatherSound.cs</c>) to the rig's procedural ones.
/// </summary>
public partial class NetworkView
{
    readonly WeatherClientModel _wxModel = new();
    WeatherRig? _wxRig;
    (WeatherId Id, WeatherPhase Phase, bool Overridden) _wxLogged = (WeatherId.None, WeatherPhase.Opening, false);
    bool _wxLoggedAny;

    /// <summary>Every client built with added content renders the weather the server sends, whatever its own <c>--content</c> (weather is presentation only and not part of the join check; a server with no weather sends none, so nothing shows). A faithful-content client gets the state section too (<c>GameClient.OnExtras</c> keeps that one tag).</summary>
    bool WeatherOn => _config is not null;

    /// <summary>A preset with its own things in the world (rain, ash, embers, the sky glow, wind) at this intensity.</summary>
    static bool WeatherNeedsRig(WeatherId id, float intensity) => id is WeatherId.Rain or WeatherId.Thunderstorm or WeatherId.AshSquall or WeatherId.EmberWind or WeatherId.LowFog && intensity > 0.02f;   // the weather finish adds Low Fog (its ground fog); Clear and None never have one

    /// <summary>The weather section of a tag; handled when the tag is weather's.</summary>
    void WeatherEnvelope(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (tag != WeatherWire.StateTag) return;
        handled = true;
        if (WeatherOn) _wxModel.Receive(payload);
    }

    /// <summary>The scoreboard's weather line (Classic PvP, W3): the active preset and where it stands, so every player (a late joiner too) can see what the host chose. None says nothing (the map as it is), except after an F6 removal (weather fix B).</summary>
    partial void ExtrasScoreboardNote(ref string note)
    {
        if (_config is not { } c || c.Extras.HasFlag(ContentExtras.Musou) || !_wxModel.Has) return;   // Warfront and Musou show their own
        if (WeatherScoreboard.Line(_wxModel.State, _wxModel.Held, _wxModel.Overridden) is { } line) note = line;   // pure, tested (WeatherScoreboardTests); None is silent unless F6 removed the weather
    }

    /// <summary>The view leaves the tree: the static link goes with it (a later Classic or Musou view must not keep this grade or show the F6 Weather section), and the rig is freed.</summary>
    partial void ExtrasExit()
    {
        WeatherAidsApply(false);   // the perf aid's vsync change goes back even when the view is freed while gated
        WeatherClientLink.Release(_wxModel);
        _wxModel.Clear();
        WeatherInstallStop();
        WeatherThunderStopAll();
        _wxFadePrev = WeatherId.None;
        if (_wxRig is not null && IsInstanceValid(_wxRig)) _wxRig.QueueFree();
        _wxRig = null;
        WeatherStormRestore();
    }

    // ---- shelter (weather W4): a few upward rays at a low rate, smoothed by the pure WeatherShelter ----

    readonly WeatherShelter _wxShelter = new();
    /// <summary>The ground under the player (Godot world height, m) from the last shelter sample; null before one or when the ray found nothing within reach (the rig then takes 1.8 m under the camera).</summary>
    float? _wxGroundY;
    double _wxShelterClock;
    int _wxShelterSamples;

    /// <summary>Samples per second of the upward rays (ours, an estimate: 6 Hz, four rays each).</summary>
    const double WxShelterHz = 6.0;

    /// <summary>
    /// Once a frame while the rig is up. Every 1/6 s four rays go straight up (game space) through the collision world the sim itself uses: from the camera (counted twice, it is what the player hears) and from the head
    /// of the player and 1.2 m to either side of it; 40 m reach. The covered share (hits over five) and the nearest ceiling feed <see cref="WeatherShelter"/>, which holds a Schmitt trigger and a minimum hold and slides the level
    /// over 0.7 s (in) and 0.9 s (out). Nothing here allocates per frame (the rays are a handful of struct casts at 6 Hz).
    /// </summary>
    void WeatherShelterFrame(PlayerState me, double delta)
    {
        _wxShelterClock += delta;
        if (_wxShelterClock >= 1.0 / WxShelterHz)
        {
            _wxShelterClock = 0;
            float cover = 0f, ceiling = WeatherShelter.RayReachM;
            long rayT0 = _wxPerfLive ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (_client?.Collision is { } world && _gameSpace is not null && _camera is not null)
            {
                var up = new System.Numerics.Vector3(0f, 0f, 1f);
                var cam = _gameSpace.GlobalTransform.AffineInverse() * _camera.GlobalPosition;
                int hits = 0;
                if (world.Weapons.Raycast(new System.Numerics.Vector3(cam.X, cam.Y, cam.Z), up, WeatherShelter.RayReachM) is { } camHit) { hits += 2; ceiling = Math.Min(ceiling, camHit.Distance); }
                var head = me.Position + new System.Numerics.Vector3(0f, 0f, 1.6f);
                for (int k = -1; k <= 1; k++)
                    if (world.Weapons.Raycast(head + new System.Numerics.Vector3(1.2f * k, 0f, 0f), up, WeatherShelter.RayReachM) is { } hit) { hits++; ceiling = Math.Min(ceiling, hit.Distance + 1.6f); }
                cover = hits / 5f;
                // the weather finish: one ray down from the head finds the ground under the player (splashes, mist and Low Fog lie on it; a jump or a third-person camera no longer lifts them)
                if (world.Weapons.Raycast(head, new System.Numerics.Vector3(0f, 0f, -1f), WeatherShelter.RayReachM) is { } floor)
                    _wxGroundY = (_gameSpace.GlobalTransform * new Vector3(head.X, head.Y, head.Z - floor.Distance)).Y;
                else _wxGroundY = null;
                _wxSunSeen = WeatherSunRays(world, cam);
            }
            if (_wxForceCover > 0) { bool covered = (long)(_connectedFor / _wxForceCover) % 2 == 1; cover = covered ? 1f : 0f; ceiling = covered ? 12f : WeatherShelter.RayReachM; }   // test aid --weather-shelter-test <s>: alternating cover and open, s seconds each, whatever the rays found
            if (_wxPerfLive) { double ms = System.Diagnostics.Stopwatch.GetElapsedTime(rayT0).TotalMilliseconds; _wxPerfRayMs += ms; _wxPerfRayMax = Math.Max(_wxPerfRayMax, ms); _wxPerfRays++; }
            bool was = _wxShelter.Covered;
            _wxShelter.Sample(cover, ceiling);
            if (LogSound("weather") && ++_wxShelterSamples % 18 == 0) GD.Print($"weather shelter sample: level {_wxShelter.Level:F2} covered share {cover:F2} ceiling {ceiling:F1} m at ({me.Position.X:F0}, {me.Position.Y:F0}, {me.Position.Z:F1}); sun seen {_wxSunSeen:F2} toward ({_wxToSun.X:F2}, {_wxToSun.Y:F2}, {_wxToSun.Z:F2}){(_wxBedShelter ? $"; Warfront wind bed {_musouWind.LiveDb:F1} dB, low-pass +{_musouWind.LiveLpf:F1} units" : "")}");   // every 3 s, a test aid
            if (was != _wxShelter.Covered && LogSound("weather")) GD.Print($"weather shelter: {(_wxShelter.Covered ? "under cover" + (_wxShelter.Tunnel ? " (tunnel)" : "") : "in the open")} (covered share {cover:F2}, ceiling {ceiling:F1} m)");
        }
        _wxShelter.Step(delta);
    }

    float _wxSunSeen = 1f;
    Vector3 _wxToSun;   // the last sun ray direction (game space; the shelter sample log)

    /// <summary>
    /// Weather fidelity tune (review: the ash grit read as near-white sleet in Waste Management's shade): with the shelter's rays, five rays toward the map's sun (from the camera and 2.5 m to either side and ahead and behind
    /// it, <see cref="WeatherRig.SunRayReachM"/> reach) give the share of the air round the camera the sun reaches (1 with no sun to read). The rig dims the layers' sun share by it, so drifting ash in a street's shade is as
    /// dark as the shade. Five more struct casts at the shelter's 6 Hz.
    /// </summary>
    float WeatherSunRays(SmSim.CollisionWorld world, Vector3 cam)
    {
        if (_sun is null || !_sun.Visible || _gameSpace is null) return 1f;
        var toSun = (_gameSpace.GlobalTransform.Basis.Inverse() * _sun.GlobalTransform.Basis.Z).Normalized();
        _wxToSun = toSun;
        if (toSun.Z <= 0.02f) return 0f;   // the sun at or under the horizon lights no air
        var dir = new System.Numerics.Vector3(toSun.X, toSun.Y, toSun.Z);
        int seen = 0;
        for (int k = 0; k < 5; k++)
        {
            var o = k switch { 1 => new Vector3(2.5f, 0f, 0f), 2 => new Vector3(-2.5f, 0f, 0f), 3 => new Vector3(0f, 2.5f, 0f), 4 => new Vector3(0f, -2.5f, 0f), _ => Vector3.Zero };
            if (world.Weapons.Raycast(new System.Numerics.Vector3(cam.X + o.X, cam.Y + o.Y, cam.Z + o.Z), dir, WeatherRig.SunRayReachM) is null) seen++;
        }
        return seen / 5f;
    }

    /// <summary>Once a frame: the clock, the link the grade hook and the F6 panel read, the rig.</summary>
    bool _wxPinRead;
    double _wxForceCover;
    bool _wxPerfLive;
    readonly System.Collections.Generic.List<double> _wxPerfFrames = new();
    double _wxPerfScriptMs;
    double _wxPerfStart = double.NaN;
    WeatherId _wxPerfId = WeatherId.None;
    double _wxPerfRayMs, _wxPerfRayMax; int _wxPerfRays;

    /// <summary>Test aid (weather W5, <c>--weather-perf-live</c>, windowed): vsync off, then a <c>WXPERF</c> line every 600 frames after a 5 s warm-up: the preset and phase, the rig's particles, the mean and 95th percentile wall frame, the engine's process time and the weather's own script cost per frame. For real-map runs; the placeholder-stage aid is the stills scene's <c>--weather-perf</c>.</summary>
    void WeatherPerfLive(double delta, double scriptMs)
    {
        if (!_wxPerfLive) return;
        _wxPerfStart = double.IsNaN(_wxPerfStart) ? _connectedFor : _wxPerfStart;
        if (_connectedFor - _wxPerfStart < 5.0) return;
        if (_wxModel.State.Id != _wxPerfId) { _wxPerfId = _wxModel.State.Id; _wxPerfFrames.Clear(); _wxPerfScriptMs = 0; _wxPerfRayMs = 0; _wxPerfRayMax = 0; _wxPerfRays = 0; }   // a window never straddles two presets
        _wxPerfFrames.Add(delta * 1000.0); _wxPerfScriptMs += scriptMs;
        if (_wxPerfFrames.Count < 600) return;
        var sorted = _wxPerfFrames.ToArray(); Array.Sort(sorted);
        var st = _wxModel.State;
        GD.Print($"WXPERF {WeatherCatalog.Of(st.Id).Key} {st.Phase} particles {_wxRig?.ParticleTotal ?? 0} shelter {_wxShelter.Level:F2} frames {sorted.Length} mean {System.Linq.Enumerable.Average(sorted):F3} ms p95 {sorted[(int)(sorted.Length * 0.95)]:F3} ms process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:F3} ms weather script {_wxPerfScriptMs / sorted.Length:F4} ms shelter samples {_wxPerfRays} mean {(_wxPerfRays > 0 ? _wxPerfRayMs / _wxPerfRays : 0):F3} ms max {_wxPerfRayMax:F3} ms preset {MapLightingView.Preset} renderer {RenderingServer.GetCurrentRenderingMethod()}");
        _wxPerfFrames.Clear(); _wxPerfScriptMs = 0; _wxPerfRayMs = 0; _wxPerfRayMax = 0; _wxPerfRays = 0;
    }

    bool _wxSpritesDumpDecided, _wxSpritesSession;
    double _wxSpriteWait;

    /// <summary>The longest the first weather waits for a sprite read under way (s) before it draws with the procedural look.</summary>
    const double WeatherSpriteWaitSeconds = 3.0;

    /// <summary>
    /// Weather fidelity: the weather's original sprites are read on a worker (<see cref="WeatherSprites"/>) as soon as the match's effects package is read (tune: from the first weather frame the background effects read has
    /// finished, without waiting for the effect player to be built; the weather frames run once the client is connected), and the first weather waits up to 3 s for a read under way so it does not pop in; a bot never draws, so
    /// never reads them. The <c>--weather-sprites-dump</c> test aid acts only in an F6 developer-tools session and never into the repository, any folder inside a Git work tree, or the game install; while it is asked for, the read
    /// waits until the session announces developer tools or 4 s have passed.
    /// </summary>
    void WeatherSpritesFrame(bool connected)
    {
        if (_bot || WeatherSprites.Procedural) return;
        if (!_wxSpritesSession) { _wxSpritesSession = true; WeatherSprites.ClearDump(); }   // tune 3 (review): no dump folder carries over from an earlier session
        if (!_wxSpritesDumpDecided && CmdArg("weather-sprites-dump") is { } dump)
        {
            if (!connected || (_client?.Dev is null && _connectedFor < 4.0)) return;   // the gate is known once the session announces developer tools, or after 4 s without
            _wxSpritesDumpDecided = true;
            string repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
            if (_client?.Dev is null) GD.Print("weather sprites dump ignored: this server has no developer tools");
            else if (WeatherSprites.TrySetDump(dump, repoRoot, _gameDir) is { } refusal) GD.Print(refusal);
        }
        _wxSpritesDumpDecided = true;
        var library = _fx?.Library ?? (_fxLoad is { IsCompletedSuccessfully: true } load ? load.Result.Library : null);
        WeatherSprites.Request(library);
        WeatherSprites.Pump();
    }

    void WeatherFrame(PlayerState me, double delta)
    {
        long wxT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        WeatherFrameCore(me, delta);
        if (_wxPerfLive) WeatherPerfLive(delta, System.Diagnostics.Stopwatch.GetElapsedTime(wxT0).TotalMilliseconds);
    }

    double _wxReqCover;
    bool _wxReqPerf, _wxAidsGate, _wxAidsNoteLogged;
    DisplayServer.VSyncMode _wxVsyncPrev;

    /// <summary>
    /// Weather fix 2: the three W5 test aids (<c>--weather-pin</c>, <c>--weather-shelter-test</c>, <c>--weather-perf-live</c>) act only while the server announces developer tools (<c>GameClient.Dev</c>, the F6 gate: loopback server,
    /// loopback client, <c>--dev-tools</c>); on any other server they are ignored with one log line, so a release client always draws the match's phase and the real shelter. When the gate lapses (or the connection ends) the clock
    /// returns to the server's, the forced cover stops, and the vsync mode the perf aid replaced comes back.
    /// </summary>
    void WeatherAidsApply(bool gate)
    {
        bool wanted = _wxReqCover > 0 || _wxReqPerf || _wxModel.PinPhase is not null || _wxModel.PinLull || _wxModel.PinStrike || _wxFlashOverride is not null;
        if (wanted && !gate && !_wxAidsNoteLogged && _client is { Status: ClientStatus.Connected } && _connectedFor >= 4.0) { _wxAidsNoteLogged = true; GD.Print("weather test aids ignored: this server has no developer tools"); }
        if (gate == _wxAidsGate) return;
        _wxAidsGate = gate;
        _wxModel.PinAllowed = gate;
        _wxForceCover = gate ? _wxReqCover : 0;
        if (gate && _wxReqPerf) { _wxVsyncPrev = DisplayServer.WindowGetVsyncMode(); DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); _wxPerfLive = true; }
        else if (_wxPerfLive)
        {
            _wxPerfLive = false; if (DisplayServer.WindowGetVsyncMode() == DisplayServer.VSyncMode.Disabled) DisplayServer.WindowSetVsyncMode(_wxVsyncPrev);   // not over a choice the player made in Options meanwhile
            _wxPerfFrames.Clear(); _wxPerfScriptMs = 0; _wxPerfStart = double.NaN; _wxPerfId = WeatherId.None; _wxPerfRayMs = 0; _wxPerfRayMax = 0; _wxPerfRays = 0;
        }
    }

    void WeatherFrameCore(PlayerState me, double delta)
    {
        if (!_wxPinRead)
        {
            _wxPinRead = true;   // test aids (client only, F6 developer-tools session only; weather fix 2): read once, acted on only while WeatherAidsGate
            if (CmdArg("weather-shelter-test") is { } forced && double.TryParse(forced, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double period) && period >= 1) _wxReqCover = period;
            _wxReqPerf = CmdFlag("weather-perf-live");
            _wxReqDensity = CmdFlag("weather-density");
            if (CmdArg("storm-lightning") is { } sl)   // repair 3 test aid: the Storm lightning option for this run only (the saved setting is not touched)
            {
                string k = sl.Trim().ToLowerInvariant();
                if (k is "full" or "reduced") { _wxFlashOverride = k == "full"; GD.Print($"weather: storm lightning {k} for this run (--storm-lightning; not saved)"); }
                else GD.Print($"weather: --storm-lightning '{sl}' is not full or reduced; ignored");
            }
            if (CmdArg("weather-pin") is { } pin)
            {
                if (WeatherClientModel.TryParsePinWord(pin, out var pinned, out bool lull)) { _wxModel.PinPhase = pinned; _wxModel.PinLull = lull; _wxModel.PinStrike = pin.Trim().Equals("strike", StringComparison.OrdinalIgnoreCase); }
                else GD.Print($"weather: --weather-pin '{pin}' is not opening, buildup, peak, recovery, lull, full or strike; ignored");
            }
        }
        bool connected = WeatherOn && _client is { Status: ClientStatus.Connected };
        if (WeatherOn) WeatherSpritesFrame(connected);   // tune: before the connected check below (the weather frames themselves run only once connected)
        if (!connected)
        {
            _musouWind.LiveDb = 0f; _musouWind.LiveLpf = 0f; _wxBedShelter = false;
            WeatherAidsApply(false);
            if (_wxModel.Has) _wxModel.Clear();   // the connection ended (or never was): the default look at once
            WeatherClientLink.Release(_wxModel);
            WeatherInstallStop();   // at once, fading loops included (repair 3)
            if (_wxRig is not null) { WeatherThunderStopAll(); _wxRig.QueueFree(); _wxRig = null; }
            else if (_wxOneShots.Count > 0) WeatherThunderStopAll();
            _wxFadePrev = WeatherId.None;
            WeatherStormRestore();
            return;
        }
        WeatherClientLink.Model = _wxModel;
        WeatherClientLink.Send = payload => _client?.SendExtrasCommand(WeatherWire.CommandTag, payload) == true;
        WeatherClientLink.TestMove = WeatherTestMoveWord;   // assigned each frame like Send (review: never a stale view's delegate)
        WeatherTestSlide();   // repair 1 test aid (F6 only): the camera slid sideways before the rig reads it
        _wxModel.FullFlash = (_client?.Dev is not null ? _wxFlashOverride : null) ?? WeatherSettings.FullFlash;   // the player's storm lightning option (client only); repair 3: --storm-lightning or wxflash for this run only, never saved, and only in an F6 developer-tools session
        WeatherAidsApply(_client?.Dev is not null);
        _wxModel.Advance(delta);
        var st = _wxModel.State;
        float intensity = _wxModel.EffectiveIntensity;
        var now = (st.Id, st.Phase, _wxModel.Overridden);   // (the rig below is drawn for this state)
        if (LogSound("weather") && (_wxLevelLogAt += delta) >= 2.0) { _wxLevelLogAt = 0; GD.Print($"weather level: {WeatherCatalog.Of(st.Id).Key} {st.Phase} intensity {intensity:F2}{(_wxModel.Pinned ? " (pinned)" : "")}{(_wxRig is { } r ? $" hiss {r.HissDb:F1} dB wind {r.WindDb:F1} dB light {r.LightFactor:F2} (ambient {r.MapLevelsNow.Ambient:F2} sun {r.MapLevelsNow.Sun:F2} exposure {r.MapLevelsNow.Exposure:F2} sun seen {r.SunSeen:F2} shelter {r.Shelter:F2})" : "")}"); }   // repair 2 test aid: the drawn level and the beds' gains every 2 s
        if (!_bot && (!_wxLoggedAny || now != _wxLogged))
        {
            _wxLoggedAny = true; _wxLogged = now;
            GD.Print($"weather: {WeatherCatalog.Of(st.Id).Key} {st.Phase}{(now.Item3 ? " (dev tools)" : "")}{(WeatherCatalog.Of(st.Id).Real ? "" : " (not built: default look)")}");
        }
        bool wantRig = !_bot && WeatherNeedsRig(st.Id, intensity) && _camera is not null;   // a bot client draws and hears nothing
        if (wantRig && _wxRig is null && WeatherSprites.Pending && (_wxSpriteWait += delta) < WeatherSpriteWaitSeconds) wantRig = false;   // tune: the first weather waits briefly for a read under way (no procedural-to-sprite pop)
        if (wantRig && _wxRig is null)
        {
            AddChild(_wxRig = new WeatherRig { Name = "WeatherRig" });
            _wxRig.TrimChanged += (level, total) => { if (LogSound("weather")) GD.Print($"weather trim: level {level} ({WeatherTrim.Steps[level] * 100:0} % of the preset's counts, floors kept), {total} particles"); };
            if (!_wxBedShelter) { _wxShelter.Reset(); _wxShelterClock = 1.0; }   // the first frame samples at once; a Warfront bed already under cover keeps its level (no audible open-up)
        }
        bool shelterRan = false;
        if (_wxRig is not null)
        {
            if (!wantRig && !_wxRig.Active) { WeatherInstallStop(fade: true); _wxRig.QueueFree(); _wxRig = null; if (!_wxBedShelter) _wxShelter.Reset(); }
            else
            {
                shelterRan = true;
                _wxRig.FullFlash = _wxModel.FullFlash;
                _wxRig.MapSun = _sun; _wxRig.MapEnvironment = _camera?.Environment ?? _environment; _wxRig.SunSeen = _wxForceCover > 0 ? 1f - _wxShelter.Level : _wxSunSeen;   // weather fidelity tune: the layers follow the map's light at the camera
                WeatherShelterFrame(me, delta);
                _wxRig.SetShelter(_wxShelter.Level, _wxShelter.Tunnel);
                _wxRig.StepTrim(delta);
                Vector3? leadFrom = _wxSlideHas || _gameSpace is null ? null : _gameSpace.GlobalTransform * new Vector3(me.Position.X, me.Position.Y, me.Position.Z);   // the player's place; the camera while the wxslide aid moves it alone
                WeatherDensityLog(delta);   // before the update: the layers and the camera as the last frame drew them
                _wxRig.Update(st, intensity, _camera?.GlobalPosition ?? Vector3.Zero, _wxGroundY, leadFrom);
                WeatherInstallSounds(st, intensity);
            }
        }
        WeatherFlashLog(st);
        WeatherInstallFades();   // repair 3: the grit's and the sheltered wind's stop fades, rig or not
        WeatherThunderFadeFrame(st.Id);
        shelterRan = WeatherBedFrame(me, delta, shelterRan);
        WeatherStormFrame(me, delta, st, shelterRan);
    }

    bool _wxReqDensity;
    double _wxDensityAt, _wxDensityClock;
    Vector3 _wxDensityCam;
    bool _wxDensityHas;

    /// <summary>
    /// Rain motion (series 37) test aid, <c>--weather-density</c> (F6 developer-tools session only, like the W5 aids): every 0.1 s a <c>weather density:</c> line with the session time, the camera's place and velocity (m/s, Godot axes, y up)
    /// and the live drops of the near rain, far rain and ash grit within <see cref="WeatherRig.DensityRadiusM"/> m of the camera, computed by the fields' reference arithmetic (`WeatherField.CountWithin`, the shader's placement mirrored in C#; not read back from the GPU: rendering is confirmed by stills).
    /// </summary>
    void WeatherDensityLog(double delta)
    {
        if (!_wxReqDensity || !_wxAidsGate || _wxRig is null || _camera is null) { _wxDensityHas = false; return; }
        _wxDensityClock += delta;
        if (_wxDensityClock - _wxDensityAt < 0.1) return;
        var cam = _wxRig.GlobalPosition;   // the camera the rig followed last frame
        var v = _wxDensityHas ? (cam - _wxDensityCam) / (float)(_wxDensityClock - _wxDensityAt) : Vector3.Zero;
        _wxDensityAt = _wxDensityClock; _wxDensityCam = cam; _wxDensityHas = true;
        var (near, far, grit) = _wxRig.DensityAround();
        GD.Print($"weather density: t={_connectedFor:F2} cam ({cam.X:F1}, {cam.Y:F1}, {cam.Z:F1}) v ({v.X:F1}, {v.Y:F1}, {v.Z:F1}) near {near} far {far} grit {grit} within {WeatherRig.DensityRadiusM:0} m");
    }

    bool _wxBedShelter;
    double _wxLevelLogAt;
    /// <summary>Repair 3: the Storm lightning setting for this run only (<c>--storm-lightning</c>, <c>wxflash</c>); null: the saved option.</summary>
    bool? _wxFlashOverride;

    // ---- repair 3: the flash log (a test aid; no sound needed) ----

    bool _wxFlashOn;
    float _wxFlashGlow, _wxFlashExposure;
    double _wxFlashStart;

    /// <summary>
    /// One <c>weather flash:</c> line per strike pulse while the strike pin acts or <c>--log-sound weather</c> (which needs no audio device: it only prints): when the drawn pulse ends, the storm time it began at, the flash setting
    /// (reduced or full), the peak sky glow alpha (reduced at most 0.06, full at most 0.30) and the peak exposure lift of the grade (reduced 0, full at most +0.09). A Thunderstorm only.
    /// </summary>
    void WeatherFlashLog(WeatherState st)
    {
        if (st.Id != WeatherId.Thunderstorm || !(LogSound("weather") || _wxModel.PinStrike && _wxModel.Pinned)) { _wxFlashOn = false; return; }
        float glow = _wxRig?.GlowAlpha ?? 0f;
        float lift = _wxModel.Grade.Exposure - WeatherGrade.For(st.Id, _wxModel.EffectiveIntensity).Exposure;
        bool on = glow > 0.0005f || lift > 0.0005f;
        if (on && !_wxFlashOn) { _wxFlashOn = true; _wxFlashGlow = 0f; _wxFlashExposure = 0f; _wxFlashStart = st.Elapsed; }
        if (on) { _wxFlashGlow = Math.Max(_wxFlashGlow, glow); _wxFlashExposure = Math.Max(_wxFlashExposure, lift); }
        else if (_wxFlashOn)
        {
            _wxFlashOn = false;
            GD.Print($"weather flash: strike pulse at {_wxFlashStart:F1} s, setting {(_wxModel.FullFlash ? "full" : "reduced")}, sky glow peak {_wxFlashGlow:F3} (cap {(_wxModel.FullFlash ? WeatherPlan.FullGlowMaxAlpha : WeatherPlan.GlowMaxAlpha):F2}), exposure lift +{_wxFlashExposure:F3}{(_wxModel.PinStrike && _wxModel.Pinned ? " (strike pin)" : "")}");
        }
    }

    /// <summary>
    /// Weather W5 (Warfront only): the battle's open-air wind bed (the install's <c>play_wind_open</c> loop) goes through the same shelter state as the weather's own sounds, so it is duller and quieter under a roof, in the authored
    /// look (None, no rig) and in every preset. With no rig the shelter is sampled here; with one it was sampled above. Classic PvP and Musou never run this: their bed (Musou) or lack of one (Classic) is untouched.
    /// </summary>
    bool WeatherBedFrame(PlayerState me, double delta, bool shelterRan)
    {
        bool bed = !_bot && _config is { } c && c.Extras.HasFlag(ContentExtras.Warfront) && _musouBedOn && _camera is not null;
        if (!bed)
        {
            if (_wxBedShelter) { _wxBedShelter = false; if (_wxRig is null) _wxShelter.Reset(); }
            _musouWind.LiveDb = 0f; _musouWind.LiveLpf = 0f;
            return shelterRan;
        }
        if (!shelterRan)
        {
            if (!_wxBedShelter) { _wxShelter.Reset(); _wxShelterClock = 1.0; }   // the first frame samples at once
            WeatherShelterFrame(me, delta);
            shelterRan = true;
        }
        _wxBedShelter = true;
        _musouWind.LiveDb = WeatherShelterMix.BedWindDb(_wxShelter.Level);
        _musouWind.LiveLpf = WeatherShelterMix.BedWindLpf(_wxShelter.Level);
        return shelterRan;
    }

    // ---- the storm's airborne layers (weather fix 1; Warfront only) ----

    AshfallStorm? _wxStorm;
    double _wxStormLookClock;
    float _wxStormNear = 1f, _wxStormFar = 1f;
    bool _wxStormShelter, _wxStormSeed;
    int _wxStormShape = -1, _wxStormMisses;
    (WeatherId Id, WeatherPhase Phase, bool Covered, bool Any) _wxStormLogged;
    bool _wxStormSettledLogged = true;

    /// <summary>
    /// Weather fix 1 (Warfront only): Warfront's map always drew the Ashfall storm's ash, flakes, embers and dust, whatever the weather. Now <see cref="WeatherStormLayers"/> says how much of its two airborne groups each preset keeps (None, Ash Squall
    /// and Ember Wind keep the storm, the near group fading out under a roof; Clear, Low Fog and Rain have none; a Thunderstorm has the far group only, ramping with its phases) and the shown value eases over 2 s to it. The storm node is found under the map
    /// (it is freed with the map). The shelter level is sampled here when neither the rig nor the wind bed did it this frame. Classic PvP and Musou never run this.
    /// </summary>
    void WeatherStormFrame(PlayerState me, double delta, WeatherState st, bool shelterRan)
    {
        if (_bot || _config is not { } c || !c.Extras.HasFlag(ContentExtras.Warfront)) return;
        if (_wxStorm is not null && !IsInstanceValid(_wxStorm)) { _wxStorm = null; _wxStormLookClock = 2.0; _wxStormMisses = 0; }   // a stage change freed it: look again at once
        if (_wxStorm is null)
        {
            if (_gameSpace is null) return;
            // The search walks the map's tree, so it runs only after the map's top-level shape changed (a band or its dressing was added or removed; the storm is attached by such a step), up to three tries two seconds apart, then rests
            // until the shape changes again (the hub, the Stormbird, --ashfall-weather plain and a failed Attach have no storm and cost nothing).
            int shape = 0;
            foreach (var child in _gameSpace.GetChildren()) shape = shape * 31 + child.GetChildCount() + 1;
            if (shape != _wxStormShape) { _wxStormShape = shape; _wxStormMisses = 0; _wxStormLookClock = 2.0; }
            if (_wxStormMisses >= 3) return;
            _wxStormLookClock += delta;
            if (_wxStormLookClock < 2.0) return;
            _wxStormLookClock = 0;
            _wxStorm = _gameSpace.FindChild("WarfrontStorm", true, false)?.GetNodeOrNull<AshfallStorm>("AshfallStorm");
            if (_wxStorm is null) { _wxStormMisses++; _wxStormShelter = false; return; }
            _wxStormSeed = true;   // a new storm starts at the preset's look, not at full ash that then eases away
            _wxStormLogged = default;
        }
        if (!shelterRan)
        {
            if (!_wxStormShelter && _wxRig is null && !_wxBedShelter) { _wxShelter.Reset(); _wxShelterClock = 1.0; }   // the first frame samples at once
            WeatherShelterFrame(me, delta);
        }
        _wxStormShelter = true;
        var def = WeatherCatalog.Of(st.Id);
        double progress = def.Duration(st.Phase) > 0 ? st.PhaseSeconds / def.Duration(st.Phase) : 1.0;
        var (near, far) = WeatherStormLayers.Of(st.Id, st.Phase, progress, _wxShelter.Level);
        if (_wxStormSeed) { _wxStormSeed = false; _wxStormNear = near; _wxStormFar = far; }
        else { _wxStormNear = WeatherStormLayers.Ease(_wxStormNear, near, delta); _wxStormFar = WeatherStormLayers.Ease(_wxStormFar, far, delta); }
        _wxStorm.SetAirborne(_wxStormNear, _wxStormFar);
        if (LogSound("weather"))
        {
            var key = (st.Id, st.Phase, _wxShelter.Covered, true);
            if (key != _wxStormLogged) { _wxStormLogged = key; _wxStormSettledLogged = false; GD.Print($"weather storm: {def.Key} {st.Phase}{(_wxShelter.Covered ? ", under cover" : ", in the open")}: target near {near:0.00} far {far:0.00}; now {_wxStorm.AirborneReport()}"); }
            else if (!_wxStormSettledLogged && _wxStormNear == near && _wxStormFar == far) { _wxStormSettledLogged = true; GD.Print($"weather storm: settled at near {near:0.00} far {far:0.00}: {_wxStorm.AirborneReport()}"); }
        }
    }

    /// <summary>Leaving the map or the connection: every layer back to as built (the storm node goes with the map anyway).</summary>
    void WeatherStormRestore()
    {
        if (_wxStorm is not null && IsInstanceValid(_wxStorm)) _wxStorm.SetAirborne(1f, 1f);
        _wxStorm = null; _wxStormNear = _wxStormFar = 1f; _wxStormShelter = false; _wxStormSeed = false; _wxStormShape = -1; _wxStormMisses = 0; _wxStormLookClock = 0; _wxStormLogged = default; _wxStormSettledLogged = true;
    }
}
