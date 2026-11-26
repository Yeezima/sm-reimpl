using System.Numerics;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos;

namespace SmNet.Tests.Extras.Warfront.Chaos.Breach;

/// <summary>Chaos Warfront C4: the Chaos section (tag 51): the breach list (kind 1), the closure (kind 2), C5's kinds 16 to 31 left alone, and only a Chaos stage sends it.</summary>
[Collection("WarfrontStage")]
public class ChaosWireTests : IDisposable
{
    [Fact]
    public void BreachListRoundTrip()
    {
        var b = WarfrontBreaches.At(30, new Vector3(12.34f, -56.78f, 3.21f), new Vector3(-3000f, 4000f, 0f));
        var f = new BreachRig.Field();
        for (int t = 0; t <= WarfrontBreachRules.Ticks(6f, 30); t++) b.Step(t, f);
        b.TryClose(b.All[1].Centre, 4, 9);
        byte[] bytes = WarfrontChaosWire.EncodeBreaches(b);
        Assert.Equal(2 + 9 * 2, bytes.Length);
        Assert.True(WarfrontChaosWire.TryDecode(bytes, out var s));
        Assert.Equal(WarfrontChaosWire.KindBreaches, s.Kind);
        var list = s.Breaches!;
        Assert.Equal(2, list.Length);
        Assert.Equal(1, list[0].Id);
        Assert.Equal(WarfrontBreachState.Open, list[0].State);
        Assert.Equal(5, list[0].Alive);
        Assert.Equal(12.3f, list[0].Position.X, 3);
        Assert.Equal(-56.8f, list[0].Position.Y, 3);
        Assert.Equal(3.2f, list[0].Position.Z, 3);
        Assert.Equal(list[0].Position + new Vector3(0f, 0f, WarfrontBreachRules.CentreHeight), list[0].Centre);
        Assert.Equal(WarfrontBreachState.Closed, list[1].State);
        Assert.Equal(-3000f, list[1].Position.X, 3);   // decimetres to +-3276.7 m
        Assert.Equal(short.MaxValue, WarfrontChaosWire.Of(WarfrontBreaches.At(30, new Vector3(9999f, 0f, 0f)).All[0]).Xdm);   // clamped
        byte[] empty = WarfrontChaosWire.EncodeBreaches((WarfrontBreaches?)null);
        Assert.Equal([1, 0], empty);
        Assert.True(WarfrontChaosWire.TryDecode(empty, out var e) && e.Breaches!.Length == 0);
    }

    [Fact]
    public void ClosureRoundTrip()
    {
        var c = new WarfrontChaosWire.Closure(0xDEADBEEF, 3, 0xBEEF);
        byte[] bytes = WarfrontChaosWire.EncodeClosure(c);
        Assert.Equal(WarfrontChaosWire.ClosureLength, bytes.Length);
        Assert.True(WarfrontChaosWire.TryDecode(bytes, out var s));
        Assert.Equal(WarfrontChaosWire.KindClosure, s.Kind);
        Assert.Equal(c, s.Closed);
        Assert.Equal(new WarfrontChaosWire.Closure(5, 2, 7), WarfrontChaosWire.Of(new WarfrontBreachClosure(5, 2, 7)));
    }

    [Fact]
    public void DecoderNeverThrowsFuzz()
    {
        var rng = new Random(51);
        for (int k = 0; k < 20000; k++)
        {
            var data = new byte[rng.Next(0, 90)];
            rng.NextBytes(data);
            if (data.Length > 0 && k % 3 == 0) data[0] = (byte)rng.Next(0, 4);
            if (WarfrontChaosWire.TryDecode(data, out var s))
            {
                Assert.True(s.Kind is 1 or 2);
                if (s.Kind == 1) Assert.All(s.Breaches!, x => Assert.True((byte)x.State <= 2));
            }
        }
        Assert.False(WarfrontChaosWire.TryDecode([], out _));
        Assert.False(WarfrontChaosWire.TryDecode([1], out _));
        Assert.False(WarfrontChaosWire.TryDecode([1, 9, .. new byte[81]], out _));   // n above 8
        var one = WarfrontChaosWire.EncodeBreaches([new WarfrontChaosWire.Breach(1, 0, 0, 0, WarfrontBreachState.Open, 0)]);
        one[2 + 7] = 3;
        Assert.False(WarfrontChaosWire.TryDecode(one, out _));   // a state above 2
        Assert.False(WarfrontChaosWire.TryDecode(one.AsSpan(0, one.Length - 1), out _));   // short
        Assert.False(WarfrontChaosWire.TryDecode([2, 0, 0, 0, 0, 1, 0], out _));   // a closure one short
        Assert.False(WarfrontChaosWire.TryDecode([3], out _));   // an unknown kind
    }

    [Fact]
    public void Kinds16To31Reserved()
    {
        for (int kind = 0; kind <= 255; kind++)
        {
            byte[] data = [(byte)kind, 0, 0, 0, 0, 0, 0, 0];
            bool duel = WarfrontChaosWire.IsDuelKind(data);
            Assert.Equal(kind is >= 16 and <= 31, duel);
            if (duel) Assert.False(WarfrontChaosWire.TryDecode(data, out _), "C5's kinds are left to its own decoder");
        }
        Assert.Equal(51, WarfrontChaosWire.Tag);
        Assert.Equal(16, WarfrontChaosWire.DuelKindFirst);
        Assert.Equal(31, WarfrontChaosWire.DuelKindLast);
    }

