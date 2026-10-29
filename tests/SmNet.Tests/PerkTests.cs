using System.Numerics;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M43: perks in the simulation. Variants and player-blueprint upgrades are built here as the content loader builds them from the
/// data (synthetic values; the loader itself is tested in SmFormats.Tests.PerkVariantTests): a player with the perk uses the
/// variant's numbers, a player without it the base, and the client's prediction picks the same variant as the server.
/// </summary>
public class PerkTests(ITestOutputHelper output)
{
    const string Mag = "u_mag", Burst = "u_burst", Fnp = "u_fnp", Imp = "u_imp", Larr = "u_larr", Art = "u_art", Halo = "u_halo", Veng = "u_veng",
        React = "u_react", BlastOff = "u_blastoff", Recharge = "u_recharge", KnifePerk = "u_knife", HealPerk = "u_heal", Armory = "u_armory", Master = "u_master",
        Inert = "u_inert", Cool = "u_cool";
    static readonly string[] Strings = [Mag, Burst, Fnp, Imp, Larr, Art, Halo, Veng, React, BlastOff, Recharge, KnifePerk, HealPerk, Armory, Master, Inert, Cool];
    static int P(string upgrade) => Array.IndexOf(Strings, upgrade);
    /// <summary>The perk files: named after the original's where a status note applies (Rapid Deployment inert; Burst Fire has had no note since weapons/spread).</summary>
    static string NameOf(string u) => u switch { Burst => "mp_perk_weap_stalker_zoom_modes", Inert => "mp_perk_respawn_timer", _ => "mp_perk_" + u };

    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static WeaponSpec Gun(string name, WeaponSlot slot, TriggerKind trigger, float damage, float cooldown, int clip, int reserve) =>
        new(name, slot, trigger, damage, cooldown, clip, reserve, 1.5f, 250f, 245f, 250f, 1f, 2.5f, 1f, 0.5f, 0.5f) { HitType = DamageType.Ranged };

