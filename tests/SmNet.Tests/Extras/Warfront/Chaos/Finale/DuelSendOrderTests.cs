using System.Numerics;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Chaos.Breach;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Warfront.Chaos.Finale;
using SmSim.Extras.Warfront.Fortress;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>
/// Chaos Warfront C5c (lead 40): on the snapshot tick the server sends the finale's state (tag 51 kind 16) before the breach list that shows the silent <c>CloseAll</c>'s breaches Closed, so the client sets
/// <c>ChaosBreachModel.SilentCloses</c> first and plays no closure effect for the duel's silent close.
/// </summary>
[Collection("WarfrontStage")]
public class DuelSendOrderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-chaos-duel-order-" + Guid.NewGuid().ToString("N"));

    public DuelSendOrderTests()
    {
        Environment.SetEnvironmentVariable("SM_WF_ROLLS", "LW");
        Directory.CreateDirectory(_dir);
        WarfrontRunFiles.DirOverride = _dir;
        WarfrontRunFiles.NavCacheOverride = Path.Combine(_dir, "nav");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SM_WF_ROLLS", null);
        WarfrontRunFiles.DirOverride = null;
        WarfrontRunFiles.NavCacheOverride = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void On_the_snapshot_tick_the_duel_state_goes_out_before_the_closed_breach_list()
    {
        var config = BreachRig.Chaos() with { Extras = BreachRig.Chaos().Extras | ContentExtras.Musou | ContentExtras.Warfront };
        var map = GameServer.TfwGameMap(TfwTestArena.Data, "wf_b1");
        var h = new Harness(LinkConditions.Perfect, 5, 1, config, new ServerOptions { Match = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        var got = new List<byte[]>();
        void Run(double s) => h.Run(TimeSpan.FromSeconds(s), after: () => { foreach (var x in h.Clients[0].TakeExtras()) if (x.Tag == WarfrontChaosWire.Tag) got.Add(x.Payload); });
        Run(2);
        Assert.True(h.Clients[0].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: 0xC5C5C5C5C5C5C5C5, Name: "Alpha"))));
        Run(1);
        var w = h.Server.World;
        int id = h.Clients[0].PlayerId;
        // a fortress battle (the fortress tests' stub arena, the war's front at the stronghold), as FinaleRig does on a bare World
        w.Tfw!.Warfront!.Fortress = WarfrontFortressSetup.For(new WarfrontFortressBattleTests.StubArena());
        w.Tfw.War.SetFrontsForTests(WarRules.FrontStronghold);
        Assert.True(w.Tfw.Enqueue(id, new TfwCommandRecord(1, TfwCommand.Deploy, 0)));
        Run(8);
        var battle = w.Tfw.Battle;
        Assert.NotNull(battle);
        Assert.True(battle!.ChaosFortress);
        var breaches = w.WarfrontChaosBreaches;
        Assert.True(breaches is { AnyStanding: true }, "the fortress approach's breaches stand (the order test needs one)");
        // the bodyguard falls (C5's rig): the next tick is the snapshot, the silent close and the first duel state
        got.Clear();
        foreach (ushort chosen in battle.BossOrks.ToList()) w.Musou!.Damage(chosen, id, 1e7f, Vector3.Zero, out _);
        Run(1);
        Assert.False(breaches.AnyStanding);
        var sections = got.Select(b => (Bytes: b, Breach: WarfrontChaosWire.TryDecode(b, out var s) ? s : default, Duel: WarfrontChaosDuelWire.TryDecode(b, out var d) ? d : default)).ToList();
        int firstState = sections.FindIndex(x => x.Duel.Kind == WarfrontChaosDuelWire.KindState);
        int firstClosedList = sections.FindIndex(x => x.Breach.Kind == WarfrontChaosWire.KindBreaches && x.Breach.Breaches!.Length > 0 && x.Breach.Breaches.All(b => b.State == WarfrontBreachState.Closed));
        Assert.True(firstState >= 0, "a duel state went out");
        Assert.True(firstClosedList >= 0, "the list with every breach closed went out");
        Assert.True(firstState < firstClosedList, $"kind 16 (section {firstState}) before the Closed list (section {firstClosedList})");
        Assert.NotEqual(WarfrontDuelPhase.Bodyguard, sections[firstState].Duel.Duel.Phase);
        Assert.DoesNotContain(sections, x => x.Breach.Kind == WarfrontChaosWire.KindClosure);   // silent: no kind 2
    }

    // ---- C5c review (Fable): the end's resend, the Lost state's health, the server's outcome mapping -------------------------------------------------

    /// <summary>A Chaos fortress stage on the server harness with one identified human, the duel begun (the Chosen killed); every tag 51 section the client got from then on.</summary>
    static (Harness H, List<byte[]> Got, Action<double> Run, int Id) Relay()
    {
        var config = BreachRig.Chaos() with { Extras = BreachRig.Chaos().Extras | ContentExtras.Musou | ContentExtras.Warfront };
        var map = GameServer.TfwGameMap(TfwTestArena.Data, "wf_b1");
        var h = new Harness(LinkConditions.Perfect, 5, 1, config, new ServerOptions { Match = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        var got = new List<byte[]>();
        void Run(double s) => h.Run(TimeSpan.FromSeconds(s), after: () => { foreach (var x in h.Clients[0].TakeExtras()) if (x.Tag == WarfrontChaosWire.Tag) got.Add(x.Payload); });
        Run(2);
        Assert.True(h.Clients[0].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: 0xC5C5C5C5C5C5C5C6, Name: "Alpha"))));
        Run(1);
        var w = h.Server.World;
        int id = h.Clients[0].PlayerId;
        w.Tfw!.Warfront!.Fortress = WarfrontFortressSetup.For(new WarfrontFortressBattleTests.StubArena());
        w.Tfw.War.SetFrontsForTests(WarRules.FrontStronghold);
        Assert.True(w.Tfw.Enqueue(id, new TfwCommandRecord(1, TfwCommand.Deploy, 0)));
        Run(8);
        var battle = w.Tfw.Battle!;
        Assert.True(battle.ChaosFortress);
        foreach (ushort chosen in battle.BossOrks.ToList()) w.Musou!.Damage(chosen, id, 1e7f, Vector3.Zero, out _);
        Run(0.5);
        Assert.Equal(WarfrontDuelPhase.Duel, w.WarfrontChaosDuel!.Phase);
        got.Clear();
        return (h, got, Run, id);
    }

    static List<WarfrontChaosDuelWire.State> States(List<byte[]> got) =>
        got.Select(b => WarfrontChaosDuelWire.TryDecode(b, out var d) && d.Kind == WarfrontChaosDuelWire.KindState ? d.Duel : (WarfrontChaosDuelWire.State?)null).OfType<WarfrontChaosDuelWire.State>().ToList();

    [Fact]
    public void The_end_is_resent_for_three_seconds_then_only_a_change_goes_out()
    {
        var (h, got, run, id) = Relay();
        var w = h.Server.World;
        ushort lord = w.Tfw!.Battle!.BossId;
        for (int k = 0; k < 60 && w.Musou!.TryGet(lord, out var l) && l.Alive; k++) { w.Musou.Damage(lord, id, l.Health + 1f, Vector3.Zero, out _); run(0.1); }
        Assert.Equal(WarfrontDuelPhase.Won, w.WarfrontChaosDuel!.Phase);
        got.Clear();
        run(1.0);
        Assert.Contains(States(got), s => s.Phase == WarfrontDuelPhase.Won);   // the 2 Hz repeat of the end, within its 3 s
        run(WarfrontDuelRules.EndResendSeconds + 0.5);
        got.Clear();
        run(2.0);
        Assert.DoesNotContain(States(got), s => s.Phase == WarfrontDuelPhase.Won);   // past the 3 s: nothing more (no change)
    }

    [Fact]
    public void A_lost_relay_sends_the_lords_last_health_and_the_campaign_records_FinaleLost()
    {
        var (h, got, run, id) = Relay();
        var w = h.Server.World;
        Assert.True(w.Musou!.TryGet(w.Tfw!.Battle!.BossId, out var lord));
        float before = lord.Health;
        Assert.True(w.TfwKillForTests(id));
        run(1.5);
        var lost = States(got).Where(s => s.Phase == WarfrontDuelPhase.Lost).ToList();
        Assert.NotEmpty(lost);
        Assert.All(lost, s => Assert.True(s.Health > 0f && s.Permille > 0, $"the Lost state carries the standing Lord's health ({s.Health} hp, {s.Permille} permille), not 0"));
        Assert.Equal(BitConverter.SingleToInt32Bits(before), BitConverter.SingleToInt32Bits(lost[0].Health));   // nobody hurt him: the last health is his health at the fall
        Assert.True(h.Server.WarfrontChaosFinaleLostForTests(TfwMapResult.Lost));   // the relay ran out: the battle's loss is FinaleLost (the campaign record itself needs a real deployment: the loopback run in the milestone)
    }

    [Fact]
    public void The_last_human_leaving_during_the_relay_maps_to_FinaleLost_on_the_server()
    {
        var (h, _, run, id) = Relay();
        Assert.True(h.Server.WarfrontChaosFinaleLostForTests(TfwMapResult.Left));    // the relay is on: leaving never dodges the one-attempt rule
        Assert.False(h.Server.WarfrontChaosFinaleLostForTests(TfwMapResult.Won));    // a win is a win
        // the Lord killed by the duelist: the relay is over, a later leave is no FinaleLost
        var w = h.Server.World;
        ushort lord = w.Tfw!.Battle!.BossId;
        for (int k = 0; k < 60 && w.Musou!.TryGet(lord, out var l) && l.Alive; k++) { w.Musou.Damage(lord, id, l.Health + 1f, Vector3.Zero, out _); run(0.1); }
        Assert.Equal(WarfrontDuelPhase.Won, w.WarfrontChaosDuel!.Phase);
        Assert.False(h.Server.WarfrontChaosFinaleLostForTests(TfwMapResult.Left));
    }

    [Theory]
    [InlineData(false, null, false)]   // no developer tools (a non-loopback bind never has them)
    [InlineData(false, "C:\\private", false)]
    [InlineData(true, null, false)]    // developer tools but the default campaign: refused, it could end the owner's run
    [InlineData(true, "  ", false)]
    [InlineData(true, "C:\\private", true)]
    public void The_finale_aid_acts_only_with_dev_tools_and_a_private_run_folder(bool devTools, string? dir, bool acts)
    {
        string? why = GameServer.FinaleAidRefusal(devTools, dir);
        Assert.Equal(acts, why is null);
    }
}
