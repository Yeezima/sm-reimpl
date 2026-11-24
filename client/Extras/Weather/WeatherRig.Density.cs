using Godot;

namespace SmClient.Extras.Weather;

/// <summary>
/// Rain motion (series 37) test aid: how many drops of each precipitation field are within a sphere round the camera now, for the <c>--weather-density</c> log. The count is COMPUTED by the fields' reference arithmetic
/// (<see cref="WeatherField.CountWithin"/>, the shader's placement mirrored in C#), not read back from the renderer; that the shader draws them is confirmed by windowed stills. (The series 37 "before" run read the old CPU emitters'
/// instance buffers, a branch removed with those emitters.) Never used in play.
/// </summary>
public sealed partial class WeatherRig
{
    /// <summary>The sphere's radius round the camera (m) the density aid counts in.</summary>
    public const float DensityRadiusM = 8f;

    /// <summary>The drops of the near rain, the far rain and the ash grit within <see cref="DensityRadiusM"/> of the camera, computed from the reference arithmetic.</summary>
    public (int Near, int Far, int Grit) DensityAround()
    {
        var c = GlobalPosition;
        return (Live(_rain, c), Live(_rainFar, c), Live(_ashMain, c));
    }

    static int Live(WeatherField? field, Vector3 c) => field is null || !field.Visible ? 0 : field.CountWithin(c, DensityRadiusM);
}
