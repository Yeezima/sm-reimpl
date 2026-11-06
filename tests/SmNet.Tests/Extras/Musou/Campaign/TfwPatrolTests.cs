using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode's patrols and hunter columns (the owner, 2026-11-06: "patrols that both patrol on set routes and also hunt down the players no matter where they are", from the existing Orks): the routes on the walking
/// graph, the columns' ranks, the march round the loop, a woken column going as one pack, the re-forming at a source, a hunter finding a far player round a wall, the count never above the crowds' cap, determinism.
/// </summary>
public class TfwPatrolTests
{
    // A 240 x 160 m floor with a wall across the middle (a 24 m gap at y 68 to 92), the battle's places on both sides.
    static readonly TfwLayout Places = new(
        new Vector3(20f, 80f, 0f),
        [new(60f, 30f, 0f), new(170f, 30f, 0f), new(190f, 130f, 0f)],
        new Vector3(110f, 140f, 0f),
        [new(66f, 36f, 0f), new(176f, 36f, 0f), new(184f, 124f, 0f), new(116f, 134f, 0f)]);

    static MusouSim Field() =>
        new(NavGraph.Build(new NavigationTests.Level().Floor(0f, 0f, 240f, 160f).Wall(120f, 0f, 120f, 68f).Wall(120f, 92f, 120f, 160f).Mesh(), null, NavSettings.From(new SimConfig())))
        { Swarm = MusouSwarmRules.Default };

    static TfwBattle Battle(MusouSim sim)
    {
        var battle = new TfwBattle(sim, new GuardSim(sim), Places, 100.0, 1, false, default, TfwBattleRules.EliteBase, false) { Trickle = false, BossOrksOn = false };
        battle.Start();
        return battle;
    }

    /// <summary><paramref name="ticks"/> ticks of the battle and the enemies with targets at <paramref name="feet"/> (none: the players are aboard).</summary>
    static void Run(MusouSim sim, TfwBattle battle, int ticks, ref uint tick, params Vector3[] feet)
    {
        var targets = feet.Select((p, k) => new MusouTarget(k + 1, p, true)).ToArray();
        var attacks = new List<MusouAttack>();
        for (int k = 0; k < ticks; k++)
        {
            sim.BeginTick(++tick);
            battle.Step(feet);
            sim.Step(targets, null, false, attacks);
        }
    }

    static IEnumerable<(int Index, bool Hunter, bool Formed, ushort[] Ids, Vector3 Head, bool Holding)> Routes(TfwBattle b) => b.Columns().Where(c => !c.Hunter);

    [Fact]
    public void Every_column_forms_on_its_route_in_ranks_with_none_near_the_landing_zone()
    {
        var sim = Field();
        var battle = Battle(sim);
        uint tick = 0;
        Run(sim, battle, 100, ref tick);   // the routes are taken 3 s after the start
        Assert.Equal(TfwPatrolRules.RoutePatrols + TfwPatrolRules.Hunters, battle.Columns().Count);
        Assert.All(battle.Columns(), c =>
        {
            Assert.True(c.Formed, $"column {c.Index} formed");
            Assert.Equal(TfwPatrolRules.Size, c.Ids.Length);
            Assert.All(c.Ids, id => Assert.True(sim.Leashed(id)));
        });
        Assert.True(battle.RouteSearches <= 12, $"{battle.RouteSearches} searches");
        foreach (var c in Routes(battle))
        {
            var route = battle.RouteOf(c.Index);
            Assert.NotEmpty(route);
            Assert.True(Vector2.Distance(new(c.Head.X, c.Head.Y), new(Places.LandingZone.X, Places.LandingZone.Y)) >= TfwPatrolRules.FromLanding);
            // The loop goes through the wall's gap, never through the wall.
            Assert.All(route, p => Assert.True(MathF.Abs(p.X - 120f) > 0.3f || (p.Y > 68f && p.Y < 92f), $"route point {p} in the wall"));
        }
        // The ranks: after 10 s of marching each Ork is near its own place (4 abreast, 1.6 m apart), so the column is a column, not a blob or a file.
        Run(sim, battle, 300, ref tick);
        foreach (var c in Routes(battle))
        {
            var near = c.Ids.Count(id => sim.TryGet(id, out var e) && sim.HomeOf(id) is { } home && Vector2.Distance(new(e.Position.X, e.Position.Y), new(home.X, home.Y)) < 2.5f);
            Assert.True(near >= c.Ids.Length * 3 / 4, $"column {c.Index}: {near} of {c.Ids.Length} at their places");
        }
    }

