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

    /// <summary>The weather carrier: a Warfront match (the profile's flag in the config the handshake agreed).</summary>
    bool WeatherOn => _config is { } c && c.Extras.HasFlag(ContentExtras.Warfront);

    /// <summary>A preset with its own things in the world (rain, ash, embers, the sky glow, wind) at this intensity.</summary>
    static bool WeatherNeedsRig(WeatherId id, float intensity) => id is WeatherId.Rain or WeatherId.Thunderstorm or WeatherId.AshSquall or WeatherId.EmberWind && intensity > 0.02f;

    /// <summary>The weather section of a tag; handled when the tag is weather's.</summary>
    void WeatherEnvelope(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (tag != WeatherWire.StateTag) return;
        handled = true;
        if (WeatherOn) _wxModel.Receive(payload);
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

    /// <summary>Once a frame: the clock, the link the grade hook and the F6 panel read, the rig.</summary>
    void WeatherFrame(double delta)
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
        if (wantRig && _wxRig is null) AddChild(_wxRig = new WeatherRig { Name = "WeatherRig" });
        if (_wxRig is not null)
        {
            if (!wantRig && !_wxRig.Active) { WeatherInstallStop(); _wxRig.QueueFree(); _wxRig = null; }
            else
            {
                _wxRig.FullFlash = _wxModel.FullFlash;
                _wxRig.Update(st, intensity, _camera?.GlobalPosition ?? Vector3.Zero);
                WeatherInstallSounds(st, intensity);
            }
        }
    }
}
