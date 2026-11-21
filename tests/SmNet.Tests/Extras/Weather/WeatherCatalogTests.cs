using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Weather;

/// <summary>Weather W1: the catalog (stable ids), the phase clock and the server's director (override, Follow Match, reset). Pure; presentation only.</summary>
public class WeatherCatalogTests
{
    [Fact]
    public void The_catalog_has_the_seven_stable_ids_in_wire_order()
    {
        string[] keys = ["none", "clear", "low_fog", "ash_squall", "rain", "thunderstorm", "ember_wind"];
        Assert.Equal(keys, WeatherCatalog.All.Select(d => d.Key));
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal(i, (int)WeatherCatalog.All[i].Id);
            Assert.Same(WeatherCatalog.All[i], WeatherCatalog.Of((byte)i));
            Assert.Same(WeatherCatalog.All[i], WeatherCatalog.ByKey(keys[i]));
        }
        Assert.Null(WeatherCatalog.Of((byte)7));
        Assert.Null(WeatherCatalog.ByKey("blizzard"));
        Assert.False(WeatherCatalog.IsKnown(200));
    }

    [Fact]
    public void W1_builds_none_clear_low_fog_and_rain_and_leaves_the_rest_as_stubs()
    {
        Assert.Equal(["none", "clear", "low_fog", "rain"], WeatherCatalog.All.Where(d => d.Real).Select(d => d.Key));
    }

    [Theory]
    [InlineData(WeatherId.Clear)]
    [InlineData(WeatherId.LowFog)]
    [InlineData(WeatherId.Rain)]
    [InlineData(WeatherId.Thunderstorm)]
    public void The_cycle_runs_opening_once_then_buildup_peak_recovery_repeating(WeatherId id)
    {
        var d = WeatherCatalog.Of(id);
        Assert.Equal((WeatherPhase.Opening, 0f), (WeatherClock.At(id, 0).Phase, WeatherClock.At(id, 0).Intensity));
        double t = d.Duration(WeatherPhase.Opening);
        Assert.Equal(WeatherPhase.Buildup, WeatherClock.At(id, t + 0.01).Phase);
        t += d.Duration(WeatherPhase.Buildup);
        Assert.Equal(WeatherPhase.Peak, WeatherClock.At(id, t + 0.01).Phase);
        Assert.Equal(1f, WeatherClock.At(id, t + 0.01).Intensity);
        t += d.Duration(WeatherPhase.Peak);
        Assert.Equal(WeatherPhase.Recovery, WeatherClock.At(id, t + 0.01).Phase);
        t += d.Duration(WeatherPhase.Recovery);
        Assert.Equal(WeatherPhase.Buildup, WeatherClock.At(id, t + 0.01).Phase);   // the next cycle skips the opening
        Assert.Equal(WeatherPhase.Peak, WeatherClock.At(id, t + d.Duration(WeatherPhase.Buildup) + 0.01).Phase);
    }

    [Fact]
    public void Intensity_is_continuous_and_stays_within_zero_to_one()
    {
        foreach (var d in WeatherCatalog.All.Where(d => d.Id != WeatherId.None))
        {
            float last = 0f;
            for (double t = 0; t < 1200; t += 0.5)
            {
                float i = WeatherClock.At(d.Id, t).Intensity;
                Assert.InRange(i, 0f, 1f);
                Assert.True(Math.Abs(i - last) < 0.06f, $"{d.Key} jumps at {t}: {last} to {i}");
                last = i;
            }
        }
    }

    [Fact]
    public void None_has_phases_but_no_intensity_and_nonsense_time_is_the_start()
    {
        Assert.All(new[] { 0.0, 10, 100, 1000 }, t => Assert.Equal(0f, WeatherClock.At(WeatherId.None, t).Intensity));
        Assert.Equal(WeatherPhase.Opening, WeatherClock.At(WeatherId.Rain, double.NaN).Phase);
        Assert.Equal(WeatherPhase.Opening, WeatherClock.At(WeatherId.Rain, -5).Phase);
    }

    [Fact]
    public void The_director_follows_the_match_and_an_override_starts_at_its_opening_phase()
    {
        var d = new WeatherDirector(WeatherId.LowFog);
        d.Advance(200);
        var match = d.Current;
        Assert.Equal((WeatherId.LowFog, false), (match.Id, d.Overridden));
        d.SetOverride(WeatherId.Rain);
        Assert.Equal((WeatherId.Rain, WeatherPhase.Opening, true), (d.Current.Id, d.Current.Phase, d.Overridden));
        Assert.Equal(0.0, d.Current.Elapsed);
        d.Advance(100);
        Assert.Equal(WeatherPhase.Buildup, d.Current.Phase);   // 100 s into rain: past its 45 s opening
        d.FollowMatch();
        Assert.False(d.Overridden);
        Assert.Equal(WeatherId.LowFog, d.Current.Id);
        Assert.Equal(300.0, d.Current.Elapsed, 6);   // the match's own clock kept running underneath: 200 + 100
    }

    [Fact]
    public void None_as_an_override_removes_the_added_weather_at_once_and_follow_brings_it_back()
    {
        var d = new WeatherDirector(WeatherId.Clear);
        d.Advance(300);
        Assert.True(d.Current.Intensity > 0f);
        d.SetOverride(WeatherId.None);
        Assert.Equal((WeatherId.None, 0f), (d.Current.Id, d.Current.Intensity));
        d.FollowMatch();
        Assert.Equal(WeatherId.Clear, d.Current.Id);
        Assert.True(d.Current.Intensity > 0f);
    }

    [Fact]
    public void A_reset_takes_a_new_baseline_from_its_opening_and_clears_the_override()
    {
        var d = new WeatherDirector(WeatherId.Rain);
        d.Advance(500);
        d.SetOverride(WeatherId.Clear);
        byte serial = d.Serial;
        d.Reset(WeatherId.LowFog);
        Assert.False(d.Overridden);
        Assert.Equal((WeatherId.LowFog, WeatherPhase.Opening, 0.0), (d.Current.Id, d.Current.Phase, d.Current.Elapsed));
        Assert.NotEqual(serial, d.Serial);
    }

    [Fact]
    public void Follow_without_an_override_changes_nothing()
    {
        var d = new WeatherDirector(WeatherId.Rain);
        byte serial = d.Serial;
        d.FollowMatch();
        Assert.Equal(serial, d.Serial);
    }
}
