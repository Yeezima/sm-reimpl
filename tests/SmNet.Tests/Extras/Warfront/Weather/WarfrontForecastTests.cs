using System.Text.Json;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Maps;
using SmSim.Extras.Warfront.Weather;
using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Warfront.Weather;

/// <summary>Weather W1: the Warfront forecast (a deterministic draw from a versioned pool), its place in the save (round trip, legacy saves) and its independence from the rolls and the maps.</summary>
public class WarfrontForecastTests
{
    [Fact]
    public void The_forecast_is_deterministic_per_seed_and_has_nine_valid_slots()
    {
        foreach (ulong seed in new ulong[] { 0, 1, 42, 4242, 0xDEADBEEF, ulong.MaxValue })
        {
            var a = WarfrontForecast.Draw(seed);
            Assert.Equal(a, WarfrontForecast.Draw(seed));
            Assert.Equal(WarfrontForecast.Slots, a.Length);
            Assert.True(WarfrontForecast.IsValid(a));
        }
        Assert.Equal(WarfrontSlots.All.Count, WarfrontForecast.Slots);
    }

    [Fact]
    public void Different_seeds_give_different_forecasts()
    {
        var distinct = Enumerable.Range(1, 200).Select(s => string.Join(',', WarfrontForecast.Draw((ulong)s))).Distinct().Count();
        Assert.True(distinct > 150, $"only {distinct} distinct forecasts in 200 seeds");
    }

    [Fact]
    public void Every_run_has_a_clear_enough_opener_on_some_first_node()
    {
        for (ulong seed = 0; seed < 3000; seed++)
        {
            var f = WarfrontForecast.Draw(seed);
            Assert.True(new[] { 0, 3, 6 }.Any(i => WarfrontForecast.ClearEnough((WeatherId)f[i])), $"seed {seed}: no clear-enough opener in {string.Join(',', f)}");
        }
    }

    [Fact]
    public void The_pool_holds_only_built_presets_and_the_stubs_stay_out_until_their_milestone()
    {
        Assert.All(WarfrontForecast.Pool, c => Assert.True(WeatherCatalog.Of(c.Id).Real));
        var drawn = Enumerable.Range(0, 2000).SelectMany(s => WarfrontForecast.Draw((ulong)s)).Distinct().Select(b => (WeatherId)b).ToHashSet();
        Assert.Equal([WeatherId.None, WeatherId.Clear, WeatherId.LowFog, WeatherId.Rain], drawn.OrderBy(i => (int)i));
    }

    [Fact]
    public void Soft_weights_tilt_depth_toward_fog_and_rain_in_the_dense_city()
    {
        int bad1 = 0, bad3 = 0, n = 3000;
        for (ulong s = 0; s < (ulong)n; s++)
        {
            var f = WarfrontForecast.Draw(s);
            bad1 += new[] { 0, 3, 6 }.Count(i => f[i] is (byte)WeatherId.LowFog or (byte)WeatherId.Rain);
            bad3 += new[] { 2, 5, 8 }.Count(i => f[i] is (byte)WeatherId.LowFog or (byte)WeatherId.Rain);
        }
        Assert.True(bad3 > bad1 * 2, $"fog/rain on node 3: {bad3}, on node 1: {bad1}");
    }

    [Fact]
    public void The_hq_and_the_fortress_are_never_drawn()
    {
        var f = WarfrontForecast.Draw(99);
        Assert.Equal(WeatherId.None, WarfrontForecast.ForSlot(f, 0));
        Assert.Equal(WeatherId.None, WarfrontForecast.ForSlot(f, 10));
        Assert.Equal(WeatherId.None, WarfrontForecast.ForSlot(f, 11));
        Assert.Equal(WeatherId.None, WarfrontForecast.ForSlot(null, 1));
        for (int n = 1; n <= 9; n++) Assert.Equal((WeatherId)f[n - 1], WarfrontForecast.ForSlot(f, n));
        Assert.Null(WarfrontSlots.ByName("wf_hq"));   // the HQ is not a lane slot, so no slot number reaches the forecast
    }

    [Fact]
    public void A_new_campaign_draws_its_forecast_from_its_seed_alone()
    {
        var a = new WarfrontCampaign(777, new ForcedRolls(true, true));
        var b = new WarfrontCampaign(777, new ForcedRolls(false, false));
        Assert.Equal(WarfrontForecast.Draw(777), a.Forecast);
        Assert.Equal(a.Forecast, b.Forecast);   // not a function of the roll source
        Assert.Equal(WarfrontForecast.PoolVersion, a.ForecastPool);
    }

