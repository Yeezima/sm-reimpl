using SmSim;
using SmSim.Extras.Weather;

namespace SmServer;

/// <summary>
/// The command line's side of Classic weather (weather W3): what <c>Program.cs</c> asks of the added content without naming it (the console server's composition root tests <c>SM_EXTRAS</c> and calls this). The one rule is
/// <see cref="WeatherClassic.Carries"/>: only a profile with the weather flag has weather; a server of any other profile ignores <c>--weather</c> with a line in the log.
/// </summary>
public static class ServerWeather
{
    /// <summary>Why <paramref name="key"/> is not a weather the server takes (the message after <c>--weather 'x':</c>), or null when it is one (or absent).</summary>
    public static string? KeyError(string? key) => key is null || WeatherClassic.NormalizeKey(key) is not null ? null : $"expected {WeatherClassic.Expected}";

    /// <summary>The profile carries Classic weather (the weather flag, in <c>extended</c> or alone).</summary>
    public static bool Carried(ContentExtras extras) => WeatherClassic.Carries(extras);
}
