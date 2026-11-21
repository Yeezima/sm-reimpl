using SmClient.Extras.Weather;
using SmSim;

namespace SmClient.FrontEnd;

public sealed partial class OptionsView
{
    /// <summary>
    /// Weather W2: the Thunderstorm lightning choice, REDUCED (the default) or FULL. Client only; saved by <see cref="WeatherSettings"/> beside the other settings. Only in a build with added content.
    /// </summary>
    partial void ExtrasGraphicsRows()
    {
        if (!ContentExtras.Compiled) return;
        Choice("Storm lightning", ["REDUCED", "FULL"], WeatherSettings.FullFlash ? 1 : 0, i => WeatherSettings.FullFlash = i == 1,
            "Warfront's Thunderstorm only. REDUCED is a faint glow in the sky, wind and thunder, with no change to the picture's brightness. FULL adds brighter lightning flashes that briefly light the street (one flash per strike, at least 6 seconds apart, never a strobe).");
    }
}
