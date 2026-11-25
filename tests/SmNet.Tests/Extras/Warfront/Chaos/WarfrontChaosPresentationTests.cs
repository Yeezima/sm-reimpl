using SmClient.Extras.Warfront.Stage;
using SmContent.Extras.Warfront;
using SmNet.Extras.Warfront;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Warfront.Faction;

namespace SmNet.Tests.Extras.Warfront.Chaos;

/// <summary>
/// Chaos Warfront C3: the client's presentation tables without Godot. The Hub's faction reaches the hub model and its labels; every kind a Chaos battle fields has a Chaos body and no Ork-only kind
/// is drawn; the marine kinds have a Chaos class look; the HUD's Ork words become the Chaos units'; the Host panel's explainer names the enemy of each FACTION choice; the local page's plate passes it.
/// </summary>
public class WarfrontChaosPresentationTests
{
    static WarfrontStageModel ModelFor(WarfrontFaction faction)
    {
        var c = new WarfrontCampaign(77, faction: faction);
        c.Join(111, "Alpha", out _);
        c.Embark();
        var m = new WarfrontStageModel(111, "Alpha");
        Assert.True(m.ApplyHub(WarfrontWire.Encode(WarfrontWire.HubFor(c, 111))));
        return m;
    }

    [Theory]
    [InlineData(WarfrontFaction.Orks)]
    [InlineData(WarfrontFaction.Chaos)]
    public void The_hub_faction_reaches_the_model_and_round_trips(WarfrontFaction faction)
    {
        var m = ModelFor(faction);
        Assert.Equal(faction, m.Hub.View.Faction);
        var c = new WarfrontCampaign(77, faction: faction);
        c.Join(111, "Alpha", out _); c.Embark();
        var bytes = WarfrontWire.Encode(WarfrontWire.HubFor(c, 111));
        Assert.True(WarfrontWire.TryDecode(bytes, out WarfrontHubWire wire));
        Assert.Equal(bytes, WarfrontWire.Encode(WarfrontStageModel.ToWire(WarfrontStageModel.ToView(wire))));
    }

    [Fact]
    public void The_war_map_names_the_chaos_stronghold_and_lord_and_keeps_the_ork_words_for_orks()
    {
        var orks = ModelFor(WarfrontFaction.Orks).Hub;
        Assert.Equal("ORK FORTRESS: LOCKED", orks.DeployRowText(4));
        Assert.Equal("ORK-HELD", orks.NodeStateText(0, 3));
        Assert.Equal("WARBOSS", orks.CommanderName);
        var chaos = ModelFor(WarfrontFaction.Chaos).Hub;
        Assert.Equal("CHAOS STRONGHOLD: LOCKED", chaos.DeployRowText(4));
        Assert.Equal("CHAOS-HELD", chaos.NodeStateText(0, 3));
        Assert.Equal("CHAOS LORD", chaos.CommanderName);
        Assert.DoesNotContain("ORK", chaos.FortressName);
    }

    [Fact]
    public void Every_kind_a_chaos_battle_fields_has_a_chaos_body_and_no_ork_only_kind_is_drawn()
    {
        foreach (var kind in Enum.GetValues<MusouEnemyType>())
        {
            var body = WarfrontChaosLooksPlan.BodyOf(kind);
            if (WarfrontChaosRoster.Fields(kind) || kind == MusouEnemyType.ArdBoy) Assert.NotEqual(WarfrontChaosBody.None, body);   // the far impostors' ArdBoy byte draws the Chaos Marine
            else Assert.Equal(WarfrontChaosBody.None, body);
            Assert.Equal(body == WarfrontChaosBody.Marine, WarfrontChaosLooksPlan.MarineLook(kind) is not null);
            if (body is WarfrontChaosBody.Renegade or WarfrontChaosBody.Cultist or WarfrontChaosBody.Bloodletter)
                Assert.Contains(WarfrontChaosLooksPlan.SoundFiles, f => f.Kind == kind && f.Blueprint.StartsWith("chaos_coop_", StringComparison.Ordinal));
            Assert.DoesNotContain(WarfrontChaosLooksPlan.SoundFiles, f => f.Kind == kind && (f.Blueprint.StartsWith("ork", StringComparison.Ordinal) || f.Melee.StartsWith("ork", StringComparison.Ordinal)));
        }
        Assert.Equal(WarfrontChaosBody.Cultist, WarfrontChaosLooksPlan.BodyOf(MusouEnemyType.Boy));
        Assert.Equal(WarfrontChaosBody.Renegade, WarfrontChaosLooksPlan.BodyOf(MusouEnemyType.ShootaBoy));
        Assert.Equal(WarfrontChaosBody.Bloodletter, WarfrontChaosLooksPlan.BodyOf(MusouEnemyType.Gretchin));
        Assert.Equal(WarfrontChaosBody.Marine, WarfrontChaosLooksPlan.BodyOf(MusouEnemyType.Warboss));
        Assert.Equal(MusouEnemyType.ChaosMarine, WarfrontChaosLooksPlan.DrawnKind(MusouEnemyType.ArdBoy));
        Assert.Equal(MusouEnemyType.Gretchin, WarfrontChaosLooksPlan.DrawnKind(MusouEnemyType.Gretchin));
    }

