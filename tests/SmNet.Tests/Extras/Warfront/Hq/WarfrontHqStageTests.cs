using SmContent.Extras.Warfront;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;

namespace SmNet.Tests.Extras.Warfront.Hq;

/// <summary>
/// W13b: the campaign's HQ defence on a real server in process (loopback network, manual clock) on the composed HQ map: a run in Travel to the HQ lands on it (the run's own Deploy, the HQ layout), a win after the 8 minutes resets the fronts
/// to node 2 with no roll and the party stays on this map's ship, a loss ends the run. Serial with the other stage tests (environment variables, the run folder).
/// </summary>
[Collection("WarfrontStage")]
public class WarfrontHqStageTests : IDisposable
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
    static readonly SimConfig Config = new() { Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou | ContentExtras.Warfront };
    static MatchRules Rules => new() { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-hq-" + Guid.NewGuid().ToString("N"));

    readonly Xunit.Abstractions.ITestOutputHelper output;

    public WarfrontHqStageTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(_dir);
        WarfrontRunFiles.DirOverride = _dir;
        WarfrontRunFiles.NavCacheOverride = null;   // the private cache: the HQ graph is built once (26 s) and read back
    }

    public void Dispose()
    {
        WarfrontRunFiles.DirOverride = null;
        WarfrontRunFiles.NavCacheOverride = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A run in Travel to the HQ (every lane advanced and at node 0: the HQ is mandatory), the server on wf_hq with one client.</summary>
    Harness Start(out GameServer server)
    {
        var seed = new WarfrontCampaign(5);
        seed.Embark();
        var node = System.Text.Json.Nodes.JsonNode.Parse(seed.ToJson())!.AsObject();
        node["HqMandatory"] = true;
        node["Fronts"] = new System.Text.Json.Nodes.JsonArray(0, 0, 0);
        node["Advanced"] = new System.Text.Json.Nodes.JsonArray(true, true, true);
        node["Phase"] = "Travel";
        node["CurrentTarget"] = new System.Text.Json.Nodes.JsonObject { ["Kind"] = "Hq", ["Lane"] = -1 };
        File.WriteAllText(Path.Combine(_dir, "run.json"), node.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "stage.json"), System.Text.Json.JsonSerializer.Serialize(new { Map = "wf_hq", Expected = Array.Empty<ulong>(), Ship = "wf_b1" }));
        var loaded = WarfrontMaps.Load(WarfrontMaps.HqName, out _)!;
        var map = GameServer.TfwGameMap(loaded.Tfw!, "wf_hq");
        var h = new Harness(LinkConditions.Perfect, 5, 1, Config, new ServerOptions { Match = Rules }, map: map, clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        server = h.Server;
        h.Run(TimeSpan.FromSeconds(2));
        h.Clients[0].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: 111, Name: "Alpha")));
        return h;
    }

    [Fact]
    public void The_hq_target_lands_on_the_hq_map_and_surviving_eight_minutes_resets_the_fronts_with_no_roll_and_stays_on_the_ship()
    {
        var h = Start(out var server);
        h.Run(TimeSpan.FromSeconds(12));
        var run = server.World.Tfw!;
        var battle = Assert.IsType<TfwBattle>(run.Battle);
        Assert.True(battle.WarfrontHq);
        Assert.Equal(WarfrontPhase.Battle, server.WarfrontCampaignForTests!.Phase);
        server.World.Musou!.Frozen = true;
        h.Run(TimeSpan.FromSeconds(WarfrontHqBattleRulesSeconds + 5));
        var c = server.WarfrontCampaignForTests!;
        Assert.Null(run.Battle);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
        Assert.Equal([2, 2, 2], [c.Front(0), c.Front(1), c.Front(2)]);
        Assert.False(c.HqMandatory);
        Assert.Empty(c.Rolls);   // the HQ resolves with no roll
        Assert.Equal(WarfrontCampaignOutcomeHq, c.History.Last().Target.Kind);
    }

    const float WarfrontHqBattleRulesSeconds = SmSim.Extras.Warfront.Hq.WarfrontHqBattleRules.SurviveSeconds;
    const WarfrontTargetKind WarfrontCampaignOutcomeHq = WarfrontTargetKind.Hq;
}
