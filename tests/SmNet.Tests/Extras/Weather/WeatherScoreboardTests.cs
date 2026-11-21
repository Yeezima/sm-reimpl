using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Weather;

/// <summary>Weather fix B: the Classic scoreboard's weather line. None is silent (a plain None and a Random draw of None alike) except after an F6 removal.</summary>
public class WeatherScoreboardTests
{
    static WeatherState S(WeatherId id, WeatherPhase phase = WeatherPhase.Peak, double elapsed = 200) => new(id, phase, 10, elapsed, 1f);

    [Fact]
    public void None_is_silent_unless_dev_tools_removed_the_weather()
    {
        Assert.Null(WeatherScoreboard.Line(S(WeatherId.None), held: false, overridden: false));
        Assert.Null(WeatherScoreboard.Line(S(WeatherId.None), held: true, overridden: false));
        Assert.Equal("WEATHER: NONE (DEV TOOLS)", WeatherScoreboard.Line(S(WeatherId.None), held: false, overridden: true));
    }

    [Fact]
    public void A_preset_names_itself_and_its_phase()
    {
        Assert.Equal("WEATHER: RAIN, AT ITS HEIGHT", WeatherScoreboard.Line(S(WeatherId.Rain), false, false));
        Assert.Equal("WEATHER: RAIN, BUILDING (DEV TOOLS)", WeatherScoreboard.Line(S(WeatherId.Rain, WeatherPhase.Buildup), false, true));
        Assert.Equal("WEATHER: RAIN, STARTS WITH THE MATCH", WeatherScoreboard.Line(S(WeatherId.Rain, WeatherPhase.Opening, 0), true, false));
    }
}