    // ---- the server -------------------------------------------------------------------------------------------------------------------------

    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-chaos-wire-" + Guid.NewGuid().ToString("N"));

    public ChaosWireTests()
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

    /// <summary>A Warfront stage of <paramref name="config"/> on the test arena, one identified client, a battle landed in the stage's own world; every tag-51 section the client got.</summary>
    static (List<byte[]> Sections, GameServer Server, Action<double> Run, List<uint> Ticks) Stage(SimConfig config, double seconds)
    {
        var map = GameServer.TfwGameMap(TfwTestArena.Data, "wf_b1");
        var h = new Harness(LinkConditions.Perfect, 5, 1, config, new ServerOptions { Match = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        var got = new List<byte[]>();
        var ticks = new List<uint>();   // the server tick of each section in got
        void Run(double s) => h.Run(TimeSpan.FromSeconds(s), after: () => { foreach (var x in h.Clients[0].TakeExtras()) if (x.Tag == WarfrontChaosWire.Tag) { got.Add(x.Payload); ticks.Add(x.ServerTick); } });
        Run(2);
        Assert.True(h.Clients[0].SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: 0xC4C4C4C4C4C4C4C4, Name: "Alpha"))));
        Run(1);
        int id = h.Clients[0].PlayerId;
        Assert.True(h.Server.World.Tfw!.Enqueue(id, new TfwCommandRecord(1, TfwCommand.Deploy, 0)));
        Run(seconds);
        return (got, h.Server, Run, ticks);
    }

    static SimConfig StageConfig(SimConfig c) => c with { Extras = c.Extras | ContentExtras.Musou | ContentExtras.Warfront };

    [Fact]
    public void A_chaos_stage_sends_the_breach_list()
    {
        var (got, server, _, _) = Stage(StageConfig(BreachRig.Chaos()), 14);
        var battle = server.World.Tfw!.Battle;
        Assert.NotNull(battle);
        Assert.NotNull(battle.ChaosBreaches);
        Assert.NotEmpty(got);
        var lists = got.Select(b => WarfrontChaosWire.TryDecode(b, out var s) ? s : default).Where(s => s.Kind == WarfrontChaosWire.KindBreaches).ToList();
        Assert.NotEmpty(lists);
        Assert.Contains(lists, s => s.Breaches!.Length == battle.ChaosBreaches.All.Count && s.Breaches.All(x => x.State == WarfrontBreachState.Open) && s.Breaches.Any(x => x.Alive > 0));
    }

    [Fact]
    public void A_closure_is_sent_three_times_three_ticks_apart()
    {
        var (got, server, run, ticks) = Stage(StageConfig(BreachRig.Chaos()), 8);
        var breaches = server.World.WarfrontChaosBreaches!;
        var breach = breaches.All[0];
        uint tick = server.World.Tick;
        Assert.Equal(breach.Id, breaches.TryClose(breach.Centre, 7, tick));
        got.Clear(); ticks.Clear();
        run(1);
        var sections = got.Select(b => WarfrontChaosWire.TryDecode(b, out var s) ? s : default).ToList();
        var at = Enumerable.Range(0, sections.Count).Where(i => sections[i].Kind == WarfrontChaosWire.KindClosure).ToList();
        Assert.Equal(WarfrontChaosWire.ClosureRepeats, at.Count);
        Assert.All(at, i => Assert.Equal(new WarfrontChaosWire.Closure(tick, breach.Id, 7), sections[i].Closed));
        for (int n = 1; n < at.Count; n++) Assert.Equal((uint)WarfrontChaosWire.ClosureRepeatTicks, ticks[at[n]] - ticks[at[n - 1]]);   // three ticks apart
        Assert.Contains(sections, s => s.Kind == WarfrontChaosWire.KindBreaches && s.Breaches!.Single(x => x.Id == breach.Id).State == WarfrontBreachState.Closed);
    }

    [Fact]
    public void The_battles_end_sends_the_empty_list_three_times_three_ticks_apart()
    {
        var (got, server, run, ticks) = Stage(StageConfig(BreachRig.Chaos()), 8);
        Assert.NotNull(server.World.WarfrontChaosBreaches);
        server.World.Tfw!.PickupForTests(server.World);   // the battle ends (as won): its breaches go
        got.Clear(); ticks.Clear();
        run(2);
        Assert.Null(server.World.WarfrontChaosBreaches);
        var empty = Enumerable.Range(0, got.Count).Where(i => WarfrontChaosWire.TryDecode(got[i], out var s) && s.Kind == WarfrontChaosWire.KindBreaches && s.Breaches!.Length == 0).ToList();
        Assert.Equal(WarfrontChaosWire.ClosureRepeats, empty.Count);
        for (int n = 1; n < empty.Count; n++) Assert.Equal((uint)WarfrontChaosWire.ClosureRepeatTicks, ticks[empty[n]] - ticks[empty[n - 1]]);
        Assert.Equal(empty.Count, got.Count(b => WarfrontChaosWire.TryDecode(b, out var s) && s.Kind == WarfrontChaosWire.KindBreaches));   // no list with breaches after the end
    }

    [Fact]
    public void OrkStageNeverSendsTag51()
    {
        var (got, server, _, _) = Stage(StageConfig(BreachRig.Orks()), 9);
        Assert.NotNull(server.World.Tfw!.Battle);
        Assert.Null(server.World.WarfrontChaosBreaches);
        Assert.Empty(got);
    }
}
