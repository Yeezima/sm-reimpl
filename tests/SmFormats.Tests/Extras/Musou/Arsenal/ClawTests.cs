using SmContent;
using SmFormats.Baf;
using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>W2, the lightning claw (docs/new-weapons-design.md section 5): the sword's combo at 1.25x with every hit x0.8, an Assault melee choice in Musou only, the sword's map at clip speed 1.25, the sword's animations.</summary>
public class ClawTests
{
    static readonly MeleeStep Light = new(1.0f, 0.4f, 0.5f, 0.8f, 0.7f, 0.6f, 70f, 40f, 1f)
    {
        NoReactFrom = 0.2f, NoReactTo = 0.9f, YComboOpen = 0.5f, YComboClose = 0.9f, ShieldDamage = 100f, SecondaryShieldDamage = 50f, StringResetAt = 0.8f, EvadeAt = 0.7f,
        Reach = new MeleeReach(0.1f, 0.3f, 0.2f, 0.4f, 3.5f, 3.5f, [new MeleeVariant(3.5f, [0f, 0.5f, 1f])]),
    };
    static readonly MeleeStep Heavy = Light with { Damage = 120f, Stomp = new SplashSpec(2f, new DamageSpec(200f, 0f, 1f, 1f, 1f, 1f, false), [new(0f, 1f)], false, false, "target_all") };

    static ClassLoadout Class(string name, Team side, string role, params string[] melees) =>
        new(name, -1, -1, [Light]) { Side = side, Role = role, MeleeChoices = melees.Select(m => new MeleeCombo(m, [Light]) { Heavy = [Heavy] }).ToList() };

    [Fact]
    public void Every_step_time_is_over_the_speed_and_every_damage_is_x08()
    {
        var sword = new MeleeCombo(Arsenal.ClawBase, [Light]) { Heavy = [Heavy] };
        var claw = Arsenal.ClawCombo(sword);
        Assert.Equal(ArsenalRules.LightningClaw, claw.Wargear);
        var s = claw.Steps[0];
        Assert.Equal(1.0f / 1.25f, s.Duration, 5);
        Assert.Equal(0.4f / 1.25f, s.HitTime, 5);
        Assert.Equal(0.5f / 1.25f, s.ComboOpen, 5);
        Assert.Equal(0.9f / 1.25f, s.NoReactTo, 5);
        Assert.Equal(0.9f / 1.25f, s.YComboClose, 5);
        Assert.Equal(0.8f / 1.25f, s.StringResetAt, 5);
        Assert.Equal(0.1f / 1.25f, s.Reach!.AlignFrom, 5);
        Assert.Equal(0.4f / 1.25f, s.Reach.DistanceTo, 5);
        Assert.Equal(70f * 0.8f, s.Damage, 4);
        Assert.Equal(40f * 0.8f, s.SecondaryDamage, 4);
        Assert.Equal(100f * 0.8f, s.ShieldDamage, 4);
        Assert.Equal(1f, s.Lunge);   // distances stay the sword's
        Assert.Equal(200f * 0.8f, claw.Heavy[0].Stomp!.Damage.Damage, 4);
        Assert.Equal(120f * 0.8f, claw.Heavy[0].Damage, 4);
        // The same damage a second: each hit x0.8 at 1.25x the swings.
        Assert.Equal(70f / 1.0f, s.Damage / s.Duration, 3);
        Assert.Empty(claw.Variants);
    }

    [Fact]
    public void An_unset_window_stays_unset()
    {
        var claw = Arsenal.ClawCombo(new MeleeCombo(Arsenal.ClawBase, [new MeleeStep(1f, 0.4f, 0f, 0f, 1f, 1f, 70f, 0f, 1f)]));
        Assert.True(float.IsPositiveInfinity(claw.Steps[0].StringResetAt));
        Assert.True(float.IsPositiveInfinity(claw.Steps[0].EvadeAt));
        Assert.Equal(0f, claw.Steps[0].ComboOpen);
    }

    [Fact]
    public void Append_gives_only_the_Assault_Marine_the_claw_at_the_end_of_its_choices()
    {
        var config = new SimConfig
        {
            Extras = ContentExtras.Musou,
            Classes = [Class("sm - assault marine", Team.SpaceMarines, "assault", "sm_mp_melee_chainsword", "sm_mp_melee_powersword"), Class("sm - tactical", Team.SpaceMarines, "tactical", "sm_mp_melee_combat_knife"),
                       Class("csm - raptor", Team.Chaos, "assault", "csm_mp_melee_chainsword", "csm_mp_melee_powersword")],
        };
        var notes = new List<string>();
        var result = Arsenal.Append(config, "", notes);
        Assert.Equal(["sm_mp_melee_chainsword", "sm_mp_melee_powersword", ArsenalRules.LightningClaw], result.Classes[0].MeleeChoices.Select(m => m.Wargear));
        Assert.Same(config.Classes[1], result.Classes[1]);
        Assert.Same(config.Classes[2], result.Classes[2]);
        Assert.Equal(config.Classes[0].Melee, result.Classes[0].Melee);   // the default combo is untouched
        var again = Arsenal.Append(result, "", notes);
        Assert.Same(result.Classes[0], again.Classes[0]);   // a second run adds none
        Assert.Equal(3, again.Classes[0].MeleeChoices.Count);
        Assert.Empty(notes);
    }

