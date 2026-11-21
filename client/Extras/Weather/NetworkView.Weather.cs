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
    static bool WeatherNeedsRig(WeatherId id, float intensity) => id is WeatherId.Rain or WeatherId.Thunderstorm or WeatherId.AshSquall or WeatherId.EmberWind && intensity > 0.02f;

    /// <summary>The weather section of a tag; handled when the tag is weather's.</summary>
    void WeatherEnvelope(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (tag != WeatherWire.StateTag) return;
        handled = true;
        if (WeatherOn) _wxModel.Receive(payload);
    }

    /// <summary>The scoreboard's weather line (Classic PvP, W3): the active preset and where it stands, so every player (a late joiner too) can see what the host chose. None says nothing (the map as it is).</summary>
    partial void ExtrasScoreboardNote(ref string note)
    {
        if (_config is not { } c || c.Extras.HasFlag(ContentExtras.Musou) || !_wxModel.Has) return;   // Warfront and Musou show their own
        var s = _wxModel.State;
        if (s.Id == WeatherId.None) return;
        string phase = _wxModel.Held && !_wxModel.Overridden && s.Elapsed < 1 ? "STARTS WITH THE MATCH" : s.Phase switch
        {
            WeatherPhase.Opening => "SETTING IN",
            WeatherPhase.Buildup => "BUILDING",
            WeatherPhase.Peak => "AT ITS HEIGHT",
            _ => "EASING",
        };
        note = $"WEATHER: {WeatherCatalog.Of(s.Id).Name.ToUpperInvariant()}, {phase}{(_wxModel.Overridden ? " (DEV TOOLS)" : "")}";
    }

    /// <summary>The view leaves the tree: the static link goes with it (a later Classic or Musou view must not keep this grade or show the F6 Weather section), and the rig is freed.</summary>
    partial void ExtrasExit()
    {
        WeatherClientLink.Release(_wxModel);
        _wxModel.Clear();
        WeatherInstallStop();
        if (_wxRig is not null && IsInstanceValid(_wxRig)) _wxRig.QueueFree();
        _wxRig = null;
    }

    // ---- shelter (weather W4): a few upward rays at a low rate, smoothed by the pure WeatherShelter ----

    readonly WeatherShelter _wxShelter = new();
    double _wxShelterClock;

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
            }
            bool was = _wxShelter.Covered;
            _wxShelter.Sample(cover, ceiling);
            if (was != _wxShelter.Covered && LogSound("weather")) GD.Print($"weather shelter: {(_wxShelter.Covered ? "under cover" + (_wxShelter.Tunnel ? " (tunnel)" : "") : "in the open")} (covered share {cover:F2}, ceiling {ceiling:F1} m)");
        }
        _wxShelter.Step(delta);
    }

    /// <summary>Once a frame: the clock, the link the grade hook and the F6 panel read, the rig.</summary>
    void WeatherFrame(PlayerState me, double delta)
    {
        bool connected = WeatherOn && _client is { Status: ClientStatus.Connected };
        if (!connected)
        {
            if (_wxModel.Has) _wxModel.Clear();   // the connection ended (or never was): the default look at once
            WeatherClientLink.Release(_wxModel);
            if (_wxRig is not null) { WeatherInstallStop(); _wxRig.QueueFree(); _wxRig = null; }
            return;
        }
        WeatherClientLink.Model = _wxModel;
        WeatherClientLink.Send = payload => _client?.SendExtrasCommand(WeatherWire.CommandTag, payload) == true;
        _wxModel.FullFlash = WeatherSettings.FullFlash;   // the player's storm lightning option (client only)
        _wxModel.Advance(delta);
        var st = _wxModel.State;
        float intensity = _wxModel.EffectiveIntensity;
        var now = (st.Id, st.Phase, _wxModel.Overridden);   // (the rig below is drawn for this state)
        if (!_bot && (!_wxLoggedAny || now != _wxLogged))
        {
            _wxLoggedAny = true; _wxLogged = now;
            GD.Print($"weather: {WeatherCatalog.Of(st.Id).Key} {st.Phase}{(now.Item3 ? " (dev tools)" : "")}{(WeatherCatalog.Of(st.Id).Real ? "" : " (not built: default look)")}");
        }
        bool wantRig = !_bot && WeatherNeedsRig(st.Id, intensity) && _camera is not null;   // a bot client draws and hears nothing
        if (wantRig && _wxRig is null)
        {
            AddChild(_wxRig = new WeatherRig { Name = "WeatherRig" });
            _wxRig.TrimChanged += (level, total) => { if (LogSound("weather")) GD.Print($"weather trim: level {level} ({WeatherTrim.Steps[level] * 100:0} % of the preset's counts, floors kept), {total} particles"); };
            _wxShelter.Reset(); _wxShelterClock = 1.0;   // the first frame samples at once
        }
        if (_wxRig is not null)
        {
            if (!wantRig && !_wxRig.Active) { WeatherInstallStop(); _wxRig.QueueFree(); _wxRig = null; _wxShelter.Reset(); }
            else
            {
                _wxRig.FullFlash = _wxModel.FullFlash;
                WeatherShelterFrame(me, delta);
                _wxRig.SetShelter(_wxShelter.Level, _wxShelter.Tunnel);
                _wxRig.StepTrim(delta);
                _wxRig.Update(st, intensity, _camera?.GlobalPosition ?? Vector3.Zero);
                WeatherInstallSounds(st, intensity);
            }
        }
    }
}