    [Fact]
    public void A_route_patrol_walks_its_loop_at_its_pace()
    {
        var sim = Field();
        var battle = Battle(sim);
        uint tick = 0;
        Run(sim, battle, 100, ref tick);
        var before = Routes(battle).ToDictionary(c => c.Index, c => c.Head);
        Run(sim, battle, 20 * 30, ref tick);
        foreach (var c in Routes(battle))
        {
            Assert.True(c.Formed);
            float moved = Vector2.Distance(new(c.Head.X, c.Head.Y), new(before[c.Index].X, before[c.Index].Y));
            Assert.True(moved > 15f, $"column {c.Index} moved {moved:F1} m in 20 s");
            Assert.True(moved <= TfwPatrolRules.PatrolSpeed * 20f + 1f, $"column {c.Index} moved {moved:F1} m in 20 s");
            // its Orks are on the loop: each within a few metres of a point of the route
            var route = battle.RouteOf(c.Index, 1f);
            foreach (ushort id in c.Ids)
            {
                Assert.True(sim.TryGet(id, out var e));
                float off = route.Min(p => Vector2.Distance(new(p.X, p.Y), new(e.Position.X, e.Position.Y)));
                Assert.True(off < 8f, $"Ork {id} of column {c.Index} {off:F1} m off its route");
            }
        }
    }

    [Fact]
    public void A_player_near_a_column_wakes_all_of_it_as_one_pack_and_it_forms_again_at_its_source()
    {
        var sim = Field();
        var battle = Battle(sim);
        uint tick = 0;
        Run(sim, battle, 130, ref tick);   // formed at 3 s, past their 0.5 s spawning stand (a spawning Ork notices nobody)
        var column = Routes(battle).First();
        var woke = new List<(int Index, ushort[] Ids)>();
        battle.OnColumnWoke = (index, _, ids) => woke.Add((index, ids));
        var player = sim.TryGet(column.Ids[0], out var first) ? first.Position + new Vector3(3f, 0f, 0f) : default;
        Run(sim, battle, 3, ref tick, player);
        var mine = Assert.Single(woke, x => x.Index == column.Index);
        Assert.True(mine.Ids.Length >= TfwPatrolRules.Size - 4, $"{mine.Ids.Length} woke");
        int pack = sim.PackOf(column.Ids[^1]);
        Assert.NotEqual(0, pack);
        Assert.All(column.Ids, id => { Assert.False(sim.Leashed(id)); Assert.Equal(pack, sim.PackOf(id)); });
        Assert.False(battle.Columns()[column.Index].Formed);

        // Away from it, it forms again after its wait, at its source's end of the loop, with fresh Orks.
        Run(sim, battle, (int)(TfwPatrolRules.ReformSeconds * 30f) + 5, ref tick, new Vector3(20f, 150f, 0f));
        var again = battle.Columns()[column.Index];
        Assert.True(again.Formed, $"column {column.Index} not formed again: {sim.Alive} alive");
        Assert.Empty(again.Ids.Intersect(column.Ids));
        var source = Places.Sources[column.Index % Places.Sources.Length];
        Assert.True(Vector2.Distance(new(again.Head.X, again.Head.Y), new(source.X, source.Y)) < 25f, $"formed again {again.Head} far from its source {source}");
    }

    [Fact]
    public void A_hunter_column_finds_a_far_player_round_the_wall()
    {
        var sim = Field();
        var battle = Battle(sim);
        uint tick = 0;
        var player = new Vector3(225f, 150f, 0f);   // beyond the wall from the hunters' sources (0 at x 66; 2 at x 184 is on the player's side)
        Run(sim, battle, 30, ref tick, player);
        var hunter = battle.Columns().First(c => c.Hunter && c.Ids.Length > 0 && sim.TryGet(c.Ids[0], out var e) && e.Position.X < 120f);
        var woke = new List<int>();
        battle.OnColumnWoke = (index, _, _) => woke.Add(index);
        float start = Vector2.Distance(new(hunter.Head.X, hunter.Head.Y), new(player.X, player.Y));
        int seconds = 0;
        for (; seconds < 90 && !woke.Contains(hunter.Index); seconds++) Run(sim, battle, 30, ref tick, player);
        Assert.True(woke.Contains(hunter.Index), $"the hunter column {hunter.Index} never reached the player ({start:F0} m away at the start)");
        Assert.True(seconds * TfwPatrolRules.HunterSpeed > start - TfwPatrolRules.Aggro - 10f, "it went the long way round, as a walker must");
    }

