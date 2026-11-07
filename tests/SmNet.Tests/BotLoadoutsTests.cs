using SmSim;

namespace SmNet.Tests;

/// <summary>
/// The owner's fixed bot loadouts (2026-11-07; <see cref="BotLoadouts"/>, ours). A synthetic config with the install's class shapes and wargear names (names only, no
/// data): both sides' Tactical, Assault/Raptor and Devastator/Havoc, their choice lists and perk lists.
/// </summary>
public class BotLoadoutsTests
{
    static WeaponSpec Gun(string name, WeaponSlot slot) => new(name, slot, TriggerKind.SemiAuto, 20f, 0.1f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static ThrownSpec Thrown(string name) => new(name, 1, 1, 1f, 1f, new ProjectileSpec("p", 1, 1, "raycast", 0, [], false, false, false, 0, 0, 0, 0, null, null));
    static PerkSpec Perk(string name) => new(name, name, PerkKind.Class, 1);

    static readonly string[] TacticalPerks = ["mp_perk_friendly_spawn_target", "mp_perk_secondary_weapon", "mp_perk_shield_recharge", "mp_perk_weap_bolter_accuracy", "mp_perk_weap_storm_bolter_hip_fire"];
    static readonly string[] AssaultPerks = ["mp_perk_improved_bullrush", "mp_perk_jump_pack_reduced_cooldown", "mp_perk_ranged_damage_resistance", "mp_perk_weap_chainsword_heal_on_hit"];
    static readonly string[] HeavyPerks = ["mp_perk_melee_damage_resistance", "mp_perk_iron_halo", "mp_perk_shield_regen_delay"];

    /// <summary>Classes: 0 sm tactical, 1 sm assault, 2 sm devastator, 3 csm tactical, 4 csm raptor, 5 csm havoc.</summary>
    static SimConfig Make(bool withMelta = true)
    {
        var weapons = new List<WeaponSpec>();
        var perks = TacticalPerks.Concat(AssaultPerks).Concat(HeavyPerks).Select(Perk).ToList();
        int P(string name) => perks.FindIndex(p => p.Name == name);
        var thrown = new List<ThrownSpec> { Thrown("sm_mp_eq_frag_grenade"), Thrown("sm_mp_eq_krak_grenade"), Thrown("sm_mp_eq_stimpack"), Thrown("csm_mp_eq_frag_grenade"), Thrown("csm_mp_eq_krak_grenade") };
        int W(string name, WeaponSlot slot) { weapons.Add(Gun(name, slot)); return weapons.Count - 1; }
        var classes = new List<ClassLoadout>();
        foreach (var (side, team) in new[] { ("sm", Team.SpaceMarines), ("csm", Team.Chaos) })
        {
            int frag = thrown.FindIndex(t => t.Name == $"{side}_mp_eq_frag_grenade"), krak = thrown.FindIndex(t => t.Name == $"{side}_mp_eq_krak_grenade"), stim = 2;
            int boltPistol = W($"{side}_mp_pistol_bolt_pistol", WeaponSlot.Pistol), plasmaPistol = W($"{side}_mp_pistol_plasma_pistol", WeaponSlot.Pistol);
            var primaries = new List<int> { W($"{side}_mp_ranged_bolter", WeaponSlot.Ranged), W($"{side}_mp_ranged_sniper_rifle", WeaponSlot.Ranged), W($"{side}_mp_ranged_plasma_gun", WeaponSlot.Ranged) };
            if (withMelta) primaries.Add(W($"{side}_mp_ranged_melta", WeaponSlot.Ranged));
            primaries.Add(W($"{side}_mp_ranged_storm_bolter", WeaponSlot.Ranged));
            classes.Add(new ClassLoadout($"{side} - tactical", primaries[0], boltPistol, SimConfig.PlaceholderMeleeSteps)
            {
                PrimaryChoices = primaries, PistolChoices = [boltPistol], EquipmentChoices = [frag, krak, stim], Side = team, Role = "tactical",
                PerkChoices = TacticalPerks.Select(P).ToList(), DefaultPerk = P("mp_perk_friendly_spawn_target"),
            });
            var melees = new List<MeleeCombo> { new($"{side}_mp_melee_chainsword", SimConfig.PlaceholderMeleeSteps), new($"{side}_mp_melee_chainsword_gold", SimConfig.PlaceholderMeleeSteps),
                new($"{side}_mp_melee_powersword", SimConfig.PlaceholderMeleeSteps),
                new(side == "sm" ? "sm_mp_melee_2hand_thunder_hammer" : "csm_mp_melee_2hand_power_mace", SimConfig.PlaceholderMeleeSteps) };
            classes.Add(new ClassLoadout(side == "sm" ? "sm - assault marine" : "csm - raptor", -1, boltPistol, SimConfig.PlaceholderMeleeSteps)
            {
                PistolChoices = [boltPistol, plasmaPistol], MeleeChoices = melees, EquipmentChoices = [frag, krak, stim], Side = team, Role = "assault",
                PerkChoices = AssaultPerks.Select(P).ToList(), DefaultPerk = P("mp_perk_improved_bullrush"),
            });
            int devPistol = W($"{side}_mp_pistol_bolt_pistol_devastator", WeaponSlot.Pistol);
            var heavies = new List<int> { W($"{side}_mp_exotic_heavy_bolter", WeaponSlot.Ranged), W($"{side}_mp_exotic_las_cannon", WeaponSlot.Ranged), W($"{side}_mp_exotic_plasma_cannon", WeaponSlot.Ranged) };
            classes.Add(new ClassLoadout(side == "sm" ? "sm - devastator" : "csm - havoc", heavies[0], devPistol, SimConfig.PlaceholderMeleeSteps)
            {
                PrimaryChoices = heavies, PistolChoices = [devPistol], EquipmentChoices = [frag, krak, stim], Side = team, Role = "heavy",
                PerkChoices = HeavyPerks.Select(P).ToList(), DefaultPerk = P("mp_perk_melee_damage_resistance"),
            });
        }
        return new SimConfig { Weapons = weapons, Thrown = thrown, Classes = classes, Perks = perks };
    }

    [Fact]
    public void The_owner_list_has_three_tactical_three_assault_and_two_heavy_presets()
    {
        Assert.Equal(3, BotLoadouts.For("tactical").Count);
        Assert.Equal(3, BotLoadouts.For("assault").Count);
        Assert.Equal(2, BotLoadouts.For("heavy").Count);
        Assert.Empty(BotLoadouts.For("ork"));
        // Two perks each (the original's two perk slots); Weapon Versatility on every Tactical preset, which also names a second primary.
        Assert.All(BotLoadouts.Presets, p => Assert.NotEqual(p.Perk1, p.Perk2));
        Assert.All(BotLoadouts.For("tactical"), p => Assert.True(p.Second is not null && (p.Perk1 == Loadouts.SecondWeaponPerk || p.Perk2 == Loadouts.SecondWeaponPerk)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Every_preset_is_legal_for_both_sides_classes(int playerClass)
    {
        var c = Make();
        var presets = BotLoadouts.For(c.Classes[playerClass].Role);
        Assert.NotEmpty(presets);
        foreach (var preset in presets)
        {
            var pick = BotLoadouts.Resolve(c, playerClass, preset);
            Assert.True(pick.Legal, BotLoadouts.Describe(c, pick));
            // What the server makes of it at the spawn is what was asked for.
            var s = Loadouts.Choose(c, new PlayerState(1, default, default, 0f, true) { Class = playerClass }, pick.Primary, pick.Pistol, pick.Melee, pick.Equipment, pick.Perk1, pick.Perk2, pick.Second);
            string side = c.Classes[playerClass].Side == Team.Chaos ? "csm_mp_" : "sm_mp_";
            if (preset.Primary is { } primary) Assert.True(BotLoadouts.Matches(c.Weapons[s.Primary].Name, primary) && c.Weapons[s.Primary].Name.StartsWith(side));
            if (preset.Second is { } second) Assert.True(Loadouts.SecondIndex(c, s) is var k and >= 0 && BotLoadouts.Matches(c.Weapons[k].Name, second));
            if (preset.Pistol is { } pistol) Assert.True(BotLoadouts.Matches(c.Weapons[s.Pistol].Name, pistol));
            if (preset.Melee is { } melee) Assert.True(BotLoadouts.Matches(c.Classes[playerClass].MeleeChoices[s.MeleeChoice].Wargear, melee));
            Assert.True(BotLoadouts.Matches(c.Thrown[s.Equipment].Name, preset.Equipment));
            Assert.Equal(new[] { preset.Perk1, preset.Perk2 }.Order(), new[] { c.Perks[s.Perk1].Name, c.Perks[s.Perk2].Name }.Order());
        }
    }

    [Fact]
    public void The_raptor_thunder_hammer_is_the_daemon_maul_and_the_chaos_stims_are_the_shared_item()
    {
        var c = Make();
        var hammer = BotLoadouts.Resolve(c, 4, BotLoadouts.For("assault").Single(p => p.Key == "c"));
        Assert.Equal("csm_mp_melee_2hand_power_mace", c.Classes[4].MeleeChoices[hammer.Melee].Wargear);
        var stims = BotLoadouts.Resolve(c, 3, BotLoadouts.For("tactical").Single(p => p.Key == "a"));
        Assert.Equal("sm_mp_eq_stimpack", c.Thrown[c.Classes[3].EquipmentChoices[stims.Equipment]].Name);
        // The chainsword is not the relic chainsword.
        var chain = BotLoadouts.Resolve(c, 1, BotLoadouts.For("assault").Single(p => p.Key == "b"));
        Assert.Equal("sm_mp_melee_chainsword", c.Classes[1].MeleeChoices[chain.Melee].Wargear);
    }

    [Fact]
    public void A_missing_item_is_reported_not_replaced()
    {
        var c = Make(withMelta: false);
        var pick = BotLoadouts.Resolve(c, 0, BotLoadouts.For("tactical").Single(p => p.Key == "a"));
        Assert.False(pick.Legal);
        Assert.Contains(pick.Problems, p => p.Contains("ranged_melta"));
        Assert.Equal(-1, pick.Second);
        // A preset for another role is refused.
        Assert.False(BotLoadouts.Resolve(c, 2, BotLoadouts.For("tactical")[0]).Legal);
    }

    [Fact]
    public void The_random_pick_covers_every_preset_of_the_role_and_repeats_for_a_seed()
    {
        var c = Make();
        var seen = new HashSet<string>();
        var random = new Random(5);
        for (int i = 0; i < 200; i++) seen.Add(BotLoadouts.Pick(c, 1, random)!.Preset.Key);
        Assert.Equal(new[] { "a", "b", "c" }, seen.Order());
        Assert.Equal(BotLoadouts.Pick(c, 5, new Random(9))!.Preset, BotLoadouts.Pick(c, 5, new Random(9))!.Preset);
        Assert.InRange(BotLoadouts.PickIndex(int.MinValue, 3), 0, 2);
        Assert.Equal(-1, BotLoadouts.PickIndex(4, 0));
    }
}
