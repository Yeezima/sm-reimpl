using SmContent;
using SmFormats.Baf;

namespace SmFormats.Tests;

/// <summary>Grenade effects pass (protocol 83): the catalogue reads the Master-Crafted blueprint beside the base one (a synthetic wargear file, no original bytes).</summary>
public class GrenadeUpgradedEffectsTests
{
    const string Master = "mp_perk_equipment_effectiveness", Base = "ebps\\projectiles\\pp_grenade_multiplayer", Up = "ebps\\projectiles\\pp_grenade_multiplayer_upgraded";

    static (string, object) N(string name, params (string, object)[] items) => (name, items);
    static (string, object) Entry(string? upgrade, string ebp) => N("entry",
        N("required_upgrade_name", upgrade is null ? N("required_list") : N("required_list", ("upgrade_name", upgrade))),
        N("type", ("ebp", ebp)));

    static BafDocument Wargear() => BafDocument.Read(SyntheticBafWriter.Build(
        N("ability_data", N("ability_definitions", N("ability_utility", N("delivery", N("ranged_delivery_type", Entry(null, Base), Entry(Master, Up))))))));

    [Fact]
    public void The_upgraded_blueprint_is_read_beside_the_base_one()
    {
        var asked = new List<string>();
        var read = ThrownEffects.Read("sm_mp_eq_frag_grenade", Wargear(), p => { asked.Add(p); return BafDocument.Read(SyntheticBafWriter.Build(N("x"))); }, []);
        Assert.Equal(Base, read.Projectile!.Blueprint);
        Assert.Equal(Up, read.Upgraded!.Blueprint);
        Assert.Equal([Base, Up], asked);
    }

    [Fact]
    public void An_item_without_a_second_blueprint_has_no_upgraded_effects()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.Build(N("ability_data", N("ability_definitions", N("ability_utility", N("delivery", N("ranged_delivery_type", Entry(null, Base))))))));
        var read = ThrownEffects.Read("sm_mp_eq_frag_grenade", doc, p => BafDocument.Read(SyntheticBafWriter.Build(N("x"))), []);
        Assert.NotNull(read.Projectile);
        Assert.Null(read.Upgraded);
    }
}