    [Fact]
    public void The_forecast_round_trips_through_the_save()
    {
        var c = new WarfrontCampaign(31337);
        c.Join(1, "A", out _); c.Embark();
        var back = WarfrontSave.FromJson(c.ToJson());
        Assert.Equal(c.Forecast, back.Forecast);
        Assert.Equal(c.ForecastPool, back.ForecastPool);
        Assert.Equal(c.StateHash(), back.StateHash());
        Assert.Equal(WarfrontCampaign.SaveVersion, 5);
    }

    [Fact]
    public void An_older_save_gets_the_legacy_forecast_and_keeps_its_progress()
    {
        var c = new WarfrontCampaign(31337, new ForcedRolls(true, false));
        c.Join(1, "A", out _); c.Embark();
        Assert.True(c.TryDeploy(1, WarfrontTarget.Node(1), out _));
        c.BeginBattle(); c.Resolve(WarfrontOutcome.Win);
        // a version 4 file: no Forecast keys at all
        var node = System.Text.Json.Nodes.JsonNode.Parse(c.ToJson())!.AsObject();
        node["Version"] = 4;
        node.Remove("Forecast"); node.Remove("ForecastPool");
        var old = WarfrontSave.FromJson(node.ToJsonString());
        Assert.All(old.Forecast, id => Assert.Equal((byte)WeatherId.None, id));
        Assert.Equal(WarfrontForecast.Slots, old.Forecast.Count);
        Assert.Equal(0, old.ForecastPool);
        // progress and fronts untouched; the off-front rolls are the same ones
        Assert.Equal((c.Fronts(), c.Deployments, c.Rolls.Count), (old.Fronts(), old.Deployments, old.Rolls.Count));
        Assert.Equal(c.Rolls.Select(r => (r.Lane, r.Lost)), old.Rolls.Select(r => (r.Lane, r.Lost)));
        // and the legacy forecast is stable once saved again
        Assert.Equal(old.Forecast, WarfrontSave.FromJson(old.ToJson()).Forecast);
    }

    [Fact]
    public void A_damaged_forecast_in_a_new_save_falls_back_to_the_legacy_one()
    {
        var c = new WarfrontCampaign(5);
        var node = System.Text.Json.Nodes.JsonNode.Parse(c.ToJson())!.AsObject();
        node["Forecast"] = new System.Text.Json.Nodes.JsonArray(1, 2, 3);
        var back = WarfrontSave.FromJson(node.ToJsonString());
        Assert.All(back.Forecast, id => Assert.Equal((byte)WeatherId.None, id));
        node["Forecast"] = new System.Text.Json.Nodes.JsonArray(1, 2, 3, 4, 5, 6, 7, 8, 99);
        Assert.All(WarfrontSave.FromJson(node.ToJsonString()).Forecast, id => Assert.Equal((byte)WeatherId.None, id));
    }

    [Fact]
    public void Off_front_outcomes_are_the_same_with_any_forecast()
    {
        // The forecast is never read by the rules: the same plays on a run with its drawn forecast and on one with the legacy forecast roll identically.
        static WarfrontCampaign Play(bool legacy)
        {
            var c = new WarfrontCampaign(2024);
            if (legacy) c = WarfrontSave.FromJson(Strip(c.ToJson()));
            c.Join(1, "A", out _); c.Embark();
            for (int i = 0; i < 6 && !c.IsOver; i++)
            {
                var t = c.AvailableTargets().First(t => t.Kind == WarfrontTargetKind.Node);
                if (!c.TryDeploy(1, t, out _)) break;
                c.BeginBattle(); c.Resolve(i % 2 == 0 ? WarfrontOutcome.Win : WarfrontOutcome.Wipe);
            }
            return c;
        }
        var a = Play(false); var b = Play(true);
        Assert.Equal(a.Rolls.Select(r => (r.Deployment, r.Lane, r.Lost)), b.Rolls.Select(r => (r.Deployment, r.Lane, r.Lost)));
        Assert.Equal((a.Fronts(), a.Phase, a.Deployments), (b.Fronts(), b.Phase, b.Deployments));
    }

    static string Strip(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["Version"] = 4; node.Remove("Forecast"); node.Remove("ForecastPool");
        return node.ToJsonString();
    }
}

static class CampaignTestExtensions
{
    public static string Fronts(this WarfrontCampaign c) => $"{c.Front(0)},{c.Front(1)},{c.Front(2)}";
}
