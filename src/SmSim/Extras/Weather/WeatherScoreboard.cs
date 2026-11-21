namespace SmSim.Extras.Weather;

/// <summary>
/// The Classic scoreboard's weather line (W3; pure, so it is tested): the active preset and where it stands. None says nothing (the map as it is), except after an F6 removal (weather fix B, owner 2026-11-22),
/// which says so, so a dev-tools test can tell a removal from a server without weather. A Random draw of None stays silent like a plain None.
/// </summary>
public static class WeatherScoreboard
{
    public static string? Line(WeatherState s, bool held, bool overridden)
    {
        if (s.Id == WeatherId.None) return overridden ? "WEATHER: NONE (DEV TOOLS)" : null;
        string phase = held && !overridden && s.Elapsed < 1 ? "STARTS WITH THE MATCH" : s.Phase switch
        {
            WeatherPhase.Opening => "SETTING IN",
            WeatherPhase.Buildup => "BUILDING",
            WeatherPhase.Peak => "AT ITS HEIGHT",
            _ => "EASING",
        };
        return $"WEATHER: {WeatherCatalog.Of(s.Id).Name.ToUpperInvariant()}, {phase}{(overridden ? " (DEV TOOLS)" : "")}";
    }
}
