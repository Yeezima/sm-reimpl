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
/// <c>LightingGradeView</c>'s hook (<see cref="WeatherGrade"/>), and <see cref="WeatherRig"/> draws rain and its sound around the camera. Warfront only today. No weather, a stub preset or None leaves the map exactly as it
/// was drawn: the model's grade is the identity, the rig is freed. Test aid: every change of the shown preset or phase is logged as <c>weather: ...</c>.
/// </summary>
public partial class NetworkView
{
    readonly WeatherClientModel _wxModel = new();
    WeatherRig? _wxRig;
    string _wxLogged = "";

    /// <summary>The weather carrier: a Warfront match (the profile's flag in the config the handshake agreed).</summary>
    bool WeatherOn => _config is { } c && c.Extras.HasFlag(ContentExtras.Warfront);

    /// <summary>The weather section of a tag; handled when the tag is weather's.</summary>
    void WeatherEnvelope(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (tag != WeatherWire.StateTag) return;
        handled = true;
        if (WeatherOn) _wxModel.Receive(payload);
    }

    /// <summary>Once a frame: the clock, the link the grade hook and the F6 panel read, the rig.</summary>
    void WeatherFrame(double delta)
    {
        bool connected = WeatherOn && _client is { Status: ClientStatus.Connected };
        if (!connected)
        {
            if (_wxModel.Has) _wxModel.Clear();   // the connection ended (or never was): the default look at once
            if (WeatherClientLink.Model == _wxModel) { WeatherClientLink.Model = null; WeatherClientLink.Send = null; }
            if (_wxRig is not null) { _wxRig.QueueFree(); _wxRig = null; }
            return;
        }
        WeatherClientLink.Model = _wxModel;
        WeatherClientLink.Send = payload => _client?.SendExtrasCommand(WeatherWire.CommandTag, payload) == true;
        _wxModel.Advance(delta);
        var st = _wxModel.State;
        float intensity = _wxModel.EffectiveIntensity;
        if (!_bot)
        {
            string line = $"{WeatherCatalog.Of(st.Id).Key} {st.Phase}{(_wxModel.Overridden ? " (dev tools)" : "")}{(WeatherCatalog.Of(st.Id).Real ? "" : " (stub: default look)")}";
            if (line != _wxLogged) { _wxLogged = line; GD.Print("weather: " + line); }
        }
        bool wantRig = st.Id == WeatherId.Rain && intensity > 0.02f && _camera is not null;
        if (wantRig && _wxRig is null) AddChild(_wxRig = new WeatherRig { Name = "WeatherRig" });
        if (_wxRig is not null)
        {
            if (!wantRig && !_wxRig.Raining) { _wxRig.QueueFree(); _wxRig = null; }
            else _wxRig.Update(st.Id, intensity, _camera?.GlobalPosition ?? Vector3.Zero);
        }
    }
}
