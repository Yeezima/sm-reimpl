using System.Diagnostics;
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

/// <summary>
/// Independent acceptance tests for codebase fix 1 (Warfront save safety + persistent progression), written from the owner-approved criteria, not from the implementation:
/// the host and the player's own file meet through the real server wiring (in process, loopback, manual clock), the client's file store and the supervisor loop. Temp folders only.
/// </summary>
[Collection("WarfrontStage")]
public class WarfrontProgressAcceptanceTests : IDisposable
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

    const ulong KeyA = 0xA1A1A1A1A1A1A1A1, KeyB = 0xB2B2B2B2B2B2B2B2;
    static readonly int Speed = (int)WarfrontCard.AttackSpeed;
    static readonly int Retainer = (int)WarfrontCard.Retainer;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-accept-" + Guid.NewGuid().ToString("N"));
    readonly string _clientDir = Path.Combine(Path.GetTempPath(), "sm-wf-accept-client-" + Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, string?> _env = new();

    public WarfrontProgressAcceptanceTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_clientDir);
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
        try { Directory.Delete(_clientDir, true); } catch (IOException) { }
    }

    void Env(string name, string? value)
    {
        if (!_env.ContainsKey(name)) _env[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    // ---- a little stage with per-client tag-49 bookkeeping -----------------------------------------------------------------------------------

    sealed class Stage
    {
        public Harness H = null!;
        public GameServer Server => H.Server;
        public WarfrontCampaign Campaign => Server.WarfrontCampaignForTests!;
        public readonly Dictionary<int, List<byte[]>> Saves49 = new();
        public readonly List<(byte Tag, byte[] Payload)> Heard = new();
        byte _transfer;

        public void Tick(double seconds) => H.Run(TimeSpan.FromSeconds(seconds), after: Drain);
        void Drain()
        {
            for (int i = 0; i < H.Clients.Count; i++)
                foreach (var s in H.Clients[i].TakeExtras())
                {
                    if (s.Tag is >= 40 and <= 49) Heard.Add((s.Tag, s.Payload));
                    if (s.Tag == WarfrontProgressSaveWire.EnvelopeTag) { if (!Saves49.TryGetValue(i, out var l)) Saves49[i] = l = new(); l.Add(s.Payload); }
                }
        }
        public WarfrontProgressRecord? Last49(int client) =>
            Saves49.TryGetValue(client, out var l) && l.Count > 0 && WarfrontProgressSaveWire.TryDecode(l[^1], out var r) ? r : null;
        public int Count49(int client) => Saves49.TryGetValue(client, out var l) ? l.Count : 0;

        public void Identify(int client, ulong key, string name) =>
            Assert.True(H.Clients[client].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: key, Name: name))));

        public void SendProgress(int client, WarfrontProgressFile? file, byte flags = 0)
        {
            var record = WarfrontProgressSaveWire.Encode(file, flags);
            foreach (var chunk in WarfrontProgressSaveWire.CommandChunks(record, ++_transfer))
                Assert.True(H.Clients[client].SendExtrasCommand(WarfrontWire.CommandTag, chunk));
        }

        public void Deploy(int client, int node) =>
            Assert.True(H.Clients[client].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Node(node)))));
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

    static WarfrontProgressFile F(long xp, int picks, int[]? cards = null, params (string Class, string Name)[] extras) => new()
    {
        Xp = xp, PicksTaken = picks, Cards = (cards ?? new int[picks]).ToList(),
        ExtraRetainers = extras.Select(e => new WarfrontExtraRetainer { ClassName = e.Class, Name = e.Name }).ToList(),
    };

    static WarfrontProgressFile Decoded(byte[] tag49) { Assert.True(WarfrontProgressSaveWire.TryDecode(tag49, out var r)); return r.State!; }

    WarfrontProgressStore ClientStore(ulong key) => new(Path.Combine(_clientDir, key.ToString("x") + ".progress.json"), key) { Log = _ => { } };

    // ---- the two copies meet ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Two_players_merge_independently_and_each_gets_only_his_own_ProgressSave()
    {
        var s = StartStage(2);
        s.Identify(0, KeyA, "Alpha");
        s.Identify(1, KeyB, "Beta");
        s.Tick(1);
        s.SendProgress(0, F(8000, 3, [Speed, Speed, Retainer], ("Tactical", "Cassian")));
        s.SendProgress(1, F(2000, 1, [Speed]));
        s.Tick(2);
        Assert.Equal(8000, s.Campaign.Player(KeyA)!.Xp);
        Assert.Equal(2000, s.Campaign.Player(KeyB)!.Xp);
        Assert.Empty(s.Campaign.Player(KeyA)!.Squad);   // the retired Retainer card brings no retainer (owner 2026-11-23, card 2)
        Assert.Equal([Speed, Speed], s.Campaign.Player(KeyA)!.Cards);
        Assert.Equal(2, s.Campaign.Player(KeyA)!.PicksTaken);   // the Retainer card's pick given back (owner "Give picks back")
        Assert.Empty(s.Campaign.Player(KeyB)!.Squad);
        Assert.Equal(8000, s.Last49(0)!.State!.Xp);
        Assert.Equal(2000, s.Last49(1)!.State!.Xp);
        foreach (var raw in s.Saves49[1]) Assert.Empty(Decoded(raw).ExtraRetainers);   // B never hears about A's retainer
    }

    [Fact]
    public void A_client_that_never_sent_a_record_gets_no_ProgressSave()
    {
        var s = StartStage(2);
        s.Identify(0, KeyA, "Alpha");
        s.Identify(1, KeyB, "Beta");
        s.Tick(1);
        s.SendProgress(0, null);
        s.Tick(2);
        s.Campaign.AddXp(KeyB, 2500);
        s.Tick(12);   // past the 10 s change cadence
        Assert.True(s.Count49(0) > 0);
        Assert.Equal(0, s.Count49(1));
    }

    [Fact]
    public void Reconnect_mid_battle_with_a_stale_file_keeps_the_host_copy_and_the_client_file_is_raised_never_lowered()
    {
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.SendProgress(0, F(8000, 3, [Speed, Speed, Speed]));
        s.Tick(1);
        s.Campaign.AddXp(KeyA, 3000);   // earned in play: the host copy is now ahead of the file on A's PC (the connection drops before the client saved)
        long hostXp = s.Campaign.Player(KeyA)!.Xp;
        Assert.True(hostXp > 8000);

        s.H.Clients[0].Leave();
        s.Tick(2);
        s.H.AddClient(nonce: 1234);   // the new connection: the same key, a stale file
        int c = s.H.Clients.Count - 1;
        s.Tick(1);
        s.Identify(c, KeyA, "Alpha");
        s.Tick(1);
        s.SendProgress(c, F(8000, 3, [Speed, Speed, Speed]));
        s.Tick(2);
        Assert.Equal(hostXp, s.Campaign.Player(KeyA)!.Xp);

        s.Tick(12);
        Assert.True(s.Campaign.Player(KeyA)!.Xp >= hostXp, "a stale record never lowers the host copy");
        var host = Decoded(s.Saves49[c][^1]);
        Assert.True(host.Xp >= hostXp);

        // the client's side: its own 8000 file is raised to the host's copy
        var store = ClientStore(KeyA);
        Assert.True(store.Save(F(8000, 3, [Speed, Speed, Speed])));
        Assert.True(store.ApplyHostCopy(host));
        Assert.True(store.Current!.Xp >= hostXp);
        Assert.False(store.ApplyHostCopy(F(8000, 3, [Speed, Speed, Speed])));   // and never lowered back
        Assert.True(ClientStore(KeyA).Load() == WarfrontProgressLoad.Ok);
        Assert.True(ClientStore(KeyA).Tap().Xp >= hostXp);
    }

    [Fact]
    public void A_player_copy_lower_than_the_host_gets_the_host_copy_and_his_file_is_raised()
    {
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.SendProgress(0, F(6500, 3, [Speed, Speed, Retainer], ("Tactical", "Cassian")));   // the host learns the better copy from this connection
        s.Tick(2);
        s.H.Clients[0].Leave();
        s.Tick(2);
        s.H.AddClient(nonce: 4321);   // the same player on another PC with an old, lower file
        s.Tick(1);
        s.Identify(1, KeyA, "Alpha");
        s.Tick(1);
        var store = ClientStore(KeyA);
        Assert.True(store.Save(F(1000, 0)));
        s.SendProgress(1, store.Current);
        s.Tick(2);
        Assert.Equal(6500, s.Campaign.Player(KeyA)!.Xp);
        var host = Decoded(s.Saves49[1][^1]);
        Assert.Equal(6500, host.Xp);
        Assert.True(store.ApplyHostCopy(host));
        var onDisk = ClientStore(KeyA).Tap();
        Assert.Equal(6500, onDisk.Xp);
        Assert.Empty(onDisk.ExtraRetainers);          // the host's copy converts the player's file: the Retainer card and its retainer are dropped
        Assert.Equal([Speed, Speed], onDisk.Cards);
        Assert.Equal(2, onDisk.PicksTaken);           // the Retainer card's pick given back (owner "Give picks back")
    }

    [Fact]
    public void Reset_then_rejoin_with_a_run_that_still_holds_old_progress_does_not_restore_it()
    {
        // the run on disk (the host's mirror) has the old progress; the player resets on the ship and later rejoins in a new process
        var c0 = WarfrontSave.NewRun(0x5EED); c0.Embark(); c0.Join(KeyA, "Alpha", out _);
        c0.ApplyProgress(KeyA, F(9000, 4, [Speed, Speed, Speed, Retainer], ("Tactical", "Cassian")));
        WarfrontSave.Write(_dir, c0);

        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        var store = ClientStore(KeyA);
        Assert.True(store.Save(F(9000, 4, [Speed, Speed, Speed, Retainer], ("Tactical", "Cassian"))));
        Assert.True(store.Reset(DateTime.UtcNow));
        Assert.True(WarfrontProgressStore.IsPendingReset(store.Current));
        // T0 review (2026-11-25): the reset sizes the roster with Rosters.Conform to the run's two, keeping the Marines' names and records
        var wiring = s.Server.WarfrontWiringForTests!;
        Assert.True(wiring.AddStartingRetainer(KeyA));
        wiring.Rosters.Conform(KeyA, 3);   // an over-full roster, as an old run could hold
        wiring.Rosters.Marine(KeyA, 1)!.Record.Kills = 4;
        var names = Enumerable.Range(0, 2).Select(i => wiring.Rosters.Marine(KeyA, i)!.Name).ToArray();
        s.SendProgress(0, store.Current, WarfrontProgressSaveWire.FlagReset);
        s.Tick(2);
        Assert.Equal(0, s.Campaign.Player(KeyA)!.Xp);
        Assert.Equal(1, s.Campaign.Player(KeyA)!.Level);
        Assert.Empty(s.Campaign.Player(KeyA)!.Squad);
        Assert.Equal(2, wiring.Rosters.Count(KeyA));
        Assert.Equal(names, Enumerable.Range(0, 2).Select(i => wiring.Rosters.Marine(KeyA, i)!.Name).ToArray());
        Assert.Equal(4, wiring.Rosters.Marine(KeyA, 1)!.Record.Kills);
        Assert.Equal(0, s.Last49(0)!.State!.Xp);
        s.Tick(35);   // an autosave goes by
        s.Server.GetType();
        var onDisk = WarfrontSave.Load(_dir)!;
        Assert.Equal(0, onDisk.Player(KeyA)!.Xp);   // the mirror no longer holds the old copy: a rejoin cannot restore it
        Assert.Empty(onDisk.Player(KeyA)!.Cards);
        // and what the client's file says: a level-1 file stays level 1 after the host's tag 49
        Assert.False(store.ApplyHostCopy(Decoded(s.Saves49[0][^1])) && ClientStore(KeyA).Tap().Xp > 0);
        Assert.Equal(0, ClientStore(KeyA).Tap().Xp);
    }

    [Fact]
    public void A_reset_outside_the_ship_does_not_wipe_the_running_campaign()
    {
        Env("SM_WF_ROLLS", "LW"); Env("SM_WF_RESOLVE", "win");
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.SendProgress(0, F(8000, 3, [Speed, Speed, Speed]));
        s.Tick(1);
        s.Deploy(0, 1);
        s.Tick(3);
        Assert.Equal(WarfrontPhase.Travel, s.Campaign.Phase);
        s.SendProgress(0, WarfrontProgressFile.Fresh(""), WarfrontProgressSaveWire.FlagReset);
        s.Tick(1);
        Assert.Equal(8000, s.Campaign.Player(KeyA)!.Xp);   // flag 2 is honoured only at the Ship
    }

    [Fact]
    public void A_v5_save_with_a_progressed_player_and_no_progress_file_migrates_to_the_clients_first_file()
    {
        // a first process plays and saves (run.json at the current SaveVersion plus squads.json), then a new process loads it: the player's PC has no progress.json yet
        var first = StartStage(1);
        first.Identify(0, KeyA, "Alpha");
        first.Tick(1);
        first.SendProgress(0, F(5200, 2, [Speed, Retainer], ("Heavy", "Tiberius")));
        first.Tick(2);
        first.Server.GetType();
        first.Tick(35);   // an autosave goes by
        for (int i = 0; i < 50 && !File.Exists(Path.Combine(_dir, "squads.json")); i++) Thread.Sleep(50);
        Assert.Equal(WarfrontCampaign.SaveVersion, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "run.json")))!["Version"]!.GetValue<int>());

        var s = StartStage(1);
        Assert.Equal(5200, s.Campaign.Player(KeyA)!.Xp);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        var store = ClientStore(KeyA);
        Assert.Equal(WarfrontProgressLoad.Absent, store.Load());
        s.SendProgress(0, null);   // flag 1: no file here
        s.Tick(2);
        var host = Decoded(s.Saves49[0][^1]);
        Assert.Equal(5200, host.Xp);
        Assert.True(store.ApplyHostCopy(host));
        var made = ClientStore(KeyA).Tap();
        Assert.Equal(5200, made.Xp);
        Assert.Equal(1, made.PicksTaken);   // the Retainer card's pick given back (owner "Give picks back")
        Assert.Empty(made.ExtraRetainers);   // no retainer is earned since 2026-11-23 (owner card 2)
        Assert.Equal(KeyA.ToString("x16"), made.Key);
    }

    [Fact]
    public void A_brand_new_key_with_no_file_starts_at_level_one_with_no_grant_even_beside_a_veteran()
    {
        var s = StartStage(2);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.SendProgress(0, F(30_000, 5, [Speed, Speed, Speed, Speed, Speed]));
        s.Tick(1);
        s.Identify(1, KeyB, "Beta");
        s.Tick(1);
        s.SendProgress(1, null);
        s.Tick(2);
        Assert.Equal(0, s.Campaign.Player(KeyB)!.Xp);
        Assert.Equal(1, s.Campaign.Player(KeyB)!.Level);
    }

    [Fact]
    public void Max_level_50_clamps_end_to_end_server_and_client_file()
    {
        var s = StartStage(1);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(1);
        s.SendProgress(0, F(uint.MaxValue, 255, Enumerable.Repeat(Speed, 60).ToArray()));
        s.Tick(2);
        var p = s.Campaign.Player(KeyA)!;
        Assert.Equal(50, p.Level);
        Assert.Equal(WarfrontProgressFile.MaxXp, p.Xp);
        Assert.True(p.PicksTaken <= 49);
        var host = Decoded(s.Saves49[0][^1]);
        Assert.Equal(WarfrontProgressFile.MaxXp, host.Xp);
        Assert.Equal(50, host.Level);
        var store = ClientStore(KeyA);
        Assert.True(store.ApplyHostCopy(host));
        Assert.Equal(50, ClientStore(KeyA).Tap().Level);
        // in-run XP is uncapped (documented) but what is sent and written is clamped
        s.Campaign.AddXp(KeyA, 5_000_000);
        s.Tick(12);
        Assert.Equal(50, Decoded(s.Saves49[0][^1]).Level);
        Assert.True(Decoded(s.Saves49[0][^1]).Xp <= WarfrontProgressFile.MaxXp);
    }

    // ---- hostile input -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hostile_progress_chunks_fuzzed_out_of_order_and_interleaved_never_throw_or_corrupt()
    {
        var s = StartStage(2);
        s.Identify(0, KeyA, "Alpha");
        s.Identify(1, KeyB, "Beta");
        s.Tick(1);
        var rng = new Random(1234);
        var good = WarfrontProgressSaveWire.Encode(F(4000, 2, [Speed, Speed]));
        for (int round = 0; round < 60; round++)
        {
            // random bytes under the Progress kind, any length
            var junk = new byte[rng.Next(1, 64)];
            rng.NextBytes(junk); junk[0] = WarfrontProgressSaveWire.CommandKind;
            s.H.Clients[1].SendExtrasCommand(WarfrontWire.CommandTag, junk);
            // chunks of one real transfer, shuffled, with a second transfer id interleaved and one repeated
            var a = WarfrontProgressSaveWire.CommandChunks(good, 7).ToList();
            var b = WarfrontProgressSaveWire.CommandChunks(WarfrontProgressSaveWire.Encode(F(9_999_999, 99, new int[99])), 8).ToList();
            var all = a.Concat(b).Concat(a.Take(1)).OrderBy(_ => rng.Next()).ToList();
            foreach (var chunk in all) s.H.Clients[1].SendExtrasCommand(WarfrontWire.CommandTag, chunk);
            s.Tick(0.2);
        }
        s.Tick(2);
        var p = s.Campaign.Player(KeyB)!;
        Assert.InRange(p.Level, 1, 50);
        Assert.InRange(p.Xp, 0, WarfrontProgressFile.MaxXp);
        Assert.True(p.PicksTaken <= Math.Max(0, p.Level - 1));
        Assert.True(p.Cards.Count <= p.PicksTaken);   // a pick taken may hold no card since record version 2 (a dropped card: the pick stays taken)
        Assert.Equal(0, s.Campaign.Player(KeyA)!.Xp);   // the other player is untouched
        Assert.NotNull(s.Campaign.Player(KeyB));
        s.Tick(1);   // the server still runs
    }

    [Fact]
    public void The_reassembler_survives_a_second_transfer_id_in_the_middle_and_completes_only_a_whole_record()
    {
        var one = WarfrontProgressSaveWire.Encode(F(40000, 20, Enumerable.Repeat(Speed, 20).ToArray(), ("Tactical", "Cassian-Longname"), ("Heavy", "Tiberius-Longname"), ("Assault", "Vitellius-Longname")));
        var two = WarfrontProgressSaveWire.Encode(F(100, 0));
        var a = WarfrontProgressSaveWire.CommandChunks(one, 3).ToList();
        var b = WarfrontProgressSaveWire.CommandChunks(two, 4).ToList();
        Assert.True(a.Count > 1);
        var r = new WarfrontProgressSaveWire.Reassembler();
        Assert.Null(r.Add(a[0].AsSpan(1)));
        WarfrontProgressRecord? done = null;
        foreach (var c in b) done = r.Add(c.AsSpan(1)) ?? done;   // the second transfer restarts the collection and completes alone
        Assert.NotNull(done);
        Assert.Equal(100, done!.State!.Xp);
        // the first transfer's remaining chunks alone never complete a record
        WarfrontProgressRecord? stray = null;
        foreach (var c in a.Skip(1)) stray = r.Add(c.AsSpan(1)) ?? stray;
        Assert.Null(stray);
    }

    // ---- save safety on the server -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_damaged_squads_json_alone_loads_the_run_and_starts_the_squads_empty()
    {
        var c = WarfrontSave.NewRun(0x5EED); c.Embark(); c.Join(KeyA, "Alpha", out _);
        c.ApplyProgress(KeyA, F(4200, 1, [Retainer], ("Tactical", "Cassian")));
        WarfrontSave.Write(_dir, c);
        File.WriteAllText(Path.Combine(_dir, "squads.json"), "{ not json \0\0");

        var load = WarfrontRunLoader.Load(_dir, null, new WarfrontLoaderIo { SleepMs = _ => { } }, () => DateTime.UtcNow);
        Assert.Equal(WarfrontLoadState.Loaded, load.State);   // the run itself is fine: never set aside for a bad squads file
        Assert.False(load.NoSave);
        Assert.False(Directory.Exists(Path.Combine(_dir, "records")));
        Assert.Equal(4200, load.Campaign.Player(KeyA)!.Xp);

        var s = StartStage(1);   // the stage wiring tolerates it too
        Assert.False(s.Server.WarfrontNoSaveForTests);
        Assert.Equal(4200, s.Campaign.Player(KeyA)!.Xp);
        s.Identify(0, KeyA, "Alpha");
        s.Tick(2);
        Assert.NotNull(s.Campaign.Player(KeyA));
    }

    [Fact]
    public void A_damaged_run_json_in_the_stage_is_set_aside_and_the_first_identify_hears_the_notice()
    {
        File.WriteAllText(Path.Combine(_dir, "run.json"), "{ \"Version\": 5, truncated");
        var s = StartStage(1);
        Assert.False(s.Server.WarfrontNoSaveForTests);
        var set = Directory.GetFiles(Path.Combine(_dir, "records"), "*-unreadable*.json");
        Assert.Single(set);
        Assert.Equal("{ \"Version\": 5, truncated", File.ReadAllText(set[0]));   // contents kept byte for byte
        Assert.True(File.Exists(Path.Combine(_dir, "notice.json")));
        s.Identify(0, KeyA, "Alpha");
        s.Tick(2);
        Assert.NotNull(s.Campaign.Player(KeyA));
        Assert.Contains(s.Heard, h => h.Tag == WarfrontWire.StageTag && WarfrontWire.TryDecode(h.Payload, out WarfrontStageWire g) && g.Status.Contains("damaged"));
    }

    // ---- the supervisor path ---------------------------------------------------------------------------------------------------------------

    sealed class OneShotLauncher : IWarfrontStageLauncher
    {
        public readonly List<string> Maps = [];
        public IWarfrontStageProcess Start(string map) { Maps.Add(map); return new Child(); }
        sealed class Child : IWarfrontStageProcess
        {
            bool _killed;
            public int ExitCode => 0;
            public bool WaitForExit(int ms) { var w = Stopwatch.StartNew(); while (!_killed && (ms < 0 || w.ElapsedMilliseconds < ms)) Thread.Sleep(5); return _killed; }
            public void Kill() => _killed = true;
            public void Dispose() { }
        }
    }

    static WarfrontSupervisorTiming Fast() => new()
    {
        Backoff = [TimeSpan.FromMilliseconds(10)], StableAfter = TimeSpan.FromSeconds(10), StopGrace = TimeSpan.FromMilliseconds(300), PollMilliseconds = 10,
    };

    [Theory]
    [InlineData("damaged")]
    [InlineData("newer")]
    public void The_dedicated_supervisor_with_an_unusable_run_reaches_the_stage_and_keeps_serving(string kind)
    {
        string text = kind == "damaged" ? "{ not a campaign" : System.Text.Json.Nodes.JsonNode.Parse(WarfrontSave.NewRun(1).ToJson())!.AsObject().Also(n => n["Version"] = 99).ToJsonString();
        File.WriteAllText(Path.Combine(_dir, "run.json"), text);
        var launcher = new OneShotLauncher();
        using var stop = new ManualResetEventSlim(false);
        var log = new List<string>();
        var task = Task.Run(() => WarfrontSupervisor.Loop(_dir, launcher, dedicated: true, Fast(), stop, log.Add));
        var until = Stopwatch.StartNew();
        while (launcher.Maps.Count == 0 && until.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
        Assert.Single(launcher.Maps);   // reached the loader's stage: the supervisor neither exited nor crashed
        Assert.False(task.IsCompleted, "the supervisor keeps serving");
        stop.Set();
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(text, File.ReadAllText(Path.Combine(_dir, "run.json")));   // the supervisor itself never touched the file
        Assert.False(Directory.Exists(Path.Combine(_dir, "records")));
    }
}

static class AcceptanceExtensions
{
    public static T Also<T>(this T value, Action<T> act) { act(value); return value; }
    public static WarfrontProgressFile Tap(this WarfrontProgressStore s)
    {
        Assert.Equal(WarfrontProgressLoad.Ok, s.Load());
        return s.Current!;
    }
}
