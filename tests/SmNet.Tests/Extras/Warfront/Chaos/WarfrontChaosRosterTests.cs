using System.Diagnostics;
using System.Numerics;
using SmContent.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Warfront.Faction;
using SmSim.Extras.Warfront.Fortress;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Chaos;

/// <summary>
/// Chaos Warfront C2: the Chaos roster on the enemy sim (design "Army aliasing"). The table (bodies kept, tougher units), the kind map at the spawn chokepoint, each unit's behaviour on its Ork code,
/// no Ork kind in a Chaos lane, HQ or fortress battle, the caps at 0.6, the damage and stagger gate (C0 review findings 3 and 4), and a short tick-time bound against the Orks.
/// </summary>
public class WarfrontChaosRosterTests(ITestOutputHelper output)
{
    internal static readonly SimConfig ChaosConfig = WarfrontChaosTuning.Apply(WarfrontBattleTests.WarfrontConfig, WarfrontFaction.Chaos);

    static readonly MusouEnemyType[] Aliased =
    [
        MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.ChaosMarine, MusouEnemyType.Nob, MusouEnemyType.BigShootaNob, MusouEnemyType.RokkitBoy, MusouEnemyType.Stormboy, MusouEnemyType.Gretchin,
    ];

    // ---------------------------------------------------------------- rigs

