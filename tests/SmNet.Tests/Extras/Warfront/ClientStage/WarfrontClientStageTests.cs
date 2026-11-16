using SmClient.Extras.Warfront.Hub;
using SmClient.Extras.Warfront.Stage;
using SmNet.Extras.Warfront;
using SmNet.Extras.Warfront.Results;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Results;

namespace SmNet.Tests.Extras.Warfront.ClientStage;

/// <summary>W5b: the client's stage model (no Godot): the wire to the hub view and back, the commands' resend, the stage sequence through a rejoin, the results chunks, the down line, the key file.</summary>
public class WarfrontClientStageTests
{
    const ulong KeyA = 0xA1A1, KeyB = 0xB2B2;

    static WarfrontCampaign Campaign()
    {
        var c = new WarfrontCampaign(77, new ForcedRolls(true, false));
        c.Join(KeyA, "Alpha", out _); c.Join(KeyB, "Beta", out _);
        c.Embark();
        return c;
    }

    static byte[] HubBytes(WarfrontCampaign c, ulong viewer) => WarfrontWire.Encode(WarfrontWire.HubFor(c, viewer));

    static byte[] OverBytes(WarfrontCampaign c, ulong viewer) => WarfrontWire.Encode(WarfrontWire.HubFor(c, viewer) with { Phase = WarfrontPhase.Won });

    static byte[] Stage(WarfrontStageState state, int percent = 0, int arrived = 0, int expected = 2, string status = "", string map = "wf_b2", int elapsed = 0)
        => WarfrontWire.Encode(new WarfrontStageWire(state, WarfrontTargetKind.Node, 1, map, percent, elapsed, arrived, expected, status));

    [Fact]
    public void Hub_bytes_become_the_view_and_encode_back_to_the_same_bytes()
    {
        var c = Campaign();
        Assert.True(c.TryDeploy(KeyA, WarfrontTarget.Node(1), out _));
        c.BeginBattle(); c.Resolve(WarfrontOutcome.Win);
        foreach (ulong viewer in new[] { KeyA, KeyB })
        {
            var bytes = HubBytes(c, viewer);
            Assert.True(WarfrontWire.TryDecode(bytes, out WarfrontHubWire wire));
            var view = WarfrontStageModel.ToView(wire);
            Assert.Equal(bytes, WarfrontWire.Encode(WarfrontStageModel.ToWire(view)));
            Assert.Equal(viewer, view.ViewerKey);
            Assert.Equal(viewer == KeyA, view.ViewerIsLeader);
            Assert.Equal(c.AvailableTargets(), view.Targets);
            Assert.Equal(HubLastKind.PlayedWon, view.Last[1]);
        }
    }

    [Fact]
    public void A_hub_section_reaches_the_model_and_a_malformed_one_is_dropped()
    {
        var m = new WarfrontStageModel(KeyB, "Beta");
        Assert.False(m.ApplyHub(new byte[] { 1, 2, 3 }));
        Assert.False(m.ApplyHub(Array.Empty<byte>()));
        Assert.True(m.ApplyHub(HubBytes(Campaign(), KeyB)));
        Assert.True(m.Identified);
        Assert.Equal(KeyB, m.Hub.View.ViewerKey);
        Assert.Equal(2, m.Hub.View.Players.Count);
        var rng = new Random(5);
        var good = HubBytes(Campaign(), KeyB);
        for (int i = 0; i < 500; i++)
        {
            var bad = (byte[])good.Clone();
            for (int k = 0; k < 3; k++) bad[rng.Next(bad.Length)] = (byte)rng.Next(256);
            m.ApplyHub(bad.AsSpan(0, rng.Next(bad.Length + 1)));   // never throws
        }
    }

    [Fact]
    public void Identify_is_sent_until_the_hub_names_this_client()
    {
        var m = new WarfrontStageModel(KeyB, "Beta");
        var first = m.IdentifyDue(0.0);
        Assert.NotNull(first);
        Assert.True(WarfrontWire.TryDecode(first, out WarfrontClientCommand cmd));
        Assert.Equal((WarfrontClientCommandKind.Identify, KeyB, "Beta"), (cmd.Kind, cmd.Key, cmd.Name));
        Assert.Null(m.IdentifyDue(0.4));
        Assert.NotNull(m.IdentifyDue(1.0));
        m.ApplyHub(HubBytes(Campaign(), KeyA));   // a hub that names someone else is not an answer
        Assert.NotNull(m.IdentifyDue(2.0));
        m.ApplyHub(HubBytes(Campaign(), KeyB));
        Assert.Null(m.IdentifyDue(3.0));
    }