    static readonly WeaponSpec RifleBase = Gun("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270);
    static readonly WeaponSpec Rifle = RifleBase with { Variants = [new([Mag], RifleBase with { ClipSize = 16 })] };
    static readonly WeaponSpec StalkerBase = Gun("stalker", WeaponSlot.Ranged, TriggerKind.SemiAuto, 50f, 0.267f, 10, 40);
    static readonly WeaponSpec Stalker = StalkerBase with { Variants = [new([Burst], StalkerBase with { ShotCooldown = 0.475f, BurstLength = 2, BurstInterval = 0.1f, ReserveSize = 60 })] };
    static readonly WeaponSpec PistolBase = Gun("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 15f, 0.2f, 10, 80);
    static readonly WeaponSpec Pistol = PistolBase with { Variants = [new([Mag], PistolBase with { ClipSize = 16 })] };
    static readonly WeaponSpec HeatGunBase = Gun("heat", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.1f, 400, 0) with { Heat = new HeatSpec(3f, 100f, 75f, 20f, 25f, 0.3f, 0f) };
    static readonly WeaponSpec HeatGun = HeatGunBase with { Variants = [new([Cool], HeatGunBase with { Heat = HeatGunBase.Heat! with { PerShot = 1f } })] };

    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 40f, 20f, 3.5f);
    static readonly MeleeCombo Knife = new("knife", [Light])
    {
        Variants = [new([KnifePerk], new MeleeCombo("knife", [Light with { Damage = 65f, SecondaryDamage = 32f }])), new([HealPerk], new MeleeCombo("knife", [Light with { Heal = 30f }]))],
    };

    static readonly ThrownSpec Frag = GrenadeTests.Frag with
    {
        Variants =
        [
            new([Armory], GrenadeTests.Frag with { Count = 4, InitialCount = 2 }),
            new([Master], GrenadeTests.Frag with { Projectile = GrenadeTests.FragBall with { Splash = GrenadeTests.FragBall.Splash! with { Radius = 15f, Damage = GrenadeTests.FragBall.Splash.Damage with { Damage = 300f } } } }),
        ],
    };

    static readonly SplashSpec TakeOff = new(4f, Plain with { Damage = 25f, ShieldDamage = 50f, Type = DamageType.Explosive }, [new(0f, 1f), new(3.5f, 1f), new(4f, 0.25f)], true, true, World.EnemiesOnly);
    static readonly JumpPackSpec Pack = JumpPackTests.Pack with
    {
        Variants = [new([BlastOff], JumpPackTests.Pack with { TakeOffBlasts = [TakeOff] }), new([Recharge], JumpPackTests.Pack with { RechargeRate = 15f })],
    };

    static readonly PlayerUpgrade[] Upgrades =
    [
        new(Fnp) { ReceivedMelee = 0.7f, ReceivedExplosive = 0.7f },
        new(Imp) { ReceivedRanged = 0.7f },
        new(Larr) { HealthRegenRate = 45f, HealthRegenDelay = -1.5f },
        new(Art) { ShieldRegenDelay = -5f },
        new(Halo) { Shield = new ShieldInfo(150f, 7f, 33f) },
        new(Veng) { DeathBlasts = [new SplashSpec(8f, Plain with { Damage = 100f, ShieldDamage = 100f, Type = DamageType.Explosive }, [new(0f, 1f), new(5f, 1f), new(8f, 0.25f)], true, true, "target_all")], DeathBlastDelay = 2.5f },
        new(React) { ShieldBreakBlasts = [new SplashSpec(4.5f, Plain with { Damage = 25f, ShieldDamage = 35f, Type = DamageType.Explosive }, [], true, true, "target_all")] },   // as the data: no fall-off, target_all
    ];

    const int Tac = 0, Chaos = 1, Jumper = 2, Dev = 3;
    static readonly IReadOnlyList<int> AllPerks = Enumerable.Range(0, Strings.Length).ToArray();

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Stalker, Pistol, HeatGun],
        Thrown = [Frag],
        JumpPacks = [Pack],
        Perks = Strings.Select(u => new PerkSpec(NameOf(u), u, PerkKind.Class, 1)).Append(new PerkSpec("mp_perk_secondary_weapon", null, PerkKind.Class, 13)).ToArray(),
        PlayerUpgrades = Upgrades,
        Classes =
        [
            new ClassLoadout("tac", 0, 2, [Light]) { PrimaryChoices = [0, 1], PistolChoices = [2], MeleeChoices = [Knife], EquipmentChoices = [0], Side = Team.SpaceMarines, PerkChoices = AllPerks },
            new ClassLoadout("chaos", 0, 2, [Light]) { PrimaryChoices = [0, 1], PistolChoices = [2], MeleeChoices = [Knife], EquipmentChoices = [0], Side = Team.Chaos, PerkChoices = AllPerks },
            new ClassLoadout("jumper", -1, 2, [Light]) { MeleeChoices = [Knife], JumpPack = 0, Side = Team.SpaceMarines, PerkChoices = AllPerks },
            new ClassLoadout("dev", 3, 2, []) { Side = Team.SpaceMarines, PerkChoices = AllPerks },
        ],
        HealthMax = 100f, ShieldMax = 0f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static PlayerState Fresh(int cls, string? perk1 = null, string? perk2 = null, SimConfig? c = null, int primary = -1) =>
        Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true)
        {
            Class = cls, Perk1 = perk1 is null ? -1 : P(perk1), Perk2 = perk2 is null ? -1 : P(perk2), Primary = primary,
        }, c ?? Config);

    /// <summary>A world with players spawned at the given points, in order (no rules: the cursor takes the points in order).</summary>
    static World Arena(SimConfig c, params (int Class, Vector3 At, string? Perk1, string? Perk2)[] players)
    {
        var world = new World(c, null, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray());
        for (int i = 0; i < players.Length; i++)
            world.Spawn(i + 1, players[i].Class, perk1Choice: players[i].Perk1 is { } a ? P(a) : -1, perk2Choice: players[i].Perk2 is { } b ? P(b) : -1);
        return world;
    }

    static void Run(World w, int ticks, Func<int, int, InputButtons> buttons, Func<int, float>? yaw = null, Action<int>? each = null)
    {
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            uint view = w.Tick;
            w.Step(id => new PlayerInput(0, 0f, 0f, yaw?.Invoke(id) ?? 0f, buttons(tick, id)) with { ViewTick = view });
            each?.Invoke(tick);
        }
    }

    // ---------------------------------------------------------------- variant numbers

    [Fact]
    public void A_perk_gives_its_variant_numbers_and_a_player_without_it_keeps_the_base()
    {
        // Weapons (both slots), equipment, jump pack and melee: the variant's numbers with the perk, the base without.
        var mag = Fresh(Tac, Mag);
        Assert.Equal((16, 16), (mag.Clip, mag.OtherClip));                   // the rifle in hand and the holstered pistol both have the variant
        var plain = Fresh(Tac);
        Assert.Equal((30, 10), (plain.Clip, plain.OtherClip));
        Assert.Equal((16, 16), (Fresh(Tac, Fnp, Mag).Clip, Fresh(Tac, Fnp, Mag).OtherClip));   // either slot
        Assert.Equal((2, 1), (Fresh(Tac, Armory).Grenades, Fresh(Tac).Grenades));
        Assert.Equal(4, Loadouts.Equipment(Config, Fresh(Tac, Armory))!.Count);
        Assert.Equal((15f, 10f), (Loadouts.JumpPack(Config, Fresh(Jumper, Recharge))!.RechargeRate, Loadouts.JumpPack(Config, Fresh(Jumper))!.RechargeRate));
        Assert.Equal((65f, 32f, 40f, 20f), (Loadouts.Melee(Config, Fresh(Tac, KnifePerk))[0].Damage, Loadouts.Melee(Config, Fresh(Tac, KnifePerk))[0].SecondaryDamage,
            Loadouts.Melee(Config, Fresh(Tac))[0].Damage, Loadouts.Melee(Config, Fresh(Tac))[0].SecondaryDamage));
        Assert.Equal((30f, 0f), (Loadouts.Melee(Config, Fresh(Tac, HealPerk))[0].Heal, Loadouts.Melee(Config, Fresh(Tac))[0].Heal));
        var heat = Fresh(Dev, Cool);
        Assert.Equal((1f, 3f), (Loadouts.InHand(Config, heat)!.Heat!.PerShot, Loadouts.InHand(Config, Fresh(Dev))!.Heat!.PerShot));
        // The player blueprint: health and shield values.
        Assert.Equal((150f, 0f), (Fresh(Dev, Halo).Shield, Fresh(Dev).Shield));
        var larr = Loadouts.Body(Config, Fresh(Tac, Larr));
        Assert.Equal((50f, 1e6f - 1.5f), (larr.HealthRegenRate, larr.HealthRegenDelay));
        var both = Loadouts.Body(Config, Fresh(Dev, Halo, Art));
        Assert.Equal((150f, 2f, 33f), (both.ShieldMax, both.ShieldRegenDelay, both.ShieldRegenRate));   // Iron Halo's shield, then Artificer's −5 s
        Assert.Equal((0.7f, 0.7f, 1f), (Loadouts.Body(Config, Fresh(Dev, Fnp)).ReceivedMelee, Loadouts.Body(Config, Fresh(Dev, Fnp)).ReceivedExplosive, Loadouts.Body(Config, Fresh(Dev, Fnp)).ReceivedRanged));
        Assert.Equal(new BodyStats(100f, 1e6f, 5f, 0f, 1e6f, 33f, 1f, 1f, 1f), Loadouts.Body(Config, Fresh(Dev)));
    }

    [Fact]
    public void Regeneration_follows_the_perks()
    {
        var c = Config with { HealthRegenDelay = 5f, ShieldMax = 100f, ShieldRegenDelay = 7f };
        // Larraman's Blessing: health from 3.5 s at 50/s (base 5 s at 5/s). Artificer Armor: the shield from 2 s (base 7 s).
        PlayerState Hurt(params string[] perks) => Fresh(Tac, perks.ElementAtOrDefault(0), perks.ElementAtOrDefault(1), c) with { Health = 10f, Shield = 0f, SinceDamage = 0f };
        PlayerState After(PlayerState s, float seconds)
        {
            for (int k = 0; k < (int)MathF.Round(seconds * c.TickRate); k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _);
            return s;
        }
        Assert.Equal(10f, After(Hurt(), 4f).Health, 3);                          // still waiting (5 s)
        Assert.True(After(Hurt(Larr), 4f).Health > 30f);                          // 0.5 s of 50/s
        Assert.Equal(0f, After(Hurt(), 3f).Shield, 3);
        Assert.True(After(Hurt(Art), 3f).Shield > 30f);                           // 1 s of 33/s
        Assert.Equal(100f, After(Hurt(Larr), 6f).Health, 3);                      // capped at the maximum
    }

    // ---------------------------------------------------------------- received damage

    [Fact]
    public void Feel_No_Pain_and_Impenetrable_scale_melee_ranged_and_explosive_damage_taken()
    {
        float Taken(string? victimPerk, bool melee)
        {
            // Attacker (player 1) at the origin facing +Y; the victim 2 m (melee) or 5 m (rifle) ahead.
            var w = Arena(Config, (Tac, Vector3.Zero, null, null), (Chaos, new Vector3(0f, melee ? 2f : 5f, 0f), victimPerk, null));
            float health = 0f;
            Run(w, 10, (tick, id) => id == 1 && tick == 0 ? (melee ? InputButtons.Melee : InputButtons.Fire) : InputButtons.None,
                each: _ => { foreach (var h in w.LastHits) if (h.Target == 2) health += h.HealthDamage; });
            return health;
        }
        Assert.Equal(40f, Taken(null, melee: true), 3);
        Assert.Equal(28f, Taken(Fnp, melee: true), 3);    // ×0.7
        Assert.Equal(40f, Taken(Imp, melee: true), 3);    // ranged only
        Assert.Equal(20f, Taken(null, melee: false), 3);
        Assert.Equal(14f, Taken(Imp, melee: false), 3);   // ×0.7
        Assert.Equal(20f, Taken(Fnp, melee: false), 3);   // melee and explosive only
    }

    // ---------------------------------------------------------------- blasts

    [Fact]
    public void Blast_Off_hits_enemies_at_the_lift_off_but_spares_allies_and_explosive_damage_meets_Feel_No_Pain()
    {
        (float Enemy, float Ally, int Victims) Lift(string? jumperPerk, string? enemyPerk)
        {
            // Rules with friendly fire: the blast's own filter (enemies and neutrals) spares the ally. Spawn order: the enemy at
            // the origin, then the jumper and its ally at the free points farthest from it (3 m and 2.5 m).
            var c = Config with { HealthMax = 1000f };
            var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(3f, 0f, 0f), 0f), new SpawnPoint(new Vector3(0f, 2.5f, 0f), 0f)])
            { Rules = new MatchRules { TimeLimit = 0f, FriendlyFire = true } };
            w.Spawn(1, Chaos, perk1Choice: enemyPerk is { } e ? P(e) : -1);
            w.Spawn(2, Jumper, perk1Choice: jumperPerk is { } j ? P(j) : -1);
            w.Spawn(3, Tac);
            Assert.Equal((Vector3.Zero, new Vector3(3f, 0f, 0f), new Vector3(0f, 2.5f, 0f)), (w.Players[1].Position, w.Players[2].Position, w.Players[3].Position));
            float enemy = 0f, ally = 0f;
            int launches = 0, victims = 0;
            Run(w, 12, (tick, id) => id == 2 && tick == 0 ? InputButtons.Jump : InputButtons.None, each: _ =>
            {
                foreach (var h in w.LastHits.Where(h => h.Shooter == 2)) { if (h.Target == 1) enemy += h.HealthDamage; if (h.Target == 3) ally += h.HealthDamage; }
                foreach (var j in w.LastJumps.Where(j => j.Kind == JumpEventKind.Launch)) { launches++; victims += j.Victims; }
            });
            Assert.Equal(1, launches);
            return (enemy, ally, victims);
        }
        Assert.Equal((0f, 0f, 0), Lift(null, null));               // the base pack: no take-off blast
        Assert.Equal((25f, 0f, 1), Lift(BlastOff, null));          // 25 at full intensity (3 m), the ally 3.9 m away spared by the filter
        Assert.Equal((17.5f, 0f, 1), Lift(BlastOff, Fnp));         // explosive ×0.7
    }

    [Fact]
    public void Reactive_Blast_goes_off_when_the_armour_breaks()
    {
        var c = Config with { ShieldMax = 50f, HealthMax = 1000f };
        foreach (var perk in new[] { React, (string?)null })
        {
            // Player 2 (the victim, maybe with the perk) is shot by player 1 from 3 m: 10 shield per shot (rifle 20 × 0.5), broken on the fifth.
            var w = Arena(c, (Tac, Vector3.Zero, null, null), (Chaos, new Vector3(0f, 3f, 0f), perk, null));
            int brokeAt = -1, blastAt = -1, blasts = 0;
            float blastDamage = 0f;
            Run(w, 40, (tick, id) => id == 1 ? InputButtons.Fire : InputButtons.None, each: tick =>
            {
                if (brokeAt < 0 && w.Players[2].Shield <= 0f) brokeAt = tick;
                foreach (var h in w.LastHits.Where(h => h.Shooter == 2 && h.Target == 1 && h.Splash)) { blastAt = blastAt < 0 ? tick : blastAt; blastDamage += h.ShieldDamage + h.HealthDamage; blasts++; }
            });
            Assert.True(brokeAt > 0);
            if (perk is null) { Assert.Equal(-1, blastAt); continue; }
            Assert.Equal(brokeAt, blastAt);              // the same tick as the breaking hit
            Assert.Equal(1, blasts);                     // once: the shield stays broken (no regeneration here)
            // No fall-off (as the data): 35 off the shield only, then 25 through the shield, at full intensity within 4.5 m.
            Assert.Equal(35f + 25f, blastDamage, 3);
        }
    }

    [Fact]
    public void Final_Vengeance_explodes_where_the_player_died_two_and_a_half_seconds_later()
    {
        var c = Config with { HealthMax = 30f };
        var w = Arena(c, (Tac, Vector3.Zero, null, null), (Chaos, new Vector3(0f, 3f, 0f), Veng, null));
        int diedAt = -1, blastAt = -1;
        KillEvent? kill = null;
        Run(w, 120, (tick, id) => id == 1 && diedAt < 0 ? InputButtons.Fire : InputButtons.None, each: tick =>
        {
            if (diedAt < 0 && !w.Players[2].Alive) diedAt = tick;
            if (w.LastHits.Any(h => h.Shooter == 2 && h.Target == 1)) blastAt = blastAt < 0 ? tick : blastAt;
            foreach (var k in w.LastKills.Where(k => k.Victim == 1)) kill = k;
            if (w.LastPerkBlasts.Count > 0) output.WriteLine($"tick {tick}: {w.LastPerkBlasts.Count} perk blast(s)");
        });
        Assert.True(diedAt > 0, "the victim did not die");
        Assert.Equal(diedAt + 75, blastAt);              // 2.5 s at 30 Hz
        Assert.False(w.Players[1].Alive);                // 100 at 3 m kills the 30-health shooter
        Assert.Equal((2, KillCause.Perk, P(Veng)), (kill!.Value.Killer, kill.Value.Cause, kill.Value.Index));   // a perk kill naming the perk
    }

    [Fact]
    public void Final_Vengeance_chains_a_blast_that_kills_another_carrier_sets_off_its_own()
    {
        // S shoots A (Final Vengeance); A's blast kills B (Final Vengeance, 3 m away) 2.5 s later; B's blast 2.5 s after that kills C
        // (6 m from B, out of A's 8 m reach at 9 m). Nobody respawns meanwhile.
        var c = Config with { HealthMax = 30f, RespawnTime = 1e6f };
        var w = Arena(c, (Tac, Vector3.Zero, null, null), (Chaos, new Vector3(0f, 3f, 0f), Veng, null), (Chaos, new Vector3(0f, 6f, 0f), Veng, null),
            (Tac, new Vector3(0f, 12f, 0f), null, null));
        var died = new Dictionary<int, int>();
        var blasts = new List<(int Tick, int Owner)>();
        var kills = new List<KillEvent>();
        Run(w, 200, (tick, id) => id == 1 && !died.ContainsKey(2) ? InputButtons.Fire : InputButtons.None, each: tick =>
        {
            foreach (var (id, p) in w.Players) if (!p.Alive && !died.ContainsKey(id)) died[id] = tick;
            blasts.AddRange(w.LastPerkBlasts.Select(b => (tick, b.Owner)));
            kills.AddRange(w.LastKills);
        });
        Assert.Equal(died[2] + 75, died[3]);                                   // A's blast kills B
        Assert.Equal(died[3] + 75, died[4]);                                   // B's blast kills C
        Assert.Equal([(died[2] + 75, 2), (died[3] + 75, 3)], blasts.Where(b => b.Owner is 2 or 3).ToList());
        Assert.Contains(kills, k => (k.Killer, k.Victim, k.Cause, k.Index) == (3, 4, KillCause.Perk, P(Veng)));
        Assert.Empty(w.PendingPerkBlasts);
    }

    [Fact]
    public void Perk_blasts_follow_friendly_fire_the_data_filters_and_the_match_end_and_a_restart_clears_them()
    {
        // Reactive Blast (target_all in the data): the victim's ally beside it is spared without friendly fire and hit with it; the
        // shooter (an enemy) always. Spawn order with rules: the Chaos shooter at the origin, then the Marine victim and its ally.
        (float Shooter, float Ally) Reactive(bool friendlyFire)
        {
            var c = Config with { ShieldMax = 50f, HealthMax = 1000f };
            var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 4f, 0f), 0f), new SpawnPoint(new Vector3(2f, 3f, 0f), 0f)])
            { Rules = new MatchRules { TimeLimit = 0f, FriendlyFire = friendlyFire } };
            w.Spawn(1, Chaos); w.Spawn(2, Tac, perk1Choice: P(React)); w.Spawn(3, Tac);
            Assert.Equal((new Vector3(0f, 4f, 0f), new Vector3(2f, 3f, 0f)), (w.Players[2].Position, w.Players[3].Position));   // the farthest free points from the shooter
            float shooter = 0f, ally = 0f;
            Run(w, 40, (tick, id) => id == 1 ? InputButtons.Fire : InputButtons.None, each: _ =>
            {
                foreach (var h in w.LastHits.Where(h => h.Shooter == 2 && h.Perk == P(React))) { if (h.Target == 1) shooter += h.ShieldDamage + h.HealthDamage; if (h.Target == 3) ally += h.ShieldDamage + h.HealthDamage; }
            });
            return (shooter, ally);
        }
        Assert.Equal((60f, 0f), Reactive(friendlyFire: false));
        Assert.Equal((60f, 60f), Reactive(friendlyFire: true));

        // A death blast due after the match ended deals nothing; one due after the restart never goes off (cleared).
        foreach (float restart in new[] { 10f, 1f })
        {
            var c = Config with { HealthMax = 30f };
            var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 3f, 0f), 0f)])
            { Rules = new MatchRules { TimeLimit = 0f, ScoreLimit = 1, RestartDelay = restart } };
            w.Spawn(1, Tac); w.Spawn(2, Chaos, perk1Choice: P(Veng));
            int diedAt = -1, fired = 0, hits = 0;
            Run(w, 120, (tick, id) => id == 1 && diedAt < 0 ? InputButtons.Fire : InputButtons.None, each: tick =>
            {
                if (diedAt < 0 && !w.Players[2].Alive) diedAt = tick;
                fired += w.LastPerkBlasts.Count;
                hits += w.LastHits.Count(h => h.Perk >= 0);
            });
            Assert.True(diedAt > 0);
            Assert.Equal(restart > 2.5f ? 1 : 0, fired);   // ended (fires, harms nobody) or restarted (cleared)
            Assert.Equal(0, hits);
        }
    }

    [Fact]
    public void The_config_hash_covers_every_perk_field()
    {
        ulong Hash(SimConfig c) => c.Hash();
        var baseHash = Hash(Config);
        Assert.Equal(baseHash, Hash(Config with { }));
        var rifle = Config.Weapons[0];
        Assert.NotEqual(baseHash, Hash(Config with { Weapons = [rifle with { Variants = [new([Mag], RifleBase with { ClipSize = 17 })] }, .. Config.Weapons.Skip(1)] }));
        Assert.NotEqual(baseHash, Hash(Config with { Weapons = [rifle with { Variants = [new([Burst], RifleBase with { ClipSize = 16 })] }, .. Config.Weapons.Skip(1)] }));   // the upgrade string
        Assert.NotEqual(baseHash, Hash(Config with { Weapons = [rifle with { BurstLength = 2, BurstInterval = 0.05f }, .. Config.Weapons.Skip(1)] }));
        Assert.NotEqual(baseHash, Hash(Config with { Weapons = [rifle with { BurstInterval = 0.05f }, .. Config.Weapons.Skip(1)] }));
        Assert.NotEqual(baseHash, Hash(Config with { Weapons = [rifle with { HitType = DamageType.Explosive }, .. Config.Weapons.Skip(1)] }));
        Assert.NotEqual(baseHash, Hash(Config with { PlayerUpgrades = [.. Upgrades.Take(6)] }));
        Assert.NotEqual(baseHash, Hash(Config with { PlayerUpgrades = [Upgrades[0] with { ReceivedMelee = 0.71f }, .. Upgrades.Skip(1)] }));
        Assert.NotEqual(baseHash, Hash(Config with { PlayerUpgrades = [.. Upgrades.SkipLast(1), Upgrades[^1] with { ShieldBreakBlasts = [Upgrades[^1].ShieldBreakBlasts[0] with { Damage = Plain with { Damage = 25f } }] }] }));
        ClassLoadout WithKnife(MeleeCombo k) => Config.Classes[Tac] with { MeleeChoices = [k] };
        Assert.NotEqual(baseHash, Hash(Config with { Classes = [WithKnife(Knife with { Variants = [new([HealPerk], new MeleeCombo("knife", [Light with { Heal = 31f }]))] }), .. Config.Classes.Skip(1)] }));
        Assert.NotEqual(baseHash, Hash(Config with { Classes = [WithKnife(Knife with { Steps = [Light with { DamageType = DamageType.Explosive }] }), .. Config.Classes.Skip(1)] }));
        Assert.NotEqual(baseHash, Hash(Config with { JumpPacks = [Pack with { TakeOffBlasts = [TakeOff] }] }));
        Assert.NotEqual(baseHash, Hash(Config with { Thrown = [Frag with { Variants = [Frag.Variants[0]] }] }));
    }

    // ---------------------------------------------------------------- melee heal, burst, grenades

    [Fact]
    public void Swordsmans_Zeal_heals_the_attacker_on_a_hit_below_full_health()
    {
        foreach (var perk in new[] { HealPerk, (string?)null })
        {
            // Player 2 strikes player 1 first (40), then player 1 strikes back twice.
            var w = Arena(Config, (Tac, Vector3.Zero, perk, null), (Chaos, new Vector3(0f, 2f, 0f), null, null));
            Run(w, 70, (tick, id) => (id == 2 && tick == 0) || (id == 1 && tick is 10 or 45) ? InputButtons.Melee : InputButtons.None, id => id == 2 ? MathF.PI : 0f);
            Assert.Equal(100f - 80f, w.Players[2].Health, 3);   // both strikes back landed (40 each)
            Assert.Equal(perk is null ? 60f : 100f, w.Players[1].Health, 3);   // 60 + 30 + 30, capped at 100
        }
    }

    [Fact]
    public void Burst_Fire_fires_two_rounds_a_tenth_of_a_second_apart_per_pull()
    {
        foreach (var perk in new[] { Burst, (string?)null })
        {
            var s = Fresh(Tac, perk, primary: 1);
            Assert.Equal("stalker", Loadouts.InHand(Config, s)!.Name);
            var shots = new List<int>();
            for (int k = 0; k < 30; k++)
            {
                var press = k is 0 or 5 or 20 ? InputButtons.Fire : InputButtons.None;   // a pull during the cooldown does nothing
                s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, press), Config, out var e);
                if (e.Fired) shots.Add(k);
            }
            if (perk is null) Assert.Equal([0, 20], shots);                          // the pull at 5 (0.167 s) comes inside the 0.267 s cooldown
            else Assert.Equal([0, 3, 20, 23], shots);                                // 0.1 s = 3 ticks; 0.475 s from the first round
            Assert.Equal(perk is null ? 8 : 6, s.Clip);
            Assert.Equal(0, s.BurstLeft);
        }
    }

    [Fact]
    public void Master_Crafted_Wargear_throws_the_upgraded_grenade()
    {
        foreach (var perk in new[] { Master, (string?)null })
        {
            var w = Arena(Config, (Tac, Vector3.Zero, perk, null));
            Run(w, 8, (tick, _) => tick == 0 ? InputButtons.Grenade : InputButtons.None);
            var grenade = Assert.Single(w.Projectiles);
            Assert.Equal(perk is null ? 10f : 15f, grenade.Spec!.Splash!.Radius);
        }
    }

    // ---------------------------------------------------------------- status and wire

    [Fact]
    public void A_perk_is_active_only_when_something_the_class_carries_needs_its_string()
    {
        var c = Config;
        PerkStatus Of(string name) => Perks.StatusOf(c, c.Perks.Single(p => p.Name == name));
        Assert.Equal(PerkActivity.Active, Of("mp_perk_u_mag").Activity);
        Assert.Equal(PerkActivity.Active, Of("mp_perk_u_fnp").Activity);                         // a player-blueprint upgrade
        Assert.Equal(PerkActivity.Active, Of("mp_perk_weap_stalker_zoom_modes").Activity);       // weapons/spread: its scatter sets act, the note is gone
        var inert = Of("mp_perk_respawn_timer");
        Assert.Equal(PerkActivity.Inert, inert.Activity);
        Assert.Contains("owner question", inert.Note);
        Assert.Equal(PerkActivity.Inert, Of("mp_perk_secondary_weapon").Activity);               // no upgrade string at all
        Assert.Equal("NOT ACTIVE YET", PerkStatus.NotActiveLabel);
        // A class that carries nothing reacting to a string: that class's view of the perk is inert.
        Assert.DoesNotContain(Burst, Perks.Consumed(c, Jumper));   // the jumper carries no stalker
        Assert.Contains(Burst, Perks.Consumed(c, Tac));
        Assert.Contains(Mag, Perks.Consumed(c, Jumper));           // its pistol has the magazine variant
    }

    [Fact]
    public void Protocol_23_carries_the_own_perks_and_burst_and_not_the_others()
    {
        Assert.Equal(42, Protocol.Version);
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, Perk1 = 5, Perk2 = 126, BurstLeft = 1 };
        var other = new PlayerState(4, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, Perk1 = 7, Perk2 = 8 };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [me, other], Self: 3))));
        Assert.Equal(me, back.Players[0]);
        Assert.Equal((-1, -1, 0), (back.Players[1].Perk1, back.Players[1].Perk2, back.Players[1].BurstLeft));   // slim: none
        var none = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [me with { Perk1 = -1, Perk2 = 200, BurstLeft = 9 }], Self: 3)))).Players[0];
        Assert.Equal((-1, -1, Protocol.MaxBurstLeft), (none.Perk1, none.Perk2, none.BurstLeft));   // out of the field's range: none; the burst clamped
        Assert.Equal(1200, Protocol.MaxSnapshotBytes);
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(43)]
    [InlineData(44)]
    public void Prediction_matches_the_server_with_perks_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // Damage 0 (hits are not predicted); shots go up. Client 0: Tactical with Burst Fire on the stalker and the magazine perk on the
        // pistol; client 1: the heat gun with its coolant perk and Iron Halo; client 2: the jumper with the recharge perk.
        var config = Config with
        {
            Weapons = [Rifle, Stalker with { Damage = 0f, Variants = [new([Burst], Stalker.Variants[0].Spec with { Damage = 0f })] },
                Pistol with { Damage = 0f, Variants = [new([Mag], Pistol.Variants[0].Spec with { Damage = 0f })] },
                HeatGun with { Damage = 0f, Variants = [new([Cool], HeatGun.Variants[0].Spec with { Damage = 0f })] }],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Tac, RequestedPrimary = 1, RequestedPerk1 = (byte)P(Burst), RequestedPerk2 = (byte)P(Mag) });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Dev, RequestedPerk1 = (byte)P(Cool), RequestedPerk2 = (byte)P(Halo) });
        h.AddClient(nonce: 102, options: new ClientOptions { RequestedClass = Jumper, RequestedPerk1 = (byte)P(Recharge) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~InputButtons.Jump;
            uint phase = (t + (uint)i * 11) % 200;
            if (i == 0 && phase % 20 == 0 && phase < 120) buttons |= InputButtons.Fire;                // stalker pulls (bursts)
            if (i == 0 && phase is 130 or 131) buttons |= InputButtons.Switch;                          // to the pistol
            if (i == 0 && phase is >= 135 and < 190 && phase % 4 == 0) buttons |= InputButtons.Fire;   // pistol taps through a reload
            if (i == 0 && phase is 195 or 196) buttons |= InputButtons.Switch;                          // and back
            if (i == 1 && phase < 150) buttons |= InputButtons.Fire;                                    // the heat gun, held
            if (i == 2 && phase % 50 == 0) buttons |= InputButtons.Jump;                                // jumps (power recharge)
            return move with { Buttons = buttons, Pitch = 1.3f };
        }
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 3; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });

        for (int i = 0; i < 3; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int bursts = mine.Zip(mine.Skip(1)).Count(p => p.First.BurstLeft == 1 && p.Second.BurstLeft == 0 && p.Second.Clip < p.First.Clip);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches, {bursts} second rounds, perks {mine[^1].Perk1}/{mine[^1].Perk2}, shield {mine[^1].Shield}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.Empty(mismatches);
            if (i == 0) Assert.True(bursts >= 5, "too few bursts");
            if (i == 1) Assert.Equal(150f, mine[^1].Shield);
        }
        Assert.Equal((P(Burst), P(Mag)), (h.Clients[0].Predicted.Perk1, h.Clients[0].Predicted.Perk2));
    }
}
