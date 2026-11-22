using SmNet.Extras.Musou.Campaign;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;

namespace SmNet.Tests.Extras.Warfront.Wiring;

/// <summary>
/// Codebase fix 1 on the server, in process (a loopback network, a manual clock, a synthetic arena named as a lane slot): the Progress command and its merge, the ProgressSave section (tag 49), the
/// NoSave refusals and the write retry. Serial with the other stage tests: the test aids are environment variables and the run folder is a process-wide override. Temp folders only.
/// </summary>
[Collection("WarfrontStage")]
public class WarfrontProgressServerTests : IDisposable
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

    const ulong KeyA = 111, KeyB = 222;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-progress-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, string?> _env = new();

    public WarfrontProgressServerTests()
    {
        Directory.CreateDirectory(_dir);
        WarfrontRunFiles.DirOverride = _dir;
        WarfrontRunFiles.NavCacheOverride = Path.Combine(_dir, "nav");
        GameServer.WarfrontPrebuild = (map, _, progress) => { progress?.Invoke(30, "stub"); progress?.Invoke(100, "ready"); return map + " (stub)"; };
    }

    public void Dispose()
    {
        WarfrontRunFiles.DirOverride = null;
        WarfrontRunFiles.NavCacheOverride = null;
        GameServer.WarfrontLoaderIoForTests = null;
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
        public readonly List<(byte Tag, byte[] Payload)> Heard = new();
        byte _transfer;

        public void Tick(double seconds) => H.Run(TimeSpan.FromSeconds(seconds), after: Drain);

        void Drain()
        {
            foreach (var c in H.Clients)
                foreach (var s in c.TakeExtras())
                    if (s.Tag is >= 40 and <= 49) Heard.Add((s.Tag, s.Payload));
        }

        public void Identify(int client, ulong key, string name) =>
            Assert.True(H.Clients[client].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: key, Name: name))));

        public void SendProgress(int client, WarfrontProgressFile? file, byte flags = 0)
        {
            var record = WarfrontProgressSaveWire.Encode(file, flags);
            foreach (var chunk in WarfrontProgressSaveWire.CommandChunks(record, ++_transfer))
                Assert.True(H.Clients[client].SendExtrasCommand(WarfrontWire.CommandTag, chunk));
        }

        public void SendNewRun(int client) =>
            Assert.True(H.Clients[client].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.NewRun))));

        public List<WarfrontProgressRecord> Saves() =>
            Heard.Where(h => h.Tag == WarfrontProgressSaveWire.EnvelopeTag).Select(h => { Assert.True(WarfrontProgressSaveWire.TryDecode(h.Payload, out var r)); return r; }).ToList();
    }

    static Stage StartStage(int clients)
    {
        var map = GameServer.TfwGameMap(TfwTestArena.Data, "wf_b1");
        var stage = new Stage
        {
            H = new Harness(LinkConditions.Perfect, 5, clients, Config, new ServerOptions { Match = Rules }, map: map,
                clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) }),
        };
        stage.Tick(2);
        return stage;
    }

    static WarfrontProgressFile File_(long xp, int picks, int[]? cards = null, params (string Class, string Name)[] extras) => new()
    {
        Xp = xp, PicksTaken = picks, Cards = (cards ?? new int[picks]).ToList(),
        ExtraRetainers = extras.Select(e => new WarfrontExtraRetainer { ClassName = e.Class, Name = e.Name }).ToList(),
    };

    static readonly int Speed = (int)WarfrontCard.AttackSpeed;
    static readonly int Retainer = (int)WarfrontCard.Retainer;

    // ---- the Progress command ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Player_file_higher_than_the_run_wins_whole_and_is_acknowledged()
    {
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        Assert.Empty(s.Saves());   // a client that sent no record gets no ProgressSave
        s.SendProgress(0, File_(8000, 3, [Speed, Speed, Retainer], ("Tactical", "Cassian")));
        s.Tick(1);
        var p = s.Campaign.Player(KeyA)!;
        Assert.Equal(8000, p.Xp);
        Assert.Equal(4, p.Level);
        Assert.Equal(3, p.PicksTaken);
        Assert.Equal([Speed, Speed, Retainer], p.Cards);
        Assert.Equal("Cassian", Assert.Single(p.Squad).Name);
        var ack = Assert.Single(s.Saves());   // the first send is the acknowledgement
        Assert.Equal(8000, ack.State!.Xp);
        Assert.Equal("Cassian", Assert.Single(ack.State.ExtraRetainers).Name);
    }

    [Fact]
    public void Run_higher_than_the_player_file_keeps_the_run_copy_and_sends_it_back()
    {
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.Campaign.ApplyProgress(KeyA, File_(6500, 3, [Speed, Speed, Speed]));
        s.SendProgress(0, File_(1000, 0));
        s.Tick(1);
        Assert.Equal(6500, s.Campaign.Player(KeyA)!.Xp);
        Assert.Equal(3, s.Campaign.Player(KeyA)!.PicksTaken);
        Assert.Equal(6500, s.Saves().Last().State!.Xp);
    }

    [Fact]
    public void A_tie_goes_to_the_run_copy_cards_included()
    {
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.Campaign.ApplyProgress(KeyA, File_(6500, 2, [Speed, Speed]));
        s.SendProgress(0, File_(6500, 2, [(int)WarfrontCard.MaxArmor, (int)WarfrontCard.MaxArmor]));
        s.Tick(1);
        Assert.Equal([Speed, Speed], s.Campaign.Player(KeyA)!.Cards);
    }

    [Fact]
    public void Absent_file_keeps_the_run_copy_of_a_returning_key_and_a_new_key_starts_at_level_one()
    {
        var s = StartStage(2);
        s.Identify(0, KeyA, "Alpha");
        s.Identify(1, KeyB, "Beta");
        s.Tick(1);
        s.Campaign.ApplyProgress(KeyA, File_(4000, 2, [Speed, Speed]));   // a v5 run: this key already has progress in run.json, but no file on its PC
        s.SendProgress(0, null);
        s.SendProgress(1, null);
        s.Tick(1);
        Assert.Equal(4000, s.Campaign.Player(KeyA)!.Xp);
        Assert.Equal(1, s.Campaign.Player(KeyB)!.Level);   // no median grant any more
        Assert.Equal(0, s.Campaign.Player(KeyB)!.Xp);
        // the run's copy goes back to the client so it writes its first file
        Assert.Equal(4000, s.Saves().Last(r => r.State!.Xp > 0).State!.Xp);
    }

    [Fact]
    public void Sanitize_runs_on_arrival()
    {
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.SendProgress(0, File_(uint.MaxValue, 200, Enumerable.Repeat(Speed, 49).ToArray()));
        s.Tick(1);
        var p = s.Campaign.Player(KeyA)!;
        Assert.Equal(WarfrontProgressFile.MaxXp, p.Xp);
        Assert.Equal(50, p.Level);
        Assert.True(p.PicksTaken <= 49);
        Assert.Equal(p.PicksTaken, p.Cards.Count);
    }

    [Fact]
    public void Reset_is_honoured_in_the_ship_phase_and_keeps_the_run()
    {
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.Campaign.ApplyProgress(KeyA, File_(8000, 3, [Speed, Speed, Retainer], ("Tactical", "Cassian")));
        s.SendProgress(0, WarfrontProgressFile.Fresh(""), WarfrontProgressSaveWire.FlagReset);
        s.Tick(1);
        var p = s.Campaign.Player(KeyA)!;
        Assert.Equal(0, p.Xp);
        Assert.Equal(1, p.Level);
        Assert.Empty(p.Cards);
        Assert.Empty(p.Squad);
        Assert.NotNull(s.Campaign.Stats);   // the stats and the run stay
        Assert.Equal(0, s.Saves().Last().State!.Xp);
    }

    [Fact]
    public void A_record_that_arrives_outside_the_ship_waits_for_the_next_ship_stop()
    {
        Env("SM_WF_ROLLS", "LW");
        Env("SM_WF_RESOLVE", "win");
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        Assert.True(s.H.Clients[0].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Node(1)))));
        s.Tick(3);
        Assert.Equal(WarfrontPhase.Travel, s.Campaign.Phase);
        s.SendProgress(0, File_(8000, 3, [Speed, Speed, Speed]));
        s.Tick(1);
        Assert.Equal(0, s.Campaign.Player(KeyA)!.Xp);   // queued, not applied mid-run
        Assert.Single(s.Saves());                        // but acknowledged at once
        s.Tick(12);
        Assert.Equal(WarfrontPhase.Ship, s.Campaign.Phase);
        Assert.Equal(8000, s.Campaign.Player(KeyA)!.Xp);
        Assert.Equal(3, s.Campaign.Player(KeyA)!.PicksTaken);
    }

    [Fact]
    public void A_pick_sends_a_ProgressSave_at_once()
    {
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.SendProgress(0, null);
        s.Tick(1);
        int before = s.Saves().Count;
        s.Campaign.AddXp(KeyA, 2000);   // level 2, a pick pending
        s.Tick(1);
        Assert.True(s.Saves().Count > before, "a level change sends ProgressSave");
        before = s.Saves().Count;
        Assert.True(s.Campaign.TryTakePick(KeyA, Speed));
        s.Tick(1);
        Assert.True(s.Saves().Count > before, "a pick sends ProgressSave");
        var last = s.Saves().Last().State!;
        Assert.Equal(1, last.PicksTaken);
        Assert.Equal([Speed], last.Cards);
    }

    // ---- NoSave and the write retry ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Newer_build_file_refuses_every_identify_and_writes_nothing()
    {
        var c = WarfrontSave.NewRun(0x5EED);
        c.Embark();
        var node = System.Text.Json.Nodes.JsonNode.Parse(c.ToJson())!.AsObject();
        node["Version"] = 99;
        string path = Path.Combine(_dir, "run.json");
        File.WriteAllText(path, node.ToJsonString());
        var bytes = File.ReadAllBytes(path);

        var s = StartStage(1);
        Assert.True(s.Server.WarfrontNoSaveForTests);
        s.Identify(0, KeyA, "Alpha");
        s.SendNewRun(0);
        s.Tick(3);
        Assert.Null(s.Campaign.Player(KeyA));   // never admitted
        Assert.Contains(s.Heard, h => h.Tag == WarfrontWire.StageTag && WarfrontWire.TryDecode(h.Payload, out WarfrontStageWire g) && g.Status == "this campaign needs a newer build");
        s.Tick(40);   // past an autosave
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(_dir, "squads.json")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "records")));
        Assert.True(File.Exists(Path.Combine(_dir, "notice.json")));
    }

    [Fact]
    public void A_failed_rewrite_after_loading_plays_on_and_retries_after_five_seconds()
    {
        var c = WarfrontSave.NewRun(0x5EED);
        c.Embark();
        c.Join(KeyA, "Alpha", out _);
        c.AddXp(KeyA, 4200);
        WarfrontSave.Write(_dir, c);
        string path = Path.Combine(_dir, "run.json");
        var bytes = File.ReadAllBytes(path);
        int writes = 0;
        GameServer.WarfrontLoaderIoForTests = new WarfrontLoaderIo { Write = (_, _) => { writes++; throw new IOException("injected"); } };

        var s = StartStage(1);   // 2 s of ticks pass inside StartStage; the retry is due at 5 s
        Assert.Equal(1, writes);
        Assert.True(s.Server.WarfrontSavePendingForTests);
        Assert.Equal(0x5EEDUL, s.Campaign.RunSeed);   // the same campaign, never a new run
        Assert.Equal(4200, s.Campaign.Player(KeyA)!.Xp);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        File.Delete(path);   // the proof of a rewrite: the retry must put the file back
        s.Tick(4);
        Assert.False(s.Server.WarfrontSavePendingForTests);
        Assert.True(File.Exists(path));
        Assert.Equal(4200, WarfrontSave.FromJson(File.ReadAllText(path)).Player(KeyA)!.Xp);
    }
}