    [Fact]
    public void The_lord_and_the_chosen_carry_the_hammer_and_a_modest_scale()
    {
        var lord = WarfrontChaosLooksPlan.MarineLook(MusouEnemyType.Warboss)!;
        Assert.Contains(lord.Held, h => h.Contains("hammer", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(lord.Scale, 1.1f, 1.6f);
        foreach (var chosen in new[] { MusouEnemyType.BossBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob })
            Assert.Contains(WarfrontChaosLooksPlan.MarineLook(chosen)!.Held, h => h.Contains("hammer", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("raptor", WarfrontChaosLooksPlan.MarineLook(MusouEnemyType.Stormboy)!.ClassWord);
        Assert.Equal("havoc", WarfrontChaosLooksPlan.MarineLook(MusouEnemyType.BigShootaNob)!.ClassWord);
        Assert.Equal("havoc", WarfrontChaosLooksPlan.MarineLook(MusouEnemyType.RokkitBoy)!.ClassWord);
    }

    [Fact]
    public void Every_marine_look_wears_the_default_scheme_or_an_allowed_crimson_set()
    {
        foreach (var kind in Enum.GetValues<MusouEnemyType>())
            if (WarfrontChaosLooksPlan.MarineLook(kind) is { } look)
                foreach (var word in look.SetWords) Assert.True(WarfrontChaosLooksPlan.SetAllowed(word), $"{kind}: set word '{word}' is not an allowed crimson set");
        Assert.Empty(WarfrontChaosLooksPlan.MarineLook(MusouEnemyType.Nob)!.SetWords);   // the Champion: the race's default scheme
        // the install's predefined Chaos sets (names from the C3 capture log): only the Khorne ones pass
        Assert.True(WarfrontChaosLooksPlan.SetAllowed("khornate_berserker_1"));
        Assert.True(WarfrontChaosLooksPlan.SetAllowed("khorne_berserker_berserker_3"));
        foreach (var other in new[] { "dlc1_set2", "emperors_children_dlc", "iron_warriors_dlc", "black_crusader_havoc_1", "raptor_1", "chaos_default" })
            Assert.False(WarfrontChaosLooksPlan.SetAllowed(other), other);
    }

    [Fact]
    public void The_chaos_shot_bodies_are_the_drawn_models_and_the_orks_keep_theirs()
    {
        var orks = MusouRules.Default;
        var chaos = WarfrontChaosTuning.Rules(orks);
        Assert.Equal(2.15f, MusouSim.ShotBodyOf(orks, MusouEnemyType.Boy).Height);
        Assert.Equal(1.5f, MusouSim.ShotBodyOf(orks, MusouEnemyType.Gretchin).Height);
        Assert.Equal(3f, MusouSim.ShotBodyOf(orks, MusouEnemyType.Nob).Height);
        Assert.Equal(WarfrontChaosTuning.ManHeight, MusouSim.ShotBodyOf(chaos, MusouEnemyType.Boy).Height);
        Assert.Equal(WarfrontChaosTuning.ManHeight, MusouSim.ShotBodyOf(chaos, MusouEnemyType.ShootaBoy).Height);
        Assert.Equal(WarfrontChaosTuning.MarineHeight, MusouSim.ShotBodyOf(chaos, MusouEnemyType.ChaosMarine).Height);
        Assert.Equal(WarfrontChaosTuning.DaemonHeight, MusouSim.ShotBodyOf(chaos, MusouEnemyType.Gretchin).Height);   // the Daemon
        Assert.Equal(orks.Of(MusouEnemyType.Boy).Radius, chaos.Of(MusouEnemyType.Boy).Radius);
        Assert.Equal(chaos.Of(MusouEnemyType.BossNob).Height, MusouSim.ShotBodyOf(chaos, MusouEnemyType.BossNob).Height);   // the Chosen at their row's height, not the Boss Orks' 3x
        Assert.Equal(WarfrontChaosTuning.MarineHeight * 1.1f, WarfrontChaosTuning.ChampionHeight, 3);
    }

    [Theory]
    [InlineData("ORK WARBOSS", "CHAOS LORD")]
    [InlineData("BOSS NOB", "CHOSEN")]
    [InlineData("BOSS 'ARD BOY", "CHOSEN")]
    [InlineData("BOSS ORK INCOMING", "CHOSEN INCOMING")]
    [InlineData("BOSS ORK SLAIN", "CHOSEN SLAIN")]
    [InlineData("THE WARBOSS IS DEAD", "THE CHAOS LORD IS DEAD")]
    [InlineData("KILL THE WARBOSS  40%", "KILL THE CHAOS LORD  40%")]
    [InlineData("THE WARBOSS IS IN THE ARENA", "THE CHAOS LORD IS IN THE ARENA")]
    [InlineData("BREACH THE FORTRESS", "BREACH THE STRONGHOLD")]
    [InlineData("FIGHT THROUGH TO THE ARENA", "FIGHT THROUGH TO THE ARENA")]
    [InlineData("OBJECTIVE CAPTURED", "OBJECTIVE CAPTURED")]
    public void The_hud_words_become_the_chaos_units(string ork, string chaos) => Assert.Equal(chaos, WarfrontChaosLooksPlan.HudLabel(ork));

    [Fact]
    public void The_host_explainer_names_the_enemy_of_each_faction_choice()
    {
        Assert.Contains("against the Orks.", WarfrontLocal.HostExplainerFor(WarfrontFactionSetting.Orks));
        Assert.Contains("against the forces of Chaos.", WarfrontLocal.HostExplainerFor(WarfrontFactionSetting.Chaos));
        Assert.Contains("the Orks or Chaos, by turns", WarfrontLocal.HostExplainerFor(WarfrontFactionSetting.Alternate));
        foreach (var s in Enum.GetValues<WarfrontFactionSetting>())
        {
            Assert.Contains("as Space Marines", WarfrontLocal.HostExplainerFor(s));   // the players are always Space Marines; the enemy is what changes
            Assert.Contains("up to 3 friends", WarfrontLocal.HostExplainerFor(s));
        }
        Assert.DoesNotContain("Chaos", WarfrontLocal.HostExplainerFor(WarfrontFactionSetting.Orks));
        Assert.DoesNotContain("Ork", WarfrontLocal.HostExplainerFor(WarfrontFactionSetting.Chaos));
    }

    [Fact]
    public void The_local_page_plate_passes_a_forced_faction_and_alternate_adds_nothing()
    {
        Assert.Equal(3, WarfrontLocal.FactionPlateTexts.Length);
        Assert.Equal(["--warfront-max-humans", "1"], WarfrontLocal.Options(1, "G", newCampaign: false).ExtraArgs);
        Assert.Equal(["--warfront-max-humans", "1", "--warfront-new", "--warfront-faction", "chaos"], WarfrontLocal.Options(1, "G", newCampaign: true, WarfrontFactionSetting.Chaos).ExtraArgs);
        Assert.Equal(["--warfront-max-humans", "1", "--warfront-faction", "orks"], WarfrontLocal.Options(1, "G", newCampaign: false, WarfrontFactionSetting.Orks).ExtraArgs);
    }
}
