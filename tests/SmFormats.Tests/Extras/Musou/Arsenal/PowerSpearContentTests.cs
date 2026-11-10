using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>W3, the power spear's content half (docs/new-weapons-design.md section 6): the combo appended to the Musou Assault Marine's melee choices, the hammer's map at 0.85, the hammer's animation names.</summary>
public class PowerSpearContentTests
{
    const string Hammer = "sm_mp_melee_2hand_thunder_hammer";
    static readonly MeleeStep Swing = new(2.33f, 0.57f, 0.9f, 1.4f, 0.8f, 0.9f, 200f, 10f, 3.5f);
    static readonly MeleeCombo Sword = new("sm_mp_melee_powersword", [Swing with { Damage = 120f }]);
    static readonly MeleeCombo HammerCombo = new(Hammer, [Swing]);
    static ClassLoadout Class(string name, string role, Team side, params MeleeCombo[] melee) => new(name, 0, -1, [Swing]) { MeleeChoices = melee, Role = role, Side = side };

    static SimConfig Config() => new()
    {
        Extras = ContentExtras.Musou,
        Classes = [Class("sm - tactical", "tactical", Team.SpaceMarines, Sword), Class("sm - assault", "assault", Team.SpaceMarines, Sword, HammerCombo), Class("csm - assault", "assault", Team.Chaos, Sword, HammerCombo)],
    };

    [Fact]
    public void The_spear_is_appended_at_the_end_of_the_marine_assaults_melee_choices_only()
    {
        var notes = new List<string>();
        var config = Arsenal.Append(Config(), "", notes);
        var assault = config.Classes[1];
        Assert.Equal([Sword.Wargear, Hammer, ArsenalRules.PowerSpear], assault.MeleeChoices.Select(m => m.Wargear));   // no existing index moves
        Assert.Equal(150f, assault.MeleeChoices[2].Steps[0].Damage, 3);
        Assert.Equal(2.33f / SpearRules.Tempo, assault.MeleeChoices[2].Steps[0].Duration, 3);
        Assert.Single(config.Classes[0].MeleeChoices);   // the Tactical
        Assert.Equal(2, config.Classes[2].MeleeChoices.Count);   // the Chaos twin
        Assert.Single(notes, n => n.Contains("power spear"));   // the spear's own note (W1 adds the autocannon's when the install is missing)
    }

    [Fact]
    public void Appending_twice_adds_nothing_and_a_config_without_a_hammer_is_untouched()
    {
        var once = Arsenal.Append(Config(), "", []);
        Assert.Equal(3, Arsenal.Append(once, "", []).Classes[1].MeleeChoices.Count);
        var bare = new SimConfig { Extras = ContentExtras.Musou, Classes = [Class("sm - assault", "assault", Team.SpaceMarines, Sword)] };
        Assert.Same(bare, Arsenal.Append(bare, "", []));
    }

    [Fact]
    public void The_spears_map_is_the_hammers_at_the_spears_tempo_and_borrows_its_animations()
    {
        var map = Arsenal.MeleeMap(ArsenalRules.PowerSpear)!;
        Assert.Equal(ArsenalRules.PowerSpear, map.Wargear);
        Assert.Equal(MeleeTuning.ThunderHammer.Graph, map.Graph);
        Assert.Equal(SpearRules.Tempo, map.ClipSpeed);
        Assert.Equal(0.85f, SpearRules.Tempo);
        Assert.Equal(1f, MeleeTuning.ThunderHammer.ClipSpeed);
        Assert.Equal(Hammer, Arsenal.AliasOf(ArsenalRules.PowerSpear));
        Assert.Equal(Hammer, AnimationAlias.Of(ArsenalRules.PowerSpear));
        Assert.Null(MpClasses.MeleeMapFor("sm_mp_melee_powersword") is { ClipSpeed: not 1f } ? "x" : null);
    }
}

public partial class CustomizationCatalogTests
{
    [Fact]
    public void The_spear_row_follows_the_hammer_in_the_marine_assaults_melee_row_in_musous_catalogue_only()
    {
        var global = SmFormats.Baf.BafDocument.Read(GlobalBaf());
        var data = PerkCatalog.Build(global, name => name.StartsWith("mp_perk") ? SmFormats.Baf.BafDocument.Read(Gear("perk")) : SmFormats.Baf.BafDocument.Read(Gear(name.Contains("pistol") ? "pistol" : "melee")), null, []);
        // The fixture's Assault carries no hammer: give it one, as the install's does.
        var hammer = new LoadoutItem("sm_mp_melee_2hand_thunder_hammer", "melee", "Thunder Hammer", "", 1, true, null, null);
        var withHammer = data.WithClasses(c => c.Role == "assault" ? c with { Melee = c.Melee with { Items = [.. c.Melee.Items, hammer] } } : c);

        var musou = withHammer.For(ContentExtras.Musou);
        var assault = musou.Classes.First(c => c.Role == "assault");
        Assert.Equal(["sm_mp_melee_2hand_thunder_hammer", "sm_mp_melee_power_spear"], assault.Melee.Items.Select(i => i.Name).Skip(assault.Melee.Items.Count - 2));
        Assert.True(assault.Melee.Items[^1].Simulated);
        Assert.Equal("Power Spear", assault.Melee.Items[^1].DisplayName);
        Assert.DoesNotContain(musou.Classes.Where(c => c.Role != "assault").SelectMany(c => c.Melee.Items), i => i.Name == "sm_mp_melee_power_spear");   // no other class

        foreach (var profile in new[] { ContentExtras.None, ContentExtras.Orks })
            Assert.DoesNotContain(withHammer.For(profile).Classes.SelectMany(c => c.Melee.Items), i => i.Name == "sm_mp_melee_power_spear");   // no other profile
        Assert.Equal(1, assault.Melee.Items.Count(i => i.Name == "sm_mp_melee_power_spear"));
        Assert.Equal(1, musou.For(ContentExtras.Musou).Classes.First(c => c.Role == "assault").Melee.Items.Count(i => i.Name == "sm_mp_melee_power_spear"));   // asking again adds no second row
    }
}
