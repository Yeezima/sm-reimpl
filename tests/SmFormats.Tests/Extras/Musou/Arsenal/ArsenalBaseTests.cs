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
/// and the animation names are what they were. W1 to W4 replace these expectations for their own item only (W1, the autocannon: it is no stub any more: a config with no install adds nothing and notes it, its alias is the heavy bolter's, its row is on the Devastator's primary list).
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
        // W4 landed: the Remote Detonators are appended even without the install (the data's values stand in); W1 says why it appends nothing without the install; W2 is a stub.
        var appended = Arsenal.Append(config, "", notes);
        Assert.Equal([ArsenalRules.RemoteDetonators], appended.Thrown.Select(t => t.Name).Where(ArsenalRules.IsArsenal));
        Assert.Equal(config.Weapons, appended.Weapons);
        Assert.All(notes, n => Assert.True(n.Contains("autocannon") || n.StartsWith("musou arsenal: Remote Detonators"), n));
        foreach (var name in ArsenalRules.Names.Where(n => n != ArsenalRules.PowerSpear))   // W3 has landed: the spear's own tests are PowerSpearContentTests and SpearTests
        {
            Assert.Null(Arsenal.MeleeMap(name));
            Assert.Null(MpClasses.MeleeMapFor(name));
            string alias = name == ArsenalRules.Autocannon ? "sm_mp_exotic_heavy_bolter" : name;   // W1: the autocannon plays the heavy bolter's animations
            Assert.Equal(alias, Arsenal.AliasOf(name));
            Assert.Equal(alias, AnimationAlias.Of(name));
            Assert.False(ArsenalRules.WieldsHammer(name));
        }
        // The data's names stay their own through the alias hook.
        foreach (var name in new[] { "sm_mp_exotic_heavy_bolter", "sm_mp_melee_powersword", "sm_mp_melee_2hand_thunder_hammer", "csm_mp_ranged_bolter" }) Assert.Equal(name, AnimationAlias.Of(name));
    }
}

public partial class CustomizationCatalogTests
{
    [Fact]
    public void Musous_catalogue_is_the_datas_but_for_the_autocannon_row_on_the_Devastators_primary_list()
    {
        var global = BafDocument.Read(GlobalBaf());
        var perks = PerkCatalog.Build(global, name => name.StartsWith("mp_perk") ? BafDocument.Read(Gear("perk")) : BafDocument.Read(Gear(name.Contains("pistol") ? "pistol" : "melee")), null, []);
        // W1 and W4: the autocannon at the end of the Space Marine Devastator's primaries and the Remote Detonators at the end of every Space Marine class's equipment; every other class is the data's own object, and so is Classic's catalogue.
        var shown = perks.For(ContentExtras.Musou);
        Assert.NotSame(perks, shown);
        Assert.Same(perks, perks.For(ContentExtras.None));
        Assert.Same(perks, perks.For(ContentExtras.Orks));
        Assert.Equal(perks.Classes.Count, shown.Classes.Count);
        for (int i = 0; i < perks.Classes.Count; i++)
        {
            bool marine = perks.Classes[i].Key.StartsWith("sm ", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(marine, shown.Classes[i].Equipment.Items.Any(x => x.Name == ArsenalRules.RemoteDetonators));
            if (marine) Assert.Equal(ArsenalRules.RemoteDetonators, shown.Classes[i].Equipment.Items[^1].Name);
            if (perks.Classes[i].Role == "heavy" && perks.Classes[i].Race == "space_marine")
            {
                var added = Assert.Single(shown.Classes[i].Primary.Items.Except(perks.Classes[i].Primary.Items));
                Assert.Equal((ArsenalRules.Autocannon, "ranged", "Autocannon", true), (added.Name, added.Slot, added.DisplayName, added.Simulated));
                Assert.Equal(added, shown.Classes[i].Primary.Items[^1]);
            }
            else Assert.Equal(perks.Classes[i].Primary, shown.Classes[i].Primary);
        }
    }
}

public partial class CustomizationProfileTests
{
    [Fact]
    public void Musous_set_is_read_with_Musous_catalogue_and_every_other_with_the_datas()
    {
        // W0 (hook 3's set part): a Musou slot keeps an item only Musou's catalogue offers; Classic's own slots and any other set use the data's.
        // W1 and W4: Musou's catalogue is a copy with the arsenal's rows (made afresh by each For), so Musou's set is read with the one that lists the autocannon and the Remote Detonators.
        bool Lists(CustomizationCatalog c) => c.Perks.Classes.Any(k => k.Primary.Items.Any(i => i.Name == ArsenalRules.Autocannon))
            && c.Perks.Classes.Any(k => k.Equipment.Items.Any(i => i.Name == ArsenalRules.RemoteDetonators));
        Assert.True(Lists(Catalog.For(ContentExtras.Musou)));
        Assert.True(Lists(ProfileViews.SetCatalog(MusouSlots.SetName, Catalog)));
        Assert.True(Lists(MusouSlots.SetCatalog(MusouSlots.SetName, Catalog)));
        Assert.Same(Catalog, ProfileViews.SetCatalog("other", Catalog));
        Assert.Same(Catalog, MusouSlots.SetCatalog("other", Catalog));
        Assert.Equal(Catalog.Classes.Count, Catalog.For(ContentExtras.Musou).Classes.Count);   // the arsenal's rows aside, the data's classes
        Assert.Same(Catalog, Catalog.For(ContentExtras.None));
    }
}
