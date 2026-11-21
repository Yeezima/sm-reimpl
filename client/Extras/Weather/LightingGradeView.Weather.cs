using SmClient.Customizer.Model;
using SmClient.Extras.Weather;

namespace SmClient;

public sealed partial class LightingGradeView
{
    /// <summary>
    /// Weather W1: the weather's look is multiplied into the grade the frame applies, so it lies on top of whatever the map's lighting (the Ashfall storm) and the player's lighting preset made, with the same base tracking:
    /// no weather (or a stub, or None) is the identity and every touched value is written back. Weather is presentation only.
    /// </summary>
    partial void ExtrasGrade(ref LightingGrade grade)
    {
        if (WeatherClientLink.Model is { Has: true } model) grade = WeatherGrade.Combine(grade, model.Grade);
    }
}
