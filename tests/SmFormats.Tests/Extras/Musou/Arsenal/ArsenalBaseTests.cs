using SmContent;
using SmContent.Customization;
using SmContent.Extras.Musou;
using SmContent.Extras.Musou.Arsenal;
using SmFormats.Baf;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>
/// W0, Musou's arsenal base (docs/new-weapons-design.md section 8): the registry's four names, and every entry a stub that adds nothing, so the Musou config, the catalogue, the melee maps
/// and the animation names are what they were. W1 to W4 replace these expectations for their own item only.
/// </summary>
public class ArsenalBaseTests
{
    [Fact]
    public void The_four_names_are_the_arsenals_and_none_is_a_data_weapons_map()
    {
        Assert.Equal(["sm_mp_exotic_auto_cannon", "sm_mp_melee_lightning_claw", "sm_mp_melee_power_spear", "sm_mp_eq_demolition_charge"], ArsenalRules.Names);
        Assert.All(ArsenalRules.Names, n => Assert.True(ArsenalRules.IsArsenal(n.ToUpperInvariant())));
        Assert.False(ArsenalRules.IsArsenal(null));
        Assert.False(ArsenalRules.IsArsenal("sm_mp_melee_powersword"));
        Assert.False(ArsenalRules.IsArsenal("csm_mp_exotic_auto_cannon"));   // the install's Chaos file is not ours
        // No added name ends with a data map's suffix, so the loader and the client never read one as a data weapon.
        Assert.All(ArsenalRules.Names, n => Assert.DoesNotContain(MpClasses.SimulatedMeleeSuffixes, s => n.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Every_entry_is_a_stub_until_its_milestone()
    {
        var config = new SimConfig { Extras = ContentExtras.Musou };
        var notes = new List<string>();
        // W4 landed: the Remote Detonators are the one item appended (W1 to W3 are still stubs).
        var appended = Arsenal.Append(config, "", notes);
        Assert.Equal([ArsenalRules.RemoteDetonators], appended.Thrown.Select(t => t.Name).Where(ArsenalRules.IsArsenal));
        Assert.Equal(config.Weapons, appended.Weapons);
        Assert.All(notes, n => Assert.StartsWith("musou arsenal: Remote Detonators", n));
        foreach (var name in ArsenalRules.Names)
        {
            Assert.Null(Arsenal.MeleeMap(name));
            Assert.Null(MpClasses.MeleeMapFor(name));
            Assert.Equal(name, Arsenal.AliasOf(name));
            Assert.Equal(name, AnimationAlias.Of(name));
            Assert.False(ArsenalRules.WieldsHammer(name));
        }
        // The data's names stay their own through the alias hook.
        foreach (var name in new[] { "sm_mp_exotic_heavy_bolter", "sm_mp_melee_powersword", "sm_mp_melee_2hand_thunder_hammer", "csm_mp_ranged_bolter" }) Assert.Equal(name, AnimationAlias.Of(name));
    }
}

public partial class CustomizationCatalogTests
{
    [Fact]
    public void Musous_catalogue_is_the_datas_while_every_arsenal_entry_is_a_stub()
    {
        var global = BafDocument.Read(GlobalBaf());
        var perks = PerkCatalog.Build(global, name => name.StartsWith("mp_perk") ? BafDocument.Read(Gear("perk")) : BafDocument.Read(Gear(name.Contains("pistol") ? "pistol" : "melee")), null, []);
        // W4 landed: Musou's Space Marine classes list the Remote Detonators last in their equipment; every other profile gets the data's catalogue itself (W1 to W3 are still stubs).
        var musou = perks.For(ContentExtras.Musou);
        Assert.Same(perks, perks.For(ContentExtras.None));
        Assert.Same(perks, perks.For(ContentExtras.Orks));
        Assert.NotSame(perks, musou);
        foreach (var c in musou.Classes)
        {
            bool marine = c.Key.StartsWith("sm ", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(marine, c.Equipment.Items.Any(i => i.Name == ArsenalRules.RemoteDetonators));
            if (marine) Assert.Equal(ArsenalRules.RemoteDetonators, c.Equipment.Items[^1].Name);
            Assert.Equal(perks.Class(c.Key)!.Primary, c.Primary);
            Assert.Equal(perks.Class(c.Key)!.Melee, c.Melee);
        }
        Assert.Same(musou.Classes.First(c => !c.Key.StartsWith("sm ", StringComparison.OrdinalIgnoreCase)), musou.Classes.First(c => !c.Key.StartsWith("sm ", StringComparison.OrdinalIgnoreCase)));
    }
}

public partial class CustomizationProfileTests
{
    [Fact]
    public void Musous_set_is_read_with_Musous_catalogue_and_every_other_with_the_datas()
    {
        // W0 (hook 3's set part): a Musou slot keeps an item only Musou's catalogue offers; Classic's own slots and any other set use the data's.
        // (W4 landed: Musou's catalogue is built afresh each time, so the two are compared by the rows they offer.)
        var rows = (CustomizationCatalog c) => c.Perks.Classes.Select(k => string.Join(",", k.Equipment.Items.Select(i => i.Name))).ToList();
        Assert.Equal(rows(Catalog.For(ContentExtras.Musou)), rows(ProfileViews.SetCatalog(MusouSlots.SetName, Catalog)));
        Assert.NotEqual(rows(Catalog), rows(ProfileViews.SetCatalog(MusouSlots.SetName, Catalog)));
        Assert.Same(Catalog, ProfileViews.SetCatalog("other", Catalog));
        Assert.Same(Catalog, MusouSlots.SetCatalog("other", Catalog));
        // W4 landed: Musou's catalogue is the data's plus the Remote Detonators row; no other profile's changes.
        Assert.NotSame(Catalog, Catalog.For(ContentExtras.Musou));
        Assert.Same(Catalog, Catalog.For(ContentExtras.None));
    }
}