    [Fact]
    public void The_columns_come_out_of_the_crowds_cap_so_the_map_holds_no_more_orks()
    {
        foreach (bool patrols in new[] { true, false })
        {
            var sim = Field();
            var battle = new TfwBattle(sim, new GuardSim(sim), Places, 100.0, 1, false, default, TfwBattleRules.EliteBase, false) { BossOrksOn = false, Patrols = patrols };
            battle.Start();
            uint tick = 0;
            int most = 0;
            for (int s = 0; s < 120; s++)
            {
                Run(sim, battle, 30, ref tick);
                int garrisons = battle.Garrisoned(0) + battle.Garrisoned(1) + battle.Garrisoned(2);
                int counted = sim.Alive - garrisons - battle.Trickled;
                Assert.True(counted <= TfwBattleRules.AliveCap(1), $"{counted} counted at {s} s (patrols {patrols})");
                most = Math.Max(most, sim.Alive);
            }
            Assert.True(most <= TfwBattleRules.AliveCap(1) + 3 * TfwBattleRules.Garrison + TfwBattleRules.TrickleCap(1), $"{most} alive at most");
        }
    }

    [Fact]
    public void The_columns_are_deterministic()
    {
        string Trace()
        {
            var sim = Field();
            var battle = Battle(sim);
            uint tick = 0;
            var s = new System.Text.StringBuilder();
            for (int k = 0; k < 20; k++)
            {
                Run(sim, battle, 30, ref tick, new Vector3(225f, 150f, 0f));
                foreach (var c in battle.Columns()) s.Append($"{c.Index}:{c.Formed}:{c.Ids.Length}:{c.Head.X:F3},{c.Head.Y:F3};");
            }
            return s.ToString();
        }
        Assert.Equal(Trace(), Trace());
    }

    [Fact]
    public void A_moving_home_is_walked_after_and_a_still_one_as_before()
    {
        var sim = Field();
        ushort marcher = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(30f, 30f, 0f), 0f, 85f, 40, 5f);
        ushort stander = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(30f, 40f, 0f), 0f, 85f, 41, 5f);
        uint tick = 0;
        var attacks = new List<MusouAttack>();
        for (int k = 0; k < 20; k++) { sim.BeginTick(++tick); sim.Step([], null, false, attacks); }   // past the spawn stand
        float speeds = 0f;
        for (int k = 0; k < 150; k++)
        {
            var home = new Vector3(30f + 2.5f * (k + 1) / 30f, 30f, 0f);
            Assert.True(sim.MoveHome(marcher, home, new Vector2(2.5f, 0f)));
            sim.BeginTick(++tick); sim.Step([], null, false, attacks);
            Assert.True(sim.TryGet(marcher, out var e));
            if (k >= 30) speeds = MathF.Max(speeds, MathF.Abs(e.Velocity.Length() - 2.5f));
        }
        Assert.True(speeds < 0.6f, $"the marcher's speed strayed {speeds:F2} m/s from the home's 2.5");   // a walk, not run-and-stop
        Assert.True(sim.TryGet(marcher, out var m) && MathF.Abs(m.Position.X - 42.5f) < 1.5f, $"the marcher at {m.Position}");
        Assert.True(sim.TryGet(stander, out var still) && Vector2.Distance(new(still.Position.X, still.Position.Y), new(30f, 40f)) < 0.6f);
        Assert.True(sim.Unleash(marcher));
        Assert.False(sim.Leashed(marcher));
        Assert.False(sim.MoveHome(marcher, Vector3.Zero, Vector2.Zero));
    }

    [Fact]
    public void On_the_real_walking_graph_the_columns_march_and_a_position_is_still_secured()
    {
        var map = TfwTestArena.ServerMap();
        var h = new Harness(LinkConditions.Perfect, 5, 0, TfwRig.Config, new SmServer.ServerOptions { Match = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map);
        var nav = Assert.IsType<NavGraph>(h.Server.World.Musou!.Nav);
        var w = TfwRig.War(nav);
        w.Tfw!.Patrols = true;
        var battle = TfwRig.Deploy(w);
        TfwRig.Seconds(w, TfwPatrolRules.RouteSeconds + 1f);
        var columns = battle.Columns();
        Assert.Equal(TfwPatrolRules.RoutePatrols + TfwPatrolRules.Hunters, columns.Count);
        Assert.True(columns.Count(c => c.Formed) >= TfwPatrolRules.Hunters + 2, $"formed: {string.Join(", ", columns.Select(c => $"{c.Index} {c.Formed}"))}");
        Assert.InRange(battle.RouteSearches, 1, 12);   // run off the tick: their time is no tick's
        var heads = columns.ToDictionary(c => c.Index, c => c.Head);
        TfwRig.Seconds(w, 10f);
        Assert.Contains(battle.Columns(), c => c.Formed && Vector3.Distance(c.Head, heads[c.Index]) > 10f);
        TfwRig.Secure(w, battle, 0);
    }
}