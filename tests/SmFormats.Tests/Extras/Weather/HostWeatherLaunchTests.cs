using SmContent;
using SmSim;
using SmSim.Extras.Weather;
using Xunit;

namespace SmFormats.Tests.Extras.Weather;

/// <summary>
/// Weather W3: the Host panel's WEATHER choice reaches the real server launch through <c>HostOptions.Weather</c> and <c>ServerLaunch.Arguments</c> (<c>--weather &lt;key&gt;</c>), None sends nothing (the launch of every earlier
/// build is byte for byte unchanged), and the selector offers exactly what the server's flag takes. The Host panel itself is Godot code (checked in <c>UiSelfTest</c>); this is its launch contract.
/// </summary>
public class HostWeatherLaunchTests
{
    static HostOptions Options(string? weather, bool localOnly = true, string mode = ServerLaunch.ModeAnnihilation) =>
        new(mode, 27583, localOnly, "GAME", false, "extended", 0, BotDifficulty.Easy, null, null, true, null, null, null, weather);

    [Theory]
    [InlineData("clear")]
    [InlineData("low_fog")]
    [InlineData("rain")]
    [InlineData("thunderstorm")]
    [InlineData("ash_squall")]
    [InlineData("ember_wind")]
    [InlineData("random")]
    public void A_chosen_weather_is_the_servers_weather_flag_in_both_modes_and_both_reaches(string key)
    {
        foreach (var mode in new[] { ServerLaunch.ModeAnnihilation, ServerLaunch.ModeSeize })
            foreach (bool local in new[] { true, false })
            {
                var args = ServerLaunch.Arguments(Options(key, local, mode));
                int at = args.IndexOf("--weather");
                Assert.True(at >= 0 && at + 1 < args.Count, string.Join(" ", args));
                Assert.Equal(key, args[at + 1]);
                Assert.Equal(mode, args[args.IndexOf("--mode") + 1]);
                Assert.Single(args, "--weather");
                // the flag the server parses is one it accepts
                Assert.Equal(key, WeatherClassic.NormalizeKey(args[at + 1]));
            }
    }

    [Fact]
    public void None_and_no_choice_add_nothing_so_the_launch_is_what_it_was()
    {
        var without = ServerLaunch.Arguments(Options(null));
        Assert.DoesNotContain("--weather", without);
        Assert.Equal(without, ServerLaunch.Arguments(Options("")));
        Assert.Equal(without, ServerLaunch.Arguments(Options("  ")));
        var earlier = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27583, true, "GAME", false, "extended", 0, BotDifficulty.Easy, null, null, true, null, null, null));
        Assert.Equal(earlier, without);   // a HostOptions built before W3 launches exactly as one with no weather
    }

    [Fact]
    public void The_weather_comes_before_the_added_contents_own_arguments_and_leaves_the_rest_alone()
    {
        var plain = ServerLaunch.Arguments(Options(null) with { ExtraArgs = ["--x", "1"] });
        var weathered = ServerLaunch.Arguments(Options("rain") with { ExtraArgs = ["--x", "1"] });
        Assert.Equal(["--x", "1"], weathered.TakeLast(2));
        Assert.Equal(plain, weathered.Where((_, i) => !(i == weathered.IndexOf("--weather") || i == weathered.IndexOf("--weather") + 1)));
    }

    [Fact]
    public void The_selector_offers_none_the_presets_and_random_in_that_order_and_every_key_parses()
    {
        Assert.Equal("none", WeatherClassic.HostChoices[0].Key);   // the default
        Assert.Equal("random", WeatherClassic.HostChoices[^1].Key);
        Assert.Equal(8, WeatherClassic.HostChoices.Count);
        foreach (var c in WeatherClassic.HostChoices) Assert.Equal(c.Key, WeatherClassic.NormalizeKey(c.Key));
        Assert.Equal(WeatherId.None, WeatherClassic.Resolve("none", 1, 0));
        Assert.Equal(WeatherId.Rain, WeatherClassic.Resolve("rain", 1, 0));
    }

    [Fact]
    public void Only_a_profile_with_the_weather_flag_has_the_row()
    {
        Assert.True(ContentProfiles.TryParse("extended", out var extended, out _));
        Assert.True(WeatherClassic.Carries(extended));
        foreach (var name in new[] { "weather", "orks,weather", "gore,autocannon,weather" })
        {
            Assert.True(ContentProfiles.TryParse(name, out var withWeather, out _), name);
            Assert.True(WeatherClassic.Carries(withWeather), name);   // faithful gameplay plus weather, or any mix with it
        }
        foreach (var name in new[] { "faithful", "musou", "warfront", "orks", "orks,gore", "autocannon" })
        {
            Assert.True(ContentProfiles.TryParse(name, out var extras, out _), name);
            Assert.False(WeatherClassic.Carries(extras), name);
        }
    }
}
