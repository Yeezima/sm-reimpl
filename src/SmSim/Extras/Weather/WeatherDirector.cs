namespace SmSim.Extras.Weather;

/// <summary>
/// The server's weather for one match: the baseline preset (the campaign's forecast for the slot, or Classic PvP's host choice), the match's own clock, and the developer-tools override
/// (a preset started at its opening phase, None, or Follow Match). Pure and tick-driven; presentation only. The override lives here and nowhere else: never in a save or a lobby config, cleared by <see cref="Reset"/>.
/// Classic PvP (W3) fits the baseline's cycle to the match (<paramref name="span"/>, seconds: <see cref="WeatherClock.At(WeatherId, double, int)"/>) and holds the match clock while the match is not being played
/// (<see cref="Held"/>: waiting, the countdown, the end screen); Warfront's has no span (the catalog's own lengths) and is never held.
/// </summary>
public sealed class WeatherDirector(WeatherId baseline, int span = 0)
{
    double _match, _override;
    WeatherId _baseline = baseline;
    WeatherId? _forced;
    int _span = Math.Max(0, span);

    public WeatherId Baseline => _baseline;
    /// <summary>The override preset, or null while the match's own weather runs (Follow Match).</summary>
    public WeatherId? Override => _forced;
    public bool Overridden => _forced is not null;
    /// <summary>The match length (seconds) the baseline's cycle is fitted to; 0: the catalog's own lengths. An override always runs the catalog's lengths from its opening phase.</summary>
    public int Span => _span;
    /// <summary>The match clock stands still (Classic PvP: not being played). An override's own clock never does.</summary>
    public bool Held
    {
        get => _held;
        set { if (_held == value) return; _held = value; Serial++; }   // the clients hear it at once, not at the next repeat
    }
    bool _held;
    /// <summary>The span and the hold of what is shown now: the override's are always none.</summary>
    public int ShownSpan => _forced is null ? _span : 0;
    public bool ShownHeld => _forced is null && Held;
    /// <summary>Rises whenever the shown preset changes or restarts; the log and tests can tell a new look from a repeat.</summary>
    public byte Serial { get; private set; }

    /// <summary>The weather now: the override's own clock while overridden, else the match's.</summary>
    public WeatherState Current => _forced is { } f ? WeatherClock.At(f, _override) : WeatherClock.At(_baseline, _match, _span);

    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        if (!Held) _match += seconds;
        if (_forced is not null) _override += seconds;
    }

    /// <summary>Picks a preset: it starts at its opening phase now. None is a preset like the others (it removes the added weather at once).</summary>
    public void SetOverride(WeatherId id)
    {
        _forced = id;
        _override = 0;
        Serial++;
    }

    /// <summary>Follow Match: the override ends and the match's weather resumes at its own current phase.</summary>
    public void FollowMatch()
    {
        if (_forced is null) return;
        _forced = null;
        _override = 0;
        Serial++;
    }

    /// <summary>A new map or match: a new baseline from its opening, no override. A span of null keeps the current one.</summary>
    public void Reset(WeatherId newBaseline, int? newSpan = null)
    {
        _baseline = newBaseline;
        if (newSpan is { } s) _span = Math.Max(0, s);
        _match = 0;
        _forced = null;
        _override = 0;
        Serial++;
    }
}
