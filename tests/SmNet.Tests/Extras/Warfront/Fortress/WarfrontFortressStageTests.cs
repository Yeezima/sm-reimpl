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
    Harness Start(out GameServer server, int lane = 0, bool brains = false, int humans = 1)
    {
        var seed = new WarfrontCampaign(5);
        seed.Embark();
        var node = System.Text.Json.Nodes.JsonNode.Parse(seed.ToJson())!.AsObject();
        node["Fronts"] = new System.Text.Json.Nodes.JsonArray(lane == 0 ? 4 : 2, lane == 1 ? 4 : 2, lane == 2 ? 4 : 2);
        node["Advanced"] = new System.Text.Json.Nodes.JsonArray(lane == 0, lane == 1, lane == 2);
        node["Phase"] = "Travel";
        // the human is a returning player of the run (a key the run has never seen joins a battle as a held, locked and unharmed spectating joiner): its record, as the campaign saves it
        var keys = Enumerable.Range(0, humans).Select(i => 111UL + (ulong)i).ToArray();
        node["JoinOrder"] = new System.Text.Json.Nodes.JsonArray(keys.Select(k => (System.Text.Json.Nodes.JsonNode?)k).ToArray());
        node["Players"] = new System.Text.Json.Nodes.JsonArray(keys.Select(k => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
        {
            ["Key"] = k, ["Name"] = k == 111UL ? "Alpha" : $"P{k}", ["Xp"] = 0, ["Level"] = 1, ["PicksTaken"] = 0, ["Cards"] = new System.Text.Json.Nodes.JsonArray(), ["Squad"] = new System.Text.Json.Nodes.JsonArray(),
            ["JoinGrantApplied"] = true, ["JoinedAtDeployment"] = 0, ["ClassConfirmed"] = true, ["PendingPicks"] = 0,
        }).ToArray());
        node["CurrentTarget"] = new System.Text.Json.Nodes.JsonObject { ["Kind"] = "Fortress", ["Lane"] = lane };
        File.WriteAllText(Path.Combine(_dir, "run.json"), node.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "stage.json"), System.Text.Json.JsonSerializer.Serialize(new { Map = "wf_fortress", Expected = keys, Ship = "wf_" + (char)('a' + lane) + "3" }));
        var loaded = WarfrontMaps.Load(WarfrontMaps.FortressName, out var problem)!;
        Assert.Null(problem);
        var map = GameServer.TfwGameMap(loaded.Tfw!, "wf_fortress");
        var h = new Harness(LinkConditions.Perfect, 5, humans, Config, new ServerOptions { Match = Rules }, map: map, clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        server = h.Server;
        // Phase B T4: these cases watch the Warboss against one human; the retainers' brain would fight (and kill) him, so they stand as before T4 (review B1: through the server's own test seam,
        // which drops the brains the Warfront set-up attached); case 5 keeps the brains and takes the retainers out of the boss's line instead
        if (!brains) Assert.True(server.WarfrontRetainersStandForTests());
        h.Run(TimeSpan.FromSeconds(2));
        for (int i = 0; i < humans; i++)
            h.Clients[i].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: keys[i], Name: i == 0 ? "Alpha" : $"P{keys[i]}")));
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

    /// <summary>
    /// The finding-3 and brief measurements in game seconds (W14d: the first version stepped TickRate harness steps per "second", and a harness step is 1 ms, so its "60 s" was about two seconds of game time). A shooting human, stood up again when it falls,
    /// stands 30 m south of the boss spot (inside the plaza) for 90 s of game time (90 000 harness steps of 1 ms) with the retainer and the Orks unfrozen; every 10 s the Orks alive and the Orks inside the plaza radius are written, and for each stage-2
    /// source (and layout source 2) the seconds it was open (sampled every 0.1 s), the Orks alive and in the plaza averaged, and the step time.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void The_plaza_fight_measured_in_game_seconds_the_stage_opens_and_the_plaza_average_is_read()
    {
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        var battle = run.Battle!;
        var arena = WarfrontSlots.Fortress;
        var plan = WarfrontFortressSetup.For(arena).Plan;
        int id = HumanOf(server);
        var from = arena.BossSpot + new System.Numerics.Vector3(0f, -30f, 0f);
        Assert.True(server.World.TfwRespawnAt(id, from, 0f));
        var times = new List<double>();
        var all = new MusouEnemy[MusouSim.Capacity];
        var sw = new Stopwatch();
        const int stepsPerSecond = 1000;   // the harness steps 1 ms of its manual clock; the server ticks on that clock
        int target = -1; float yaw = 0f, pitch = 0f;
        var seen = plan.Layout.Sources.Select((_, i) => i).Where(i => plan.SourceStage[i] >= 2 || i == 2).ToArray();
        var open = seen.ToDictionary(i => i, _ => 0.0);
        var inPlazaSamples = new List<int>();
        var aliveSamples = new List<int>();
        for (int sec = 1; sec <= 90 && run.Battle is not null; sec++)
        {
            for (int t = 0; t < stepsPerSecond && run.Battle is not null; t++)
            {
                sw.Restart();
                h.Step((i, tick) =>
                {
                    if (!server.World.Players.TryGetValue(id, out var me) || !me.Alive) return new PlayerInput(tick, 0, 0, 0, InputButtons.None);
                    if (t % 20 == 0)
                    {
                        int n = server.World.Musou!.CopyTo(all);
                        var eye = me.Position + new System.Numerics.Vector3(0, 0, 1.7f);
                        float bestD = 25f; target = -1;
                        for (int k = 0; k < n; k++) { if (!all[k].Alive) continue; float d = System.Numerics.Vector3.Distance(all[k].Position, me.Position); if (d < bestD) { bestD = d; target = k; } }
                        if (target >= 0) { var d3 = all[target].Position + new System.Numerics.Vector3(0, 0, 1.2f) - eye; yaw = MathF.Atan2(-d3.X, d3.Y); pitch = MathF.Atan2(d3.Z, new System.Numerics.Vector2(d3.X, d3.Y).Length()); }
                    }
                    return target >= 0 ? new PlayerInput(tick, 0, 0, yaw, InputButtons.Fire, pitch) : new PlayerInput(tick, 0, 0, 0, InputButtons.None);
                });
                times.Add(sw.Elapsed.TotalMilliseconds);
                if (t % 100 == 0 && battle.WarfrontFortressFlow is { } flow)
                    foreach (int i in seen) if (flow.SourceOpen(i, plan.Layout.Sources[i], i < plan.Layout.Positions.Length ? plan.Layout.Positions[i] : null)) open[i] += 0.1;
                if (t % 100 == 50 && server.World.Players.TryGetValue(id, out var pl) && !pl.Alive) server.World.TfwRespawnAt(id, from, 0f);
            }
            if (sec % 10 == 0 && run.Battle is not null)
            {
                int n = server.World.Musou!.CopyTo(all);
                int alive = all.Take(n).Count(e => e.Alive);
                int inPlaza = all.Take(n).Count(e => e.Alive && System.Numerics.Vector2.Distance(new(e.Position.X, e.Position.Y), new(arena.BossSpot.X, arena.BossSpot.Y)) <= arena.ArenaRadius);
                aliveSamples.Add(alive); inPlazaSamples.Add(inPlaza);
                output.WriteLine($"{sec} s: stage {battle.WarfrontFortressFlow!.Stage}, plaza entered {battle.WarfrontFortressFlow.PlazaEntered}, orks alive {alive}, in the plaza {inPlaza}, human alive {server.World.Players[id].Alive}, warboss {battle.FortressWarbossPercent}%");
            }
        }
        times.Sort();
        output.WriteLine($"orks in the plaza: average {(inPlazaSamples.Count == 0 ? double.NaN : inPlazaSamples.Average()):0.0} over {inPlazaSamples.Count} samples (10 s apart), alive average {(aliveSamples.Count == 0 ? double.NaN : aliveSamples.Average()):0.0}");
        output.WriteLine("seconds open of 90: " + string.Join(", ", seen.Select(i => $"source {i} (stage {plan.SourceStage[i]}, at {plan.Layout.Sources[i]}) {open[i]:0.0} s")));
        output.WriteLine($"step ms (1 ms of game time per step): p50 {times[times.Count / 2]:0.00}, p99 {times[(int)(times.Count * 0.99)]:0.00}, max {times[^1]:0.00} over {times.Count} steps; elites made {battle.FortressElitesMade}, boss orks {battle.FortressBossOrksMade}");
        Assert.True(battle.WarfrontFortressFlow!.PlazaEntered);
    }

    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(o)!;

    /// <summary>One run of <see cref="Pace"/>: the Warboss's and the human's positions every 0.25 s (T seconds after the human was placed) and the Warboss's stuck lines.</summary>
    sealed record PaceRun(List<(double T, System.Numerics.Vector3 Boss, System.Numerics.Vector3 Human)> Samples, string[] BossStuck, List<(double T, System.Numerics.Vector3 From, double StoodSince)> Homes, float MinHealth, float StartHealth, List<(double T, bool Attacking)> Attacks);

    static float Flat(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));

    /// <summary>
    /// W14c/W14d: the freed Warboss for <paramref name="seconds"/> s of game time (1000 harness steps of 1 ms each), printed once a second (position, speed, boss action, distance, leash, node). With <paramref name="freeFirst"/> a human enters the plaza
    /// first (which frees the Warboss) and is then put at <paramref name="humanAt"/>; each <paramref name="jumps"/> entry puts the human somewhere else at that second. The human stands still unless <paramref name="walk"/> gives a point to walk to.
    /// </summary>
    PaceRun Pace(string name, System.Numerics.Vector3 humanAt, Func<double, System.Numerics.Vector3>? walk = null, bool clearOrks = false, int seconds = 30, bool freeFirst = false, (int At, System.Numerics.Vector3 To)[]? jumps = null)
    {
        using var capture = new ConsoleCapture();
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        var battle = run.Battle!;
        var sim = server.World.Musou!;
        int id = HumanOf(server);
        var all = new MusouEnemy[MusouSim.Capacity];
        void Clear() { int n = sim.CopyTo(all); for (int k = 0; k < n; k++) if (all[k].Id != battle.BossId) sim.Remove(all[k].Id); }
        if (freeFirst)
        {
            // a human enters the plaza (frees the Warboss), then stands where the case wants him
            Assert.True(server.World.TfwRespawnAt(id, WarfrontSlots.Fortress.BossSpot + new System.Numerics.Vector3(0f, -20f, 0f), 0f));
            h.Run(TimeSpan.FromMilliseconds(300));
            Assert.False(sim.Leashed(battle.BossId));
        }
        if (clearOrks) Clear();
        Assert.True(server.World.TfwRespawnAt(id, humanAt, 0f));
        const int rate = 1000;   // the harness steps 1 ms of its manual clock: 1000 steps are one second of game time
        sim.TryGet(battle.BossId, out var b0);
        output.WriteLine($"== {name}: human at {humanAt}, boss at {b0.Position}, clearOrks {clearOrks}");
        var samples = new List<(double, System.Numerics.Vector3, System.Numerics.Vector3)>();
        var last = b0.Position; double total = 0; var begin = b0.Position;
        var homes = new List<(double, System.Numerics.Vector3, double)>(); var attacks = new List<(double, bool)>();
        int homesSeen = battle.FortressWarbossHomes; var prev = b0.Position; double standSince = -1; float minHealth = b0.Health; var prevAt = b0.Position;
        for (int sec = 1; sec <= seconds; sec++)
        {
            foreach (var j in jumps ?? []) if (j.At == sec - 1) Assert.True(server.World.TfwRespawnAt(id, j.To, 0f));
            for (int t = 0; t < rate; t++)
            {
                if (clearOrks && t % 50 == 0) Clear();
                double now = sec - 1 + (double)t / rate;
                h.Step((i, tick) =>
                {
                    if (!server.World.Players.TryGetValue(id, out var me) || !me.Alive || walk is null) return new PlayerInput(tick, 0, 0, 0, InputButtons.None);
                    var to = walk(now) - me.Position;
                    return new PlayerInput(tick, 0, 1f, MathF.Atan2(-to.X, to.Y), InputButtons.None);
                });
                if (t % 250 == 0 && sim.TryGet(battle.BossId, out var sb) && server.World.Players.TryGetValue(id, out var sp)) samples.Add((now, sb.Position, sp.Position));
                if (t % 50 == 0 && sim.TryGet(battle.BossId, out var wb))
                {
                    minHealth = MathF.Min(minHealth, wb.Health);
                    attacks.Add((now, wb.State is MusouEnemyState.WindUp or MusouEnemyState.Strike or MusouEnemyState.Recover));
                    if (battle.FortressWarbossHomes > homesSeen) { if (server.World.Players.TryGetValue(id, out var shooter) && battle.WarfrontFortressFlow!.Setup.Sight is { } sight) output.WriteLine($"  a human at {shooter.Position} sees the pinned Warboss at {prev} through the stage's ray: {sight(shooter.Position + new System.Numerics.Vector3(0f, 0f, 1.7f), prev + new System.Numerics.Vector3(0f, 0f, 1.7f))}"); homes.Add((now, prev, now)); homesSeen = battle.FortressWarbossHomes; standSince = -1; }
                    else if (Flat(wb.Position, prev) / 0.05 < 0.5) { if (standSince < 0) standSince = now; }   // the 0.5 m/s of the rule, read every 50 ms of game time
                    else standSince = -1;
                    prev = wb.Position; if (t % 250 == 0) prevAt = wb.Position;
                }
                if (server.World.Players.TryGetValue(id, out var pl) && !pl.Alive) server.World.TfwRespawnAt(id, pl.Position, 0f);
            }
            if (!sim.TryGet(battle.BossId, out var b)) { output.WriteLine($"{sec,2} s: boss gone"); break; }
            var me2 = server.World.Players[id];
            float step = Flat(b.Position, last);
            total += step; last = b.Position;
            output.WriteLine($"{sec,2} s: boss ({b.Position.X:0.0}, {b.Position.Y:0.0}) speed {step:0.00} m/s state {b.State} act {server.World.MusouBoss?.Action} dist {Flat(b.Position, me2.Position):0.0} leashed {sim.Leashed(b.Id)} node {b.Node} room {(sim.Walk is { } wk && b.Node >= 0 ? wk.EdgeDistance(b.Node).ToString("0.00") : "n/a")} (a lean wants {MusouSim.BossWallRoom(sim.Rules.Of(MusouEnemyType.Warboss).Radius):0.00} m) | human ({me2.Position.X:0.0}, {me2.Position.Y:0.0}) alive {me2.Alive}");
        }
        output.WriteLine($"== {name}: travelled {total:0.0} m in {seconds} s, net {Flat(begin, last):0.0} m");
        return new PaceRun(samples, capture.Lines().Where(l => l.Contains("warfront stuck:") && l.Contains(", Warboss at")).ToArray(), homes, minHealth, b0.Health, attacks);
    }

    /// <summary>The first sample time at which <paramref name="hit"/> holds, or -1.</summary>
    static double FirstTime(PaceRun r, Func<(double T, System.Numerics.Vector3 Boss, System.Numerics.Vector3 Human), bool> hit)
    {
        foreach (var s in r.Samples) if (hit(s)) return s.T;
        return -1;
    }

    /// <summary>
    /// W14e rule for cases 3 and 4 (the arena-boss rule replaces W14d's "through the opening"): a human stands outside an opening and the freed Warboss is drawn to the wall. Asserts: he is put back on his spot at least once; from the start of
    /// the stand that ended in each home it is under 4 s; he lands within 2 m of his spot; he is never more than the plaza's radius plus 20 m from the spot for longer than 2 s (samples every 0.25 s); Musou's failsafe never moves him (no stuck line);
    /// his health only ever fell (no heal). The times are written.
    /// </summary>
    void AssertArenaBoss(string name, PaceRun r)
    {
        var arena = WarfrontSlots.Fortress;
        Assert.NotEmpty(r.Homes);
        foreach (var h0 in r.Homes)
        {
            // pinned since: the earliest sample from which he stayed within 1 m of where he stood when he was put back (he shuffles 0.18 m each way on the wall)
            double since = h0.T;
            foreach (var smp in r.Samples.Where(x => x.T < h0.T).Reverse()) { if (Flat(smp.Boss, h0.From) <= 1f) since = smp.T; else break; }
            var home = (h0.T, h0.From, StoodSince: since);
            output.WriteLine($"{name}: put back at {home.T:0.00} s from {home.From} after standing since {home.StoodSince:0.00} s ({home.T - home.StoodSince:0.00} s)");
            Assert.True(home.T - home.StoodSince <= 4.0, $"{name}: pinned {home.T - home.StoodSince:0.00} s before he was put back");
        }
        double beyondSince = -1, worst = 0;
        foreach (var s in r.Samples)
        {
            if (Flat(s.Boss, arena.BossSpot) > arena.ArenaRadius + WarfrontFortressBattleRules.HomeBeyond) { if (beyondSince < 0) beyondSince = s.T; worst = Math.Max(worst, s.T - beyondSince); }
            else beyondSince = -1;
        }
        output.WriteLine($"{name}: longest stretch beyond the plaza's radius + {WarfrontFortressBattleRules.HomeBeyond:0} m {worst:0.00} s; health {r.StartHealth:0} -> min {r.MinHealth:0}; homes {r.Homes.Count}");
        Assert.True(worst <= 2.0, $"{name}: {worst:0.00} s beyond");
        Assert.Empty(r.BossStuck);   // Musou's failsafe never carried him
        Assert.True(r.MinHealth <= r.StartHealth);
    }

    static readonly System.Numerics.Vector3 South = new(0f, -11f, 0f), West = new(-61f, 50f, 0f), East = new(61f, 50f, 0f);

    /// <summary>Case 1: a human 20 m south of the Warboss inside the plaza: no regression, he reaches within 6 m in under 8 s at about 5.5 m/s.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Case_1_human_20m_in_the_plaza_the_warboss_reaches_him_in_under_8_s()
    {
        var r = Pace("case 1", WarfrontSlots.Fortress.BossSpot + new System.Numerics.Vector3(0f, -20f, 0f), seconds: 10);
        double reach = FirstTime(r, s => Flat(s.Boss, s.Human) <= 6f);
        output.WriteLine($"case 1: within 6 m at {reach:0.00} s");
        Assert.InRange(reach, 0.0, 8.0);
        var arrive = r.Samples.First(s => s.T >= reach).Boss;
        double speed = Flat(r.Samples[0].Boss, arrive) / Math.Max(reach, 0.25);
        output.WriteLine($"case 1: average {speed:0.00} m/s over the approach");
        Assert.True(speed >= 2.5, $"{speed:0.00} m/s");   // the floor is set from the measured value in the W14d report
    }

    /// <summary>Case 2: a human walking the plaza's edge (38 m out, 5 m/s): the Warboss gains or keeps up.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Case_2_a_human_circling_the_plaza_edge_does_not_get_away_from_the_warboss()
    {
        var c = WarfrontSlots.Fortress.BossSpot;
        var r = Pace("case 2", c + new System.Numerics.Vector3(38f, 0f, 0f), t => { double a = t * 5.0 / 38.0; return c + new System.Numerics.Vector3(38f * MathF.Cos((float)a), 38f * MathF.Sin((float)a), 0f); }, seconds: 12);
        double d0 = Flat(r.Samples[0].Boss, r.Samples[0].Human), d1 = Flat(r.Samples[^1].Boss, r.Samples[^1].Human);
        output.WriteLine($"case 2: distance {d0:0.0} m at the start, {d1:0.0} m at {r.Samples[^1].T:0.0} s");
        Assert.True(d1 <= d0 + 1f, $"{d0:0.0} -> {d1:0.0}");
    }

    /// <summary>Cases 3 and 4: a human outside each opening, with the plaza's Orks (3) and without (4).</summary>
    [Theory]
    [Trait("Speed", "Slow")]
    [InlineData("south", false)]
    [InlineData("west", false)]
    [InlineData("east", false)]
    [InlineData("south", true)]
    [InlineData("west", true)]
    [InlineData("east", true)]
    public void Cases_3_and_4_the_warboss_goes_through_each_opening_toward_a_human_outside(string opening, bool noOrks)
    {
        var at = opening switch { "south" => South, "west" => West, _ => East };
        string name = $"case {(noOrks ? 4 : 3)} {opening}";
        var r = Pace(name, at, clearOrks: noOrks, freeFirst: true, seconds: 15);
        AssertArenaBoss(name, r);
    }

    /// <summary>The failsafe's way (a): Musou's stranded failsafe reports the Warboss (why 1, "relocated") and carries him away; the next tick he is put back on his spot, health unchanged, never removed.</summary>
    [Fact]
    public void A_warboss_carried_off_by_the_failsafe_is_put_back_on_his_spot_at_once_and_not_removed()
    {
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var battle = server.World.Tfw!.Battle!;
        var sim = server.World.Musou!;
        int id = HumanOf(server);
        Assert.True(server.World.TfwRespawnAt(id, WarfrontSlots.Fortress.BossSpot + new System.Numerics.Vector3(0f, -20f, 0f), 0f));
        h.Run(TimeSpan.FromMilliseconds(300));
        Assert.False(sim.Leashed(battle.BossId));
        Assert.True(server.World.TfwRespawnAt(id, South, 0f));   // nobody in the plaza now
        sim.TryGet(battle.BossId, out var before);
        var far = new System.Numerics.Vector3(0f, -160f, 0f);   // where Musou's failsafe sends a stranded boss: a still-sending outer source
        Assert.True(sim.Relocate(battle.BossId, far));
        typeof(TfwBattle).GetMethod("WarfrontStuck", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(battle, [battle.BossId, MusouEnemyType.Warboss, before.Position, (byte)1]);
        int homes = battle.FortressWarbossHomes;
        h.Run(TimeSpan.FromMilliseconds(200));
        Assert.True(sim.TryGet(battle.BossId, out var after));
        Assert.Equal(homes + 1, battle.FortressWarbossHomes);
        Assert.True(Flat(after.Position, WarfrontSlots.Fortress.BossSpot) < 5f, $"{after.Position}");
        Assert.True(after.Health <= before.Health);
        Assert.False(battle.Won);
    }

    /// <summary>The human walks back into the plaza from outside the south opening: the Warboss follows him back in within 10 s (he is not left outside).</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void The_human_walks_back_into_the_plaza_and_the_warboss_follows_him_in_within_10_s()
    {
        var arena = WarfrontSlots.Fortress;
        var inside = arena.BossSpot + new System.Numerics.Vector3(0f, -25f, 0f);
        var r = Pace("walk back", South, freeFirst: true, seconds: 25, jumps: [(12, inside)]);
        double back = FirstTime(r, s => s.T >= 12 && Flat(s.Boss, s.Human) <= 10f);
        output.WriteLine($"walk back: the Warboss is within 10 m of him again at {back:0.00} s (the human went back at 12 s)");
        Assert.InRange(back, 12.0, 22.0);
        double attack = r.Attacks.FirstOrDefault(a => a.T >= 12 && a.Attacking).T;
        output.WriteLine($"walk back: his first attack after the human is back in the plaza at {attack:0.00} s (homes {r.Homes.Count})");
        Assert.InRange(attack, 12.0, 22.0);   // within 10 s of the human's return
        Assert.Empty(r.BossStuck);
    }

    /// <summary>
    /// Review F5 (measurement, output only beyond the counts): the tick cost on the real fortress stage with its walking graph, four humans with two driven retainers each, 20 s of the battle after 12 s to land.
    /// The whole server step (h.Step) and the brains' own step (<c>WarfrontRetainerBrains.LastMilliseconds</c>), p50 and p99.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void F5_tick_cost_on_the_fortress_with_its_graph_four_humans_and_eight_driven_retainers()
    {
        var h = Start(out var server, brains: true, humans: 4);
        h.Run(TimeSpan.FromSeconds(12));
        var brains = server.WarfrontBrainsForTests!;
        var wiring = server.WarfrontWiringForTests!;
        output.WriteLine($"retainers driven: {wiring.Retainers.All.Count(r => r.WorldId != 0)}; graph {(brains.BrainOf(wiring.Retainers.All.First(r => r.WorldId != 0).WorldId)?.Graph is { } g ? g.NodeCount + " cells" : "none")}");
        var step = new List<double>(); var own = new List<double>();
        long seen = brains.Steps;
        for (int t = 0; t < 600; t++)
        {
            long s0 = Stopwatch.GetTimestamp();
            h.Step();
            step.Add(Stopwatch.GetElapsedTime(s0).TotalMilliseconds);
            if (brains.Steps != seen) { seen = brains.Steps; own.Add(brains.LastMilliseconds); }
        }
        static double Q(List<double> v, double q) { var c = v.OrderBy(x => x).ToList(); return c.Count == 0 ? 0 : c[Math.Min(c.Count - 1, (int)(c.Count * q))]; }
        output.WriteLine(FormattableString.Invariant($"fortress, 4 humans x 2 driven retainers: harness step p50 {Q(step, 0.5):0.00} ms, p99 {Q(step, 0.99):0.00} ms over {step.Count}; the brains' own step p50 {Q(own, 0.5):0.00} ms, p99 {Q(own, 0.99):0.00} ms over {own.Count}"));
        Assert.Equal(8, wiring.Retainers.All.Count(r => r.WorldId != 0));
        Assert.True(own.Count > 0);
    }

    /// <summary>Case 5: a second Warboss, never leashed (a plain Spawn, no fortress rule, no boss script), on the fortress's open outer yard (no wall within 30 m) 30 m from the human; the fortress's own Warboss stays leashed in the plaza. Unchanged by the lean: he closes the 30 m.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Case_5_a_second_warboss_on_the_open_yard_closes_30m_to_a_human()
    {
        var h = Start(out var server, brains: true);
        h.Run(TimeSpan.FromSeconds(12));
        var sim = server.World.Musou!;
        var battle = server.World.Tfw!.Battle!;
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = sim.CopyTo(all); for (int k = 0; k < n; k++) if (all[k].Id != battle.BossId) sim.Remove(all[k].Id);   // the fortress's own Warboss stays, leashed in the plaza (removing him would end the battle)
        int id = HumanOf(server);
        var human = new System.Numerics.Vector3(0f, -140f, 0f);
        Assert.True(server.World.TfwRespawnAt(id, human, 0f));
        // owner 2026-11-25: bosses target retainers like humans (weight 1 against a human's 0.8), so a retainer nearer the boss would draw him; the retainers (driven by their brains) hold 30 m beyond
        // the human, 60 m from the boss: no retainer is nearer, and the case watches the boss against the human alone
        var wiring = server.WarfrontWiringForTests!;
        var holdAt = human + new System.Numerics.Vector3(0f, 30f, 0f);
        int moved = 0;
        foreach (var r in wiring.Retainers.All)
            if (r.WorldId != 0 && server.World.TfwRespawnAt(r.WorldId, holdAt + new System.Numerics.Vector3(2f * moved++, 0f, 0f), 0f, faceCentre: false)) { }
        Assert.True(wiring.Retainers.ServerOrder(id, MarineOrder.Hold, holdAt));
        Assert.NotNull(server.WarfrontBrainsForTests);
        var from = new System.Numerics.Vector3(0f, -170f, 0f);
        ushort boss = sim.Spawn(MusouEnemyType.Warboss, from, 0f, 1e6f);
        Assert.NotEqual(0, (int)boss);
        double reach = -1;
        for (int sec = 1; sec <= 15 && reach < 0; sec++)
        {
            if (sim.TryGet(boss, out var bs)) output.WriteLine($"{sec - 1} s: boss ({bs.Position.X:0.0}, {bs.Position.Y:0.0}) state {bs.State} dist {Flat(bs.Position, human):0.0}");
            for (int t = 0; t < 1000; t++)
            {
                h.Step();
                if (t % 100 == 0) { int m = sim.CopyTo(all); for (int k = 0; k < m; k++) if (all[k].Id != boss && all[k].Id != battle.BossId) sim.Remove(all[k].Id); }
                if (t % 50 == 0 && sim.TryGet(boss, out var b) && Flat(b.Position, human) <= 10f) { reach = sec - 1 + t / 1000.0; break; }
            }
        }
        output.WriteLine($"case 5: within 10 m of the human (his melee ring) at {reach:0.00} s ({moved} retainer(s) holding 30 m beyond him)");
        Assert.InRange(reach, 0.0, 15.0);
        foreach (var r in wiring.Retainers.All)
            if (r.WorldId != 0 && server.World.Players.TryGetValue(r.WorldId, out var rp)) Assert.True(Flat(rp.Position, from) > Flat(human, from), $"retainer P{r.WorldId} stayed out of the boss's line");
    }

    /// <summary>Output only: does Musou's own Warboss (the Warfront object null) pin at the walls of a plaza opening as the fortress's did? A Musou-run Warboss is the same Move without the lean. The finding goes to the lead; Musou is not changed here.</summary>
    [Fact(Skip = "W14d diagnostic (output only): the answer is in the W14d report; run by hand with the console logger")]
    public void W14d_musou_own_warboss_pinned_on_a_wall_opening_with_the_warfront_object_null()
    {
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var sim = server.World.Musou!;
        var battle = server.World.Tfw!.Battle!;
        typeof(MusouSim).GetProperty("Warfront", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(sim, null);   // (internal setter)  // Musou's own run: no Warfront object, so the lean hook leaves the Warboss as Musou has him
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = sim.CopyTo(all); for (int k = 0; k < n; k++) if (all[k].Id != battle.BossId) sim.Remove(all[k].Id);
        int id = HumanOf(server);
        Assert.True(server.World.TfwRespawnAt(id, WarfrontSlots.Fortress.BossSpot + new System.Numerics.Vector3(0f, -20f, 0f), 0f));
        h.Run(TimeSpan.FromMilliseconds(300));
        Assert.True(server.World.TfwRespawnAt(id, South, 0f));
        var last = default(System.Numerics.Vector3);
        for (int sec = 1; sec <= 20; sec++)
        {
            h.Run(TimeSpan.FromSeconds(1));
            if (sim.TryGet(battle.BossId, out var b)) { output.WriteLine($"{sec,2} s: boss ({b.Position.X:0.0}, {b.Position.Y:0.0}) moved {Flat(b.Position, last):0.00} m, human {South}"); last = b.Position; }
        }
    }

    /// <summary>The whole party falls: the retainers first (a standing one revives its fallen owner in seconds), then the human; the window and the spectator time pass, then the wipe rule ends the battle.</summary>
    void WipeTheParty(Harness h, GameServer server, TfwRun run)
    {
        var retainers = server.WarfrontWiringForTests!.Retainers;
        int knocked = 0;
        foreach (var r in retainers.All)
            if (r.State == SmSim.Extras.Warfront.Squad.SquadState.Standing && server.World.TfwKillForTests(r.WorldId)) knocked++;   // Phase B T2: the retainers are World players
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

    /// <summary>
    /// W14e part 2 (output only, then pinned by the assert at the end): the breach lure that "ended the battle around 50 s with the Warboss gone" in W14d, run as W14d ran it (the Hold sequence up to the breach), with the Warboss's health, state, position and the
    /// console's stuck lines written every second and at the last step before the battle ends. Asserts that the battle ends only with the Warboss dead of damage (health at or under 0 at his last sample) or not at all.
    /// </summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void W14e_the_breach_lure_never_ends_the_battle_with_the_warboss_alive()
    {
        using var capture = new ConsoleCapture();
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        var battle = run.Battle!;
        var plan = run.Warfront!.Fortress!.Plan;
        var sim = server.World.Musou!;
        int id = HumanOf(server);
        var last = (Time: 0.0, Pos: default(System.Numerics.Vector3), Health: 0f, State: default(MusouEnemyState), Alive: true, Leashed: false, Seen: false);
        double t0 = 0;
        string Line(double t) => $"{t:0.00} s: boss ({last.Pos.X:0.0}, {last.Pos.Y:0.0}) health {last.Health:0} state {last.State} alive {last.Alive} leashed {last.Leashed}";
        bool Watch(System.Numerics.Vector3 at, int seconds, string what)
        {
            server.World.TfwRespawnAt(id, at, 0f);
            for (int sec = 0; sec < seconds; sec++)
            {
                for (int t = 0; t < 1000; t++)
                {
                    h.Step();
                    if (run.Battle is null) { output.WriteLine($"battle ended during '{what}' at {sec} s (+{t} ms): last sample " + Line(t0 + sec + t / 1000.0) + $"; the human was at {(server.World.Players.TryGetValue(id, out var hp) ? hp.Position.ToString() : "gone")}; campaign phase {server.WarfrontCampaignForTests!.Phase}; battle.Won {battle.Won}"); return false; }
                    if (sim.TryGet(battle.BossId == 0 ? (ushort)0 : battle.BossId, out var b)) last = (t0 + sec + t / 1000.0, b.Position, b.Health, b.State, b.Alive, sim.Leashed(b.Id), true);
                }
                if (server.World.Players.TryGetValue(id, out var me) && !me.Alive) server.World.TfwRespawnAt(id, at, 0f);
                if (sec % 5 == 4) output.WriteLine($"  [{what}] " + Line(t0 + sec + 1));
            }
            t0 += seconds;
            return true;
        }
        var inPlaza = plan.BossSpot + new System.Numerics.Vector3(0f, -30f, 0f);
        bool ok = Watch(inPlaza, 10, "in the plaza")
            && Watch(plan.BossSpot + new System.Numerics.Vector3(0f, -12f, 0f), 30, "12 m south of him")
            && Watch(plan.PlazaGate + new System.Numerics.Vector3(0f, -15f, 0f), 90, "outside the plaza gate")
            && Watch(plan.Gate + new System.Numerics.Vector3(0f, -19f, 0f), 90, "outside the south gate")
            && Watch(inPlaza, 90, "back in the plaza")
            && Watch(plan.Breach + new System.Numerics.Vector3(0f, -25f, 0f), 90, "outside the breach");
        foreach (var l in capture.Lines().Where(l => l.Contains("warfront stuck:") && l.Contains(", Warboss at"))) output.WriteLine("  boss stuck line: " + l);
        output.WriteLine(ok ? "the battle ran to the end of the sequence; last sample " + Line(t0) : "the battle ended");
        if (!ok) Assert.True(last.Health <= 0f || !last.Alive, "the battle ended with the Warboss alive: " + Line(0));
    }

    /// <summary>Review 2, item 2 (output only): once the Warboss is freed, does a human outside each opening draw him through it? The human is held 90 s just outside the plaza gate, outside the south gate, back in the plaza and outside the breach; his nearest approach to each opening and his south-most y are written.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void The_freed_warboss_is_drawn_out_of_the_plaza_through_each_opening_and_back()
    {
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        var battle = run.Battle!;
        var plan = run.Warfront!.Fortress!.Plan;
        int id = HumanOf(server);
        const int rate = 1000;   // W14d: the harness steps 1 ms of game time, so 1000 steps are one second (it was TickRate steps: 60 ms)
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