    [Fact]
    public void Deploy_is_resent_until_the_stage_or_the_hub_shows_it_was_taken()
    {
        var m = new WarfrontStageModel(KeyA, "Alpha");
        m.ApplyHub(HubBytes(Campaign(), KeyA));
        Assert.Null(m.DeployDue(0));
        m.RequestDeploy(WarfrontTarget.Node(1));
        var one = m.DeployDue(0);
        Assert.NotNull(one);
        Assert.True(WarfrontWire.TryDecode(one, out WarfrontClientCommand cmd));
        Assert.Equal((WarfrontClientCommandKind.Deploy, WarfrontTarget.Node(1)), (cmd.Kind, cmd.Target));
        Assert.Null(m.DeployDue(0.5));
        Assert.NotNull(m.DeployDue(1.0));
        Assert.NotNull(m.DeployDue(2.0));
        m.ApplyStage(Stage(WarfrontStageState.Preparing, 5), 2.1);   // the echo
        Assert.Null(m.PendingDeploy);
        Assert.Null(m.DeployDue(5.0));
    }

    [Fact]
    public void Deploy_stops_on_the_hub_echo_a_refusal_and_after_the_cap()
    {
        var c = Campaign();
        var m = new WarfrontStageModel(KeyA, "Alpha");
        m.ApplyHub(HubBytes(c, KeyA));
        m.RequestDeploy(WarfrontTarget.Node(0));
        Assert.NotNull(m.DeployDue(0));
        Assert.True(c.TryDeploy(KeyA, WarfrontTarget.Node(0), out _));
        m.ApplyHub(HubBytes(c, KeyA));   // phase Travel, a current target
        Assert.Null(m.PendingDeploy);

        var r = new WarfrontStageModel(KeyA, "Alpha");
        r.RequestDeploy(WarfrontTarget.Hq);
        Assert.NotNull(r.DeployDue(0));
        r.ApplyStage(Stage(WarfrontStageState.None, status: "ONLY THE LEADER DEPLOYS"), 0.2);
        Assert.Null(r.PendingDeploy);
        Assert.Equal("ONLY THE LEADER DEPLOYS", r.Refusal);
        Assert.False(r.TravelVisible);

        var cap = new WarfrontStageModel(KeyA, "Alpha");
        cap.RequestDeploy(WarfrontTarget.Node(2));
        int sent = 0;
        for (int t = 0; t < 40; t++) if (cap.DeployDue(t) is not null) sent++;
        Assert.Equal(WarfrontStageModel.MaxSends, sent);
    }

    [Fact]
    public void NewRun_is_for_the_leader_when_the_run_is_over_and_stops_when_it_is_not()
    {
        var m = new WarfrontStageModel(KeyA, "Alpha");
        m.ApplyHub(HubBytes(Campaign(), KeyA));
        m.RequestNewRun();
        Assert.Null(m.NewRunDue(0));   // not over
        m.ApplyHub(OverBytes(Campaign(), KeyA));
        Assert.True(m.Hub.IsOver);
        m.RequestNewRun();
        var bytes = m.NewRunDue(0);
        Assert.NotNull(bytes);
        Assert.True(WarfrontWire.TryDecode(bytes, out WarfrontClientCommand cmd));
        Assert.Equal(WarfrontClientCommandKind.NewRun, cmd.Kind);
        m.ApplyHub(HubBytes(Campaign(), KeyA));   // a fresh run
        Assert.False(m.NewRunPending);
        Assert.Null(m.NewRunDue(5));

        var notLeader = new WarfrontStageModel(KeyB, "Beta");
        notLeader.ApplyHub(OverBytes(Campaign(), KeyB));
        notLeader.RequestNewRun();
        Assert.False(notLeader.NewRunPending);
    }

