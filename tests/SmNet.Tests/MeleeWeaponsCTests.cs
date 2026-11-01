using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Weapons C: the power axe's armour-shield damage, the thunder hammer's swing, the Devastator/Havoc kick and stomp (the kick's chain into
/// the stomp, its blast and reactions, reaction immunity while it lasts), a light attack that ends the string, the Y,Y finisher chain, and
/// prediction exactness at 150 ms RTT with these combos. Synthetic timings, placed half a tick off the 30 Hz grid.
/// </summary>
public class MeleeWeaponsCTests(ITestOutputHelper output)
{
    static float T(float ticks) => ticks / 30f;

    static readonly ReactionEntry Interrupt = new(0f, ReactionKind.Interrupt);
    static readonly ReactionEntry Stagger = new(0f, ReactionKind.Stagger, Backward: 1f, Duration: 0.2f);

    /// <summary>A light attack: hit at tick 7, combo window ticks 13..19, fire from 16, moves from 22, over at 31.</summary>
    static MeleeStep Light(float damage, float shield = 0f) => new MeleeStep(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), damage, damage / 2f, 3.5f)
        { PrimaryReaction = Interrupt, SecondaryReaction = Interrupt, ShieldDamage = shield, SecondaryShieldDamage = shield / 2f };

    /// <summary>A heavy strike: hit at tick 10, Y-chain window ticks 15..25 when it chains, over at 41.</summary>
    static MeleeStep Heavy(float damage, int yNext = 0, SplashSpec? blast = null) =>
        new MeleeStep(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), damage, 0f, 3.5f)
        {
            PrimaryReaction = Stagger, YNext = yNext, YComboOpen = yNext > 0 ? T(14.5f) : 0f, YComboClose = yNext > 0 ? T(24.5f) : 0f, Stomp = blast,
        };

    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);

    /// <summary>The heavy evade's stomp shape: radius 7, 25 + 50 shield, full to 4.5 m; stagger from 0.8, stun from 0.61, else interrupt.</summary>
    static readonly SplashSpec StompBlast = new(7f, Plain with { Damage = 25f, ShieldDamage = 50f }, [new(0f, 1f), new(4.5f, 1f), new(6f, 0.5f), new(7f, 0.15f)], true, true, "target_all")
    {
        Reactions = [new(0.8f, ReactionKind.Stagger), new(0.61f, ReactionKind.Stun), new(0f, ReactionKind.Interrupt)],
    };

    /// <summary>The kick (one light attack that ends the string and chains to heavy strike 1) and the stomp (a blast strike immune to reactions).</summary>
    static readonly MeleeStep Kick = Light(30f) with { StringEnd = true, HeavyNext = 1, PrimaryReaction = Stagger };
    static readonly MeleeStep Stomp = Heavy(0f, blast: StompBlast) with { NoReactFrom = 0f, NoReactTo = T(40.5f) };

    const int Axe = 0, Hammer = 1, Devastator = 2, Finisher = 3;

    static readonly SimConfig Config = new()
    {
        Classes =
        [
            new ClassLoadout("axe", -1, -1, [Light(70f, 250f), Light(70f, 250f)]) { Side = Team.SpaceMarines },
            new ClassLoadout("hammer", -1, -1, [Light(200f), Light(200f) with { StringEnd = true, HeavyNext = -1 }]) { MeleeHeavy = [Heavy(25f), Heavy(35f)], Side = Team.Chaos },
            new ClassLoadout("devastator", -1, -1, [Kick]) { MeleeHeavy = [Stomp], Side = Team.SpaceMarines },
            new ClassLoadout("finisher", -1, -1, [Light(10f)]) { MeleeHeavy = [Heavy(10f, yNext: 3), Heavy(10f, yNext: 3), Heavy(0f, blast: StompBlast)], Side = Team.Chaos },
        ],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        HealthMax = 200f, ShieldMax = 300f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static World Arena(SimConfig c, params (int Class, Vector3 At)[] players)
    {
        var world = new World(c, null, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray());
        for (int i = 0; i < players.Length; i++) world.Spawn(i + 1, players[i].Class);
        return world;
    }

    /// <summary>Steps the world with a script: (tick from 0, player id) → buttons; player 1 faces +Y, others face −Y.</summary>
    static void Run(World w, int ticks, Func<int, int, InputButtons> script, Action<int>? each = null)
    {
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            w.Step(id => new PlayerInput(0, 0f, 0f, id == 1 ? 0f : MathF.PI, script(tick, id)));
            each?.Invoke(k);
        }
    }

    [Fact]
    public void An_axe_hit_takes_its_armour_damage_off_the_shield_before_its_damage()
    {
        var w = Arena(Config, (Axe, Vector3.Zero), (Finisher, new Vector3(0, 2, 0)));
        Assert.Equal((300f, 200f), (w.Players[2].Shield, w.Players[2].Health));
        var hits = new List<HitEvent>();
        Run(w, 9, (k, id) => id == 1 && k == 0 ? InputButtons.Melee : InputButtons.None, _ => hits.AddRange(w.LastHits));
        var hit = Assert.Single(hits);
        // 250 off the shield alone, then 70 at the shield multiplier 1: the last 50 of the shield, 20 on health.
        Assert.Equal((300f, 20f, true), (hit.ShieldDamage, hit.HealthDamage, hit.Melee));
        Assert.Equal((0f, 180f), (w.Players[2].Shield, w.Players[2].Health));
    }

    /// <summary>One axe-style hit (light attack 1 of class 0 with the given step) on player 2; returns the hit and player 2 after it.</summary>
    static (HitEvent Hit, PlayerState Victim) OneHit(SimConfig c, MeleeStep step, Func<World>? arena = null)
    {
        var config = c with { Classes = [c.Classes[Axe] with { Melee = [step] }, .. c.Classes.Skip(1)] };
        var w = arena?.Invoke() ?? Arena(config, (Axe, Vector3.Zero), (Finisher, new Vector3(0, 2, 0)));
        var hits = new List<HitEvent>();
        Run(w, 9, (k, id) => id == 1 && k == 0 ? InputButtons.Melee : InputButtons.None, _ => hits.AddRange(w.LastHits));
        return (Assert.Single(hits), w.Players[2]);
    }

    [Fact]
    public void Killing_blow_drains_the_whole_shield_and_kills_and_the_part_and_damage_together_kill_exactly()
    {
        // Killing Blow: 2000 + 2000 against 300 shield and 200 health: the part takes the shield (300 < 2000), the damage the health (200 < 2000).
        var (hit, victim) = OneHit(Config, Light(2000f, 2000f));
        Assert.Equal((300f, 2000f, true), (hit.ShieldDamage, hit.HealthDamage, hit.Killed));   // health damage as Combat.ApplyDamage reports it (the overkill included)
        Assert.False(victim.Alive);
        // 250 + 250: 250 off the shield, then 50 more off it and exactly the 200 health.
        (hit, victim) = OneHit(Config, Light(250f, 250f));
        Assert.Equal((300f, 200f, true), (hit.ShieldDamage, hit.HealthDamage, hit.Killed));
        // One less and it survives with no health lost below zero.
        (hit, victim) = OneHit(Config, Light(249f, 250f));
        Assert.Equal((300f, 199f, false), (hit.ShieldDamage, hit.HealthDamage, hit.Killed));
        Assert.Equal((0f, 1f), (victim.Shield, victim.Health));
    }

    [Fact]
    public void Against_no_shield_the_armour_part_does_nothing_and_the_damage_goes_to_health()
    {
        var (hit, victim) = OneHit(Config with { ShieldMax = 0f }, Light(70f, 250f));
        Assert.Equal((0f, 70f), (hit.ShieldDamage, hit.HealthDamage));
        Assert.Equal((0f, 130f), (victim.Shield, victim.Health));
    }

    [Fact]
    public void Feel_no_pain_scales_the_armour_part_too()
    {
        var fnp = new PerkSpec("mp_perk_melee_damage_resistance", "u_fnp", PerkKind.Class, 1);
        var c = Config with
        {
            Perks = [fnp], PlayerUpgrades = [new PlayerUpgrade("u_fnp") { ReceivedMelee = 0.5f }],
            Classes = [.. Config.Classes.Select(k => k with { PerkChoices = [0] })],
        };
        var (hit, victim) = OneHit(c, Light(70f, 250f), () =>
        {
            var config = c with { Classes = [c.Classes[Axe] with { Melee = [Light(70f, 250f)] }, .. c.Classes.Skip(1)] };
            var w = new World(config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 2, 0), 0f)]);
            w.Spawn(1, Axe);
            w.Spawn(2, Finisher, perk1Choice: 0);
            return w;
        });
        // 125 off the shield alone, then 35 at multiplier 1.
        Assert.Equal((160f, 0f), (hit.ShieldDamage, hit.HealthDamage));
        Assert.Equal((140f, 200f), (victim.Shield, victim.Health));
    }

    [Fact]
    public void With_friendly_fire_off_the_stomp_spares_an_ally_and_hits_an_enemy()
    {
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 2, 0), 0f), new SpawnPoint(new Vector3(2, 0, 0), 0f)])
            { Rules = new MatchRules { TimeLimit = 0f } };
        w.Spawn(1, Devastator);   // Space Marines
        w.Spawn(2, Axe);          // Space Marines: an ally
        w.Spawn(3, Finisher);     // Chaos
        var hits = new List<HitEvent>();
        Run(w, 20, (k, id) => id == 1 && k == 0 ? InputButtons.MeleeHeavy : InputButtons.None, _ => hits.AddRange(w.LastHits));
        Assert.Equal([3], hits.Select(h => h.Target));
        Assert.Equal((300f, 200f), (w.Players[2].Shield, w.Players[2].Health));
    }

    [Fact]
    public void Kick_stomp_kick_follows_the_data_cadence()
    {
        var w = Arena(Config, (Devastator, Vector3.Zero), (Finisher, new Vector3(0, 30, 0)));
        var steps = new List<(int Step, bool Heavy)>();
        // Kick at 0, stun at 10 (buffered), a melee press during the stomp (no window: ignored), a melee press after it.
        Run(w, 70, (k, id) => id != 1 ? InputButtons.None : k == 0 || k == 30 || k == 56 ? InputButtons.Melee : k == 10 ? InputButtons.MeleeHeavy : InputButtons.None,
            _ => steps.Add((w.Players[1].MeleeStep, w.Players[1].MeleeHeavy)));
        Assert.Equal(13, steps.IndexOf((1, true)));                                 // the stomp starts as the kick's combo window opens
        Assert.Equal(13 + 41, steps.FindIndex(13, s => s.Step == 0));               // and lasts its 40.5 ticks
        Assert.Equal(56, steps.FindIndex(54, s => s == (1, false)));                // the next kick starts from rest at the press
        Assert.Equal(13, steps.Take(56).Count(s => s == (1, false)));               // the kick before the stomp only (the press at 30 did nothing)
    }

    [Fact]
    public void A_hammer_swing_deals_200_and_its_last_attack_ends_the_string()
    {
        var w = Arena(Config with { ShieldMax = 0f, HealthMax = 1000f }, (Hammer, Vector3.Zero), (Axe, new Vector3(0, 2, 0)));
        var steps = new List<(int Step, bool Heavy)>();
        // X, X in the window (attack 2 starts at tick 14, its window is ticks 27..32), then X and Y presses in attack 2's window: neither
        // goes on (the graph has no transition out of it).
        Run(w, 60, (k, id) => id != 1 ? InputButtons.None : k is 0 or 14 or 28 ? InputButtons.Melee : k == 30 ? InputButtons.MeleeHeavy : InputButtons.None,
            _ => steps.Add((w.Players[1].MeleeStep, w.Players[1].MeleeHeavy)));
        Assert.Equal(1000f - 400f, w.Players[2].Health);   // two swings of 200
        Assert.Equal(14, steps.IndexOf((2, false)));
        Assert.DoesNotContain(steps, s => s.Heavy);
        Assert.DoesNotContain(1, steps.Skip(14).Select(s => s.Step));   // never back to attack 1
        Assert.Equal((0, false), steps[^1]);
    }

    [Fact]
    public void The_kick_chains_into_the_stomp_whose_blast_staggers_and_damages_around_the_devastator()
    {
        var w = Arena(Config, (Devastator, Vector3.Zero), (Finisher, new Vector3(0, 2, 0)), (Hammer, new Vector3(3, 0, 0)));   // both enemies; 3 is out of the kick's cone
        var hits = new List<HitEvent>();
        var reactions = new List<ReactionEvent>();
        var steps = new List<(int Step, bool Heavy)>();
        Run(w, 60, (k, id) => id != 1 ? InputButtons.None : k == 0 ? InputButtons.Melee : k == 10 ? InputButtons.MeleeHeavy : InputButtons.None, _ =>
        {
            hits.AddRange(w.LastHits); reactions.AddRange(w.LastReactions);
            steps.Add((w.Players[1].MeleeStep, w.Players[1].MeleeHeavy));
        });
        Assert.Equal(13, steps.IndexOf((1, true)));   // the stomp starts when the kick's combo window opens
        // The kick (30) on player 2, then the stomp's blast on both (25 + 50 shield at full intensity within 4.5 m).
        Assert.Equal(30f, hits[0].ShieldDamage);
        Assert.Equal([2, 2, 3], hits.Select(h => h.Target).Order());
        var blast = hits.Skip(1).ToList();
        Assert.All(blast, h => Assert.Equal(75f, h.ShieldDamage + h.HealthDamage));
        Assert.Contains(reactions, r => r.Target == 3 && r.Kind == ReactionKind.Stagger);
    }

    [Fact]
    public void The_stomp_keeps_the_devastator_out_of_reactions_and_a_light_press_does_not_repeat_the_kick()
    {
        // A light press in the kick's combo window does nothing (the kick ends the string).
        var w = Arena(Config, (Devastator, Vector3.Zero), (Finisher, new Vector3(0, 30, 0)));
        var steps = new List<int>();
        Run(w, 40, (k, id) => id == 1 && k is 0 or 14 ? InputButtons.Melee : InputButtons.None, _ => steps.Add(w.Players[1].MeleeStep));
        Assert.Equal(0, steps[^1]);
        Assert.Equal(31, steps.Count(s => s == 1));

        // A finisher player's light attack lands on the stomping devastator: no reaction (the slam's mp_reaction_immunity).
        var w2 = Arena(Config, (Finisher, Vector3.Zero), (Devastator, new Vector3(0, 2, 0)));
        var reactions = new List<ReactionEvent>();
        Run(w2, 12, (k, id) => k == 0 ? id == 1 ? InputButtons.Melee : InputButtons.MeleeHeavy : InputButtons.None, _ => reactions.AddRange(w2.LastReactions));
        Assert.True(w2.Players[2].MeleeHeavy);
        Assert.DoesNotContain(reactions, r => r.Target == 2);
    }

    [Fact]
    public void A_heavy_press_during_a_heavy_strike_with_a_Y_chain_plays_the_finisher_when_its_window_opens()
    {
        var w = Arena(Config, (Finisher, Vector3.Zero), (Axe, new Vector3(0, 30, 0)));
        var steps = new List<(int Step, bool Heavy)>();
        Run(w, 60, (k, id) => id == 1 && k is 0 or 5 ? InputButtons.MeleeHeavy : InputButtons.None, _ => steps.Add((w.Players[1].MeleeStep, w.Players[1].MeleeHeavy)));
        // The press at tick 5 is buffered (as a combo press) and the finisher starts when the Y window opens, 15 ticks into the strike.
        Assert.Equal(15, steps.IndexOf((3, true)));
        Assert.DoesNotContain((2, true), steps);

        // Without a press in the window the strike just ends.
        var w2 = Arena(Config, (Finisher, Vector3.Zero), (Axe, new Vector3(0, 30, 0)));
        var steps2 = new List<int>();
        Run(w2, 45, (k, id) => id == 1 && k == 0 ? InputButtons.MeleeHeavy : InputButtons.None, _ => steps2.Add(w2.Players[1].MeleeStep));
        Assert.DoesNotContain(3, steps2);
        Assert.Equal(0, steps2[^1]);
    }

    [Fact]
    public void The_five_melee_perks_are_active_once_a_class_carries_a_combo_that_reads_their_strings()
    {
        (string File, string Upgrade)[] perks =
        [
            ("mp_perk_weap_power_axe_slow_on_hit", "mp_perk_weap_power_axe_heal_on_hit"), ("mp_perk_weap_power_sword_heal_on_hit", "mp_perk_weap_power_sword_heal_on_hit"),
            ("mp_perk_weap_thunder_hammer_aoe_strike", "mp_perk_weap_thunder_hammer_aoe_strike"), ("mp_perk_better_stomp", "mp_perk_better_stomp"),
            ("mp_perk_speed_increase", "mp_perk_speed_increase"),
        ];
        static MeleeCombo Combo(string name, params string[] upgrades) => new(name, [Light(10f)])
        {
            Variants = upgrades.Select(u => new UpgradeVariant<MeleeCombo>([u], new MeleeCombo(name, [Light(20f)]))).ToList(),
        };
        var specs = perks.Select(p => new PerkSpec(p.File, p.Upgrade, PerkKind.Class, 1)).ToArray();
        var assault = new ClassLoadout("assault", -1, -1, [Light(10f)])
        {
            MeleeChoices = [Combo("sm_mp_melee_power_axe", perks[0].Upgrade), Combo("sm_mp_melee_powersword", perks[1].Upgrade), Combo("sm_mp_melee_2hand_thunder_hammer", perks[2].Upgrade)],
            PerkChoices = [0, 1, 2], DefaultPerk = 0,
        };
        var heavy = new ClassLoadout("devastator", -1, -1, [Kick])
        {
            MeleeHeavy = [Stomp], MeleeChoices = [Combo("sm_mp_core_abil_heavy_evade", perks[3].Upgrade, perks[4].Upgrade)], PerkChoices = [3, 4], DefaultPerk = 3,
        };
        var c = new SimConfig { Classes = [assault, heavy], Perks = specs };
        foreach (var p in specs)
        {
            var status = Perks.StatusOf(c, p);
            Assert.True(status.Activity == PerkActivity.Active, $"{p.Name}: {status.Activity} ({status.Note})");
        }
        // Without the combos (as before Weapons C) they are inert.
        var none = c with { Classes = [assault with { MeleeChoices = [] }, heavy with { MeleeChoices = [] }] };
        Assert.All(specs, p => Assert.Equal(PerkActivity.Inert, Perks.StatusOf(none, p).Activity));
    }

    [Fact]
    public void Steps_without_the_new_fields_hash_as_before_and_with_them_differently()
    {
        // A knife-shaped config (two light attacks with M28/M43 fields, a stomp strike) hashes to the value computed on main f0cea6d before Weapons C.
        var light = new MeleeStep(1.433f, 0.28f, 0.5f, 0.769f, 0.433f, 0.779f, 40f, 20f, 3.5f, 1f, 0.75f)
            { PrimaryReaction = new ReactionEntry(0f, ReactionKind.Interrupt, Backward: 1.5f, Duration: 0.1f), NoReactFrom = 0.255f, NoReactTo = 0.35f, Heal = 5f, DamageType = DamageType.Melee };
        var blast = new SplashSpec(7f, new DamageSpec(25f, 50f, 1f, 1f, 1f, 1f, false), [new(0f, 1f), new(4.5f, 1f), new(7f, 0.15f)], true, true, "target_all")
            { Reactions = [new(0.8f, ReactionKind.Stagger), new(0f, ReactionKind.Interrupt)] };
        var stomp = new MeleeStep(2.033f, 0.9f, 0f, 0f, 1.356f, 1.661f, 0f, 0f, 3.5f) { Stomp = blast, NoReactFrom = 0.644f, NoReactTo = 1.423f };
        Assert.Equal(0xA051E9CC9852E014UL, new SimConfig { MeleeSteps = [light, light with { HitTime = 0.384f }, stomp] }.Hash());

        var plain = new SimConfig { MeleeSteps = [Light(10f)] };
        var same = new SimConfig { MeleeSteps = [Light(10f) with { ShieldDamage = 0f, StringEnd = false, HeavyNext = 0, YNext = 0 }] };
        Assert.Equal(plain.Hash(), same.Hash());
        Assert.NotEqual(plain.Hash(), new SimConfig { MeleeSteps = [Light(10f) with { ShieldDamage = 1f }] }.Hash());
        Assert.NotEqual(plain.Hash(), new SimConfig { MeleeSteps = [Light(10f) with { YNext = 1, YComboOpen = 0.1f, YComboClose = 0.2f }] }.Hash());
    }

    [Theory]
    [InlineData(331)]
    [InlineData(332)]
    public void Prediction_matches_the_server_through_kicks_stomps_finishers_and_armour_damage_under_150ms_rtt(int seed)
    {
        var config = Config with
        {
            Classes = Config.Classes.Select(k => k with { Side = Team.None }).ToArray(),
            MeleeRange = 40f, MeleeConeDegrees = 180f, MeleeVerticalReach = 60f,
            HealthMax = 1e6f, ShieldMax = 1e6f,
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        int[] classes = [Devastator, Finisher, Axe, Hammer];
        for (int i = 0; i < classes.Length; i++) h.AddClient(nonce: (uint)(300 + i), options: new ClientOptions { RequestedClass = (byte)classes[i] });
        int n = h.Clients.Count;
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        var reactedAt = new HashSet<(uint Tick, int Target)>();
        int heavySteps = 0;
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Melee | InputButtons.Fire);
            uint phase = (t + (uint)i * 41) % 120;
            if (phase is 0 or 1) buttons |= InputButtons.Melee;                                     // a light attack (the kick)
            if (phase is 12 or 13) buttons |= InputButtons.MeleeHeavy;                              // its heavy chain (the stomp)
            if (phase is 30 or 31) buttons |= InputButtons.MeleeHeavy;                              // that strike's Y chain (the finisher)
            if (phase is 75 or 76) buttons |= InputButtons.MeleeHeavy;                              // a heavy strike from rest …
            if (phase is 90 or 91) buttons |= InputButtons.MeleeHeavy;                              // … and its Y chain
            return move with { Buttons = buttons, Pitch = -0.2f };
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            foreach (var r in h.Server.World.LastReactions) reactedAt.Add((r.Tick, r.Target));
            foreach (var f in h.Server.World.LastFlinches) reactedAt.Add((f.Tick, f.Target));   // sim/light-melee-flinch: a flinch is as unpredictable as a reaction
            heavySteps += h.Server.World.Players.Values.Count(p => p.MeleeHeavy && p.MeleeStep >= 3);
            for (int i = 0; i < n; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        const uint Window = 16;   // a client cannot predict being hit: ticks right after a reaction on it are not compared
        for (int i = 0; i < n; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            var starts = reactedAt.Where(r => r.Target == id).Select(r => r.Tick).ToList();
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                if (starts.Any(s => s <= tick && tick < s + Window)) continue;
                compared++;
                // Health and shield come from others' hits (not predicted); the own state machine must match.
                var p = pred with { Health = real.Health, Shield = real.Shield, SinceDamage = real.SinceDamage };
                if (p != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            output.WriteLine($"client {i} (class {classes[i]}): compared {compared} ticks, {mismatches.Count} mismatches; reactions on it {starts.Count}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.Empty(mismatches);
        }
        output.WriteLine($"finisher ticks {heavySteps}; reactions {reactedAt.Count}");
        Assert.True(heavySteps > 0, "no Y,Y finisher was played");
        Assert.True(reactedAt.Count >= 10, $"only {reactedAt.Count} reactions");
    }
}
