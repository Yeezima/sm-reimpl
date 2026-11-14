using System.Numerics;
using SmNet;
using SmNet.Extras.Musou;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Expansion stage S3 (docs/musou-expansion.md "Stormboys", "Big Shoota Nob"): the Stormboy's hop, landing shove and crash, the squads' orders and pack, the Big Shoota Nob's burst and overheat, the wire.
/// </summary>
public class MusouExpansionS3Tests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly MusouBox Box = new(new Vector3(-100f, -100f, -10f), new Vector3(300f, 300f, 30f));

    // ---------------------------------------------------------------- fields

    /// <summary>A 240 x 160 m floor with a wall across the middle (a 24 m gap at y 68 to 92), as the patrol tests'.</summary>
    static NavGraph Floor() =>
        NavGraph.Build(new NavigationTests.Level().Floor(0f, 0f, 240f, 160f).Wall(120f, 0f, 120f, 68f).Wall(120f, 92f, 120f, 160f).Mesh(), null, NavSettings.From(new SimConfig()));

    static readonly TfwLayout Places = new(
        new Vector3(20f, 80f, 0f),
        [new(60f, 30f, 0f), new(170f, 30f, 0f), new(190f, 130f, 0f)],
        new Vector3(110f, 140f, 0f),
        [new(66f, 36f, 0f), new(176f, 36f, 0f), new(184f, 124f, 0f), new(116f, 134f, 0f)]);

    static MusouSim War(NavGraph? nav) => new(nav) { Swarm = MusouSwarmRules.Default, War = TfwCombatRules.Default };

    static TfwBattle Battle(MusouSim sim, TfwLayout? layout = null, bool bigShootas = false)
    {
        var battle = new TfwBattle(sim, new GuardSim(sim), layout ?? Places, 100.0, 1, false, default, TfwBattleRules.EliteBase, false)
        { Trickle = false, BossOrksOn = false, WeirdboyzOn = false, Patrols = true, BigShootasOn = bigShootas };   // Patrols: the S3 Orks' switch too
        battle.Start();
        return battle;
    }

    /// <summary><paramref name="ticks"/> ticks of the battle and the pool, the players at <paramref name="feet"/> with ids 1, 2, ... (fight off: nobody is hurt).</summary>
    static void Run(MusouSim sim, TfwBattle battle, int ticks, ref uint tick, List<MusouEvent>? events, params Vector3[] feet)
    {
        var targets = feet.Select((p, k) => new MusouTarget(k + 1, p, true)).ToArray();
        var attacks = new List<MusouAttack>();
        for (int k = 0; k < ticks; k++)
        {
            sim.BeginTick(++tick);
            battle.Step(feet);
            sim.Step(targets, null, false, attacks);
            events?.AddRange(sim.Events);
        }
    }

    static int Ticks(float seconds) => (int)MathF.Round(seconds * 30f);

    static ushort Stormboy(MusouSim sim, Vector3 at)
    {
        ushort id = sim.Spawn(MusouEnemyType.Stormboy, at, 0f, sim.Rules.Of(MusouEnemyType.Stormboy).Health);
        Assert.NotEqual(0, id);
        return id;
    }

    static void Steps(MusouSim sim, int ticks, ref uint tick, MusouTarget[]? targets = null, List<MusouEvent>? events = null, bool fight = false, List<MusouAttack>? attacks = null)
    {
        attacks ??= new List<MusouAttack>();
        for (int k = 0; k < ticks; k++)
        {
            sim.BeginTick(++tick);
            sim.Step(targets ?? [], null, fight, attacks);
            events?.AddRange(sim.Events);
        }
    }

    // ---------------------------------------------------------------- the hop

    [Fact]
    public void A_hop_follows_an_arc_and_lands_on_a_node_of_the_graph()
    {
        var nav = Floor();
        var sim = War(nav);
        ushort id = Stormboy(sim, new Vector3(30f, 30f, 0f));
        uint tick = 0;
        Steps(sim, 20, ref tick);   // past the spawn's stand
        var landing = new Vector3(52.3f, 41.7f, 0f);   // 24.7 m away, between nodes
        Assert.True(sim.Hop(id, landing));
        Assert.True(sim.IsAirborne(id));
        Assert.False(sim.Hop(id, landing), "no second hop in the air");
        float top = 0f;
        var events = new List<MusouEvent>();
        for (int k = 0; k < Ticks(MusouStormboy.FlightSeconds) + 2; k++)
        {
            Steps(sim, 1, ref tick, events: events);
            Assert.True(sim.TryGet(id, out var e));
            top = MathF.Max(top, e.Position.Z);
        }
        Assert.True(top > MusouStormboy.ApexHeight * 0.9f, $"the arc's top {top:F2} m");
        Assert.True(sim.TryGet(id, out var landed));
        Assert.Equal(MusouEnemyState.Seek, landed.State);
        Assert.True(landed.Node >= 0);
        Assert.Equal(nav.Position(landed.Node), landed.Position);   // exactly on a node of the graph
        Assert.True(Vector2.Distance(new(landed.Position.X, landed.Position.Y), new(landing.X, landing.Y)) < 1f);
        Assert.Contains(events, e => e.Kind == MusouEventKind.Telegraph && e.Value == MusouStormboy.LandCue && e.Enemy == id);
        // Too far, or off the graph: no hop.
        Assert.False(sim.Hop(id, landed.Position + new Vector3(40f, 0f, 0f)));
        Assert.False(sim.Hop(id, new Vector3(130f, -40f, 0f)));
        Assert.False(sim.Hop(sim.Spawn(MusouEnemyType.Boy, new Vector3(40f, 40f, 0f), 0f, 85f), landing), "only a Stormboy hops");
    }

    [Fact]
    public void A_normal_landing_shoves_aside_only_the_Orks_it_touches_without_damage()
    {
        var sim = War(Floor());
        ushort storm = Stormboy(sim, new Vector3(30f, 30f, 0f));
        var spot = new Vector3(50f, 30f, 0f);
        ushort touched = sim.Spawn(MusouEnemyType.Boy, spot + new Vector3(0.9f, 0f, 0f), 0f, 85f);
        ushort beside = sim.Spawn(MusouEnemyType.Nob, spot + new Vector3(0f, -1.0f, 0f), 0f, 600f);
        ushort clear = sim.Spawn(MusouEnemyType.Boy, spot + new Vector3(-3f, 0f, 0f), 0f, 85f);
        uint tick = 0;
        Steps(sim, 20, ref tick);
        Assert.True(sim.TryGet(storm, out var s0));
        var before = new Dictionary<ushort, Vector3>();
        foreach (var id in new[] { touched, beside, clear }) { Assert.True(sim.TryGet(id, out var e)); before[id] = e.Position; }
        Assert.True(sim.Hop(storm, spot));
        Steps(sim, Ticks(MusouStormboy.FlightSeconds) + 12, ref tick);
        Assert.Equal(1, sim.Landings);
        Assert.Equal(2, sim.Shoved);
        foreach (var id in new[] { touched, beside, clear })
        {
            Assert.True(sim.TryGet(id, out var e));
            Assert.Equal(sim.Rules.Of(e.Type).Health, e.Health);   // no damage
            Assert.NotEqual(MusouEnemyState.Dying, e.State);
        }
        Assert.True(sim.TryGet(touched, out var t) && Vector2.Distance(new(t.Position.X, t.Position.Y), new(spot.X, spot.Y)) > 1.4f, $"the touched Boy was pushed off: {t.Position}");
        Assert.True(sim.TryGet(clear, out var c) && Vector3.Distance(c.Position, before[clear]) < 0.3f, $"the Boy 3 m off stays: {c.Position}");
    }

    [Fact]
    public void A_Stormboy_shot_down_in_the_air_falls_and_crashes_as_an_Ork_blast_that_hurts_and_scatters_Orks()
    {
        var w = TfwWorld(out int human);
        var musou = w.Musou!;
        var centre = new Vector3(20f, -60f, 0f);
        ushort storm = Stormboy(musou, centre - new Vector3(1f, 0f, 0f));   // a short hop, nearly straight up: he comes down within a metre or two of the centre
        for (int k = 0; k < 20; k++) Tick(w);
        Assert.True(musou.TryGet(storm, out var ready) && ready.State == MusouEnemyState.Seek);
        Assert.True(musou.Hop(storm, centre));
        // Orks round where he will come down: one at the impact (dies), an 'Ard Boy at 6 m (hurt), a Boy at 8.5 m (lives, thrown down), a Nob at 2 m (hurt).
        ushort near = musou.SpawnLeashed(MusouEnemyType.Boy, centre + new Vector3(0f, 0.8f, 0f), 0f, 85f, 99, 0f);   // leashed: they stand where they are
        ushort mid = musou.SpawnLeashed(MusouEnemyType.ArdBoy, centre + new Vector3(0f, 6f, 0f), 0f, 170f, 99, 0f);   // leashed: they stand where they are
        ushort far = musou.SpawnLeashed(MusouEnemyType.Boy, centre + new Vector3(0f, -8.5f, 0f), 0f, 85f, 99, 0f);   // leashed: they stand where they are
        ushort nob = musou.SpawnLeashed(MusouEnemyType.Nob, centre + new Vector3(-2f, 0f, 0f), 0f, 600f, 99, 0f);   // leashed: they stand where they are
        for (int k = 0; k < 9; k++) Tick(w);
        Assert.True(musou.TryGet(storm, out var high) && high.State == MusouEnemyState.Airborne && high.Position.Z > 2f, $"in the air: {high.State} {high.Position}");
        Assert.True(musou.Damage(storm, human, 1000f, Vector3.Zero, out bool killed) && killed);
        Assert.True(musou.IsFalling(storm));
        int ticks = 0;
        MusouEvent? crash = null;
        while (musou.IsFalling(storm) && ticks++ < 90)
        {
            Tick(w);
            foreach (var e in musou.Events) if (e.Kind == MusouEventKind.Telegraph && e.Value == MusouStormboy.CrashCue) crash = e;
        }
        Assert.False(musou.IsFalling(storm), "the body reached the ground");
        Assert.NotNull(crash);
        Assert.Equal(human, crash.Value.Player);   // the crash is credited to who shot him down
        Assert.True(Vector2.Distance(new(crash.Value.Vector.X, crash.Value.Vector.Y), new(centre.X, centre.Y)) < 3.5f, $"crashed at {crash.Value.Vector}");   // the arc's way on, a metre or two
        Assert.Equal(1, musou.Crashes);
        Assert.Equal(1, w.MusouCrashesSetOff);
        Assert.False(musou.TryGet(near, out var n) && n.Alive, "the Boy at the impact died");
        Assert.True(musou.TryGet(mid, out var m) && m.Health < 170f, "the 'Ard Boy at 6 m was hurt");
        Assert.True(musou.TryGet(nob, out var b) && b.Health < 600f, "the Nob was hurt");
        Assert.True(musou.TryGet(far, out var f) && f.Alive && f.State == MusouEnemyState.Knocked, $"the Boy at 8.5 m lives and was thrown down: {f.State} {f.Health}");
        Assert.Equal(200f, MusouStormboy.CrashBlast.Damage);
        Assert.Equal(10f, MusouStormboy.CrashBlast.Radius);
    }

    [Fact]
    public void A_Stormboy_killed_on_the_ground_does_not_crash()
    {
        var sim = War(Floor());
        ushort storm = Stormboy(sim, new Vector3(30f, 30f, 0f));
        uint tick = 0;
        Steps(sim, 20, ref tick);
        Assert.True(sim.Damage(storm, 1, 1000f, Vector3.Zero, out bool killed) && killed);
        Assert.False(sim.IsFalling(storm));
        Steps(sim, 30, ref tick);
        var crashes = new List<MusouCrash>();
        Assert.Equal(0, sim.TakeCrashes(crashes));
    }

    // ---------------------------------------------------------------- the squads

    [Fact]
    public void The_Stormboys_replace_the_hunter_columns_and_the_route_patrols_stay()
    {
        Assert.Equal(0, TfwPatrolRules.Hunters);
        Assert.Equal(4, TfwPatrolRules.RoutePatrols);
        Assert.Equal(2, TfwStormboyRules.Squads);
        Assert.Equal(8, TfwStormboyRules.Size);
        var sim = War(Floor());
        var battle = Battle(sim);
        uint tick = 0;
        Run(sim, battle, Ticks(TfwStormboyRules.FirstSeconds) + 5, ref tick, null);
        var squads = battle.StormSquads();
        Assert.Equal(2, squads.Count);
        Assert.All(squads, s => { Assert.True(s.Formed); Assert.Equal(8, s.Ids.Length); Assert.All(s.Ids, id => Assert.True(sim.TryGet(id, out var e) && e.Type == MusouEnemyType.Stormboy)); });
        Assert.All(squads, s => Assert.Equal(TfwStormOrder.Hold, s.Order));   // nothing secured yet: hold the nearest Ork point
    }

    [Fact]
    public void A_squad_counterattacks_the_latest_secured_point_then_hunts_once_it_is_retaken_and_goes_back_to_capturing_when_all_players_are_down()
    {
        var sim = War(Floor());
        var battle = Battle(sim);
        uint tick = 0;
        var orders = new List<(int Squad, TfwStormOrder Order, int What)>();
        battle.OnStormOrder = (s, o, x) => orders.Add((s, o, x));
        var player = Places.Positions[2];   // the player holds point 2 once it is secured
        Run(sim, battle, Ticks(TfwStormboyRules.FirstSeconds) + 5, ref tick, null, player);
        Assert.All(battle.StormSquads(), s => Assert.Equal(TfwStormOrder.Hold, s.Order));   // nothing secured: hold the nearest Ork point
        battle.SecureForTests(0);
        Run(sim, battle, 2, ref tick, null, player);
        Assert.All(battle.StormSquads(), s => { Assert.Equal(TfwStormOrder.Counter, s.Order); Assert.Equal(0, s.Point); });   // the latest secured point
        battle.SecureForTests(2);
        Run(sim, battle, 2, ref tick, null, player);
        Assert.All(battle.StormSquads(), s => { Assert.Equal(TfwStormOrder.Counter, s.Order); Assert.Equal(0, s.Point); });   // committed to the point it set out for
        // Nobody stands on point 0: the Orks there (the garrison, then the Stormboys) take it back in 20 s, and the squads hunt the player.
        for (int s = 0; s < 40 && battle.Objectives[0].State == TfwPositionState.Secured; s++) Run(sim, battle, 30, ref tick, null, player);
        Assert.NotEqual(TfwPositionState.Secured, battle.Objectives[0].State);
        Run(sim, battle, 2, ref tick, null, player);
        Assert.All(battle.StormSquads(), s => { Assert.Equal(TfwStormOrder.Hunt, s.Order); Assert.Equal(1, s.Prey); });
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[2].State);
        // All the players down: back to capturing, the latest secured point (2, still the Imperials').
        Run(sim, battle, 2, ref tick, null);
        Assert.All(battle.StormSquads(), s => { Assert.Equal(TfwStormOrder.Counter, s.Order); Assert.Equal(2, s.Point); });
        output.WriteLine(string.Join("; ", orders.Select(o => $"{o.Squad} {o.Order} {o.What}")));
    }

    [Fact]
    public void A_counterattack_that_loses_half_the_squad_fails_and_the_squad_hunts_the_nearest_player_by_walk()
    {
        var sim = War(Floor());
        var battle = Battle(sim);
        uint tick = 0;
        // Squad 0 forms at source 1 (176, 36), east of the wall. Player 1 is nearer in a straight line but behind the wall (the walk goes round by the gap); player 2 is farther, on the east side.
        var behindWall = new Vector3(105f, 25f, 0f);
        var eastSide = new Vector3(178f, 145f, 0f);
        Run(sim, battle, Ticks(TfwStormboyRules.FirstSeconds) + 5, ref tick, null, behindWall, eastSide);
        battle.SecureForTests(1);
        Run(sim, battle, 1, ref tick, null, behindWall, eastSide);
        var squad = battle.StormSquads()[0];
        Assert.Equal(TfwStormOrder.Counter, squad.Order);
        float straightWall = Vector3.Distance(behindWall, Places.Sources[1]), straightEast = Vector3.Distance(eastSide, Places.Sources[1]);
        Assert.True(straightWall < straightEast, "the wall's side is nearer in a straight line");
        foreach (ushort id in squad.Ids.Take(4)) sim.Damage(id, 1, 1000f, Vector3.Zero, out _);
        Run(sim, battle, 1, ref tick, null, behindWall, eastSide);
        squad = battle.StormSquads()[0];
        Assert.Equal(TfwStormOrder.Hunt, squad.Order);
        Assert.Equal(2, squad.Prey);   // the nearer by walk
    }

    [Fact]
    public void Two_squads_with_a_tie_go_for_different_players_and_a_player_who_leaves_is_dropped()
    {
        // No graph: walks are straight. Both squads form near (0, 0); the two players are as far from both.
        var flat = new TfwLayout(new Vector3(0f, -150f, 0f), [new(-100f, -100f, 0f), new(100f, -100f, 0f), new(0f, 120f, 0f)], new Vector3(0f, 150f, 0f),
            [new(0f, 0f, 0f), new(0f, 1f, 0f), new(0f, 2f, 0f), new(0f, 3f, 0f)]);
        var sim = War(null);
        var battle = Battle(sim, flat);
        uint tick = 0;
        var a = new Vector3(80f, 0f, 0f);
        var b = new Vector3(-80f, 0f, 0f);
        Run(sim, battle, Ticks(TfwStormboyRules.FirstSeconds) + 5, ref tick, null, a, b);
        battle.SecureForTests(2);
        Run(sim, battle, 1, ref tick, null, a, b);
        // Both counterattack point 2; make both fail at once.
        foreach (var s in battle.StormSquads()) foreach (ushort id in s.Ids.Take(4)) sim.Damage(id, 1, 1000f, Vector3.Zero, out _);
        Run(sim, battle, 1, ref tick, null, a, b);
        var squads = battle.StormSquads();
        Assert.All(squads, s => Assert.Equal(TfwStormOrder.Hunt, s.Order));
        Assert.Equal([1, 2], squads.Select(s => s.Prey).OrderBy(x => x).ToArray());
        // Player 2 leaves (or is down): his hunters take the other.
        Run(sim, battle, 1, ref tick, null, a);
        Assert.All(battle.StormSquads(), s => Assert.Equal(1, s.Prey));
    }

    [Fact]
    public void A_hunting_squad_wiped_out_forms_again_and_the_next_squad_attacks_a_point()
    {
        var sim = War(Floor());
        var battle = Battle(sim);
        uint tick = 0;
        var onPoint = Places.Positions[0];   // a player holds point 0, so it stays secured
        var far = new Vector3(225f, 150f, 0f);
        Run(sim, battle, Ticks(TfwStormboyRules.FirstSeconds) + 5, ref tick, null, onPoint, far);
        battle.SecureForTests(0);
        Run(sim, battle, 1, ref tick, null, onPoint, far);
        var squad = battle.StormSquads()[1];
        foreach (ushort id in squad.Ids.Take(4)) sim.Damage(id, 1, 1000f, Vector3.Zero, out _);   // its counterattack fails: it hunts
        Run(sim, battle, 1, ref tick, null, onPoint, far);
        Assert.Equal(TfwStormOrder.Hunt, battle.StormSquads()[1].Order);
        foreach (ushort id in battle.StormSquads()[1].Ids) sim.Damage(id, 1, 1000f, Vector3.Zero, out _);   // wiped out
        Run(sim, battle, 1, ref tick, null, onPoint, far);
        var wiped = battle.StormSquads()[1];
        Assert.False(wiped.Formed);
        Assert.Equal(1, wiped.Wiped);
        Run(sim, battle, Ticks(TfwStormboyRules.ReformSeconds) + 3, ref tick, null, onPoint, far);
        var again = battle.StormSquads()[1];
        Assert.True(again.Formed, "formed again at its source");
        Assert.Empty(again.Ids.Intersect(squad.Ids));
        Assert.Equal(TfwStormOrder.Counter, again.Order);   // the next squad attacks a point
        Assert.Equal(0, again.Point);
    }

    [Fact]
    public void A_squad_hops_as_a_pack_every_three_seconds_and_every_hop_lands_on_the_graph()
    {
        var nav = Floor();
        var sim = War(nav);
        var battle = Battle(sim);
        uint tick = 0;
        var events = new List<MusouEvent>();
        var prey = new Vector3(30f, 150f, 0f);   // far from both sources (one is beyond the wall)
        Run(sim, battle, Ticks(TfwStormboyRules.FirstSeconds) + 5, ref tick, null, prey);
        battle.SecureForTests(1);
        Run(sim, battle, 1, ref tick, null, prey);
        foreach (var s in battle.StormSquads()) foreach (ushort id in s.Ids.Take(4)) sim.Damage(id, 1, 1000f, Vector3.Zero, out _);   // they hunt the player
        Run(sim, battle, 1, ref tick, null, prey);
        Assert.All(battle.StormSquads(), s => Assert.Equal(TfwStormOrder.Hunt, s.Order));
        float worst = 0f;
        uint worstTick = 0;
        var hops = new List<uint>();
        for (int k = 0; k < 30 * 30; k++)
        {
            int before = events.Count;
            Run(sim, battle, 1, ref tick, events, prey);
            for (int e = before; e < events.Count; e++) if (events[e].Kind == MusouEventKind.Telegraph && events[e].Value == MusouStormboy.HopCue && battle.StormSquads()[0].Ids.Contains(events[e].Enemy)) hops.Add(events[e].Tick);
            // the pack: on the ground, every Stormboy of a squad within 12 m of its middle
            foreach (var s in battle.StormSquads())
            {
                var on = s.Ids.Select(id => sim.TryGet(id, out var x) ? x : default).Where(x => x.Alive && x.State != MusouEnemyState.Airborne).ToList();
                if (on.Count < 2 || s.Ids.Any(sim.IsAirborne)) continue;
                var mid = on.Aggregate(Vector3.Zero, (acc, x) => acc + x.Position) / on.Count;
                float wide = on.Max(x => Vector2.Distance(new(x.Position.X, x.Position.Y), new(mid.X, mid.Y)));
                if (wide > worst) { worst = wide; worstTick = tick; }
            }
        }
        var lands = events.Where(e => e.Kind == MusouEventKind.Telegraph && e.Value == MusouStormboy.LandCue).ToList();
        output.WriteLine($"{lands.Count} landings, the pack's widest {worst:F1} m (at tick {worstTick}), squad 0's hop ticks {string.Join(",", hops.Distinct())}");
        Assert.True(lands.Count >= 12, $"{lands.Count} landings");
        Assert.All(lands, e => { int node = nav.Nearest(e.Vector, 0.3f, 0.5f); Assert.True(node >= 0 && Vector3.Distance(nav.Position(node), e.Vector) < 0.3f, $"landed off the graph at {e.Vector}"); });
        Assert.True(worst < 12f, $"the pack spread to {worst:F1} m");
        var starts = hops.Distinct().ToList();
        Assert.True(starts.Count >= 3, $"squad 0 hopped {starts.Count} times");
        for (int k = 1; k < starts.Count; k++) Assert.InRange((int)(starts[k] - starts[k - 1]), Ticks(TfwStormboyRules.HopSeconds) - 1, Ticks(TfwStormboyRules.HopSeconds) + 6);   // 3 s apart, take-off to take-off
        // The hunt closed in: squad 0's middle is much nearer the player than its source.
        var mine = battle.StormSquads()[0].Ids.Select(id => sim.TryGet(id, out var x) ? x.Position : default).ToList();
        var centre = mine.Aggregate(Vector3.Zero, (acc, p) => acc + p) / mine.Count;
        Assert.True(Vector3.Distance(centre, prey) < 40f, $"squad 0 at {centre}, the player at {prey}");
    }

    // ---------------------------------------------------------------- the Big Shoota Nob

    [Fact]
    public void The_Big_Shoota_fires_four_second_bursts_at_twelve_rounds_a_second_then_overheats_for_three_and_a_half()
    {
        var sim = War(null);
        ushort nob = sim.Spawn(MusouEnemyType.BigShootaNob, Vector3.Zero, 0f, 600f);
        Assert.True(sim.Post(nob, Vector3.Zero));
        var player = new MusouTarget(1, new Vector3(0f, 20f, 0f), true);
        uint tick = 0;
        var events = new List<MusouEvent>();
        var attacks = new List<MusouAttack>();
        var states = new List<(uint Tick, MusouEnemyState State)>();
        for (int k = 0; k < 30 * 20; k++)
        {
            sim.BeginTick(++tick);
            attacks.Clear();
            sim.Step([player], null, true, attacks);
            events.AddRange(sim.Events);
            Assert.True(sim.TryGet(nob, out var e));
            states.Add((tick, e.State));
            Assert.All(attacks, a => Assert.Equal(1, a.Player));
        }
        var bursts = events.Where(e => e.Kind == MusouEventKind.Telegraph && e.Value == MusouBigShoota.BurstCue).Select(e => e.Tick).ToList();
        var heats = events.Where(e => e.Kind == MusouEventKind.Telegraph && e.Value == MusouBigShoota.OverheatCue).Select(e => e.Tick).ToList();
        var vents = events.Where(e => e.Kind == MusouEventKind.Telegraph && e.Value == MusouBigShoota.VentCue).Select(e => e.Tick).ToList();
        var tracers = events.Where(e => e.Kind == MusouEventKind.Tracer).Select(e => e.Tick).ToList();
        output.WriteLine($"bursts {string.Join(",", bursts)}; overheats {string.Join(",", heats)}; vents {string.Join(",", vents)}; tracers {tracers.Count}");
        Assert.True(bursts.Count >= 2 && heats.Count >= 2 && vents.Count >= 1);
        // A burst: 4 s of fire, 48 rounds.
        Assert.Equal(Ticks(MusouBigShoota.BurstSeconds), (int)(heats[0] - bursts[0]));
        int rounds = tracers.Count(t => t >= bursts[0] && t < heats[0]);
        Assert.InRange(rounds, 47, 49);
        // The overheat: 3.5 s, steam, not a round fired.
        Assert.Equal(Ticks(MusouBigShoota.OverheatSeconds), (int)(vents[0] - heats[0]));
        Assert.DoesNotContain(tracers, t => t > heats[0] && t <= vents[0]);
        Assert.All(states.Where(s => s.Tick > heats[0] && s.Tick < vents[0]), s => Assert.Equal(MusouEnemyState.Overheated, s.State));
        // Ready again: the next burst comes straight after.
        Assert.InRange((int)(bursts[1] - vents[0]), 0, 3);
        Assert.True(sim.TryGet(nob, out var n) && Vector2.Distance(new(n.Position.X, n.Position.Y), Vector2.Zero) <= MusouBigShoota.PostRadius + 0.1f, "he held his post");
    }

    [Fact]
    public void The_Big_Shootas_bullets_never_hurt_Orks()
    {
        Assert.False(MusouBigShoota.BulletsHurtOrks);
        var sim = War(null);
        ushort nob = sim.Spawn(MusouEnemyType.BigShootaNob, Vector3.Zero, 0f, 600f);
        sim.Post(nob, Vector3.Zero);
        var boyz = new List<ushort>();
        for (int k = 0; k < 8; k++) boyz.Add(sim.Spawn(MusouEnemyType.Boy, new Vector3((k % 2 == 0 ? 0.6f : -0.6f), 3f + k * 1.5f, 0f), 0f, 85f));   // in the line of fire
        var player = new MusouTarget(1, new Vector3(0f, 18f, 0f), true);
        uint tick = 0;
        var attacks = new List<MusouAttack>();
        int hits = 0;
        for (int k = 0; k < 30 * 10; k++)
        {
            sim.BeginTick(++tick);
            attacks.Clear();
            sim.Step([player], null, true, attacks);
            hits += attacks.Count(a => a.Enemy == nob);
            Assert.All(attacks.Where(a => a.Enemy == nob), a => { Assert.Equal(1, a.Player); Assert.Equal(MusouBigShoota.RoundDamage, a.Damage); });
        }
        Assert.True(hits > 5, $"{hits} rounds hit the player");
        foreach (ushort id in boyz) Assert.True(sim.TryGet(id, out var e) && e.Health == 85f, $"Boy {id} was hurt: {e.Health}");
    }

    [Fact]
    public void Each_position_has_a_Big_Shoota_Nob_on_a_post_outside_its_ring_with_a_squad_of_ten()
    {
        var sim = War(Floor());
        var battle = Battle(sim, bigShootas: true);
        Assert.Equal(3 * TfwBattleRules.BigTwins, battle.BigShootas.Count);   // two a position since 2026-11-10 ("double boss and elite enemy spawns"): each position's first in BigShootas[k]
        for (int k = 0; k < 3; k++)
        {
            ushort id = battle.BigShootas[k];
            Assert.True(sim.TryGet(id, out var e) && e.Type == MusouEnemyType.BigShootaNob);
            var post = sim.PostOf(id);
            Assert.NotNull(post);
            float off = Vector2.Distance(new(post.Value.X, post.Value.Y), new(Places.Positions[k].X, Places.Positions[k].Y));
            Assert.True(off > Places.Radius(k), $"post {k} {off:F1} m from its point (ring {Places.Radius(k)})");
            var squad = new ushort[64];
            int n = sim.Query(post.Value, TfwBigShootaRules.RingMax + 0.5f, squad);
            Assert.True(squad.Take(n).Count(s => s != id && sim.Leashed(s)) >= TfwBigShootaRules.Squad, $"{n} round post {k}");
        }
        for (int k = 3; k < battle.BigShootas.Count; k++)   // the seconds: Big Shoota Nobs on posts of their own outside the ring, beside their position's first
        {
            Assert.True(sim.TryGet(battle.BigShootas[k], out var e) && e.Type == MusouEnemyType.BigShootaNob);
            var post = sim.PostOf(battle.BigShootas[k])!.Value;
            int p = k % 3;
            Assert.True(Vector2.Distance(new(post.X, post.Y), new(Places.Positions[p].X, Places.Positions[p].Y)) > Places.Radius(p));
            Assert.True(Vector3.Distance(post, sim.PostOf(battle.BigShootas[p])!.Value) > 3f, $"second {k} on the first's post");
        }
        var none = Battle(War(Floor()), bigShootas: false);
        Assert.Empty(none.BigShootas);
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void The_new_states_and_cues_round_trip_and_the_versions_move()
    {
        Assert.True(MusouSim.Version >= 14u);   // S3 raised it; later stages raise it further
        Assert.Equal(73u, Protocol.Version);   // main's protocol (the damage mark raised it to 71, headshots raised it to 64 at integration, net/responsive to 65, the charge cancel to 66, the ally slide to 67, the Classic stims to 68); the Musou expansion does not raise it
        Assert.True(TfwRules.Version >= 34u);
        var list = new List<MusouEntity>
        {
            new(1, new Vector3(10f, 20f, 4.5f), 1f, MusouEnemyType.Stormboy, MusouEnemyState.Airborne),
            new(2, new Vector3(-10f, 5f, 0f), 2f, MusouEnemyType.BigShootaNob, MusouEnemyState.Overheated),
        };
        var back = MusouWire.ReadEntities(MusouWire.WriteEntities(list, Box), Box);
        Assert.NotNull(back);
        Assert.Equal(list.Select(e => (e.Id, e.Type, e.State)), back.Select(e => (e.Id, e.Type, e.State)));
        Assert.True(MathF.Abs(back[0].Position.Z - 4.5f) < 0.01f, $"the arc's z {back[0].Position.Z}");
        byte[] cues = [MusouStormboy.HopCue, MusouStormboy.LandCue, MusouStormboy.CrashCue, MusouBigShoota.BurstCue, MusouBigShoota.OverheatCue, MusouBigShoota.VentCue];
        Assert.Equal(cues.Length, cues.Distinct().Count());
        Assert.All(cues, c => Assert.True(c > MusouWeirdboy.BurstCue));
        var events = cues.Select((c, k) => new MusouEvent(MusouEventKind.Telegraph, 100u + (uint)k, 1, 2, c, new Vector3(k, 2f * k, 1f))).ToList();
        var read = MusouWire.ReadEvents(MusouWire.WriteEvents(events, Box), Box);
        Assert.NotNull(read);
        Assert.Equal(events.Select(e => (e.Kind, e.Tick, e.Enemy, e.Value)), read.Select(e => (e.Kind, e.Tick, e.Enemy, e.Value)));
        Assert.True(read.Zip(events).All(p => Vector3.Distance(p.First.Vector, p.Second.Vector) < 0.05f));
    }

    // ---------------------------------------------------------------- a war World (the crash's blast needs one)

    static World TfwWorld(out int human)
    {
        human = 1;
        var config = MusouSimTests.Base with { RespawnTime = 60f };
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(human, 0);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(human, new Vector3(-60f, 60f, 0f), 0f);   // well away from the crash
        Tick(w);
        return w;
    }

    static void Tick(World w) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.None));
}
