using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Maps;
using SmSim.Extras.Warfront.Weather;
using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Warfront.Weather;

/// <summary>Weather W1: the stage baseline (what the server's <c>WarfrontWeatherBaseline</c> returns), the wipe-and-return case, and the pin that ties a weather retune to the Warfront rules version.</summary>
public class WarfrontWeatherBaselineTests
{
    [Fact]
    public void Only_a_battle_on_a_lane_slot_reads_the_forecast()
    {
        var f = WarfrontForecast.Draw(31337);
        foreach (var slot in WarfrontSlots.All)
        {
            Assert.Equal((WeatherId)f[slot.Number - 1], WarfrontForecast.Baseline(WarfrontPhase.Battle, slot.Name, f));
            Assert.Equal(WeatherId.None, WarfrontForecast.Baseline(WarfrontPhase.Ship, slot.Name, f));
            Assert.Equal(WeatherId.None, WarfrontForecast.Baseline(WarfrontPhase.Travel, slot.Name, f));
        }
        Assert.Equal(WeatherId.None, WarfrontForecast.Baseline(WarfrontPhase.Battle, "wf_hq", f));
        Assert.Equal(WeatherId.None, WarfrontForecast.Baseline(WarfrontPhase.Battle, "wf_fortress", f));
        Assert.Equal(WeatherId.None, WarfrontForecast.Baseline(WarfrontPhase.Battle, null, f));
    }

    [Fact]
    public void A_wipe_and_a_return_to_the_same_slot_read_the_same_forecast_even_through_a_save()
    {
        var c = new WarfrontCampaign(9001, new ForcedRolls(false, false));
        c.Join(1, "A", out _); c.Embark();
        var slot = WarfrontSlots.Of(TfwLane.A, 1);
        Assert.True(c.TryDeploy(1, WarfrontTarget.Node(0), out _));
        c.BeginBattle();
        var first = WarfrontForecast.Baseline(c.Phase, slot.Name, c.Forecast);
        c.Resolve(WarfrontOutcome.Wipe);
        Assert.Equal(WeatherId.None, WarfrontForecast.Baseline(c.Phase, slot.Name, c.Forecast));   // back on the ship
        c = WarfrontSave.FromJson(c.ToJson());   // a stage restart reads run.json
        c.Join(1, "A", out _);
        Assert.True(c.TryDeploy(1, WarfrontTarget.Node(0), out _));
        c.BeginBattle();
        Assert.Equal(first, WarfrontForecast.Baseline(c.Phase, slot.Name, c.Forecast));
        Assert.Equal((WeatherId)c.Forecast[slot.Number - 1], first);
    }

    [Fact]
    public void A_weather_retune_must_bump_the_warfront_rules_version()
    {
        // The catalog and the pool are in no hash; a retune that leaves WarfrontRules.Version alone would let two builds disagree silently. Change this pin together with the version.
        string signature = $"catalog {WeatherCatalog.Version}: " + string.Join(';', WeatherCatalog.All.Select(d => $"{d.Key}/{d.Real}/{d.OpeningLevel}/{string.Join(',', d.Seconds)}"))
            + $" pool {WarfrontForecast.PoolVersion}: " + string.Join(';', WarfrontForecast.Pool.Select(p => $"{p.Id}/{p.Node1}/{p.Node2}/{p.Node3}"));
        Assert.Equal("18|catalog 1: none/True/0/60,60,120,60;clear/True/0.5/60,60,240,60;low_fog/True/0.35/45,60,240,90;ash_squall/False/0.1/60,45,90,60;rain/True/0.3/45,60,180,75;thunderstorm/False/0.2/45,75,150,75;ember_wind/False/0.3/45,60,180,60 pool 1: None/3/2/2;Clear/4/2/1;LowFog/1/3/3;Rain/1/3/3",
            WarfrontRules.Version + "|" + signature);
    }
}
