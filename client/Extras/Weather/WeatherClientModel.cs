using System;
using SmClient.Customizer.Model;
using SmNet.Extras.Weather;
using SmSim.Extras.Weather;

namespace SmClient.Extras.Weather;

/// <summary>
/// What the client knows of the weather (engine-free, tested): the last state the server sent and a clock that runs from it. The server's state is authoritative; between its repeats (every two seconds) the client
/// advances the same pure clock (<see cref="WeatherClock"/>), so the phase it draws is the server's. Presentation only.
/// </summary>
public sealed class WeatherClientModel
{
    WeatherStateWire _wire;
    double _elapsed;
    bool _have;

    public int Received { get; private set; }
    public int Dropped { get; private set; }
    public bool Has => _have;
    public bool Overridden => _have && _wire.Overridden;
    public byte Serial => _wire.Serial;

    /// <summary>The weather now, on the client's clock (None, intensity 0, until the server has spoken).</summary>
    public WeatherState State => _have ? WeatherClock.At(_wire.Id, _elapsed) : WeatherClock.At(WeatherId.None, 0);

    /// <summary>The intensity the look is scaled by: the state's, but 0 for a preset that is not built yet (a stub draws the map's default look until its milestone).</summary>
    public float EffectiveIntensity
    {
        get
        {
            var s = State;
            return WeatherCatalog.Of(s.Id).Real ? s.Intensity : 0f;
        }
    }

    /// <summary>The server's state section. A malformed one is dropped whole and counted.</summary>
    public bool Receive(ReadOnlySpan<byte> payload)
    {
        if (!WeatherWire.TryDecode(payload, out WeatherStateWire wire)) { Dropped++; return false; }
        _wire = wire;
        _elapsed = wire.ElapsedMs / 1000.0;
        _have = true;
        Received++;
        return true;
    }

    /// <summary>Runs the clock by <paramref name="seconds"/> of game time.</summary>
    public void Advance(double seconds)
    {
        if (_have && double.IsFinite(seconds) && seconds > 0) _elapsed += seconds;
    }

    /// <summary>The lighting grade of the weather now (identity when none or a stub).</summary>
    public LightingGrade Grade => WeatherGrade.For(State.Id, EffectiveIntensity);

    /// <summary>The state forgotten (the connection ended): the default look.</summary>
    public void Clear() { _have = false; _elapsed = 0; _wire = default; }
}
