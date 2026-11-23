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
    public WeatherState State => _have ? WeatherClock.At(_wire.Id, _elapsed, _wire.SpanSeconds) : WeatherClock.At(WeatherId.None, 0);

    /// <summary>The server holds the match clock (Classic PvP: waiting, countdown, end screen): this clock stands still too until the server says otherwise.</summary>
    public bool Held => _have && _wire.Held;

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
        _sinceWire = 0;
        _have = true;
        ApplyPin();
        Received++;
        return true;
    }

    /// <summary>
    /// Test aid (weather W5, <c>--weather-pin &lt;phase&gt;</c>, client only, never a player option): the clock this client draws is held inside that phase of the preset the server sent (it loops over the phase), so a perf or still
    /// run on a real map can sit at the Peak without waiting for it. It takes effect only while <see cref="PinAllowed"/> (the view sets it while the server announces developer tools, the F6 gate); otherwise the server's clock runs.
    /// The server's state and every other client are untouched; null (the default) changes nothing.
    /// </summary>
    public WeatherPhase? PinPhase { get => _pinPhase; set { _pinPhase = value; Refresh(); } }

    /// <summary>Whether a requested pin may act (weather fix 2: the developer-tools session only). Lapsing returns the clock to the server's at once.</summary>
    public bool PinAllowed { get => _pinAllowed; set { _pinAllowed = value; Refresh(); } }

    /// <summary>
    /// Repair 2 test aid (<c>--weather-pin lull</c>, the F6 word <c>wxpin:lull</c>): the clock this client draws is held at the very end of the preset's recovery, its lull (the lowest the active level gets), not looping, so a still or a
    /// sound level can be taken at the lull; a phase pin held only the phase's own loop, whose recovery starts at full. Same gate as <see cref="PinPhase"/>.
    /// </summary>
    public bool PinLull { get => _pinLull; set { _pinLull = value; Refresh(); } }
    bool _pinLull;

    /// <summary>
    /// Repair 3 test aid (<c>--weather-pin strike</c>, <c>wxpin:strike</c>; a Thunderstorm only): the drawn clock loops over a window of <see cref="StrikeLoopSeconds"/> s round the first scheduled strike of the storm's peak (from 1 s before
    /// it), so a real strike, its cloud glow, the full-flash lift when that setting is on, and (once per pin: the thunder plays each strike once, strictly in time order) its thunder come every few seconds through the normal code.
    /// Same gate as the other pins. Another preset ignores it.
    /// </summary>
    public bool PinStrike { get => _pinStrike; set { _pinStrike = value; Refresh(); } }
    bool _pinStrike;
    public const double StrikeLoopSeconds = 4.0;

    /// <summary>The strike the strike pin loops round (real seconds into the preset, the match's span applied), or null when the preset has none.</summary>
    public static double? PinnedStrikeTime(WeatherId id, int span)
    {
        if (id != WeatherId.Thunderstorm) return null;
        foreach (var s in WeatherPlan.StrikesIn(0, 4000, span))
            if (WeatherClock.At(id, s.Time, span).Phase == WeatherPhase.Peak) return s.Time;
        return null;
    }

    /// <summary>The pin is acting now.</summary>
    public bool Pinned => _pinAllowed && (_pinPhase is not null || _pinLull || _pinStrike && _wire.Id == WeatherId.Thunderstorm);   // the strike pin acts only in a Thunderstorm (another preset keeps the server's clock)

    /// <summary>A pin word: the four phase names, or <c>lull</c> (repair 2), or <c>full</c> (the peak); false for anything else.</summary>
    public static bool TryParsePinWord(string? text, out WeatherPhase? phase, out bool lull)
    {
        phase = null; lull = false;
        string t = text?.Trim().ToLowerInvariant() ?? "";
        if (t == "lull") { lull = true; return true; }
        if (t == "strike") return true;   // the caller sets PinStrike (TryParsePinWord leaves phase and lull empty for it)
        if (t == "full") { phase = WeatherPhase.Peak; return true; }
        if (TryParsePin(t, out var p)) { phase = p; return true; }
        return false;
    }

    /// <summary>Only the four phase names (any case) are a pin; a number such as "7" or "-1", an empty text or any other word is not (<see cref="Enum.TryParse{T}(string, bool, out T)"/> alone takes numbers and would index past the phases).</summary>
    public static bool TryParsePin(string? text, out WeatherPhase phase)
    {
        phase = WeatherPhase.Opening;
        if (text is null) return false;
        text = text.Trim();
        foreach (char c in text) if (!char.IsAsciiLetter(c)) return false;   // no digits, signs or comma lists (Enum.TryParse reads those)
        return text.Length > 0 && Enum.TryParse(text, true, out phase) && Enum.IsDefined(phase);
    }

    WeatherPhase? _pinPhase;
    bool _pinAllowed;
    double _pinClock, _sinceWire;

    void Refresh()
    {
        if (!_have) return;
        if (Pinned) ApplyPin();
        else { _pinClock = 0; _elapsed = _wire.ElapsedMs / 1000.0 + (_wire.Held ? 0 : _sinceWire); }   // back on the server's clock, from the last state it sent
    }

    void ApplyPin()
    {
        if (!Pinned || !_have || _wire.Id == WeatherId.None) return;
        var def = WeatherCatalog.Of(_wire.Id);
        if (_pinStrike)
        {
            if (PinnedStrikeTime(_wire.Id, _wire.SpanSeconds) is { } at) _elapsed = at - 1.0 + _pinClock % StrikeLoopSeconds;
            return;
        }
        if (_pinLull)
        {
            double all = 0; foreach (var sec in def.Seconds) all += sec;
            double lullAt = all - 1e-3;   // the last moment of the first recovery: the lull
            _elapsed = _wire.SpanSeconds > 0 ? lullAt * _wire.SpanSeconds / all : lullAt;
            return;
        }
        if (_pinPhase is not { } phase || !Enum.IsDefined(phase)) return;
        double start = 0, total = 0;
        for (int i = 0; i < def.Seconds.Length; i++) { if (i < (int)phase) start += def.Seconds[i]; total += def.Seconds[i]; }
        double len = Math.Max(def.Duration(phase), 1e-3);
        double into = start + _pinClock % len;
        _elapsed = _wire.SpanSeconds > 0 ? into * _wire.SpanSeconds / total : into;
    }

    /// <summary>Runs the clock by <paramref name="seconds"/> of game time.</summary>
    public void Advance(double seconds)
    {
        if (!_have || !double.IsFinite(seconds) || seconds <= 0) return;
        // final round (D1): a pin draws its own looping clock on real time, held match or not; before, a held clock (Classic Warmup, the countdown) froze the strike pin one second before its strike, so no strike, glow or flash came
        if (Pinned) { _pinClock += seconds; ApplyPin(); }
        if (_wire.Held) return;
        _sinceWire += seconds;
        if (!Pinned) _elapsed = _wire.ElapsedMs / 1000.0 + _sinceWire;
    }

    /// <summary>The full-flash setting (a client option; the default is reduced): a Thunderstorm's strikes then also lift the exposure, sun and shade for a moment. Set by the view each frame.</summary>
    public bool FullFlash { get; set; }

    /// <summary>The lighting grade of the weather now (identity when none).</summary>
    public LightingGrade Grade
    {
        get
        {
            var s = State;
            var g = WeatherGrade.For(s.Id, EffectiveIntensity);
            return FullFlash && s.Id == WeatherId.Thunderstorm ? WeatherGrade.WithFlash(g, WeatherPlan.FullFlash(s.Elapsed, s.Span)) : g;
        }
    }

    /// <summary>The state forgotten (the connection ended): the default look.</summary>
    public void Clear() { _have = false; _elapsed = 0; _wire = default; _pinClock = 0; _sinceWire = 0; }
}