    /// <summary>A Chaos (or Orks) war on the test arena without a battle: the human at (0, -60), the run idle, no Squig drops.</summary>
    static World Idle(SimConfig config)
    {
        var map = TfwTestArena.Build();
        var w = new World(config with { RespawnTime = 60f }, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.TfwSquigDropsOn = false;
        w.Tfw.AllyUp = _ => false;
        w.Spawn(TfwRig.Human, 0);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(TfwRig.Human, new Vector3(0f, -60f, 0f), 0f);
        Tick(w);
        return w;
    }

    static void Tick(World w) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.None));

    static WarfrontChaosRoster Roster(World w) => Assert.IsType<WarfrontChaosRoster>(w.Tfw!.Warfront!.ChaosRoster);

    internal static (World World, TfwBattle Battle) Lane(SimConfig config, bool guardsmen = false) => WarfrontChaosOrkPins.Lane(config, guardsmen);
    internal static (World World, TfwBattle Battle) Hq(SimConfig config) => WarfrontChaosOrkPins.Hq(config);
    internal static (World World, TfwBattle Battle) Fortress(SimConfig config) => WarfrontChaosOrkPins.Fortress(config);

    static List<MusouEnemy> Alive(World w)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        return all.Take(n).Where(e => e.Alive).ToList();
    }

    // ---------------------------------------------------------------- the table

    [Fact]
    public void The_chaos_table_keeps_the_ork_bodies_and_makes_each_aliased_unit_tougher()
    {
        var orks = MusouRules.Default;
        var chaos = ChaosConfig.MusouRules!;
        Assert.Equal(orks.Types.Count, chaos.Types.Count);
        for (int t = 0; t < orks.Types.Count; t++) Assert.Equal((MusouEnemyType)t, chaos.Types[t].Type);
        foreach (var kind in Aliased)
        {
            var (o, c) = (orks.Of(kind), chaos.Of(kind));
            Assert.True(c.Health > o.Health, $"{WarfrontChaosRoster.Unit(kind)}: {c.Health} over the Ork row's {o.Health}");
            if (kind == MusouEnemyType.Gretchin) continue;   // the Daemon: a man-sized body (the client takes it from the Chaos table, C3/C4)
            Assert.Equal(o.Radius, c.Radius);
            Assert.Equal(o.Height, c.Height);
        }
        Assert.True(chaos.Of(MusouEnemyType.Gretchin).Height > orks.Of(MusouEnemyType.Gretchin).Height);
        // the Chosen, the Lord and the Ork-only rows are kept
        foreach (var kind in new[] { MusouEnemyType.Warboss, MusouEnemyType.BossBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob, MusouEnemyType.Weirdboy, MusouEnemyType.BombSquig, MusouEnemyType.ScrapMek, MusouEnemyType.ArdBoy })
            Assert.Equal(orks.Of(kind), chaos.Of(kind));
        // every other Musou rule is the Orks'
        Assert.Equal(orks with { Types = chaos.Types }, chaos);
    }

    [Fact]
    public void Apply_leaves_an_orks_config_alone_and_sets_both_halves_for_chaos()
    {
        var orks = WarfrontBattleTests.WarfrontConfig;
        Assert.Same(orks, WarfrontChaosTuning.Apply(orks, WarfrontFaction.Orks));
        Assert.Equal(WarfrontFaction.Orks, orks.WarfrontFaction);
        Assert.Equal(WarfrontFaction.Chaos, ChaosConfig.WarfrontFaction);
        Assert.NotNull(ChaosConfig.MusouRules);
        Assert.Equal(orks.Hash(), ChaosConfig.Hash());   // the clients' config: the army is the server's alone (the Hub byte tells them)
        Assert.Null(TfwRig.War(config: orks).Tfw!.Warfront!.ChaosRoster);
        Assert.NotNull(TfwRig.War(config: ChaosConfig).Tfw!.Warfront!.ChaosRoster);
        Assert.Null(TfwRig.War(config: ChaosConfig with { Extras = ContentExtras.Musou }).Tfw!.Warfront);   // Musou Mode never has the Warfront rules, so never the roster
    }

    [Fact]
    public void The_battle_rules_report_the_army()
    {
        var (orks, ob) = Lane(WarfrontBattleTests.WarfrontConfig);
        var (chaos, cb) = Lane(ChaosConfig);
        Assert.Equal(WarfrontFaction.Orks, orks.Tfw!.Warfront!.Faction);
        Assert.Equal(WarfrontFaction.Chaos, chaos.Tfw!.Warfront!.Faction);
        Assert.False(ob.ChaosArmy);
        Assert.True(cb.ChaosArmy);
    }

    [Fact]
    public void The_kind_map_aliases_the_ard_boy_slot_keeps_the_daemon_for_breaches_and_refuses_the_ork_only_kinds()
    {
        Assert.Equal(MusouEnemyType.ChaosMarine, WarfrontChaosRoster.Alias(MusouEnemyType.ArdBoy, false));
        Assert.Equal(MusouEnemyType.Boy, WarfrontChaosRoster.Alias(MusouEnemyType.Gretchin, false));
        Assert.Equal(MusouEnemyType.Gretchin, WarfrontChaosRoster.Alias(MusouEnemyType.Gretchin, true));
        foreach (var kind in new[] { MusouEnemyType.Weirdboy, MusouEnemyType.ScrapMek, MusouEnemyType.BombSquig })
        {
            Assert.Equal(WarfrontChaosRoster.Refused, WarfrontChaosRoster.Alias(kind, false));
            Assert.False(WarfrontChaosRoster.OrkSpawnerOn(kind));
            Assert.False(WarfrontChaosRoster.Fields(kind));
        }
        for (int t = 0; t < MusouRules.Default.Types.Count; t++)
        {
            var to = WarfrontChaosRoster.Alias((MusouEnemyType)t, (MusouEnemyType)t == MusouEnemyType.Gretchin);
            Assert.True(to == WarfrontChaosRoster.Refused || WarfrontChaosRoster.Fields(to), $"{(MusouEnemyType)t} maps to {to}");
            if (to != WarfrontChaosRoster.Refused) Assert.NotEqual("-", WarfrontChaosRoster.Unit(to));
        }
        Assert.False(WarfrontChaosRoster.Fields(MusouEnemyType.ArdBoy));
    }

    [Fact]
    public void A_chaos_spawn_takes_the_mapped_kind_and_its_health_and_an_orks_spawn_is_unchanged()
    {
        var w = Idle(ChaosConfig);
        var musou = w.Musou!;
        var roster = Roster(w);
        var at = new Vector3(10f, -40f, 0f);
        float ard = musou.Rules.Of(MusouEnemyType.ArdBoy).Health;
        ushort marine = musou.Spawn(MusouEnemyType.ArdBoy, at, 0f, ard * 2f);
        Assert.True(musou.TryGet(marine, out var m));
        Assert.Equal(MusouEnemyType.ChaosMarine, m.Type);
        Assert.Equal(2f * WarfrontChaosTuning.MarineHealth, m.Health, 3);   // the caller's scale on the unit's own health
        Assert.True(musou.TryGet(musou.Spawn(MusouEnemyType.Gretchin, at, 0f, 10f), out var cultist));
        Assert.Equal(MusouEnemyType.Boy, cultist.Type);
        Assert.True(musou.TryGet(musou.SpawnDaemon(at, 0f, WarfrontChaosTuning.DaemonHealth), out var daemon));
        Assert.Equal(MusouEnemyType.Gretchin, daemon.Type);
        Assert.Equal(WarfrontChaosTuning.DaemonHealth, daemon.Health);
        Assert.True(musou.TryGet(musou.Spawn(MusouEnemyType.Gretchin, at, 0f, 10f), out var next));
        Assert.Equal(MusouEnemyType.Boy, next.Type);   // the breach's flag is for its one spawn
        foreach (var kind in new[] { MusouEnemyType.Weirdboy, MusouEnemyType.ScrapMek, MusouEnemyType.BombSquig })
            Assert.Equal(0, musou.Spawn(kind, at, 0f, 50f));
        Assert.Equal(3, roster.Refusals);
        Assert.Equal(3, roster.Substituted);
        Assert.Equal(1, roster.Spawned(MusouEnemyType.Gretchin));
        Assert.Equal(0, roster.Spawned(MusouEnemyType.ArdBoy));

        var o = Idle(WarfrontBattleTests.WarfrontConfig);
        Assert.Null(o.Tfw!.Warfront!.ChaosRoster);
        Assert.True(o.Musou!.TryGet(o.Musou.Spawn(MusouEnemyType.ArdBoy, at, 0f, 170f), out var boy));
        Assert.Equal(MusouEnemyType.ArdBoy, boy.Type);
        Assert.Equal(170f, boy.Health);
        Assert.NotEqual(0, o.Musou.Spawn(MusouEnemyType.Weirdboy, at, 0f, 50f));
        Assert.Equal(0, o.Musou.SpawnDaemon(at, 0f, 200f));   // no Daemon outside a Chaos battle
    }

    // ---------------------------------------------------------------- each unit's behaviour

    /// <summary>Spawns one <paramref name="kind"/> <paramref name="metres"/> north of the human and steps until it acts on him (a melee strike, a burst's tracer, a rocket); the ticks it took, or -1.</summary>
    static int ActsWithin(World w, MusouEnemyType kind, float metres, float seconds, out ushort id)
    {
        var human = w.Players[TfwRig.Human].Position;
        var musou = w.Musou!;
        id = musou.Spawn(kind, human + new Vector3(0f, metres, 0f), MathF.PI, musou.Rules.Of(kind).Health);
        Assert.NotEqual(0, id);
        for (int t = 0; t < (int)(seconds * 30f); t++)
        {
            Tick(w);
            ushort me = id;
            if (musou.Events.Any(e => e.Enemy == me && e.Kind is MusouEventKind.Strike or MusouEventKind.Tracer)) return t;
            if (musou.Rockets.Any(r => r.Shooter == me)) return t;
        }
        return -1;
    }

    [Theory]
    [InlineData(MusouEnemyType.Boy, 3f)]           // Cultist: melee
    [InlineData(MusouEnemyType.ShootaBoy, 25f)]    // Traitor Guardsman: the lasgun from 20-35 m
    [InlineData(MusouEnemyType.ChaosMarine, 20f)]  // Chaos Tactical Marine: the bolter
    [InlineData(MusouEnemyType.Nob, 3f)]           // Aspiring Champion: the heavy swing
    [InlineData(MusouEnemyType.BigShootaNob, 20f)] // Havoc, heavy bolter
    [InlineData(MusouEnemyType.RokkitBoy, 32f)]    // Havoc, missile: a real rocket
    [InlineData(MusouEnemyType.Stormboy, 3f)]      // Raptor: melee
    public void Each_chaos_unit_fights_on_its_ork_code_with_its_own_numbers(MusouEnemyType kind, float metres)
    {
        var w = Idle(ChaosConfig);
        int ticks = ActsWithin(w, kind, metres, 15f, out ushort id);
        Assert.True(ticks >= 0, $"{WarfrontChaosRoster.Unit(kind)} did not attack in 15 s");
        Assert.True(w.Musou!.TryGet(id, out var e));
        Assert.Equal(kind, e.Type);
        Assert.Equal(ChaosConfig.MusouRules!.Of(kind).Health, e.Health);   // the hub: no level scale
        output.WriteLine($"{WarfrontChaosRoster.Unit(kind)}: health {e.Health:0}, first attack after {ticks / 30f:0.0} s");
    }

    [Fact]
    public void The_breach_daemon_runs_and_strikes_in_melee()
    {
        var w = Idle(ChaosConfig);
        var musou = w.Musou!;
        var human = w.Players[TfwRig.Human].Position;
        ushort id = musou.SpawnDaemon(human + new Vector3(0f, 6f, 0f), MathF.PI, WarfrontChaosTuning.DaemonHealth);
        Assert.NotEqual(0, id);
        bool struck = false;
        for (int t = 0; t < 10 * 30 && !struck; t++) { Tick(w); struck = musou.Events.Any(e => e.Enemy == id && e.Kind == MusouEventKind.Strike); }
        Assert.True(struck, "the Daemon closes and strikes");
    }

    [Fact]
    public void The_raptor_hops_on_the_stormboy_code()
    {
        var w = Idle(ChaosConfig);
        var musou = w.Musou!;
        var human = w.Players[TfwRig.Human].Position;
        ushort id = musou.Spawn(MusouEnemyType.Stormboy, human + new Vector3(20f, 20f, 0f), 0f, WarfrontChaosTuning.RaptorHealth);
        TfwRig.Steps(w, 20);   // past the stand
        Assert.True(musou.Hop(id, human + new Vector3(4f, 4f, 0f)), "the jump pack's gap-closer");
        bool airborne = false, landed = false;
        for (int t = 0; t < 8 * 30 && !landed; t++) { Tick(w); if (musou.IsAirborne(id)) airborne = true; else if (airborne) landed = true; }
        Assert.True(airborne && landed);
        Assert.True(musou.TryGet(id, out var raptor));
        Assert.True(Vector2.Distance(new Vector2(raptor.Position.X, raptor.Position.Y), new Vector2(human.X, human.Y)) < 12f, "it landed by the human");
    }

    // ---------------------------------------------------------------- no Ork kind in a Chaos battle

    static void AssertNoOrkKind(World w, string battle)
    {
        var roster = Roster(w);
        foreach (var e in Alive(w)) Assert.True(WarfrontChaosRoster.Fields(e.Type) && e.Type != MusouEnemyType.Gretchin, $"{battle}: a {e.Type} in a Chaos battle");
        foreach (var kind in new[] { MusouEnemyType.ArdBoy, MusouEnemyType.Weirdboy, MusouEnemyType.ScrapMek, MusouEnemyType.BombSquig, MusouEnemyType.Gretchin })
            Assert.Equal(0, roster.Spawned(kind));
    }

    [Fact]
    public void A_chaos_lane_battle_of_a_minute_fields_no_ork_kind()
    {
        var (w, battle) = Lane(ChaosConfig, guardsmen: true);
        var roster = Roster(w);
        for (int t = 0; t < 60 * 30; t++)
        {
            w.Step(_ => default);
            if (t % 300 == 299) { AssertNoOrkKind(w, "lane"); TfwRig.KillAll(w); }
        }
        AssertNoOrkKind(w, "lane");
        Assert.True(roster.Spawned(MusouEnemyType.ChaosMarine) > 0, "the 'Ard Boy slot fields Chaos Marines");
        Assert.True(roster.Spawned(MusouEnemyType.Boy) > 0 && roster.Spawned(MusouEnemyType.ShootaBoy) > 0, "Cultists and Traitor Guardsmen");
        output.WriteLine($"lane: {string.Join(", ", Enum.GetValues<MusouEnemyType>().Where(k => roster.Spawned(k) > 0).Select(k => $"{WarfrontChaosRoster.Unit(k)} {roster.Spawned(k)}"))}; refused {roster.Refusals}");
    }

    [Fact]
    public void A_chaos_hq_defence_fields_no_ork_kind()
    {
        var (w, _) = Hq(ChaosConfig);
        for (int t = 0; t < 40 * 30; t++)
        {
            w.Step(_ => default);
            if (t % 300 == 299) { AssertNoOrkKind(w, "hq"); TfwRig.KillAll(w); }
        }
        AssertNoOrkKind(w, "hq");
        Assert.True(Roster(w).Spawned(MusouEnemyType.Boy) + Roster(w).Spawned(MusouEnemyType.ShootaBoy) > 0);
    }

    [Fact]
    public void A_chaos_fortress_battle_fields_no_ork_kind_and_its_boss_is_the_lord_row()
    {
        var (w, _) = Fortress(ChaosConfig);
        for (int t = 0; t < 30 * 30; t++)
        {
            w.Step(_ => default);
            if (t % 300 == 299) AssertNoOrkKind(w, "fortress");
        }
        AssertNoOrkKind(w, "fortress");
        Assert.Equal(1, Alive(w).Count(e => e.Type == MusouEnemyType.Warboss));   // the Chaos Lord (the Warboss row; C5's duel)
        Assert.True(Roster(w).Spawned(MusouEnemyType.ChaosMarine) > 0);
    }

    [Fact]
    public void The_chaos_caps_are_sixty_percent_of_the_orks_and_the_orks_caps_are_unchanged()
    {
        var (o, orks) = Lane(WarfrontBattleTests.WarfrontConfig);
        var (c, chaos) = Lane(ChaosConfig);
        Assert.Equal(1f, orks.EnemyScale);
        Assert.Equal(TfwBattleRules.AliveCap(1), orks.AliveCapNow);
        Assert.Equal(TfwArmyRules.NearOrksMost, orks.NearOrksMostNow);
        Assert.Equal(WarfrontChaosRoster.EnemyScale, chaos.EnemyScale);
        Assert.Equal((int)MathF.Round(TfwBattleRules.AliveCap(1) * 0.6f), chaos.AliveCapNow);
        Assert.Equal((int)MathF.Round(TfwArmyRules.NearOrksMost * 0.6f), chaos.NearOrksMostNow);
        TfwRig.Seconds(o, 40f);
        TfwRig.Seconds(c, 40f);
        int no = Alive(o).Count, nc = Alive(c).Count;
        output.WriteLine($"alive after 40 s: Orks {no}, Chaos {nc}");
        Assert.True(nc < no, $"Chaos {nc} alive, Orks {no}");
    }

    // ---------------------------------------------------------------- the armies' leaders (C2 review R1)

    [Fact]
    public void A_chaos_army_promotes_folds_back_and_promotes_again_with_its_elites_kept_and_its_chaos_marines_leading()
    {
        var (w, b) = Lane(ChaosConfig);
        w.Musou!.Frozen = true;
        b.Trickle = false; b.Patrols = false; b.StormboysOn = false; b.BigShootasOn = false; b.WeirdboyzOn = false; b.RokkitsOn = false; b.BossOrksOn = false;
        b.OrkAggregates = true;   // no twins: each slot one body, so the elites' count is exact
        var anchor = new Vector3(80f, -60f, 0f);
        var army = b.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Hold, 0, 60, anchor, 0.3f, eliteShare: 0.5f)!;
        Vector3[] near = [anchor + new Vector3(0f, 40f, 0f)], far = [new(-400f, 400f, 0f)];
        float elites = army.FarElites;
        Assert.True(elites > 5f);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            for (int t = 0; t < 20; t++) b.Step(near);
            Assert.Equal(0, army.FarCount);
            int marines = army.Orks.Count(id => w.Musou.TryGet(id, out var e) && e.Type == MusouEnemyType.ChaosMarine);
            Assert.True(marines > 0, "the 'Ard slots came as Chaos Marines");
            Assert.Equal(elites, army.RealElites + army.FarElites, 3);   // each promoted leader moved from the far count to the real one
            Assert.True(army.Led, "Chaos Marines lead the army");
            for (int t = 0; t < 20; t++) b.Step(far);
            Assert.Equal(0, army.Real);
            Assert.Equal(elites, army.FarElites, 3);   // the fold-back gave them all back: no leak
        }
    }

    // ---------------------------------------------------------------- the 'Ard slot's rule rows and the autocannon (C2 review R3, R4)

    [Fact]
    public void The_chaos_marine_follows_the_ard_boys_rule_rows_only_in_a_chaos_battle()
    {
        var chaos = Idle(ChaosConfig).Musou!;
        var orks = Idle(WarfrontBattleTests.WarfrontConfig).Musou!;
        Assert.Equal(MusouEnemyType.ArdBoy, chaos.RuleKind(MusouEnemyType.ChaosMarine));
        Assert.Equal(MusouEnemyType.ChaosMarine, orks.RuleKind(MusouEnemyType.ChaosMarine));
        foreach (var kind in Enum.GetValues<MusouEnemyType>().Where(k => k != MusouEnemyType.ChaosMarine))
            Assert.Equal(kind, chaos.RuleKind(kind));
        Assert.Equal(MusouTargetClass.Elite, MusouTargetRules.ClassOf(chaos.RuleKind(MusouEnemyType.ChaosMarine)));
        Assert.False(MusouPowerupRules.Elite(chaos.RuleKind(MusouEnemyType.ChaosMarine)));   // the 'Ard Boy's drop rate, not an elite's
        Assert.Equal(LibrarianRules.Tier.Elite, LibrarianRules.TierOf(chaos.RuleKind(MusouEnemyType.ChaosMarine)));
    }

    [Theory]
    [InlineData(MusouEnemyType.Boy)]
    [InlineData(MusouEnemyType.ShootaBoy)]
    public void A_weapons_own_blast_still_kills_chaos_fodder_in_one(MusouEnemyType kind)
    {
        var w = Idle(ChaosConfig);
        var musou = w.Musou!;
        float blast = SmSim.AutocannonRules.BlastDamage * 1.2f;
        ushort id = musou.Spawn(kind, new Vector3(0f, -40f, 0f), 0f, musou.Rules.Of(kind).Health);
        Assert.True(musou.Rules.Of(kind).Health > blast, "the Chaos fodder outlasts the blast's damage by health");
        musou.FodderBlast = true;
        Assert.True(musou.Damage(id, TfwRig.Human, blast, Vector3.Zero, out bool killed, cause: MusouDeathKind.BoltBlast));
        musou.FodderBlast = false;
        Assert.True(killed, $"{WarfrontChaosRoster.Unit(kind)} dies to one blast");
        ushort marine = musou.Spawn(MusouEnemyType.ArdBoy, new Vector3(0f, -40f, 0f), 0f, 1000f);
        musou.FodderBlast = true;
        musou.Damage(marine, TfwRig.Human, blast, Vector3.Zero, out bool marineKilled, cause: MusouDeathKind.BoltBlast);
        musou.FodderBlast = false;
        Assert.False(marineKilled, "not the elites");
        ushort plain = musou.Spawn(kind, new Vector3(0f, -40f, 0f), 0f, musou.Rules.Of(kind).Health);
        musou.Damage(plain, TfwRig.Human, blast, Vector3.Zero, out bool plainKilled, cause: MusouDeathKind.BoltBlast);
        Assert.False(plainKilled, "only a weapon's own blast");
    }

    // ---------------------------------------------------------------- the gate (C0 review finding 4)

    [Fact]
    public void A_shielded_enemy_refuses_hits_staggers_and_throws_and_the_slot_forgets_it()
    {
        var w = Idle(ChaosConfig);
        var musou = w.Musou!;
        var roster = Roster(w);
        var at = w.Players[TfwRig.Human].Position + new Vector3(0f, 15f, 0f);
        ushort cultist = musou.Spawn(MusouEnemyType.Boy, at, 0f, 100f);
        TfwRig.Steps(w, 20);
        roster.Shield(cultist, true);
        Assert.False(musou.Damage(cultist, TfwRig.Human, 50f, Vector3.Zero, out _));
        Assert.False(musou.Stagger(cultist, Vector2.UnitY, 2f));
        Assert.False(musou.StaggerMelee(cultist, Vector2.UnitY, 2f, TfwRig.Human));
        Assert.False(musou.KnockDown(cultist, Vector2.UnitY, 2f, TfwRig.Human, 1f));
        Assert.True(musou.TryGet(cultist, out var e) && e.Health == 100f && e.State != MusouEnemyState.Stagger && e.State != MusouEnemyState.Knocked);
        roster.Shield(cultist, false);
        Assert.True(musou.Damage(cultist, TfwRig.Human, 50f, Vector3.Zero, out _));
        Assert.True(musou.Stagger(cultist, Vector2.UnitY, 2f));

        roster.Shield(cultist, true);
        Assert.True(musou.Remove(cultist));
        ushort again = 0;
        for (int k = 0; k < MusouSim.Capacity && again != cultist; k++) again = musou.Spawn(MusouEnemyType.Boy, at, 0f, 100f);   // the free list reuses a slot as late as it can
        Assert.Equal(cultist, again);
        Assert.True(musou.Damage(again, TfwRig.Human, 10f, Vector3.Zero, out _), "a new enemy in the slot is not shielded");
    }

    [Fact]
    public void An_enemy_accepting_one_attacker_takes_his_hits_and_stuns_alone()
    {
        var w = Idle(ChaosConfig);
        var musou = w.Musou!;
        var roster = Roster(w);
        var at = w.Players[TfwRig.Human].Position + new Vector3(0f, 15f, 0f);
        ushort lord = musou.Spawn(MusouEnemyType.Warboss, at, 0f, 5000f);
        TfwRig.Steps(w, 20);
        roster.AcceptOnly(lord, 7);
        Assert.False(musou.Damage(lord, TfwRig.Human, 100f, Vector3.Zero, out _));
        Assert.False(musou.Damage(lord, 0, 100f, Vector3.Zero, out _, cause: MusouDeathKind.World));
        Assert.False(musou.StaggerMelee(lord, Vector2.UnitY, 1f, TfwRig.Human));
        Assert.True(musou.Damage(lord, 7, 100f, Vector3.Zero, out _));
        Assert.True(musou.TryGet(lord, out var e) && e.Health == 4900f);
        Assert.True(musou.StaggerMelee(lord, Vector2.UnitY, 1f, 7));
        roster.AcceptOnly(lord, 0);
        Assert.True(musou.Damage(lord, TfwRig.Human, 100f, Vector3.Zero, out _));
    }

    [Fact]
    public void An_infinite_hit_lands_nothing_after_the_scales_and_the_gate()
    {
        foreach (var config in new[] { ChaosConfig, WarfrontBattleTests.WarfrontConfig })
        {
            var w = Idle(config);
            var musou = w.Musou!;
            ushort id = musou.Spawn(MusouEnemyType.Boy, new Vector3(0f, -45f, 0f), 0f, 100f);
            Assert.False(musou.Damage(id, TfwRig.Human, float.PositiveInfinity, Vector3.Zero, out _));
            Assert.True(musou.TryGet(id, out var e) && e.Health == 100f && e.Alive);
        }
    }

    // ---------------------------------------------------------------- the tick time against the Orks

    /// <summary>The 99th percentile of the World's tick time (ms) over <paramref name="seconds"/> of a lane battle, after a 10 s fill.</summary>
    static double TickP99(SimConfig config, float seconds)
    {
        var (w, _) = Lane(config, guardsmen: true);
        TfwRig.Seconds(w, 10f);
        var ms = new List<double>();
        for (int t = 0; t < (int)(seconds * 30f); t++)
        {
            long a = Stopwatch.GetTimestamp();
            w.Step(_ => default);
            ms.Add(Stopwatch.GetElapsedTime(a).TotalMilliseconds);
        }
        ms.Sort();
        return ms[(int)(ms.Count * 0.99)];
    }

    [Fact(Skip = "Wall-clock bench (C2 review R5): out of the default suite; the live-test queue runs it on a quiet PC (remove the Skip, run with the console logger)")]
    [Trait("Speed", "Slow")]
    public void A_chaos_lane_tick_p99_is_bounded_by_the_orks()
    {
        // short and bounded: 2 x (10 s fill + 10 s measured) a side, the sides interleaved; the best of two a side (a loaded machine's spikes are the machine's)
        double orks = double.MaxValue, chaos = double.MaxValue;
        for (int k = 0; k < 2; k++)
        {
            orks = Math.Min(orks, TickP99(WarfrontBattleTests.WarfrontConfig, 10f));
            chaos = Math.Min(chaos, TickP99(ChaosConfig, 10f));
        }
        output.WriteLine($"tick p99: Orks {orks:0.00} ms, Chaos {chaos:0.00} ms");
        Assert.True(chaos <= Math.Max(orks * 1.5, orks + 5.0), $"Chaos p99 {chaos:0.00} ms against the Orks' {orks:0.00} ms");
    }
}