    [Fact]
    public void Stage_sequence_preparing_go_rejoin_arrived_and_the_landing()
    {
        var m = new WarfrontStageModel(KeyA, "Alpha");
        m.ApplyHub(HubBytes(Campaign(), KeyA));
        Assert.False(m.TravelVisible);
        m.ApplyStage(Stage(WarfrontStageState.Preparing, 37, status: "layout built"), 10);
        Assert.True(m.TravelVisible);
        Assert.Contains("37 PERCENT", m.TravelStatusText);
        Assert.Contains("LAYOUT BUILT", m.TravelStatusText);
        Assert.StartsWith("TO LANE B, NODE ", m.TravelDestinationText);
        m.Tick(12); Assert.True(m.TravelVisible);
        m.Tick(10 + WarfrontStageModel.PreparingStaleSeconds + 1);   // the server vanished
        Assert.False(m.TravelVisible);

        m.ApplyStage(Stage(WarfrontStageState.Preparing, 90), 30);
        Assert.True(m.TravelVisible);
        m.ApplyStage(Stage(WarfrontStageState.Go, 100), 40);
        Assert.True(m.TravelVisible);
        Assert.False(m.RejoinDue);
        m.Tick(40 + WarfrontStageModel.RejoinDelaySeconds - 0.1);
        Assert.False(m.RejoinDue);
        m.Tick(40 + WarfrontStageModel.RejoinDelaySeconds + 0.1);
        Assert.True(m.RejoinDue);
        m.Tick(200); Assert.True(m.TravelVisible);   // the screen stays until the connection goes

        // the new view: first section Arrived, before any Hub
        var n = new WarfrontStageModel(KeyA, "Alpha");
        n.ApplyStage(Stage(WarfrontStageState.Arrived, 100, arrived: 1, expected: 2), 1);
        Assert.True(n.TravelVisible);
        Assert.Contains("1 OF 2 PLAYERS ARRIVED", n.TravelStatusText);
        var travel = Campaign();
        Assert.True(travel.TryDeploy(KeyA, WarfrontTarget.Node(1), out _));
        n.ApplyHub(HubBytes(travel, KeyA));   // the saved Travel phase
        n.ApplyStage(Stage(WarfrontStageState.Arrived, 100, arrived: 2, expected: 2), 2);
        Assert.True(n.TravelVisible);
        Assert.Contains("TO LANE B", n.TravelDestinationText);
        travel.BeginBattle();
        n.ApplyHub(HubBytes(travel, KeyA));   // the landing
        Assert.False(n.TravelVisible);
        n.ApplyStage(Stage(WarfrontStageState.Arrived, 100, arrived: 2, expected: 2), 2.2);   // the server repeats it a moment longer
        Assert.False(n.TravelVisible);
    }

    [Fact]
    public void A_stale_arrived_ends_the_screen_when_no_hub_says_so()
    {
        var m = new WarfrontStageModel(KeyA, "Alpha");
        m.ApplyStage(Stage(WarfrontStageState.Arrived, 100, arrived: 1, expected: 2), 5);
        Assert.True(m.TravelVisible);
        m.Tick(5 + WarfrontStageModel.ArrivedStaleSeconds + 0.5);
        Assert.False(m.TravelVisible);
        Assert.False(m.ApplyStage(new byte[] { 9, 9, 9 }, 6));
    }

