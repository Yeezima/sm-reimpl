using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// dreadnought-defects (review F1/F2): the Dreadnought's 5 m light caps belong to one match's effect player, never to every play of a name in the process, so a Classic match after a Musou one
/// starts with none; the Ashfall storm puts the fog energy back only if it was the one that set it.
/// </summary>
public class DreadnoughtLightCapPinTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }

    static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    [Fact]
    public void The_light_caps_are_per_effect_player_not_process_wide()
    {
        string fx = Read("client", "FxPlayer.cs");
        Assert.Contains("public readonly Dictionary<string, float> LightCaps", fx);
        Assert.DoesNotContain("static readonly Dictionary<string, float> LightCaps", fx);
        string feel = Read("client", "Extras", "Musou", "Dreadnought", "NetworkView.DreadnoughtFeel.cs");
        Assert.DoesNotContain("FxPlayer.LightCaps", feel);
    }

    [Fact]
    public void Only_the_storm_that_set_the_fog_energy_resets_it()
    {
        string storm = Read("client", "Extras", "Musou", "Ashfall", "AshfallStorm.cs");
        Assert.Contains("if (_fogOwner == this) { FxPlayer.LightFogEnergy = 1f; _fogOwner = null; }", storm);
        Assert.Contains("_fogOwner = this;", storm);
    }
}
