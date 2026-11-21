using SmNet;
using SmNet.Extras.Weather;
using SmSim.Extras.Weather;

namespace SmServer;

/// <summary>
/// Weather on the server (weather W1; docs/added-content.md "Weather"): the authoritative preset and phase of the match, replicated to every client as the <see cref="WeatherWire.StateTag"/> section, plus the
/// developer-tools override. Presentation only: nothing here is read by the simulation (damage, movement, weapons and AI never see it). A host that carries weather (Warfront today, through
/// <c>GameServer.WarfrontWeather.cs</c>) binds a <see cref="WeatherDirector"/> with <see cref="WeatherBind"/>; every other server has none and every method here returns at once.
/// </summary>
public sealed partial class GameServer
{
    WeatherDirector? _weather;
    readonly HashSet<int> _weatherKnown = new();
    byte _weatherSentSerial;
    uint _weatherNextTick;
    double _weatherLastRate = 20;

    /// <summary>The match's weather (tests and the log); null on a server that carries none.</summary>
    public WeatherDirector? Weather => _weather;
    /// <summary>Weather commands refused (not the developer-tools gate, or malformed).</summary>
    public int WeatherCommandsRefused { get; private set; }
    /// <summary>Weather commands applied.</summary>
    public int WeatherCommandsApplied { get; private set; }

    /// <summary>Starts this match's weather at <paramref name="baseline"/>. Called once by the added content that carries weather, at the end of its creation.</summary>
    void WeatherBind(WeatherId baseline)
    {
        _weather = new WeatherDirector(baseline);
        _weatherLastRate = Math.Max(1, World.Config.TickRate);
        Stepped += _ => _weather?.Advance(1.0 / _weatherLastRate);
        Log?.Invoke($"weather: baseline {WeatherCatalog.Of(baseline).Key} (catalog version {WeatherCatalog.Version}); presentation only");
    }

    /// <summary>Test seam: binds weather on a server that no weather-carrying content created.</summary>
    internal void WeatherBindForTests(WeatherId baseline) => WeatherBind(baseline);

    /// <summary>A new baseline from its opening phase and no override (a new battle on this map, or a new match).</summary>
    void WeatherRebase(WeatherId baseline)
    {
        if (_weather is not { } w) return;
        if (w.Baseline == baseline && !w.Overridden && w.Current.Elapsed < 1) return;
        w.Reset(baseline);
        _weatherKnown.Clear();
        Log?.Invoke($"weather: reset to {WeatherCatalog.Of(baseline).Key}, override cleared");
    }

    /// <summary>Once a tick (the dispatcher in GameServer.Extras.cs): the state to a client that has not heard it, on a change, and every <see cref="WeatherWire.RepeatSeconds"/> seconds.</summary>
    void WeatherSend()
    {
        if (_weather is not { } w) return;
        uint tick = World.Tick;
        int rate = (int)_weatherLastRate;
        bool changed = w.Serial != _weatherSentSerial;
        bool due = tick >= _weatherNextTick;
        byte[]? bytes = null;
        if (changed || due)
        {
            bytes = WeatherWire.Encode(WeatherWire.Of(w));
            SendExtras(new SmNet.ExtrasUpdate(tick, [(WeatherWire.StateTag, bytes)]));
            _weatherSentSerial = w.Serial;
            _weatherNextTick = tick + (uint)Math.Max(1, (int)(WeatherWire.RepeatSeconds * rate));
            _weatherKnown.Clear();
            foreach (var p in _peers.Values) _weatherKnown.Add(p.PlayerId);
            return;
        }
        // a client that joined (or rejoined) since the last send hears the current phase now
        foreach (var p in _peers.Values)
        {
            if (!_weatherKnown.Add(p.PlayerId)) continue;
            bytes ??= WeatherWire.Encode(WeatherWire.Of(w));
            SendExtras(new SmNet.ExtrasUpdate(tick, [(WeatherWire.StateTag, bytes)]), p.PlayerId);
        }
    }

    /// <summary>
    /// A weather command (command tag 43). Obeyed only under the developer-tools gate: the server started with <c>--dev-tools</c> on a loopback bind AND the request comes from a loopback address; a bot or spectator never commands it.
    /// Set starts a preset at its opening phase (None removes the added weather at once); Follow resumes the match's weather. The override is never saved and never part of a lobby config.
    /// </summary>
    void WeatherCommand(int player, byte tag, byte[] payload, ref bool handled)
    {
        if (tag != WeatherWire.CommandTag) return;
        handled = true;
        if (_weather is not { } w) { WeatherCommandsRefused++; return; }
        if (!DevToolsOn) { WeatherCommandsRefused++; NoteDevRefused("weather: this server was not started with --dev-tools on a loopback address"); return; }
        Peer? peer = null; int connection = -1;
        foreach (var (c, p) in _peers) if (p.PlayerId == player) { peer = p; connection = c; break; }
        if (peer is null || peer.Spectator || peer.Bot) { WeatherCommandsRefused++; return; }
        var remote = _transport.EndPointOf(connection);
        if (!DevToolsGate.FromLoopback(remote)) { WeatherCommandsRefused++; NoteDevRefused($"weather: {remote?.Address} is not a loopback address"); return; }
        if (!WeatherWire.TryDecode(payload, out WeatherCommand command)) { WeatherCommandsRefused++; return; }
        if (command.Kind == WeatherCommandKind.Set) { w.SetOverride(command.Id); Log?.Invoke($"dev tools: weather {WeatherCatalog.Of(command.Id).Key} from its opening phase (player {player}); not saved"); }
        else { w.FollowMatch(); Log?.Invoke($"dev tools: weather follows the match again (player {player}): {WeatherCatalog.Of(w.Baseline).Key}"); }
        WeatherCommandsApplied++;
    }
}
