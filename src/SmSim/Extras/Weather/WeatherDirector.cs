namespace SmSim.Extras.Weather;

/// <summary>
/// The server's weather for one match: the baseline preset (the campaign's forecast for the slot, later the host's choice), the match's own clock, and the developer-tools override
/// (a preset started at its opening phase, None, or Follow Match). Pure and tick-driven; presentation only. The override lives here and nowhere else: never in a save or a lobby config, cleared by <see cref="Reset"/>.
/// </summary>
public sealed class WeatherDirector(WeatherId baseline)
{
    double _match, _override;
    WeatherId _baseline = baseline;
    WeatherId? _forced;

    public WeatherId Baseline => _baseline;
    /// <summary>The override preset, or null while the match's own weather runs (Follow Match).</summary>
    public WeatherId? Override => _forced;
    public bool Overridden => _forced is not null;
    /// <summary>Rises whenever the shown preset changes or restarts; the log and tests can tell a new look from a repeat.</summary>
    public byte Serial { get; private set; }

    /// <summary>The weather now: the override's own clock while overridden, else the match's.</summary>
    public WeatherState Current => _forced is { } f ? WeatherClock.At(f, _override) : WeatherClock.At(_baseline, _match);

    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        _match += seconds;
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

    /// <summary>A new map or match: a new baseline from its opening, no override.</summary>
    public void Reset(WeatherId newBaseline)
    {
        _baseline = newBaseline;
        _match = 0;
        _forced = null;
        _override = 0;
        Serial++;
    }
}
