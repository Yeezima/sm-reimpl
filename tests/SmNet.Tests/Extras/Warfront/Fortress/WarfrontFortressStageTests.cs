using System.Diagnostics;
using SmContent.Extras.Warfront;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Fortress;
using SmSim.Extras.Warfront.Maps;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Fortress;

/// <summary>
/// W14b live half: the campaign's fortress battle on a real server in process (loopback network, manual clock) on the composed fortress map. A run in Travel to the fortress lands on it through the run's own Deploy with the arena's
/// layout, one Warboss stands at the boss spot, a kill wins the campaign at once (results out, the run finished) and a wipe drops the cleared lane to node 3 and locks the fortress. Serial with the other stage tests.
/// </summary>
[Collection("WarfrontStage")]
public class WarfrontFortressStageTests : IDisposable
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
    static readonly SimConfig Config = new() { Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou | ContentExtras.Warfront };
    static MatchRules Rules => new() { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-fortress-" + Guid.NewGuid().ToString("N"));
    readonly ITestOutputHelper output;

    public WarfrontFortressStageTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(_dir);
        WarfrontRunFiles.DirOverride = _dir;
        WarfrontRunFiles.NavCacheOverride = null;   // the private cache: the fortress graph is built once and read back
    }

    public void Dispose()
    {
        WarfrontRunFiles.DirOverride = null;
        WarfrontRunFiles.NavCacheOverride = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A run in Travel to the fortress through <paramref name="lane"/> (0 = A; cleared: front 4, the others at 2), the server on wf_fortress with one client.</summary>
    Harness Start(out GameServer server, int lane = 0)
    {
        var seed = new WarfrontCampaign(5);
        seed.Embark();
        var node = System.Text.Json.Nodes.JsonNode.Parse(seed.ToJson())!.AsObject();
        node["Fronts"] = new System.Text.Json.Nodes.JsonArray(lane == 0 ? 4 : 2, lane == 1 ? 4 : 2, lane == 2 ? 4 : 2);
        node["Advanced"] = new System.Text.Json.Nodes.JsonArray(lane == 0, lane == 1, lane == 2);
        node["Phase"] = "Travel";
        // the human is a returning player of the run (a key the run has never seen joins a battle as a held, locked and unharmed spectating joiner): its record, as the campaign saves it
        node["JoinOrder"] = new System.Text.Json.Nodes.JsonArray(111UL);
        node["Players"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["Key"] = 111UL, ["Name"] = "Alpha", ["Xp"] = 0, ["Level"] = 1, ["PicksTaken"] = 0, ["Cards"] = new System.Text.Json.Nodes.JsonArray(), ["Squad"] = new System.Text.Json.Nodes.JsonArray(),
            ["JoinGrantApplied"] = true, ["JoinedAtDeployment"] = 0, ["ClassConfirmed"] = true, ["PendingPicks"] = 0,
        });
        node["CurrentTarget"] = new System.Text.Json.Nodes.JsonObject { ["Kind"] = "Fortress", ["Lane"] = lane };
        File.WriteAllText(Path.Combine(_dir, "run.json"), node.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "stage.json"), System.Text.Json.JsonSerializer.Serialize(new { Map = "wf_fortress", Expected = new[] { 111UL }, Ship = "wf_" + (char)('a' + lane) + "3" }));
        var loaded = WarfrontMaps.Load(WarfrontMaps.FortressName, out var problem)!;
        Assert.Null(problem);
        var map = GameServer.TfwGameMap(loaded.Tfw!, "wf_fortress");
        var h = new Harness(LinkConditions.Perfect, 5, 1, Config, new ServerOptions { Match = Rules }, map: map, clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        server = h.Server;
        h.Run(TimeSpan.FromSeconds(2));
        h.Clients[0].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: 111, Name: "Alpha")));
        return h;
    }

    /// <summary>The one human's id (the players also hold the run's puppets: the Stormboys' feet and the like).</summary>
    static int HumanOf(GameServer server) => server.World.Players.Keys.First(id => !TfwContracts.IsPuppet(id));

    [Fact]
    public void The_map_composes_with_the_hub_from_the_arena_interface_and_every_orks_walk_exists()
    {
        var loaded = WarfrontMaps.Load(WarfrontMaps.FortressName, out var problem)!;
        Assert.Null(problem);
        Assert.NotNull(loaded.Tfw);
        Assert.Contains(WarfrontMaps.FortressName, WarfrontMaps.AllNames);
        var sw = Stopwatch.StartNew();
        var nav = WarfrontMaps.Nav(loaded, Config, out string note);
        output.WriteLine($"wf_fortress composed 0x{loaded.Hash:X16}UL; graph {nav.NodeCount} nodes in {sw.ElapsedMilliseconds} ms ({note})");
        Assert.Empty(WarfrontArenaGates.CheckWalk(WarfrontSlots.Fortress, nav));
    }

    [Fact]
    public void The_fortress_target_lands_with_the_arena_layout_one_leashed_warboss_and_a_warboss_kill_wins_the_campaign_at_once()
    {
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        var battle = Assert.IsType<TfwBattle>(run.Battle);
        Assert.True(battle.IsWarfrontFortress);
        Assert.NotNull(run.Warfront!.Fortress!.Sight);   // a fortress stage without a sight test is a server fault (the landing logs it); this stage has the map's weapon collision
        Assert.Equal(WarfrontPhase.Battle, server.WarfrontCampaignForTests!.Phase);
        Assert.Equal(WarfrontSlots.Fortress.BossSpot, battle.Layout.Hq);
        Assert.NotEqual(0, battle.BossId);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = server.World.Musou!.CopyTo(all);
        Assert.Equal(1, all.Take(n).Count(e => e.Alive && e.Type == MusouEnemyType.Warboss));
        int id = HumanOf(server);
        Assert.True(server.World.Musou.Damage(battle.BossId, id, 1e7f, System.Numerics.Vector3.Zero, out _));
        h.Run(TimeSpan.FromSeconds(1));
        var c = server.WarfrontCampaignForTests!;
        Assert.Null(run.Battle);
        Assert.Equal(WarfrontPhase.Won, c.Phase);
        Assert.Equal(WarfrontTargetKind.Fortress, c.History.Last().Target.Kind);
    }

    [Fact]
    public void A_wipe_in_the_fortress_drops_the_cleared_lane_to_node_3_and_locks_the_fortress()
    {
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        Assert.True(run.Battle is { } b && b.IsWarfrontFortress);
        WipeTheParty(h, server, run);
        var c = server.WarfrontCampaignForTests!;
        Assert.Null(run.Battle);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
        Assert.Equal(3, c.Front(0));
        Assert.DoesNotContain(c.AvailableTargets(), t => t.Kind == WarfrontTargetKind.Fortress);
    }

    /// <summary>The finding-3 and brief measurements (output only, one assert each that the plaza opened): a human stands in the plaza for 60 s with the retainer and the Orks unfrozen; the harness step time (server, client and loopback together), the Orks alive in the plaza radius every 10 s, and what the battle made.</summary>
    [Fact]
    public void The_plaza_fight_measured_the_stage_opens_the_warboss_is_freed_and_the_step_time_is_read()
    {
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        var battle = run.Battle!;
        var arena = WarfrontSlots.Fortress;
        int id = HumanOf(server);
        Assert.True(server.World.TfwRespawnAt(id, arena.BossSpot + new System.Numerics.Vector3(0f, -30f, 0f), 0f));
        server.World.Players.TryGetValue(id, out _);
        var times = new List<double>();
        var all = new MusouEnemy[MusouSim.Capacity];
        var sw = new Stopwatch();
        int rate = server.World.Config.TickRate;
        for (int sec = 1; sec <= 60; sec++)
        {
            for (int t = 0; t < rate; t++) { sw.Restart(); h.Step(); times.Add(sw.Elapsed.TotalMilliseconds); }
            if (sec % 10 == 0 && run.Battle is not null)
            {
                int n = server.World.Musou!.CopyTo(all);
                int inPlaza = all.Take(n).Count(e => e.Alive && System.Numerics.Vector2.Distance(new(e.Position.X, e.Position.Y), new(arena.BossSpot.X, arena.BossSpot.Y)) <= arena.ArenaRadius);
                bool alive = server.World.Players[id].Alive;
                output.WriteLine($"{sec} s: stage {battle.WarfrontFortressFlow!.Stage}, plaza entered {battle.WarfrontFortressFlow.PlazaEntered}, orks alive {all.Take(n).Count(e => e.Alive)}, in the plaza {inPlaza}, human alive {alive}, warboss {battle.FortressWarbossPercent}%");
            }
            if (run.Battle is null) { output.WriteLine($"the battle ended at {sec} s: {server.WarfrontCampaignForTests!.Phase}"); break; }
        }
        times.Sort();
        output.WriteLine($"step ms: p50 {times[times.Count / 2]:0.00}, p99 {times[(int)(times.Count * 0.99)]:0.00}, max {times[^1]:0.00} over {times.Count} steps; elites made {battle.FortressElitesMade}, boss orks {battle.FortressBossOrksMade}");
        Assert.True(battle.WarfrontFortressFlow!.PlazaEntered);
    }

    /// <summary>The whole party falls: the retainers first (a standing one revives its fallen owner in seconds), then the human; the window and the spectator time pass, then the wipe rule ends the battle.</summary>
    void WipeTheParty(Harness h, GameServer server, TfwRun run)
    {
        var sim = server.WarfrontWiringForTests!.Sim;
        int knocked = 0;
        for (int i = 0; i < SmSim.Extras.Warfront.Squad.SquadRules.MaxTotal; i++)
            if (sim.StateOf(i) == SmSim.Extras.Warfront.Squad.SquadState.Standing && sim.Hit(SmSim.Extras.Warfront.Squad.SquadRules.TargetId(i), 1e6f, System.Numerics.Vector3.Zero)) knocked++;
        Assert.True(knocked > 0);
        Assert.True(server.World.TfwKillForTests(HumanOf(server)));
        int waited = 0;
        while (run.Battle is not null && waited < 60) { h.Run(TimeSpan.FromSeconds(1)); waited++; }
        output.WriteLine($"the wipe ended the battle {waited} s after the whole party fell");
    }

    /// <summary>The review 2 case that matters: lane C is the cleared lane (the landing still uses lane A's frame). A wipe puts C back at node 3, A and B are rolled once each, the fortress is locked, and the campaign's lane A front is its own number, not Musou's war state.</summary>
    [Fact]
    public void A_wipe_in_the_fortress_through_lane_C_drops_C_to_node_3_rolls_A_and_B_once_each_and_locks_the_fortress()
    {
        var h = Start(out var server, lane: 2);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        Assert.True(run.Battle is { } b && b.IsWarfrontFortress);
        var c = server.WarfrontCampaignForTests!;
        Assert.Equal(4, c.Front(2));
        Assert.Equal(WarfrontTargetKind.Fortress, c.CurrentTarget!.Value.Kind);
        Assert.Equal(2, c.CurrentTarget.Value.Lane);
        WipeTheParty(h, server, run);
        Assert.Null(run.Battle);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
        Assert.Equal(3, c.Front(2));
        foreach (int lane in new[] { 0, 1 })
        {
            var rolls = c.Rolls.Where(r => r.Deployment == 1 && r.Lane == lane).ToList();
            Assert.Single(rolls);   // rolled once
            Assert.Equal(2, rolls[0].Before);
            Assert.Equal(rolls[0].After, c.Front(lane));   // the campaign's own number
        }
        Assert.DoesNotContain(c.Rolls, r => r.Deployment == 1 && r.Lane == 2);   // the cleared lane is dropped, not rolled
        Assert.DoesNotContain(c.AvailableTargets(), t => t.Kind == WarfrontTargetKind.Fortress);
        Assert.NotEqual(0, c.Front(0));   // lane A is not Musou's war state (SetFrontsForTests(0) put the run's lane A at node 1)
    }

    /// <summary>The three openings of the fortress (the plan's waypoints): the nearest one to a point inside 30 m, or "elsewhere".</summary>
    static string OpeningOf(System.Numerics.Vector3 at, WarfrontFortressPlan plan)
    {
        float Flat(System.Numerics.Vector3 a) => System.Numerics.Vector2.Distance(new(a.X, a.Y), new(at.X, at.Y));
        var named = new (string Name, float Distance)[] { ("gate", Flat(plan.Gate)), ("breach", Flat(plan.Breach)), ("plaza gate", Flat(plan.PlazaGate)) };
        var best = named.OrderBy(n => n.Distance).First();
        return best.Distance <= 30f ? best.Name : "elsewhere";
    }

    sealed class ConsoleCapture : IDisposable
    {
        readonly TextWriter _old = Console.Out;
        readonly StringWriter _sw = new();
        public ConsoleCapture() => Console.SetOut(TextWriter.Synchronized(_sw));
        public string[] Lines() => _sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        public void Dispose() => Console.SetOut(_old);
    }

    /// <summary>What one scripted bot run read (review 2, item 2).</summary>
    sealed record BotRun(double FirstDown, double Ended, string Outcome, int Stage, double FurthestY, double Nearest, int Waypoint, string[] Stuck);

    /// <summary>
    /// A scripted walk-and-gun bot at level 5 (the harness rifle is the tests' synthetic one: 9 damage, 0.12 s, not the original data): it stops and shoots the nearest Ork when one is within 6 m, walks the route (gate, plaza gate, boss spot)
    /// otherwise, and fires at any Ork within 25 m while it walks. With <paramref name="keepAlive"/> a fallen human is stood up again where it fell (the stuck count then covers a whole battle). The stuck lines are read from the console.
    /// </summary>
    BotRun RunBot(int seconds, bool keepAlive)
    {
        using var capture = new ConsoleCapture();
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(0.5));
        var c = server.WarfrontCampaignForTests!;
        c.AddXp(111, WarfrontCampaign.XpForLevel(5));
        Assert.Equal(5, c.Player(111)!.Level);
        var plan = WarfrontFortressSetup.For(WarfrontSlots.Fortress).Plan;
        var way = new[] { plan.Gate, plan.PlazaGate, plan.BossSpot };
        var all = new MusouEnemy[MusouSim.Capacity];
        int waypoint = 0, stage = 0;
        double nextTrace = 0;
        double firstDown = -1, nearest = double.MaxValue, furthestY = double.MinValue;
        h.Run(TimeSpan.FromSeconds(6));   // the landing and the battle's first seconds
        var run = server.World.Tfw!;
        var start = h.Clock.Now;
        int id = HumanOf(server);
        System.Numerics.Vector3 lastAlive = plan.Layout.LandingZone;
        while (run.Battle is { } battle && (h.Clock.Now - start).TotalSeconds < seconds)
        {
            h.Step((i, t) =>
            {
                if (!server.World.Players.TryGetValue(id, out var me) || !me.Alive) return new PlayerInput(t, 0, 0, 0, InputButtons.None);
                int n = server.World.Musou!.CopyTo(all);
                var eye = me.Position + new System.Numerics.Vector3(0, 0, 1.7f);
                int best = -1; float bestD = 25f;
                for (int k = 0; k < n; k++)
                {
                    if (!all[k].Alive) continue;
                    float d = System.Numerics.Vector3.Distance(all[k].Position, me.Position);
                    if (d < bestD) { bestD = d; best = k; }
                }
                if (waypoint < way.Length - 1 && System.Numerics.Vector2.Distance(new(me.Position.X, me.Position.Y), new(way[waypoint].X, way[waypoint].Y)) < 8f) waypoint++;
                var to = way[waypoint] - me.Position;
                if (best >= 0)
                {
                    var d3 = all[best].Position + new System.Numerics.Vector3(0, 0, 1.2f) - eye;
                    float yaw = MathF.Atan2(-d3.X, d3.Y);
                    return new PlayerInput(t, 0, bestD < 6f ? 0f : 1f, yaw, InputButtons.Fire, MathF.Atan2(d3.Z, new System.Numerics.Vector2(d3.X, d3.Y).Length()));
                }
                return new PlayerInput(t, 0, 1f, MathF.Atan2(-to.X, to.Y), InputButtons.None);
            });
            double now = (h.Clock.Now - start).TotalSeconds;
            if (server.World.Players.TryGetValue(id, out var p))
            {
                if (p.Alive) lastAlive = p.Position;
                else
                {
                    if (firstDown < 0) firstDown = now;
                    if (keepAlive) server.World.TfwRespawnAt(id, lastAlive, 0f);
                }
                nearest = Math.Min(nearest, System.Numerics.Vector3.Distance(p.Position, plan.BossSpot));
                furthestY = Math.Max(furthestY, p.Position.Y);
            }
            stage = Math.Max(stage, battle.WarfrontFortressFlow?.Stage ?? 0);
            if (server.World.Players.TryGetValue(id, out var q) && now >= nextTrace) { nextTrace += 15; output.WriteLine($"  bot at {now:0} s: ({q.Position.X:0}, {q.Position.Y:0}), alive {q.Alive}, hp {q.Health:0}"); }
        }
        string outcome = run.Battle is null ? "ended, " + server.WarfrontCampaignForTests!.Phase : $"still running at {seconds} s";
        return new BotRun(firstDown, (h.Clock.Now - start).TotalSeconds, outcome, stage, furthestY, nearest, waypoint, capture.Lines().Where(l => l.Contains("warfront stuck: slot wf_fortress")).ToArray());
    }

    /// <summary>The stuck lines per opening (the plan's gate, breach and plaza gate, 30 m), written out.</summary>
    void WriteStuck(string[] stuck)
    {
        var plan = WarfrontFortressSetup.For(WarfrontSlots.Fortress).Plan;
        var rx = new System.Text.RegularExpressions.Regex(@"at \((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\)");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var perOpening = new Dictionary<string, int>();
        int relocated = 0, removed = 0;
        foreach (string line in stuck)
        {
            var m = rx.Match(line);
            Assert.True(m.Success, line);
            var at = new System.Numerics.Vector3(float.Parse(m.Groups[1].Value, inv), float.Parse(m.Groups[2].Value, inv), float.Parse(m.Groups[3].Value, inv));
            string key = OpeningOf(at, plan);
            perOpening[key] = perOpening.GetValueOrDefault(key) + 1;
            if (line.Contains("relocated")) relocated++; else removed++;
        }
        output.WriteLine($"openings: gate {plan.Gate}, breach {plan.Breach}, plaza gate {plan.PlazaGate}, boss spot {plan.BossSpot}, arena radius {plan.ArenaRadius}");
        output.WriteLine($"stuck lines: {stuck.Length} ({relocated} relocated, {removed} removed): " + string.Join(", ", new[] { "gate", "breach", "plaza gate", "elsewhere" }.Select(k => $"{k} {perOpening.GetValueOrDefault(k)}")));
        foreach (var l in stuck.Where(l => OpeningOf(ParsePoint(l), plan) == "elsewhere").Take(8)) output.WriteLine("  elsewhere: " + l);
    }

    static System.Numerics.Vector3 ParsePoint(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"at \((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\)");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new(float.Parse(m.Groups[1].Value, inv), float.Parse(m.Groups[2].Value, inv), float.Parse(m.Groups[3].Value, inv));
    }

    /// <summary>Review 2, item 2 (output only): the solo walk-and-gun bot at level 5 with no help from the test: when it first falls, when the battle ends, the furthest stage, the nearest it came to the Warboss, and the stuck lines of its battle per opening.</summary>
    [Fact]
    public void A_solo_walk_and_gun_bot_at_level_5_is_timed_and_its_progress_and_stuck_units_read()
    {
        var r = RunBot(150, keepAlive: false);
        output.WriteLine($"solo bot, level 5: first down at {(r.FirstDown < 0 ? "never" : r.FirstDown.ToString("0.0") + " s")}, battle {r.Outcome} (after {r.Ended:0.0} s); furthest stage {r.Stage}, furthest y {r.FurthestY:0}, nearest to the boss spot {r.Nearest:0} m, waypoint reached {r.Waypoint} (0 gate, 1 plaza gate, 2 boss spot)");
        WriteStuck(r.Stuck);
    }

    /// <summary>Review 2, item 2 (output only): the same bot stood up again where it falls, so the battle runs 150 s whatever happens: the stuck lines per opening over a full battle and how far it gets.</summary>
    [Fact]
    public void Stuck_units_per_opening_over_a_full_battle_with_a_bot_that_stands_up_again()
    {
        var r = RunBot(150, keepAlive: true);
        output.WriteLine($"kept-alive bot, level 5: first down at {(r.FirstDown < 0 ? "never" : r.FirstDown.ToString("0.0") + " s")}, battle {r.Outcome}; furthest stage {r.Stage}, furthest y {r.FurthestY:0}, nearest to the boss spot {r.Nearest:0} m, waypoint reached {r.Waypoint}");
        WriteStuck(r.Stuck);
    }

    /// <summary>Review 2, item 2 (output only): once the Warboss is freed, does a human outside each opening draw him through it? The human is held 90 s just outside the plaza gate, outside the south gate, back in the plaza and outside the breach; his nearest approach to each opening and his south-most y are written.</summary>
    [Fact]
    public void The_freed_warboss_is_drawn_out_of_the_plaza_through_each_opening_and_back()
    {
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        var battle = run.Battle!;
        var plan = run.Warfront!.Fortress!.Plan;
        int id = HumanOf(server);
        int rate = server.World.Config.TickRate;
        var inPlaza = plan.BossSpot + new System.Numerics.Vector3(0f, -30f, 0f);
        var buffer = new MusouEnemy[MusouSim.Capacity];
        float Flat(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
        System.Numerics.Vector3 Boss() => server.World.Musou!.TryGet((ushort)battle.BossId, out var e) && e.Alive ? e.Position : new(float.NaN, float.NaN, float.NaN);

        void Hold(string what, System.Numerics.Vector3 at, int seconds, params (string Name, System.Numerics.Vector3 Point)[] points)
        {
            var nearest = points.ToDictionary(p => p.Name, _ => float.MaxValue);
            float southest = float.MaxValue;
            var trace = new System.Text.StringBuilder();
            server.World.TfwRespawnAt(id, at, 0f);
            for (int sec = 0; sec < seconds && run.Battle is not null; sec++)
            {
                for (int t = 0; t < rate; t++)
                {
                    h.Step();
                    var b = Boss();
                    if (float.IsNaN(b.X)) continue;
                    foreach (var p in points) nearest[p.Name] = Math.Min(nearest[p.Name], Flat(b, p.Point));
                    southest = Math.Min(southest, b.Y);
                }
                if (server.World.Players.TryGetValue(id, out var me) && !me.Alive) server.World.TfwRespawnAt(id, at, 0f);
                if (sec % 10 == 9) { var bb = Boss(); int near = 0; int cnt = server.World.Musou!.CopyTo(buffer); for (int k = 0; k < cnt; k++) if (buffer[k].Alive && buffer[k].Id != battle.BossId && Flat(buffer[k].Position, bb) < 12f) near++; trace.Append($" [{sec + 1} s: y {bb.Y:0}, {near} orks within 12 m]"); }
            }
            output.WriteLine("  trace:" + trace);
            output.WriteLine($"human {what} ({at.X:0}, {at.Y:0}), {seconds} s: the Warboss came within " + string.Join(", ", points.Select(p => $"{nearest[p.Name]:0} m of the {p.Name}")) + $"; south-most y {southest:0}; he is now at {Boss()}");
        }
        Hold("in the plaza", inPlaza, 10);
        Assert.True(battle.WarfrontFortressFlow!.PlazaEntered);
        output.WriteLine($"the Warboss after 10 s with a human in the plaza: {Boss()} (boss spot {plan.BossSpot})");
        Hold("12 m south of him in the plaza (his pace when a target stands close)", plan.BossSpot + new System.Numerics.Vector3(0f, -12f, 0f), 30, ("human", plan.BossSpot + new System.Numerics.Vector3(0f, -12f, 0f)));
        Hold("15 m outside the plaza gate", plan.PlazaGate + new System.Numerics.Vector3(0f, -15f, 0f), 90, ("plaza gate", plan.PlazaGate), ("south gate", plan.Gate));
        Hold("19 m outside the south gate", plan.Gate + new System.Numerics.Vector3(0f, -19f, 0f), 90, ("plaza gate", plan.PlazaGate), ("south gate", plan.Gate));
        Hold("back in the plaza", inPlaza, 90, ("boss spot", plan.BossSpot));
        Hold("25 m outside the breach", plan.Breach + new System.Numerics.Vector3(0f, -25f, 0f), 90, ("plaza gate", plan.PlazaGate), ("breach", plan.Breach));
    }
}
