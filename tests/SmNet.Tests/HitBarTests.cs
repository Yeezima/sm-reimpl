using SmSim;

namespace SmNet.Tests;

/// <summary>Ork O7a: the maxima of the aimed-enemy bar are the hit report's (the victim's class body), so the bar reads the same fraction the server sent.</summary>
public class HitBarTests
{
    static ClassLoadout Class(string name, int body = -1) => new(name, 0, -1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], Body = body };

    static readonly BodySpec OrkBody = new("ork (test)", 5f, 6.5f, 4.5f, 8f, 3f, 3f, 3f, 2f, 85f, 5f, 5f, 0f, 7f, 33f);

    static readonly SimConfig Config = new()
    {
        Weapons = [new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270, 0.5f, 250f, 245f, 250f, 1f, 2.5f, 1f, 0.5f, 0.5f)],
        Classes = [Class("sm - tactical"), Class("ork - boy", body: 0)],
        Bodies = [OrkBody],
    };

    [Fact]
    public void A_class_with_a_body_uses_it_and_the_others_use_the_configs_own()
    {
        Assert.Equal((85f, 0f), HitBars.Maxima(Config, 1));
        Assert.Equal((Config.HealthMax, Config.ShieldMax), HitBars.Maxima(Config, 0));
    }

    [Fact]
    public void An_unknown_victim_or_class_uses_the_configs_own_and_never_a_perks()
    {
        Assert.Equal((Config.HealthMax, Config.ShieldMax), HitBars.Maxima(Config, null));
        Assert.Equal((Config.HealthMax, Config.ShieldMax), HitBars.Maxima(Config, 99));
        Assert.Equal((Config.HealthMax, Config.ShieldMax), HitBars.Maxima(Config, -1));
        Assert.Same(Config, HitBars.BodyOf(Config, null));
        Assert.Same(Config.For(1), HitBars.BodyOf(Config, 1));
    }
}
