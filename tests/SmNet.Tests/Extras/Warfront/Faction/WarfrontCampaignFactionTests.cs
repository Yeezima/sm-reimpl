using System.Text.Json.Nodes;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Faction;

namespace SmNet.Tests.Extras.Warfront.Faction;

/// <summary>Chaos Warfront C1: the campaign's Faction in run.json (SaveVersion 7): saved and kept, older saves load as Orks, an unknown army is a damaged file; and the FinaleLost outcome.</summary>
public class WarfrontCampaignFactionTests
{
    [Fact]
    public void A_new_campaign_is_Orks_unless_told_otherwise()
    {
        Assert.Equal(WarfrontFaction.Orks, new WarfrontCampaign(7).Faction);
        Assert.Equal(WarfrontFaction.Chaos, new WarfrontCampaign(7, null, WarfrontFaction.Chaos).Faction);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WarfrontCampaign(7, null, (WarfrontFaction)9));
    }

    [Theory]
    [InlineData(WarfrontFaction.Orks)]
    [InlineData(WarfrontFaction.Chaos)]
    public void The_faction_round_trips_through_the_save_by_name(WarfrontFaction f)
    {
        var c = new WarfrontCampaign(4242, null, f);
        c.Join(1, "A", out _); c.Embark();
        string json = c.ToJson();
        Assert.Contains("\"Version\":" + WarfrontCampaign.SaveVersion, json);
        Assert.Contains($"\"Faction\":\"{f}\"", json);
        var back = WarfrontSave.FromJson(json);
        Assert.Equal(f, back.Faction);
        Assert.Equal(c.StateHash(), back.StateHash());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(3)]
    public void A_save_from_before_the_faction_loads_as_Orks(int version)
    {
        var node = JsonNode.Parse(new WarfrontCampaign(77).ToJson())!.AsObject();
        node["Version"] = version;
        node.Remove("Faction");
        var c = WarfrontSave.FromJson(node.ToJsonString());
        Assert.Equal(WarfrontFaction.Orks, c.Faction);
        Assert.Contains("\"Version\":" + WarfrontCampaign.SaveVersion, c.ToJson());   // the next write is the current format
    }

    [Fact]
    public void An_older_save_ignores_a_stray_faction_field()
    {
        var node = JsonNode.Parse(new WarfrontCampaign(77, null, WarfrontFaction.Chaos).ToJson())!.AsObject();
        node["Version"] = 6;
        Assert.Equal(WarfrontFaction.Orks, WarfrontSave.FromJson(node.ToJsonString()).Faction);
    }

    [Fact]
    public void A_version_7_save_without_the_field_is_Orks_and_an_unknown_army_is_damaged()
    {
        var node = JsonNode.Parse(new WarfrontCampaign(77).ToJson())!.AsObject();
        node.Remove("Faction");
        Assert.Equal(WarfrontFaction.Orks, WarfrontSave.FromJson(node.ToJsonString()).Faction);
        node["Faction"] = "Tau";
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WarfrontSave.FromJson(node.ToJsonString()));
        node["Faction"] = 5;
        Assert.Throws<InvalidDataException>(() => WarfrontSave.FromJson(node.ToJsonString()));
    }

    [Fact]
    public void The_restart_rule_keeps_the_faction()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-wf-cfac-" + Guid.NewGuid().ToString("N"));
        try
        {
            var c = new WarfrontCampaign(91, null, WarfrontFaction.Chaos);
            c.Join(1, "A", out _); c.Embark();
            Assert.True(c.TryDeploy(1, WarfrontTarget.Node(0), out _));
            c.BeginBattle();
            WarfrontSave.Write(dir, c);
            var back = WarfrontSave.Load(dir)!;   // a battle returns to the ship; the army stays
            Assert.Equal(WarfrontPhase.Ship, back.Phase);
            Assert.Equal(WarfrontFaction.Chaos, back.Faction);
            back.Join(1, "A", out _);
            Assert.True(back.TryDeploy(1, WarfrontTarget.Node(1), out _));
            back.BeginBattle();
            back.Resolve(WarfrontOutcome.Wipe);   // a wipe keeps it too
            Assert.Equal(WarfrontFaction.Chaos, WarfrontSave.FromJson(back.ToJson()).Faction);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void FinaleLost_in_the_fortress_loses_the_campaign_and_a_wipe_there_still_locks_the_lane()
    {
        WarfrontCampaign AtFortress()
        {
            var node = JsonNode.Parse(new WarfrontCampaign(5).ToJson())!.AsObject();
            node["Fronts"] = new JsonArray(4, 1, 1);
            node["Advanced"] = new JsonArray(true, false, false);
            node["Phase"] = "Ship";
            var c = WarfrontSave.FromJson(node.ToJsonString());
            c.Join(1, "A", out _);
            var fortress = c.AvailableTargets().Single(t => t.Kind == WarfrontTargetKind.Fortress);
            Assert.True(c.TryDeploy(1, fortress, out _));
            c.BeginBattle();
            return c;
        }
        var lost = AtFortress();
        lost.Resolve(WarfrontOutcome.FinaleLost);
        Assert.Equal(WarfrontPhase.Lost, lost.Phase);
        Assert.True(lost.IsOver);
        Assert.Equal(WarfrontOutcome.FinaleLost, WarfrontSave.FromJson(lost.ToJson()).History[^1].Outcome);

        var wiped = AtFortress();
        wiped.Resolve(WarfrontOutcome.Wipe);   // unchanged: the fortress locks, back on the ship
        Assert.Equal(WarfrontPhase.Ship, wiped.Phase);
        Assert.Equal(3, wiped.Front(0));
    }
}
