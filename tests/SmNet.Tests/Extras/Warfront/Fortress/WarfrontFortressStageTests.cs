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

    /// <summary>A run in Travel to the fortress through lane A (cleared: front 4), the server on wf_fortress with one client.</summary>
    Harness Start(out GameServer server)
    {
        var seed = new WarfrontCampaign(5);
        seed.Embark();
        var node = System.Text.Json.Nodes.JsonNode.Parse(seed.ToJson())!.AsObject();
        node["Fronts"] = new System.Text.Json.Nodes.JsonArray(4, 2, 2);
        node["Advanced"] = new System.Text.Json.Nodes.JsonArray(true, false, false);
        node["Phase"] = "Travel";
        node["CurrentTarget"] = new System.Text.Json.Nodes.JsonObject { ["Kind"] = "Fortress", ["Lane"] = 0 };
        File.WriteAllText(Path.Combine(_dir, "run.json"), node.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "stage.json"), System.Text.Json.JsonSerializer.Serialize(new { Map = "wf_fortress", Expected = Array.Empty<ulong>(), Ship = "wf_a3" }));
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
        // the whole party falls: the retainer first (a standing one revives its fallen owner in seconds), then the human; the window and the spectator time pass, then the wipe rule ends the battle
        var sim = server.WarfrontWiringForTests!.Sim;
        int knocked = 0;
        for (int i = 0; i < SmSim.Extras.Warfront.Squad.SquadRules.MaxTotal; i++)
            if (sim.StateOf(i) == SmSim.Extras.Warfront.Squad.SquadState.Standing && sim.Hit(SmSim.Extras.Warfront.Squad.SquadRules.TargetId(i), 1e6f, System.Numerics.Vector3.Zero)) knocked++;
        Assert.True(knocked > 0);
        Assert.True(server.World.TfwKillForTests(HumanOf(server)));
        int waited = 0;
        while (run.Battle is not null && waited < 60) { h.Run(TimeSpan.FromSeconds(1)); waited++; }
        output.WriteLine($"the wipe ended the battle {waited} s after the whole party fell");
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
}
