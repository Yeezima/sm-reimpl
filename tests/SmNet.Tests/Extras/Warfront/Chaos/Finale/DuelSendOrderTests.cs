using System.Numerics;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Chaos.Breach;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
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
}