    static WarfrontRunRecord Record(int seed)
    {
        var c = new WarfrontCampaign((ulong)seed);
        c.Join(KeyA, "Alpha", out _);
        return WarfrontResults.Build(c, null, TimeSpan.FromSeconds(100 + seed), new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Results_chunks_reassemble_in_any_order_once_and_a_broken_one_is_dropped_whole()
    {
        var record = Record(1);
        var bytes = WarfrontResultsWire.Encode(record);
        var chunks = WarfrontResultsWire.Split(bytes, 7).ToList();
        var m = new WarfrontStageModel(KeyA, "Alpha");
        WarfrontRunRecord? got = null;
        foreach (var chunk in Enumerable.Reverse(chunks)) got ??= m.ApplyResults(chunk);
        Assert.NotNull(got);
        Assert.Equal(record.Result, got.Result);
        foreach (var chunk in chunks) Assert.Null(m.ApplyResults(chunk));   // the repeat is not a new record

        var other = new WarfrontStageModel(KeyA, "Alpha");
        Assert.Null(other.ApplyResults(new byte[] { 1 }));
        Assert.Null(other.ApplyResults(new byte[] { 1, 5, 2, 0 }));
        var broken = WarfrontResultsWire.Split(bytes.Take(bytes.Length / 2).Concat(Enumerable.Repeat((byte)0xFF, 30)).ToArray(), 9);
        WarfrontRunRecord? bad = null;
        foreach (var chunk in broken) bad ??= other.ApplyResults(chunk);
        Assert.Null(bad);
        // a second, different record is new
        WarfrontRunRecord? second = null;
        foreach (var chunk in WarfrontResultsWire.Split(WarfrontResultsWire.Encode(Record(2)), 8)) second ??= other.ApplyResults(chunk);
        Assert.NotNull(second);
    }

    [Fact]
    public void Down_section_gives_the_line_and_goes_quiet()
    {
        var m = new WarfrontStageModel(KeyA, "Alpha");
        Assert.Equal("", m.DownLine(0));
        Assert.True(m.ApplyDown(WarfrontWire.Encode(new WarfrontDownWire(WarfrontDownPhase.Downed, 11.2f)), 1));
        Assert.Equal("DOWN    12 S", m.DownLine(1.1));
        Assert.Equal("", m.DownLine(4));
        Assert.True(m.ApplyDown(WarfrontWire.Encode(new WarfrontDownWire(WarfrontDownPhase.Spectator, 30f)), 5));
        Assert.Equal(WarfrontDownPhase.Spectator, m.Down.Phase);
        Assert.StartsWith("SPECTATING", m.DownLine(5.5));
        Assert.False(m.ApplyDown(new byte[] { 9, 0, 0 }, 6));
        Assert.False(m.ApplyDown(new byte[] { 1 }, 6));
    }

    [Fact]
    public void The_key_file_is_made_once_and_read_back_and_a_bad_file_is_replaced()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wf-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(dir, "sub", "client.json");
            ulong key = WarfrontClientKey.LoadOrCreate(file);
            Assert.NotEqual(0UL, key);
            Assert.True(File.Exists(file));
            Assert.Equal(key, WarfrontClientKey.LoadOrCreate(file));
            File.WriteAllText(file, "{not json");
            ulong second = WarfrontClientKey.LoadOrCreate(file);
            Assert.NotEqual(0UL, second);
            Assert.Equal(second, WarfrontClientKey.LoadOrCreate(file));
            Assert.Equal(7, WarfrontClientKey.Tail(second).Length);   // "..." and four digits: never the key
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Identify_never_gives_up_and_the_note_names_the_wait()
    {
        var m = new WarfrontStageModel(KeyB, "Beta");
        int sent = 0;
        for (int t = 0; t < 300; t++) if (m.IdentifyDue(t) is not null) sent++;
        Assert.Equal(300, sent);
        Assert.Equal("WAITING FOR THE SERVER TO KNOW YOU", m.NoteOverride(300));
        var fresh = new WarfrontStageModel(KeyB, "Beta");
        fresh.IdentifyDue(0);
        Assert.Equal("", fresh.NoteOverride(2));
        fresh.ApplyHub(HubBytes(Campaign(), KeyB));
        Assert.Equal("", fresh.NoteOverride(30));
    }

    [Fact]
    public void The_note_carries_a_throwaway_key_a_refusal_and_an_unanswered_deploy()
    {
        var m = new WarfrontStageModel(KeyA, "Alpha") { KeyNote = "THE KEY FILE CANNOT BE WRITTEN" };
        Assert.Equal("THE KEY FILE CANNOT BE WRITTEN", m.NoteOverride(0));
        var r = new WarfrontStageModel(KeyA, "Alpha");
        r.ApplyHub(HubBytes(Campaign(), KeyA));
        r.RequestDeploy(WarfrontTarget.Node(1));
        for (int t = 0; t < 40; t++) r.DeployDue(t);
        Assert.Equal("THE DEPLOYMENT WAS NOT ANSWERED", r.NoteOverride(40));
        r.RequestDeploy(WarfrontTarget.Node(1));
        r.ApplyStage(Stage(WarfrontStageState.None, status: "only the leader deploys"), 41);
        Assert.Equal("ONLY THE LEADER DEPLOYS", r.NoteOverride(41));
    }
}
