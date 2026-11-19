using System.Text.Json.Nodes;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;

namespace SmNet.Tests.Extras.Warfront.Dedicated;

/// <summary>
/// Warfront dedicated server S2: the default deployment for an idle leader (90 s), the new run (the leader's, or 2 minutes after the results, or at once when nobody is connected) and the battle left with nobody,
/// all only under <c>--warfront-dedicated</c>. In process on a loopback network and a manual clock, synthetic run folder, nothing binds a real socket. Serial with the stage tests (the run folder is a process-wide override).
/// </summary>
[Collection("WarfrontStage")]
public class WarfrontDedicatedOperationTests : IDisposable
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou | ContentExtras.Warfront,
    };
    static MatchRules Rules => new() { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-dedop-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, string?> _env = new();

    public WarfrontDedicatedOperationTests()
    {
        Directory.CreateDirectory(_dir);
        WarfrontRunFiles.DirOverride = _dir;
        WarfrontRunFiles.NavCacheOverride = Path.Combine(_dir, "nav");
        GameServer.WarfrontPrebuild = (map, _, progress) => { progress?.Invoke(30, "stub"); progress?.Invoke(100, "ready"); return map + " (stub)"; };
        WarfrontRunFiles.Dedicated = true;
    }

    public void Dispose()
    {
        WarfrontRunFiles.Dedicated = false;
        WarfrontRunFiles.DirOverride = null;
        WarfrontRunFiles.NavCacheOverride = null;
        GameServer.WarfrontPrebuild = SmContent.Extras.Warfront.WarfrontMaps.Prebuild;
        foreach (var (k, v) in _env) Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    void Env(string name, string? value)
    {
        if (!_env.ContainsKey(name)) _env[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    sealed class Stage
    {
        public Harness H = null!;
        public GameServer Server => H.Server;
        public WarfrontCampaign Campaign => Server.WarfrontCampaignForTests!;
        public void Tick(double seconds) => H.Run(TimeSpan.FromSeconds(seconds), after: () => { foreach (var c in H.Clients) c.TakeExtras(); });
        public void Send(int client, WarfrontClientCommand cmd) => Assert.True(H.Clients[client].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(cmd)));
        public void Identify(int client, ulong key, string name) => Send(client, new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: key, Name: name));
    }

    static ClientOptions Client()
    {
        var map = GameServer.TfwGameMap(TfwTestArena.Data, "wf_b1");
        return new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) };
    }

    static Stage StartStage(int clients)
    {
        var map = GameServer.TfwGameMap(TfwTestArena.Data, "wf_b1");
        var stage = new Stage { H = new Harness(LinkConditions.Perfect, 5, clients, Config, new ServerOptions { Match = Rules }, map: map, clientOptions: Client()) };
        stage.Tick(2);
        return stage;
    }

    /// <summary>A saved campaign on the ship with the given fronts (lane A, B, C), as a stage process would load it.</summary>
    WarfrontCampaign Seed(int[] fronts, bool hq = false, string phase = "Ship")
    {
        var seed = new WarfrontCampaign(9);
        seed.Embark();
        var node = JsonNode.Parse(seed.ToJson())!.AsObject();
        node["Fronts"] = new JsonArray(fronts.Select(f => (JsonNode)f).ToArray());
        node["HqMandatory"] = hq;
        node["Phase"] = phase;
        string json = node.ToJsonString();
        File.WriteAllText(Path.Combine(_dir, "run.json"), json);
        return WarfrontSave.FromJson(json);
    }

    // ---- the default target -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { 1, 1, 1 }, false, WarfrontTargetKind.Node, 0)]    // a tie: lane A
    [InlineData(new[] { 2, 1, 1 }, false, WarfrontTargetKind.Node, 1)]    // the lowest front, ties in order A, B, C
    [InlineData(new[] { 3, 2, 3 }, false, WarfrontTargetKind.Node, 1)]
    [InlineData(new[] { 3, 3, 2 }, false, WarfrontTargetKind.Node, 2)]
    [InlineData(new[] { 4, 3, 3 }, false, WarfrontTargetKind.Node, 1)]    // a cleared lane is not a node target while another can still be fought
    [InlineData(new[] { 4, 4, 4 }, false, WarfrontTargetKind.Fortress, 0)] // only the fortress is left: the first cleared lane
    [InlineData(new[] { 4, 4, 0 }, false, WarfrontTargetKind.Fortress, 0)]
    [InlineData(new[] { 2, 1, 3 }, true, WarfrontTargetKind.Hq, -1)]       // the HQ defence first when it is mandatory
    public void Default_target_choice(int[] fronts, bool hq, WarfrontTargetKind kind, int lane)
    {
        var c = Seed(fronts, hq);
        var t = GameServer.WarfrontDefaultTarget(c);
        Assert.NotNull(t);
        Assert.Equal(kind, t!.Value.Kind);
        if (kind != WarfrontTargetKind.Hq) Assert.Equal(lane, t.Value.Lane);
        Assert.Contains(t.Value, c.AvailableTargets());
    }

    [Fact]
    public void No_default_target_off_the_ship()
    {
        var c = Seed([1, 1, 1], phase: "Battle");
        Assert.Null(GameServer.WarfrontDefaultTarget(c));
    }

    // ---- the 90 s deploy fallback -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_idle_leader_is_deployed_at_90_seconds_and_not_before()
    {
        Env("SM_WF_RESOLVE", null);
        Seed([2, 1, 2]);   // lane B is the lowest: node 1 of lane B is this map (wf_b1), so it lands without a restart
        var s = StartStage(1);
        s.Identify(0, 111, "Alpha");
        s.Tick(80);
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);
        s.Tick(8);   // about 88 s on the ship
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);
        s.Tick(10);
        Assert.NotEqual(WarfrontPhase.Ship, s.Campaign.Phase);
        Assert.Equal(WarfrontTarget.Node(1), s.Campaign.CurrentTarget);
        Assert.Equal(111UL, s.Campaign.Leader);
    }

    [Fact]
    public void The_fallback_never_fires_with_nobody_connected_and_the_joiner_gets_the_full_time()
    {
        Seed([2, 1, 2]);
        var s = StartStage(0);
        s.Tick(200);
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);
        s.H.AddClient(nonce: 5, options: Client());
        s.Tick(3);
        s.Identify(0, 222, "Beta");
        s.Tick(80);
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);   // 200 s of an empty ship do not count for the one who joins
        s.Tick(20);
        Assert.NotEqual(WarfrontPhase.Ship, s.Campaign.Phase);
    }

    [Fact]
    public void A_leader_who_deployed_starts_a_fresh_clock_when_back_on_the_ship()
    {
        Env("SM_WF_ROLLS", "WW");
        Env("SM_WF_RESOLVE", "win");
        Seed([2, 1, 2]);
        var s = StartStage(1);
        s.Identify(0, 111, "Alpha");
        s.Tick(30);
        s.Send(0, new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Node(1)));
        s.Tick(20);   // travel, landing, the aid's five seconds: back on the ship
        Assert.Equal(1, s.Campaign.Deployments);
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);
        s.Tick(70);
        Assert.Equal(1, s.Campaign.Deployments);   // the leader's own deploy did not leave a running clock
        s.Tick(40);
        Assert.Equal(2, s.Campaign.Deployments);   // the server's own, 90 s after the return
    }

    [Fact]
    public void Player_hosted_mode_has_no_fallback()
    {
        WarfrontRunFiles.Dedicated = false;
        Seed([2, 1, 2]);
        var s = StartStage(1);
        s.Identify(0, 111, "Alpha");
        s.Tick(200);
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);
        Assert.Equal(0, s.Campaign.Deployments);
    }

    // ---- the new run -------------------------------------------------------------------------------------------------------------------------------

    void SeedOver()
    {
        var seed = new WarfrontCampaign(9);
        seed.Embark();
        var node = JsonNode.Parse(seed.ToJson())!.AsObject();
        node["Phase"] = "Won";
        File.WriteAllText(Path.Combine(_dir, "run.json"), node.ToJsonString());
    }

    bool RunFileExists => File.Exists(Path.Combine(_dir, "run.json"));

    [Fact]
    public void The_new_run_starts_two_minutes_after_the_results()
    {
        SeedOver();
        var s = StartStage(1);   // the run is finished on load: archived, run.json removed
        s.Identify(0, 77, "Alpha");
        s.Tick(100);
        Assert.False(RunFileExists);
        s.Tick(25);
        Assert.True(RunFileExists);
        Assert.Equal(WarfrontPhase.Ship, WarfrontSave.Load(_dir)!.Phase);
        s.Tick(5);
        Assert.True(s.Server.RestartDue(out int code));
        Assert.Equal(ServerCommandLine.WarfrontStageExit, code);
    }

    [Fact]
    public void The_leader_can_start_the_new_run_early()
    {
        SeedOver();
        var s = StartStage(1);
        s.Identify(0, 77, "Alpha");
        s.Tick(10);
        Assert.False(RunFileExists);
        s.Send(0, new WarfrontClientCommand(WarfrontClientCommandKind.NewRun));
        s.Tick(2);
        Assert.True(RunFileExists);
    }

    [Fact]
    public void The_new_run_starts_at_once_when_nobody_is_connected()
    {
        SeedOver();
        var s = StartStage(0);
        Assert.True(RunFileExists);
        s.Tick(3);
        Assert.True(s.Server.RestartDue(out _));
    }

    [Fact]
    public void Player_hosted_mode_keeps_the_results_until_the_leader_acts()
    {
        WarfrontRunFiles.Dedicated = false;
        SeedOver();
        var s = StartStage(1);
        s.Identify(0, 77, "Alpha");
        s.Tick(200);
        Assert.False(RunFileExists);
        Assert.False(s.Server.RestartDue(out _));
    }

    [Fact]
    public void Player_hosted_mode_with_nobody_connected_does_not_start_a_new_run()
    {
        WarfrontRunFiles.Dedicated = false;
        SeedOver();
        var s = StartStage(0);
        s.Tick(5);
        Assert.False(RunFileExists);
    }

    // ---- a server that empties -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_battle_left_with_nobody_is_no_result_back_on_the_ship_and_an_empty_ship_does_nothing()
    {
        Env("SM_WF_ROLLS", "LL");
        Env("SM_WF_RESOLVE", null);
        Seed([2, 1, 2]);
        var s = StartStage(1);
        s.Identify(0, 111, "Alpha");
        s.Tick(1);
        s.Send(0, new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Node(1)));
        s.Tick(12);
        Assert.Equal(WarfrontPhase.Battle, s.Campaign.Phase);
        s.H.Clients[0].Leave();
        s.Tick(10);
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);
        Assert.Equal(0, s.Campaign.Deployments);   // nothing rolled
        Assert.Equal(2, s.Campaign.Front(1));
        Assert.True(RunFileExists);                // the run is still saved
        s.Tick(300);                               // an empty ship never deploys and never starts a new run
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);
        Assert.Equal(0, s.Campaign.Deployments);
    }
}
