using SmClient.Extras.Warfront.Stage;
using SmNet.Extras.Musou.Campaign;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;

namespace SmNet.Tests.Extras.Warfront.Progression;

/// <summary>Codebase fix 1, review fix pass: a pending reset is never undone by the host's old copy, NEW CAMPAIGN on an unusable run.json, a locked progress file, the reset's safety copy, battle XP of a mid-battle joiner, the final send. Temp folders only.</summary>
[Collection("WarfrontStage")]
public class WarfrontProgressFixPassTests : IDisposable
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
    const ulong KeyA = 0xA1A1A1A1A1A1A1A1;
    static readonly int Speed = (int)WarfrontCard.AttackSpeed;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-fixpass-" + Guid.NewGuid().ToString("N"));
    readonly string _clientDir = Path.Combine(Path.GetTempPath(), "sm-wf-fixpass-client-" + Guid.NewGuid().ToString("N"));

    public WarfrontProgressFixPassTests()
    {
        Environment.SetEnvironmentVariable("SM_WF_ROLLS", "LW");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_clientDir);
        WarfrontRunFiles.DirOverride = _dir;
        WarfrontRunFiles.NavCacheOverride = Path.Combine(_dir, "nav");
        GameServer.WarfrontPrebuild = (map, _, progress) => { progress?.Invoke(30, "stub"); progress?.Invoke(100, "ready"); return map + " (stub)"; };
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SM_WF_ROLLS", null);
        WarfrontRunFiles.DirOverride = null;
        WarfrontRunFiles.NavCacheOverride = null;
        GameServer.WarfrontPrebuild = SmContent.Extras.Warfront.WarfrontMaps.Prebuild;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        try { Directory.Delete(_clientDir, true); } catch (IOException) { }
    }

    static WarfrontProgressFile F(long xp, int picks, int[]? cards = null) => new() { Xp = xp, PicksTaken = picks, Cards = (cards ?? new int[picks]).ToList() };
    WarfrontProgressStore Store(ulong key = KeyA) => new(Path.Combine(_clientDir, "progress.json"), key) { Log = _ => { } };

    // ---- a small stage ---------------------------------------------------------------------------------------------------------------------

    sealed class Stage
    {
        public Harness H = null!;
        public GameServer Server => H.Server;
        public WarfrontCampaign Campaign => Server.WarfrontCampaignForTests!;
        public readonly List<byte[]> Saves49 = new();
        byte _transfer;
        public void Tick(double seconds) => H.Run(TimeSpan.FromSeconds(seconds), after: () => { foreach (var x in H.Clients[0].TakeExtras()) if (x.Tag == WarfrontProgressSaveWire.EnvelopeTag) Saves49.Add(x.Payload); });
        public void Identify(ulong key) => Assert.True(H.Clients[0].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: key, Name: "Alpha"))));
        public void SendProgress(WarfrontProgressFile? file, byte flags = 0)
        {
            foreach (var chunk in WarfrontProgressSaveWire.CommandChunks(WarfrontProgressSaveWire.Encode(file, flags), ++_transfer))
                Assert.True(H.Clients[0].SendExtrasCommand(WarfrontWire.CommandTag, chunk));
        }
        public void Deploy(int node) => Assert.True(H.Clients[0].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Node(node)))));
        public WarfrontProgressFile Last49() { Assert.True(WarfrontProgressSaveWire.TryDecode(Saves49[^1], out var r)); return r.State!; }
    }

    Stage StartStage()
    {
        var map = GameServer.TfwGameMap(TfwTestArena.Data, "wf_b1");
        var stage = new Stage
        {
            H = new Harness(LinkConditions.Perfect, 5, 1, Config, new ServerOptions { Match = Rules }, map: map,
                clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) }),
        };
        stage.Tick(2);
        return stage;
    }

    // ---- 1: a queued reset is never undone by the ack --------------------------------------------------------------------------------------

    [Fact]
    public void A_queued_reset_is_acknowledged_with_the_level_1_copy_and_the_client_file_stays_level_1()
    {
        var s = StartStage();
        s.Identify(KeyA);
        s.Tick(1);
        s.SendProgress(F(8000, 3, [Speed, Speed, Speed]));
        s.Tick(1);
        s.Deploy(1);
        s.Tick(3);
        Assert.Equal(WarfrontPhase.Travel, s.Campaign.Phase);

        var store = Store();
        Assert.True(store.Save(F(8000, 3, [Speed, Speed, Speed])));
        Assert.True(store.Reset(DateTime.UtcNow));
        s.Saves49.Clear();
        s.SendProgress(store.Current, WarfrontProgressSaveWire.FlagReset);   // the reset joins a host in Travel: queued
        s.Tick(1);
        Assert.Equal(8000, s.Campaign.Player(KeyA)!.Xp);   // not applied before the ship
        Assert.NotEmpty(s.Saves49);
        var ack = s.Last49();
        Assert.Equal(0, ack.Xp);   // the ack carries the reset copy, not the old level
        Assert.False(store.ApplyHostCopy(ack) && store.Current!.Xp > 0);
        Assert.Equal(0, Store().Tap().Xp);

        // back at the ship the reset is applied, and the host copy stays level 1
        s.Campaign.BeginBattle(); s.Campaign.ResumeAfterRestart();
        s.Tick(2);
        Assert.Equal(0, s.Campaign.Player(KeyA)!.Xp);
        Assert.Equal(0, Store().Tap().Xp);
    }

    [Fact]
    public void A_pending_reset_file_ignores_a_host_copy_above_level_1_until_the_host_shows_level_1_and_the_reset_is_resent_until_then()
    {
        var store = Store();
        Assert.True(store.Save(F(8000, 3, [Speed, Speed, Speed])));
        Assert.True(store.Reset(DateTime.UtcNow));
        Assert.False(store.ApplyHostCopy(F(8000, 3, [Speed, Speed, Speed])));
        Assert.Equal(0, Store().Tap().Xp);

        var model = new WarfrontStageModel(KeyA, "Alpha");
        model.SetProgress(store.Current);
        double t = 0;
        model.IdentifyDue(t);
        int sends = 0;
        for (; t < 120; t += 1.1) if (model.ProgressDue(t) is not null) sends++;
        Assert.True(sends > WarfrontStageModel.MaxProgressSends, "a pending reset is not capped");
        model.ApplyProgressSave(WarfrontProgressSaveWire.Encode(F(8000, 3, [Speed, Speed, Speed])));
        Assert.False(model.ProgressAcked);   // the old level does not acknowledge a reset
        Assert.NotNull(model.ProgressDue(t + 5));
        model.ApplyProgressSave(WarfrontProgressSaveWire.Encode(F(0, 0)));
        Assert.True(model.ProgressAcked);
        Assert.Null(model.ProgressDue(t + 10));

        // the host has heard it: later copies with new XP are written again
        Assert.False(store.ApplyHostCopy(F(0, 0)));
        Assert.True(store.ApplyHostCopy(F(2500, 0)));
        Assert.Equal(2500, Store().Tap().Xp);
    }

    // ---- 2: --warfront-new on an unusable run.json ----------------------------------------------------------------------------------------

    [Fact]
    public void Warfront_new_on_a_damaged_run_sets_it_aside_starts_a_new_run_and_writes_the_notice()
    {
        File.WriteAllText(Path.Combine(_dir, "run.json"), "{ not a campaign");
        File.WriteAllText(Path.Combine(_dir, "squads.json"), "{ squads");
        var log = new List<string>(); var err = new List<string>();
        Assert.Equal(0, WarfrontSupervisor.PrepareRun(_dir, true, log.Add, err.Add));
        Assert.Empty(err);
        var kept = Directory.GetFiles(Path.Combine(_dir, "records"), "*-unreadable*.json");
        Assert.Equal(2, kept.Length);
        Assert.Contains(kept, f => File.ReadAllText(f) == "{ not a campaign");
        Assert.NotNull(WarfrontSave.Load(_dir));   // a new, readable run
        Assert.Contains("damaged", File.ReadAllText(Path.Combine(_dir, WarfrontRunLoader.NoticeFile)));
    }

    [Fact]
    public void Warfront_new_on_a_newer_run_refuses_and_leaves_it_untouched()
    {
        string text = System.Text.Json.Nodes.JsonNode.Parse(WarfrontSave.NewRun(1).ToJson())!.AsObject().Also(n => n["Version"] = 99).ToJsonString();
        File.WriteAllText(Path.Combine(_dir, "run.json"), text);
        var err = new List<string>();
        Assert.Equal(1, WarfrontSupervisor.PrepareRun(_dir, true, _ => { }, err.Add));
        Assert.Equal(text, File.ReadAllText(Path.Combine(_dir, "run.json")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "records")));
        Assert.Contains("newer", File.ReadAllText(Path.Combine(_dir, WarfrontRunLoader.NoticeFile)));
    }

    // ---- 3 and 4: the client's file --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_locked_progress_file_is_unreadable_never_absent_and_is_not_written_over()
    {
        if (!OperatingSystem.IsWindows()) return;   // a share-none handle blocks reads only on Windows
        var s = Store();
        Assert.True(s.Save(F(4000, 2, [Speed, Speed])));
        byte[] before = File.ReadAllBytes(s.Path);
        var r = Store();
        using (var hold = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(WarfrontProgressLoad.Unreadable, r.Load());
            Assert.True(r.Unreadable);
            Assert.False(r.Save(F(9000, 4)));
            Assert.False(r.ApplyHostCopy(F(9000, 4)));
            Assert.False(r.Reset(DateTime.UtcNow));
        }
        Assert.Equal(before, File.ReadAllBytes(s.Path));
        Assert.False(File.Exists(s.BeforeResetPath));
        Assert.Equal(WarfrontProgressLoad.Ok, Store().Load());
        var model = new WarfrontStageModel(KeyA, "Alpha");
        model.SetProgress(null, newer: true);   // what the view passes for an unreadable file: nothing is sent
        Assert.Null(model.ProgressRecord());
    }

    [Fact]
    public void The_reset_safety_copy_is_of_the_file_load_used_not_a_damaged_main()
    {
        var s = Store();
        Assert.True(s.Save(F(2000, 1, [Speed])));
        Assert.True(s.Save(F(4000, 2, [Speed, Speed])));   // main 4000, bak 2000
        File.WriteAllText(s.Path, "{ damaged");
        var r = Store();
        Assert.Equal(WarfrontProgressLoad.Ok, r.Load());
        Assert.True(r.Reset(DateTime.UtcNow));
        Assert.Equal(WarfrontProgressParse.Ok, WarfrontProgressFile.TryParse(File.ReadAllText(r.BeforeResetPath), out var copy));
        Assert.Equal(2000, copy!.Xp);
        Assert.Equal(0, Store().Tap().Xp);
    }

    // ---- 5: a mid-battle joiner's battle XP ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_queued_record_that_wins_gets_the_xp_the_run_copy_earned_meanwhile()
    {
        var s = StartStage();
        s.Identify(KeyA);
        s.Tick(1);
        s.Deploy(1);
        s.Tick(3);
        Assert.Equal(WarfrontPhase.Travel, s.Campaign.Phase);
        s.SendProgress(F(8000, 3, [Speed, Speed, Speed]));
        s.Tick(1);
        s.Campaign.AddXp(KeyA, 500);   // earned in the battle on the run's copy
        s.Campaign.BeginBattle(); s.Campaign.ResumeAfterRestart();
        s.Tick(2);
        var p = s.Campaign.Player(KeyA)!;
        Assert.Equal(8500, p.Xp);
        Assert.Equal(3, p.PicksTaken);
    }

    [Fact]
    public void A_queued_record_that_loses_changes_nothing_and_the_run_copy_keeps_its_battle_xp()
    {
        var s = StartStage();
        s.Identify(KeyA);
        s.Tick(1);
        s.Campaign.AddXp(KeyA, 3000);
        s.Deploy(1);
        s.Tick(3);
        s.SendProgress(F(100, 0));
        s.Tick(1);
        s.Campaign.AddXp(KeyA, 500);
        long run = s.Campaign.Player(KeyA)!.Xp;
        s.Campaign.BeginBattle(); s.Campaign.ResumeAfterRestart();
        s.Tick(2);
        Assert.Equal(run, s.Campaign.Player(KeyA)!.Xp);
    }

    // ---- 6: the final send ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_final_send_gives_the_client_the_last_xp_at_once()
    {
        var s = StartStage();
        s.Identify(KeyA);
        s.Tick(1);
        s.SendProgress(F(100, 0));
        s.Tick(2);
        s.Saves49.Clear();
        s.Campaign.AddXp(KeyA, 250);   // inside the 10 s window: no ordinary send yet
        long xp = s.Campaign.Player(KeyA)!.Xp;
        Assert.Equal(1, s.Server.WarfrontSendProgressSaveFinalForTests());
        s.Tick(0.3);
        Assert.NotEmpty(s.Saves49);
        Assert.Equal(xp, s.Last49().Xp);
    }
}
