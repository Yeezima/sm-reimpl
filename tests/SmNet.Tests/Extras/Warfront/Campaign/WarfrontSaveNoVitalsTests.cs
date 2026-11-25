using SmSim.Extras.Warfront.Campaign;

namespace SmNet.Tests.Extras.Warfront.Campaign;

/// <summary>Executions X4's overshield rule "never saved": the Warfront save holds the campaign (map, nodes, ledgers), never a player's vitals, so a reloaded run starts each stage fresh.</summary>
public class WarfrontSaveNoVitalsTests
{
    [Fact]
    public void SaveNeverWritesOvershield()
    {
        var c = WarfrontSave.NewRun(12345);
        string json = c.ToJson();
        Assert.DoesNotContain("Shield", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Health", json, StringComparison.OrdinalIgnoreCase);
        var back = WarfrontSave.FromJson(json);
        Assert.DoesNotContain("Shield", back.ToJson(), StringComparison.OrdinalIgnoreCase);
    }
}
