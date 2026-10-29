using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Custom classes, simulation side (T2; docs/customizer-architecture.md sections 4 and 7): a loadout change asked for while alive
/// applies at the respawn tick (death timer and match restart), a class of the other side maps by role, invalid and duplicate
/// perks default, the True Grit rule, perk upgrade strings, perk reading from synthetic wargear, and the config hash covering the
/// perks. Synthetic classes, weapons and perks only (no game data).
/// </summary>
public class LoadoutChangeTests
{
    const int SmTac = 0, SmAssault = 1, CsmTac = 2, CsmRaptor = 3;
    const int Rifle = 0, Sidearm = 1, RifleB = 2, PistolBolter = 3, PlasmaPistol = 4;
    const int Homer = 0, TrueGrit = 1, Charge = 2, Targeter = 3, Versatility = 4;

    static WeaponSpec Gun(string name, WeaponSlot slot) =>
        new(name, slot, TriggerKind.SemiAuto, 20f, 0.2f, 10, 30, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);

    static readonly MeleeCombo Knife = new("test_knife", SimConfig.PlaceholderMeleeSteps), Sword = new("test_sword", SimConfig.PlaceholderMeleeSteps);

    static ClassLoadout Tactical(string name, Team side) => new(name, Rifle, Sidearm, SimConfig.PlaceholderMeleeSteps)
    {
        PrimaryChoices = [Rifle, RifleB], PistolChoices = [Sidearm], Side = side, Role = "tactical",
        PerkChoices = [Homer, Targeter, Versatility], DefaultPerk = Homer,
    };
    static ClassLoadout Assault(string name, Team side) => new(name, -1, Sidearm, SimConfig.PlaceholderMeleeSteps)
    {
        PistolChoices = [Sidearm, PlasmaPistol, PistolBolter], MeleeChoices = [Knife, Sword], Side = side, Role = "assault",
        PerkChoices = [Charge, TrueGrit], DefaultPerk = Charge,
    };

    /// <summary>One rifle hit kills (no shield); respawn after 0.5 s.</summary>
    static readonly SimConfig Config = new()
    {
        Weapons = [Gun("test_rifle", WeaponSlot.Ranged), Gun("test_sidearm", WeaponSlot.Pistol), Gun("test_rifle_b", WeaponSlot.Ranged),
                   Gun("sm_mp_pistol_bolter", WeaponSlot.Pistol), Gun("test_plasma_pistol", WeaponSlot.Pistol)],
        Classes = [Tactical("sm - tactical", Team.SpaceMarines), Assault("sm - assault marine", Team.SpaceMarines),
                   Tactical("csm - tactical", Team.Chaos), Assault("csm - raptor", Team.Chaos)],
        Perks =
        [
            new PerkSpec("mp_perk_friendly_spawn_target", "mp_perk_friendly_spawn_target", PerkKind.Class, 1),
            new PerkSpec(Loadouts.TrueGritPerk, Loadouts.TrueGritPerk, PerkKind.Class, 25),
            new PerkSpec("mp_perk_improved_bullrush", "mp_perk_improved_bullrush", PerkKind.Class, 1),
            new PerkSpec("mp_perk_weap_bolter_accuracy", "mp_perk_weap_bolter_accuracy", PerkKind.Weapon, 1),
            new PerkSpec("mp_perk_secondary_weapon", null, PerkKind.Class, 13),
        ],
        HealthMax = 20f, ShieldMax = 0f, HealthRegenDelay = 1e6f, RespawnTime = 0.5f,
    };

