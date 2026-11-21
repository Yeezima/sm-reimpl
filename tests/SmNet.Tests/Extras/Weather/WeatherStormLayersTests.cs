using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Weather;

/// <summary>Weather fix 1: which share of Warfront's Ashfall storm (near and far airborne layers) each preset keeps, the 2 s ease, and the guard that only Warfront and weather code dims the storm.</summary>
public class WeatherStormLayersTests
{
    [Theory]
    [InlineData(WeatherId.None)]
    [InlineData(WeatherId.AshSquall)]
    [InlineData(WeatherId.EmberWind)]
    public void Storm_presets_keep_the_far_layers_and_fade_the_near_ones_under_a_roof(WeatherId id)
    {
        foreach (var phase in Enum.GetValues<WeatherPhase>())
        {
            Assert.Equal((1f, 1f), WeatherStormLayers.Of(id, phase, 0.5, 0f));
            Assert.Equal((0.25f, 1f), WeatherStormLayers.Of(id, phase, 0.5, 0.75f));
            Assert.Equal((0f, 1f), WeatherStormLayers.Of(id, phase, 0.5, 1f));
        }
    }

    [Theory]
    [InlineData(WeatherId.Clear)]
    [InlineData(WeatherId.LowFog)]
    [InlineData(WeatherId.Rain)]
    public void Clear_low_fog_and_rain_have_no_storm_air_at_all(WeatherId id)
    {
        foreach (var phase in Enum.GetValues<WeatherPhase>())
            foreach (float shelter in new[] { 0f, 0.5f, 1f })
                Assert.Equal((0f, 0f), WeatherStormLayers.Of(id, phase, 0.7, shelter));
    }

    [Fact]
    public void Thunderstorm_far_layers_ramp_in_through_buildup_and_out_through_recovery()
    {
        var t = WeatherId.Thunderstorm;
        Assert.Equal((0f, 0f), WeatherStormLayers.Of(t, WeatherPhase.Opening, 0.9, 0f));
        Assert.Equal((0f, 0f), WeatherStormLayers.Of(t, WeatherPhase.Buildup, 0.0, 0f));
        Assert.Equal((0f, 0.5f), WeatherStormLayers.Of(t, WeatherPhase.Buildup, 0.5, 0f));
        Assert.Equal((0f, 1f), WeatherStormLayers.Of(t, WeatherPhase.Buildup, 1.0, 0f));
        Assert.Equal((0f, 1f), WeatherStormLayers.Of(t, WeatherPhase.Peak, 0.3, 0f));
        Assert.Equal((0f, 0.75f), WeatherStormLayers.Of(t, WeatherPhase.Recovery, 0.25, 0f));
        Assert.Equal((0f, 0f), WeatherStormLayers.Of(t, WeatherPhase.Recovery, 1.0, 0f));
    }

    [Fact]
    public void Bad_inputs_stay_in_range()
    {
        var (n, f) = WeatherStormLayers.Of(WeatherId.None, WeatherPhase.Peak, double.NaN, float.NaN);
        Assert.Equal((1f, 1f), (n, f));
        Assert.Equal(0f, WeatherStormLayers.Of(WeatherId.None, WeatherPhase.Peak, 0, 5f).Near);
        Assert.Equal(1f, WeatherStormLayers.Of(WeatherId.None, WeatherPhase.Peak, 0, -3f).Near);
        Assert.Equal(1f, WeatherStormLayers.Of(WeatherId.Thunderstorm, WeatherPhase.Buildup, 9, 0f).Far);
    }

    [Fact]
    public void Ease_takes_about_two_seconds_for_a_full_swing_and_never_overshoots()
    {
        float v = 1f;
        for (double t = 0; t < 1.0; t += 1.0 / 60) v = WeatherStormLayers.Ease(v, 0f, 1.0 / 60);
        Assert.InRange(v, 0.45f, 0.55f);
        for (double t = 0; t < 1.1; t += 1.0 / 60) v = WeatherStormLayers.Ease(v, 0f, 1.0 / 60);
        Assert.Equal(0f, v);
        for (int i = 0; i < 300; i++) v = WeatherStormLayers.Ease(v, 1f, 1.0 / 60);
        Assert.Equal(1f, v);
        Assert.Equal(0.4f, WeatherStormLayers.Ease(0.4f, 1f, 0.0));
        Assert.Equal(0.4f, WeatherStormLayers.Ease(0.4f, 1f, double.NaN));
        Assert.Equal(1f, WeatherStormLayers.Ease(0f, 1f, 100.0));   // a long frame lands on the target
    }

    [Fact]
    public void Only_warfront_and_weather_client_code_dims_the_storm()
    {
        string root = AppContext.BaseDirectory;
        var d = new DirectoryInfo(root);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        var callers = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(d!.FullName, "client"), "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(d.FullName, file).Replace("\\", "/");
            if (rel.Split('/').Any(p => p is "bin" or "obj" or ".godot")) continue;
            foreach (string line in File.ReadAllLines(file))
                if (line.Contains("SetAirborne(") && !line.TrimStart().StartsWith("//") && !line.TrimStart().StartsWith("///") && !line.Contains("public void SetAirborne")) { callers.Add(rel); break; }
        }
        Assert.All(callers, c => Assert.StartsWith("client/Extras/Weather/", c));
        Assert.NotEmpty(callers);
    }
}
