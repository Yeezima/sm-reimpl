using SmNet.Extras.Musou.Campaign;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;

namespace SmNet.Tests.Extras.Warfront.Stage;

/// <summary>
/// W5a: the stage flow of a Warfront server, in process (a loopback network, a manual clock, a synthetic arena named as a lane slot, the map preparation stubbed): identity, the leader-only deploy, the
/// preparation and Go, the arrival in the next stage process, the landing, the battle end under SM_WF_RESOLVE, the rolls, and the resume after a kill on the ship, in Travel and in Battle. A "stage change" is a
/// new server on the next map reading the same run folder, as the supervisor starts one. Serial: the test aids are environment variables and the run folder is a process-wide override.
/// </summary>
[Collection("WarfrontStage")]
public class WarfrontStageTests : IDisposable
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

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-stage-" + Guid.NewGuid().ToString("N"));
    readonly List<string> _prebuilt = new();
    readonly Dictionary<string, string?> _env = new();

    public WarfrontStageTests()
    {
        Directory.CreateDirectory(_dir);
        WarfrontRunFiles.DirOverride = _dir;
        WarfrontRunFiles.NavCacheOverride = Path.Combine(_dir, "nav");
        GameServer.WarfrontPrebuild = (map, _, progress) => { _prebuilt.Add(map); progress?.Invoke(30, "stub"); progress?.Invoke(100, "ready"); return map + " (stub)"; };
    }

    public void Dispose()
    {
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

    /// <summary>One stage process: a server on a lane slot's name over the synthetic arena, with its clients.</summary>
    sealed class Stage
    {
        public Harness H = null!;
        public GameServer Server => H.Server;
        public WarfrontCampaign Campaign => Server.WarfrontCampaignForTests!;
        public readonly List<(byte Tag, byte[] Payload)> Heard = new();

        public void Tick(double seconds)
        {
            H.Run(TimeSpan.FromSeconds(seconds), after: Drain);
        }

        void Drain()
        {
            foreach (var c in H.Clients)
                foreach (var s in c.TakeExtras()) if (s.Tag is >= 40 and <= 46) Heard.Add((s.Tag, s.Payload));
        }

        public void Send(int client, WarfrontClientCommand cmd) => Assert.True(H.Clients[client].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(cmd)));
        public void Identify(int client, ulong key, string name) => Send(client, new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: key, Name: name));
    }

    static Stage StartStage(string mapName, int clients)
    {
        var map = GameServer.TfwGameMap(TfwTestArena.Data, mapName);
        var stage = new Stage
        {
            H = new Harness(LinkConditions.Perfect, 5, clients, Config, new ServerOptions { Match = Rules }, map: map,
                clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) }),
        };
        stage.Tick(2);
        return stage;
    }

    static string RunJson(string dir) => File.ReadAllText(Path.Combine(dir, "run.json"));

    [Fact]
    public void Identity_leader_and_refusals()
    {
        var s = StartStage("wf_b1", 2);
        Assert.NotNull(s.Server.WarfrontCampaignForTests);
        Assert.All(s.H.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        s.Identify(0, 111, "Alpha");
        s.Identify(1, 222, "Beta");
        s.Tick(1);
        var c = s.Campaign;
        Assert.Equal(111UL, c.Leader);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);

        s.Send(1, new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Node(0)));   // a non-leader
        s.Send(0, new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Fortress(0)));   // not available
        s.Tick(1);
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
        Assert.False(s.Server.WarfrontPreparingForTests);
        Assert.Empty(_prebuilt);

        // the Hub reached each client with its own viewer handle and no other key
        Assert.Contains(s.Heard, h => h.Tag == WarfrontWire.HubTag);
        foreach (var (tag, payload) in s.Heard.Where(h => h.Tag == WarfrontWire.HubTag))
        {
            Assert.True(WarfrontWire.TryDecode(payload, out WarfrontHubWire hub));
            Assert.True(hub.ViewerKey is 111UL or 222UL);
            ulong other = hub.ViewerKey == 111UL ? 222UL : 111UL;
            Assert.DoesNotContain(hub.Players, p => p.Key == other);
        }
    }

    [Fact]
    public void Same_map_deploy_lands_without_a_restart_then_resolves_and_rolls()
    {
        Env("SM_WF_ROLLS", "LW");
        Env("SM_WF_RESOLVE", "win");
        var s = StartStage("wf_b1", 1);
        s.Identify(0, 111, "Alpha");
        s.Tick(1);
        s.Send(0, new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Node(1)));   // lane B node 1: this map
        s.Tick(3);
        Assert.Empty(_prebuilt);
        Assert.Equal(WarfrontPhase.Travel, s.Campaign.Phase);
        s.Tick(12);   // the run's own travel, the landing, then the aid's five seconds
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);
        Assert.Equal(2, s.Campaign.Front(1));
        Assert.Equal(1, s.Campaign.Deployments);
        Assert.Equal(2, s.Campaign.Rolls.Count);   // the two other lanes
        Assert.Contains("\"Deployments\":1", RunJson(_dir));
        Assert.False(s.Server.RestartDue(out _));
    }

    [Fact]
    public void Deploy_to_another_map_prepares_says_go_and_the_next_stage_resumes_in_travel_and_lands()
    {
        Env("SM_WF_RESOLVE", "win");
        var first = StartStage("wf_b1", 2);
        first.Identify(0, 111, "Alpha");
        first.Identify(1, 222, "Beta");
        first.Tick(1);
        first.Send(0, new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Node(0)));   // lane A node 1: wf_a1
        first.Tick(1);
        Assert.Equal(new[] { "wf_a1" }, _prebuilt);
        first.Tick(1);
        Assert.Equal(WarfrontPhase.Travel, first.Campaign.Phase);
        Assert.Contains(first.Heard, h => h.Tag == WarfrontWire.StageTag && WarfrontWire.TryDecode(h.Payload, out WarfrontStageWire g) && g.State == WarfrontStageState.Go && g.Map == "wf_a1");
        Assert.False(first.Server.RestartDue(out _));
        first.Tick(2.5);
        Assert.True(first.Server.RestartDue(out int code));
        Assert.Equal(ServerCommandLine.WarfrontStageExit, code);
        Assert.Contains("wf_a1", File.ReadAllText(Path.Combine(_dir, "stage.json")));
        string saved = RunJson(_dir);

        // the supervisor starts the next stage: Travel is kept, both players rejoin and identify, the stage lands
        var next = StartStage("wf_a1", 2);
        Assert.Equal(WarfrontPhase.Travel, next.Campaign.Phase);
        next.Identify(0, 222, "Beta");
        next.Identify(1, 111, "Alpha");
        next.Tick(2);
        Assert.Contains(next.Heard, h => h.Tag == WarfrontWire.StageTag && WarfrontWire.TryDecode(h.Payload, out WarfrontStageWire g) && g.State == WarfrontStageState.Arrived && g.Arrived == 2);
        next.Tick(8);   // travel, landing
        Assert.Equal(WarfrontPhase.Battle, next.Campaign.Phase);
        Assert.Contains("\"Phase\":\"Battle\"", RunJson(_dir));
        next.Tick(8);   // the aid ends the battle
        Assert.Equal(WarfrontPhase.Ship, next.Campaign.Phase);
        Assert.Equal(2, next.Campaign.Front(0));
        Assert.Equal(1, next.Campaign.Deployments);
        Assert.NotEqual(saved, RunJson(_dir));
    }

    [Fact]
    public void A_kill_in_travel_resumes_the_stage_and_a_kill_in_battle_returns_to_the_ship_with_nothing_rolled()
    {
        Env("SM_WF_RESOLVE", null);
        var first = StartStage("wf_b1", 1);
        first.Identify(0, 111, "Alpha");
        first.Tick(1);
        first.Send(0, new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Node(0)));
        first.Tick(3);
        string travelState = RunJson(_dir);
        Assert.Equal(WarfrontPhase.Travel, first.Campaign.Phase);

        // killed in Travel: the next start is the target's stage, the same state on disk
        var resumed = StartStage("wf_a1", 1);
        Assert.Equal(WarfrontPhase.Travel, resumed.Campaign.Phase);
        Assert.Equal(WarfrontTarget.Node(0), resumed.Campaign.CurrentTarget);
        Assert.Equal(travelState, RunJson(_dir));
        resumed.Identify(0, 111, "Alpha");
        resumed.Tick(10);
        Assert.Equal(WarfrontPhase.Battle, resumed.Campaign.Phase);
        string battleSave = RunJson(_dir);
        Assert.Contains("\"Phase\":\"Battle\"", battleSave);

        // killed in Battle: the next start is on the ship before that deployment; nothing rolled or granted
        var after = StartStage("wf_a1", 0);
        var c = after.Campaign;
        Assert.Equal(WarfrontPhase.Ship, c.Phase);
        Assert.Null(c.CurrentTarget);
        Assert.Equal(0, c.Deployments);
        Assert.Empty(c.Rolls);
        Assert.Equal(1, c.Front(0));
        Assert.Equal(c.StateHash(), WarfrontSave.Load(_dir)!.StateHash());
    }

    [Fact]
    public void The_end_of_a_run_is_archived_without_keys_and_the_record_reaches_a_later_joiner()
    {
        const ulong Key = 0xABCDEF0123456789UL;
        Env("SM_WF_RESOLVE", "wipe");
        // a run whose HQ defence is under way (Travel to the HQ): written as the campaign's own save, so the stage that starts loads it
        var seed = new WarfrontCampaign(5);
        seed.Embark();
        var node = System.Text.Json.Nodes.JsonNode.Parse(seed.ToJson())!.AsObject();
        node["HqMandatory"] = true;
        node["Phase"] = "Travel";
        node["CurrentTarget"] = new System.Text.Json.Nodes.JsonObject { ["Kind"] = "Hq", ["Lane"] = -1 };
        File.WriteAllText(Path.Combine(_dir, "run.json"), node.ToJsonString());
        File.WriteAllText(Path.Combine(_dir, "stage.json"), System.Text.Json.JsonSerializer.Serialize(new { Map = "wf_hq", Expected = Array.Empty<ulong>(), Ship = "wf_b1" }));

        // the HQ map has no hub and no battle rules yet: a plain map without the battlefield
        var arena = TfwTestArena.Data;
        var map = new GameMap("wf_hq", arena.Collision, arena.Spawns, arena.Hash);
        var h = new Harness(LinkConditions.Perfect, 5, 1, Config, new ServerOptions { Match = Rules }, map: map, clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        var s = new Stage { H = h };
        s.Tick(2);
        s.Identify(0, Key, "Alpha");
        s.Tick(1);
        Assert.Equal(WarfrontPhase.Battle, s.Campaign.Phase);   // nobody else was expected: the stage lands at once
        s.Tick(8);   // the aid ends the battle five seconds after it began
        Assert.Equal(WarfrontPhase.Lost, s.Campaign.Phase);
        Assert.False(File.Exists(Path.Combine(_dir, "run.json")));
        var records = Directory.GetFiles(Path.Combine(_dir, "records"));
        Assert.NotEmpty(records);
        foreach (var file in records)
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain(Key.ToString(), text);
            Assert.DoesNotContain(Key.ToString("X"), text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains(s.Heard, x => x.Tag == WarfrontWire.ResultsTag);

        // a client that joins after the end still receives the record
        var late = h.AddClient(nonce: 77, options: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        var re = new SmNet.Extras.Warfront.Results.WarfrontResultsWire.Reassembler();
        byte[]? whole = null;
        for (int i = 0; i < 8000 && whole is null; i++)
        {
            h.Step();
            foreach (var sec in late.TakeExtras()) if (sec.Tag == WarfrontWire.ResultsTag) whole = re.Add(sec.Payload) ?? whole;
        }
        Assert.NotNull(whole);
        var record = SmNet.Extras.Warfront.Results.WarfrontResultsWire.Decode(whole!);
        Assert.Equal("lost", record.Result);
    }

    [Fact]
    public void A_human_stays_held_until_it_identifies_but_a_bot_gets_a_key_of_its_own_after_five_seconds()
    {
        var map = GameServer.TfwGameMap(TfwTestArena.Data, "wf_b1");
        var h = new Harness(LinkConditions.Perfect, 5, 0, Config, new ServerOptions { Match = Rules }, map: map);
        var loader = new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) };
        h.AddClient(options: loader, nonce: 1);
        h.AddClient(options: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash), Bot = true }, nonce: 2);
        var s = new Stage { H = h };
        s.Tick(9);
        Assert.Single(s.Campaign.JoinOrder);   // the bot only
        // the late human identifies after the wait and is admitted with its own key
        s.Identify(0, 555, "Late");
        s.Tick(1);
        Assert.Equal(2, s.Campaign.JoinOrder.Count);
        Assert.Contains(555UL, s.Campaign.JoinOrder);
    }

    [Fact]
    public void A_refused_identify_is_answered_and_late_join_waits_for_identity()
    {
        var s = StartStage("wf_b1", 2);
        s.Identify(0, 111, "Alpha");
        s.Tick(1);
        s.Identify(1, 111, "Copy");   // the same key from a second connection
        s.Tick(1);
        Assert.Single(s.Campaign.JoinOrder);
        Assert.Contains(s.Heard, x => x.Tag == WarfrontWire.StageTag && WarfrontWire.TryDecode(x.Payload, out WarfrontStageWire g) && g.Status.Contains("already connected"));
        var allowed = s.Server.World.Tfw!.Warfront!.LateJoinAllowed!;
        int identified = s.Server.WarfrontPlayerOfForTests(111)!.Value;
        int other = s.Server.Players.First(id => id != identified);
        Assert.True(allowed(identified));
        Assert.False(allowed(other));
    }

    [Fact]
    public void A_stage_whose_supervisor_is_gone_saves_and_leaves_with_code_zero()
    {
        Env("SM_WF_SUPERVISOR_PID", "2147483000");   // no such process
        var s = StartStage("wf_b1", 1);
        s.Tick(3);
        Assert.True(s.Server.RestartDue(out int code));
        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(_dir, "run.json")));
    }

    [Fact]
    public void A_run_that_is_over_when_a_stage_starts_is_finished_once_and_a_leave_after_the_end_writes_nothing_back()
    {
        var seed = new WarfrontCampaign(9);
        seed.Embark();
        var node = System.Text.Json.Nodes.JsonNode.Parse(seed.ToJson())!.AsObject();
        node["Phase"] = "Won";
        node["PlaySeconds"] = 321.5;
        File.WriteAllText(Path.Combine(_dir, "run.json"), node.ToJsonString());
        var s = StartStage("wf_b1", 1);   // finishes the run on load: the record is archived and the run file removed
        Assert.False(File.Exists(Path.Combine(_dir, "run.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "records"), "*.json").Where(f => !f.EndsWith(".results.json")));
        s.Identify(0, 77, "Alpha");
        s.Tick(7);   // the record goes to the client
        Assert.Contains(s.Heard, x => x.Tag == WarfrontWire.ResultsTag);
        var chunks = new SmNet.Extras.Warfront.Results.WarfrontResultsWire.Reassembler();
        byte[]? whole = null;
        foreach (var x in s.Heard.Where(x => x.Tag == WarfrontWire.ResultsTag)) whole = chunks.Add(x.Payload) ?? whole;
        Assert.Equal(321, SmNet.Extras.Warfront.Results.WarfrontResultsWire.Decode(whole!).DurationSeconds);   // the saved play time, not this process's uptime
        // the leader starts a new run: an empty run on disk and a stage change to it
        s.Send(0, new WarfrontClientCommand(WarfrontClientCommandKind.NewRun));
        s.Tick(1);
        Assert.True(File.Exists(Path.Combine(_dir, "run.json")));
        Assert.Equal(WarfrontPhase.Ship, WarfrontSave.Load(_dir)!.Phase);
        s.Tick(3);
        Assert.True(s.Server.RestartDue(out int newRunCode));
        Assert.Equal(ServerCommandLine.WarfrontStageExit, newRunCode);
        File.Delete(Path.Combine(_dir, "run.json"));
        s.H.Clients[0].Leave();
        s.Tick(2);
        Assert.False(File.Exists(Path.Combine(_dir, "run.json")));   // nothing writes the old run back
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "records"), "*.json").Where(f => !f.EndsWith(".results.json")));
    }

    [Fact]
    public void Play_time_counts_only_with_someone_connected_and_is_saved()
    {
        var c = new WarfrontCampaign(1);
        c.Embark();
        Assert.False(c.AddPlaySeconds(1));   // nobody connected
        c.Join(5, "A", out _);
        Assert.True(c.AddPlaySeconds(2.5));
        Assert.Equal(2.5, c.PlaySeconds);
        var back = WarfrontSave.FromJson(c.ToJson());
        Assert.Equal(2.5, back.PlaySeconds);
        var old = System.Text.Json.Nodes.JsonNode.Parse(c.ToJson())!.AsObject();
        old["Version"] = 2; old.Remove("PlaySeconds");
        Assert.Equal(0, WarfrontSave.FromJson(old.ToJsonString()).PlaySeconds);   // an older save loads with 0
    }

    [Fact]
    public void The_squad_wiring_and_the_co_op_rules_are_built_once_and_the_squads_are_saved()
    {
        var s = StartStage("wf_b1", 1);
        var rules = s.Server.World.Tfw!.Warfront!;
        Assert.NotNull(rules.SquadStep);   // the wiring's seam, subscribed at construction
        var progression = rules.Progression;
        s.Identify(0, 4242, "Alpha");
        s.Tick(2);
        Assert.Same(progression, rules.Progression);   // never replaced
        Assert.True(File.Exists(Path.Combine(_dir, "squads.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "run.json")));
        Assert.Contains(s.Heard, h => h.Tag == 46);   // the co-op section reaches the identified client
        // a restart reads the squads back
        var again = StartStage("wf_b1", 0);
        Assert.NotNull(again.Server.World.Tfw!.Warfront!.SquadStep);
    }
}