    [Fact]
    public void An_Assault_class_without_the_sword_gets_no_claw_and_a_note()
    {
        var config = new SimConfig { Classes = [Class("sm - assault marine", Team.SpaceMarines, "assault", "sm_mp_melee_chainsword")] };
        var notes = new List<string>();
        Assert.Same(config, Arsenal.Append(config, "", notes));
        Assert.Single(notes);
    }

    [Fact]
    public void The_map_is_the_swords_at_clip_speed_1_25_and_the_animations_are_the_swords()
    {
        var map = MpClasses.MeleeMapFor(ArsenalRules.LightningClaw);
        var sword = MpClasses.MeleeMapFor(Arsenal.ClawBase);
        Assert.NotNull(map); Assert.NotNull(sword);
        Assert.Equal(1.25f, map!.ClipSpeed);
        Assert.Equal(1f, sword!.ClipSpeed);
        Assert.Equal(sword.Graph, map.Graph);
        Assert.Equal(sword.Sequencers, map.Sequencers);
        Assert.Equal(ArsenalRules.LightningClaw, map.Wargear);
        Assert.Equal(Arsenal.ClawBase, AnimationAlias.Of(ArsenalRules.LightningClaw));
        Assert.Equal("sm_mp_melee_powersword", AnimationAlias.Of("sm_mp_melee_powersword"));   // the sword is its own
    }
}

public partial class CustomizationCatalogTests
{
    [Fact]
    public void Musous_catalogue_puts_the_claw_row_beside_the_swords_in_the_Space_Marine_Assault_only()
    {
        // An Assault Marine with the power sword (its twin on the Raptor), a Tactical Marine without: only the Space Marine Assault gets the claw, in Musou's catalogue only.
        var classes = T(
            ("sm - assault marine", ClassTable("space_marine", "assault", 1010, ["sm_mp_abil_sprint"], ["sm_mp_pistol_bolt_pistol", "sm_mp_melee_chainsword", "sm_mp_melee_powersword"], ["mp_perk_a"], "mp_perk_a", "sm_default")),
            ("sm - tactical", ClassTable("space_marine", "tactical", 1000, ["sm_mp_abil_sprint", "sm_mp_melee_combat_knife"], ["sm_mp_ranged_bolter"], ["mp_perk_a"], "mp_perk_a", "sm_default")),
            ("csm - raptor", ClassTable("chaos", "assault", 1030, ["sm_mp_abil_sprint"], ["csm_mp_melee_chainsword", "csm_mp_melee_powersword"], ["mp_perk_a"], "mp_perk_a", "chaos_default")));
        var global = Baf(("extensions", T(("multiplayer_settings", T(("ingame_progression", T(("classes", classes))), ("challenge_groups", T()))))));
        var perks = PerkCatalog.Build(BafDocument.Read(global), name => name.StartsWith("mp_perk") ? BafDocument.Read(Gear("perk")) : BafDocument.Read(Gear(name.Contains("pistol") ? "pistol" : name.Contains("ranged") ? "ranged" : "melee")), null, []);
        var musou = perks.For(ContentExtras.Musou);
        Assert.NotSame(perks, musou);
        var items = musou.Class("sm - assault marine")!.Melee.Items;
        Assert.Equal(["sm_mp_melee_chainsword", "sm_mp_melee_powersword", ArsenalRules.LightningClaw], items.Select(i => i.Name));
        var claw = items[^1];
        Assert.Equal("Lightning Claw", claw.DisplayName);
        Assert.True(claw.Simulated);
        Assert.Equal(items[1].Level, claw.Level);
        Assert.DoesNotContain(musou.Class("csm - raptor")!.Melee.Items, i => i.Name == ArsenalRules.LightningClaw);
        Assert.DoesNotContain(musou.Class("sm - tactical")!.Melee.Items, i => i.Name == ArsenalRules.LightningClaw);
        Assert.Same(musou, musou.For(ContentExtras.Musou));   // asking again adds nothing (the claw is listed already)
        Assert.DoesNotContain(perks.Class("sm - assault marine")!.Melee.Items, i => i.Name == ArsenalRules.LightningClaw);   // the data's catalogue is untouched
        Assert.Same(perks, perks.For(ContentExtras.None));
        Assert.Same(perks, perks.For(ContentExtras.Orks));
    }
}
