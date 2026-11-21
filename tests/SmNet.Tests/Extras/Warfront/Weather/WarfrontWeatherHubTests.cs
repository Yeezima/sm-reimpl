using SmClient.Extras.Warfront.Hub;
using SmClient.Extras.Warfront.Stage;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Weather;
using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Warfront.Weather;

/// <summary>Weather W1: the per-slot forecast in the hub section and on the war map's nine nodes.</summary>
public class WarfrontWeatherHubTests
{

    [Fact]
    public void The_hub_carries_the_nine_slot_forecast_for_every_viewer()
    {
        var c = new WarfrontCampaign(77);
        c.Join(111, "Alpha", out _); c.Join(222, "Beta", out _); c.Embark();
        foreach (ulong viewer in new ulong[] { 111, 222 })
        {
            var bytes = WarfrontWire.Encode(WarfrontWire.HubFor(c, viewer));
            Assert.True(WarfrontWire.TryDecode(bytes, out WarfrontHubWire back));
            Assert.Equal(c.Forecast, back.Forecast);
            Assert.Equal(bytes, WarfrontWire.Encode(back));
        }
    }

    [Fact]
    public void A_hub_without_the_trailing_forecast_still_decodes_with_none_and_a_bad_one_is_refused()
    {
        var c = new WarfrontCampaign(77);
        c.Join(111, "Alpha", out _); c.Embark();
        var full = WarfrontWire.Encode(WarfrontWire.HubFor(c, 111));
        var withoutTail = full[..^(1 + WarfrontForecast.Slots)];
        Assert.True(WarfrontWire.TryDecode(withoutTail, out WarfrontHubWire old));
        Assert.Null(old.Forecast);
        var emptyTail = full[..^(WarfrontForecast.Slots)]; emptyTail[^1] = 0;
        Assert.True(WarfrontWire.TryDecode(emptyTail, out WarfrontHubWire none));
        Assert.Null(none.Forecast);
        var wrongCount = (byte[])full.Clone(); wrongCount[wrongCount.Length - 1 - WarfrontForecast.Slots] = 5;
        Assert.False(WarfrontWire.TryDecode(wrongCount, out WarfrontHubWire _));
        var unknownId = (byte[])full.Clone(); unknownId[^1] = 99;
        Assert.False(WarfrontWire.TryDecode(unknownId, out WarfrontHubWire _));
        Assert.False(WarfrontWire.TryDecode(full.Append((byte)1).ToArray(), out WarfrontHubWire _));
    }

    [Fact]
    public void The_hub_view_and_the_nine_node_labels_show_every_forecast_including_the_unavailable_nodes()
    {
        var c = new WarfrontCampaign(77);
        c.Join(111, "Alpha", out _); c.Embark();
        Assert.True(WarfrontWire.TryDecode(WarfrontWire.Encode(WarfrontWire.HubFor(c, 111)), out WarfrontHubWire wire));
        var view = WarfrontStageModel.ToView(wire);
        var model = new WarfrontHubModel();
        model.Apply(view);
        for (int lane = 0; lane < 3; lane++)
            for (int node = 1; node <= 3; node++)
            {
                var id = (WeatherId)c.Forecast[lane * 3 + node - 1];
                string label = model.NodeLabel(lane, node);
                Assert.EndsWith(" - " + WeatherCatalog.Of(id).Short, label);   // nodes 2 and 3 are still ork-held and unavailable: they show it too
                Assert.Equal(WeatherCatalog.Of(id).Name, model.WeatherName(lane, node));
            }
        // the view survives the model's round trip back to the wire
        Assert.Equal(c.Forecast, WarfrontStageModel.ToWire(view).Forecast);
        // no forecast, no tag
        var plain = new WarfrontHubModel(); plain.Apply(new WarfrontHubView());
        Assert.DoesNotContain(" - ", plain.NodeLabel(0, 1));
        Assert.NotEqual(new WarfrontHubView().Signature, view.Signature);
    }

}