    /// <summary>Player 1 at the origin facing +Y, player 2 five metres north facing it (yaw π).</summary>
    static World Pair(SimConfig config, MatchRules? rules, int class1, int class2)
    {
        var world = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 5, 0), MathF.PI)]) { Rules = rules };
        world.Spawn(1, class1);
        world.Spawn(2, class2);
        Assert.Equal(Vector3.Zero, world.Players[1].Position);
        Assert.Equal(new Vector3(0, 5, 0), world.Players[2].Position);
        return world;
    }

    /// <summary>One tick: player 2 presses Fire (aimed at player 1) when <paramref name="fire"/>, everyone else idles facing its yaw.</summary>
    static void Step(World w, bool fire = false) =>
        w.Step(id => new PlayerInput(0, 0f, 0f, w.Players[id].Yaw, id == 2 && fire ? InputButtons.Fire : InputButtons.None));

    // ---------------------------------------------------------------- respawn timing

    [Fact]
    public void A_change_asked_for_while_alive_applies_at_the_respawn_tick()
    {
        var w = Pair(Config, null, SmTac, SmTac);
        Assert.Equal((Rifle, Homer, -1), (w.Players[1].Primary, w.Players[1].Perk1, w.Players[1].Perk2));
        var resolved = w.SetNextLoadout(1, new LoadoutChoice(Primary: 1, Perk1: 1, Perk2: 2));
        Assert.Equal(new LoadoutChoice(SmTac, 1, -1, -1, -1, 1, 2), resolved);   // class -1: the current one
        Assert.Equal(resolved, w.NextLoadout(1));
        for (int t = 0; t < 10; t++) Step(w);
        Assert.True(w.Players[1].Alive);
        Assert.Equal((Rifle, Homer), (w.Players[1].Primary, w.Players[1].Perk1));   // nothing changes mid-life
        Step(w, fire: true);
        Assert.False(w.Players[1].Alive);
        Assert.Equal((Rifle, Homer), (w.Players[1].Primary, w.Players[1].Perk1));   // nor while dead
        int ticks = 0;
        while (!w.Players[1].Alive && ticks++ < 100)
        {
            Step(w);
            if (!w.Players[1].Alive) Assert.DoesNotContain(1, w.LastRespawns);
        }
        Assert.True(w.Players[1].Alive);
        Assert.Equal(new[] { 1 }, w.LastRespawns);
        Assert.Equal((RifleB, Targeter, Versatility), (w.Players[1].Primary, w.Players[1].Perk1, w.Players[1].Perk2));
        Assert.Equal(10, w.Players[1].Clip);   // a full respawn with the new weapon
        Assert.Null(w.NextLoadout(1));         // consumed
        Step(w);
        Assert.Empty(w.LastRespawns);
        // A later death keeps the changed loadout (no new request).
        Step(w, fire: true);
        while (!w.Players[1].Alive && ticks++ < 200) Step(w);
        Assert.Equal((RifleB, Targeter, Versatility), (w.Players[1].Primary, w.Players[1].Perk1, w.Players[1].Perk2));
    }

    [Fact]
    public void A_change_applies_at_the_match_restart_respawn()
    {
        var rules = new MatchRules { ScoreLimit = 1, TimeLimit = 0f, RestartDelay = 0.2f };
        var w = Pair(Config, rules, SmTac, CsmTac);
        Assert.Equal((Team.SpaceMarines, Team.Chaos), (w.Players[1].Team, w.Players[2].Team));
        Step(w);
        Assert.Equal(MatchState.Playing, w.State);
        Step(w, fire: true);   // the only score needed
        Assert.Equal(MatchState.Ended, w.State);
        // Player 2 (alive) asks for the other rifle; player 1 (dead) for the other rifle too.
        Assert.NotNull(w.SetNextLoadout(2, new LoadoutChoice(Primary: 1)));
        Assert.NotNull(w.SetNextLoadout(1, new LoadoutChoice(Primary: 1, Perk2: 1)));
        int ticks = 0;
        while (w.State != MatchState.Playing && ticks++ < 100) Step(w);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal(new[] { 1, 2 }, w.LastRespawns.Order());
        Assert.Equal(RifleB, w.Players[2].Primary);
        Assert.Equal((RifleB, Targeter), (w.Players[1].Primary, w.Players[1].Perk2));
        Assert.Null(w.NextLoadout(1));
        Assert.Null(w.NextLoadout(2));
    }

    [Fact]
    public void A_later_request_replaces_an_earlier_one_and_unknown_players_and_rejoins_drop_it()
    {
        var w = Pair(Config, null, SmTac, SmTac);
        w.SetNextLoadout(1, new LoadoutChoice(Primary: 1));
        w.SetNextLoadout(1, new LoadoutChoice(Primary: 0, Perk1: 2));
        Assert.Equal(new LoadoutChoice(SmTac, 0, -1, -1, -1, 2, -1), w.NextLoadout(1));
        Assert.Null(w.SetNextLoadout(9, LoadoutChoice.Default));
        w.Spawn(1, SmTac);   // a rejoin (new Hello) starts afresh
        Assert.Null(w.NextLoadout(1));
        w.SetNextLoadout(2, LoadoutChoice.Default);
        w.Remove(2);
        Assert.Null(w.NextLoadout(2));
    }

    // ---------------------------------------------------------------- sides and roles

    [Fact]
    public void A_class_of_the_other_side_maps_to_the_players_side_by_role()
    {
        var w = Pair(Config, new MatchRules { TimeLimit = 0f }, SmTac, CsmTac);
        Assert.Equal(SmAssault, w.ClassForSide(CsmRaptor, Team.SpaceMarines));
        Assert.Equal(CsmTac, w.ClassForSide(SmTac, Team.Chaos));
        Assert.Equal(SmTac, w.ClassForSide(SmTac, Team.SpaceMarines));   // own side: itself
        Assert.Equal(CsmRaptor, w.ClassForSide(CsmRaptor, Team.None));   // no side: itself
        Assert.Equal(99, w.ClassForSide(99, Team.Chaos));                // out of range: unchanged
        // A Space Marine asking for the Raptor gets the Assault Marine; the indices carry over by position.
        var resolved = w.SetNextLoadout(1, new LoadoutChoice(CsmRaptor, -1, 1, 1, -1, 1, -1));
        Assert.Equal(new LoadoutChoice(SmAssault, -1, 1, 1, -1, 1, -1), resolved);
        Step(w, fire: true);
        int ticks = 0;
        while (!w.Players[1].Alive && ticks++ < 100) Step(w);
        var p = w.Players[1];
        Assert.Equal((SmAssault, Team.SpaceMarines, PlasmaPistol, 1, TrueGrit), (p.Class, p.Team, p.Pistol, p.MeleeChoice, p.Perk1));
        Assert.Equal(WeaponSlot.Pistol, p.Slot);   // no primary: the pistol in hand
        // A Chaos player asking for a Space Marine class stays Chaos.
        Assert.Equal(CsmRaptor, w.SetNextLoadout(2, new LoadoutChoice(SmAssault))!.Value.Class);
        // Without a side (no match rules) the class stands.
        var free = Pair(Config, null, SmTac, SmTac);
        Assert.Equal(CsmRaptor, free.SetNextLoadout(1, new LoadoutChoice(CsmRaptor))!.Value.Class);
    }

    [Fact]
    public void Indices_carry_over_by_position_to_the_other_sides_class_whose_lists_differ()
    {
        // The Raptor's lists differ in order and size from the Assault Marine's: pistols [plasma, sidearm] (no bolter), melee [sword] only, perks [true grit, charge].
        // Indices are sent for the class asked for and carried over by position (interpretation, docs/customizer-architecture.md 2.2): they
        // name whatever sits at that position in the mapped class's list, or the default when the list is shorter.
        var raptor = Config.Classes[CsmRaptor] with { PistolChoices = [PlasmaPistol, Sidearm], MeleeChoices = [Sword], PerkChoices = [TrueGrit, Charge] };
        var config = Config with { Classes = [Config.Classes[SmTac], Config.Classes[SmAssault], Config.Classes[CsmTac], raptor] };
        var w = Pair(config, new MatchRules { TimeLimit = 0f }, SmTac, CsmTac);
        // A Space Marine asking for the Raptor with pistol 0 (Raptor: plasma), melee 0 (sword), perk 1 0 (true grit) gets the Assault Marine's positions:
        Assert.Equal(new LoadoutChoice(SmAssault, -1, 0, 0, -1, 0, -1), w.SetNextLoadout(1, new LoadoutChoice(CsmRaptor, -1, 0, 0, -1, 0, -1)));
        Step(w, fire: true);
        int ticks = 0;
        while (!w.Players[1].Alive && ticks++ < 100) Step(w);
        var p = w.Players[1];
        Assert.Equal((SmAssault, Sidearm, 0, Charge), (p.Class, p.Pistol, p.MeleeChoice, p.Perk1));   // position 0 of the Assault lists
        // A Chaos player asking for the Assault Marine's pistol 2 (the bolter) and melee 1: the Raptor has no third pistol nor second melee: defaults.
        Assert.Equal(new LoadoutChoice(CsmRaptor, -1, -1, -1, -1, 1, -1), w.SetNextLoadout(2, new LoadoutChoice(SmAssault, -1, 2, 1, -1, 1, -1)));
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void Invalid_and_duplicate_choices_default()
    {
        // Out of range: every field the default; class out of range: the current class.
        Assert.Equal(new LoadoutChoice(SmTac, -1, -1, -1, -1, -1, -1), Loadouts.Resolve(Config, SmTac, new LoadoutChoice(SmTac, 9, 9, 9, 9, 9, 9)));
        var w = Pair(Config, null, SmTac, SmAssault);
        Assert.Equal(SmTac, w.SetNextLoadout(1, new LoadoutChoice(Class: 40))!.Value.Class);
        // Perk 2 naming perk 1's perk is dropped, also when perk 1 is the default perk.
        Assert.Equal(-1, Loadouts.Resolve(Config, SmTac, new LoadoutChoice(Perk1: 1, Perk2: 1)).Perk2);
        Assert.Equal(-1, Loadouts.Resolve(Config, SmTac, new LoadoutChoice(Perk2: 0)).Perk2);   // choice 0 = Homer = the default perk 1
        Assert.Equal(1, Loadouts.Resolve(Config, SmTac, new LoadoutChoice(Perk1: 2, Perk2: 1)).Perk2);
        // The same rules at a spawn (the Hello's path).
        var s = w.Spawn(3, SmTac, perk1Choice: 1, perk2Choice: 1);
        Assert.Equal((Targeter, -1), (s.Perk1, s.Perk2));
        s = w.Spawn(4, SmTac, perk2Choice: 0);
        Assert.Equal((Homer, -1), (s.Perk1, s.Perk2));
        s = w.Spawn(5, SmTac, perk1Choice: 7, perk2Choice: 2);
        Assert.Equal((Homer, Versatility), (s.Perk1, s.Perk2));
        // A state naming another class's perk resolves to the class default (perk 1) or none (perk 2).
        var odd = Combat.Respawned(s with { Perk1 = Charge, Perk2 = TrueGrit }, Config);
        Assert.Equal((Homer, -1), (odd.Perk1, odd.Perk2));
        // Without perks in the config nothing is chosen.
        var bare = new SimConfig();
        var b = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Perk1 = 3, Perk2 = 4 }, bare);
        Assert.Equal((-1, -1), (b.Perk1, b.Perk2));
    }

    [Fact]
    public void Pistol_bolters_need_the_True_Grit_perk_by_one_switchable_rule()
    {
        Assert.True(Loadouts.IsPistolBolter(Config.Weapons[PistolBolter]));
        Assert.True(Loadouts.IsPistolBolter(Gun("csm_mp_pistol_bolter_gold", WeaponSlot.Pistol)));
        Assert.False(Loadouts.IsPistolBolter(Gun("sm_mp_pistol_bolter", WeaponSlot.Ranged)));
        Assert.False(Loadouts.IsPistolBolter(Config.Weapons[PlasmaPistol]));
        var w = Pair(Config, null, SmAssault, SmAssault);
        // Pistol choice 2 is the pistol bolter: refused without True Grit (default perk: the charge).
        Assert.Equal(-1, Loadouts.Resolve(Config, SmAssault, new LoadoutChoice(Pistol: 2)).Pistol);
        Assert.Equal(Sidearm, w.Spawn(3, SmAssault, pistolChoice: 2).Pistol);
        // With True Grit in either slot it stands.
        Assert.Equal(2, Loadouts.Resolve(Config, SmAssault, new LoadoutChoice(Pistol: 2, Perk1: 1)).Pistol);
        Assert.Equal(2, Loadouts.Resolve(Config, SmAssault, new LoadoutChoice(Pistol: 2, Perk2: 1)).Pistol);
        var grit = w.Spawn(4, SmAssault, pistolChoice: 2, perk2Choice: 1);
        Assert.Equal((PistolBolter, Charge, TrueGrit), (grit.Pistol, grit.Perk1, grit.Perk2));
        Assert.Equal("sm_mp_pistol_bolter", Loadouts.InHand(Config, grit)!.Name);
        // Other pistols never need it.
        Assert.Equal(PlasmaPistol, w.Spawn(5, SmAssault, pistolChoice: 1).Pistol);
        // The rule switched off: the bolter without the perk.
        var off = Config with { PistolBoltersNeedTrueGrit = false };
        Assert.Equal(2, Loadouts.Resolve(off, SmAssault, new LoadoutChoice(Pistol: 2)).Pistol);
        Assert.Equal(PistolBolter, new World(off).Spawn(1, SmAssault, pistolChoice: 2).Pistol);
        Assert.NotEqual(Config.Hash(), off.Hash());
    }

    [Fact]
    public void Upgrade_strings_name_the_selected_perks_and_skip_one_without_a_string()
    {
        var w = new World(Config);
        var s = w.Spawn(1, SmAssault, perk2Choice: 1);
        Assert.Equal(new[] { "mp_perk_improved_bullrush", Loadouts.TrueGritPerk }, Loadouts.UpgradeStrings(Config, s));
        Assert.True(Loadouts.HasPerk(Config, s, Loadouts.TrueGritPerk));
        var t = w.Spawn(2, SmTac, perk1Choice: 2, perk2Choice: 1);   // Weapon Versatility (no string) and the bolter perk
        Assert.Equal(new[] { "mp_perk_weap_bolter_accuracy" }, Loadouts.UpgradeStrings(Config, t));
        Assert.Empty(Loadouts.UpgradeStrings(new SimConfig(), new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true)));
        Assert.Contains("perks mp_perk_secondary_weapon + mp_perk_weap_bolter_accuracy", Loadouts.Describe(Config, t));
    }

    // ---------------------------------------------------------------- config hash and content

    [Fact]
    public void The_config_hash_changes_with_the_perks()
    {
        ulong baseline = Config.Hash();
        Assert.Equal(baseline, (Config with { }).Hash());
        var perks = Config.Perks.ToArray();
        SimConfig WithPerk(int i, PerkSpec p) { var copy = perks.ToArray(); copy[i] = p; return Config with { Perks = copy }; }
        var variants = new[]
        {
            Config with { Perks = perks[..4] },
            WithPerk(0, perks[0] with { Name = "mp_perk_respawn_timer" }),
            WithPerk(0, perks[0] with { UpgradeString = null }),
            WithPerk(0, perks[0] with { UpgradeString = "" }),
            WithPerk(3, perks[3] with { Kind = PerkKind.Class }),
            WithPerk(1, perks[1] with { Level = 24 }),
            Config with { Classes = [Config.Classes[0] with { PerkChoices = [Homer, Targeter] }, .. Config.Classes.Skip(1)] },
            Config with { Classes = [Config.Classes[0] with { DefaultPerk = Targeter }, .. Config.Classes.Skip(1)] },
            Config with { PistolBoltersNeedTrueGrit = false },
        };
        var hashes = variants.Select(v => v.Hash()).Append(baseline).ToList();
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public void Perks_are_read_from_their_wargear_files()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.Build(
            ("action_data", new (string, object)[]
            {
                ("lifetime_actions", new (string, object)[]
                {
                    ("commands", new (string, object)[]
                    {
                        ("apply_upgrade_string", new (string, object)[] { ("lifetime", 0f), ("strings", new (string, object)[] { ("entry", "mp_perk_weap_las_cannon_reload") }) }),
                    }),
                }),
            }),
            ("common_data", new (string, object)[]
            {
                ("slot", "perk"),
                ("unlock_info", new (string, object)[] { ("level_requirement", new (string, object)[] { ("level_required", 1) }) }),
            })));
        Assert.Equal(new PerkSpec("mp_perk_weap_las_cannon_recharge", "mp_perk_weap_las_cannon_reload", PerkKind.Weapon, 1),
            MpClasses.ReadPerk("mp_perk_weap_las_cannon_recharge", doc));
        var bare = BafDocument.Read(SyntheticBafWriter.Build(("common_data", new (string, object)[]
            { ("unlock_info", new (string, object)[] { ("level_requirement", new (string, object)[] { ("level_required", 13) }) }) })));
        Assert.Equal(new PerkSpec("mp_perk_secondary_weapon", null, PerkKind.Class, 13), MpClasses.ReadPerk("mp_perk_secondary_weapon", bare));
    }

    [Fact]
    public void The_content_loader_keeps_perks_and_refuses_bad_perk_indices()
    {
        var weapons = Config.Weapons;
        var loaded = GameplayTuning.WithClasses(new SimConfig(), weapons, Config.Classes, perks: Config.Perks);
        Assert.NotNull(loaded);
        Assert.Equal(Config.Perks, loaded!.Perks);
        Assert.Equal(Config.Classes[SmAssault].PerkChoices, loaded.Classes[SmAssault].PerkChoices);
        Assert.Equal(Charge, loaded.Classes[SmAssault].DefaultPerk);
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), weapons, Config.Classes));   // perk indices without perks
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), weapons, [Config.Classes[0] with { DefaultPerk = 5 }], perks: Config.Perks));
        Assert.Null(GameplayTuning.WithClasses(new SimConfig(), weapons, Config.Classes, perks: [.. Config.Perks.Take(4), Config.Perks[4] with { Level = -1 }]));
    }
}
