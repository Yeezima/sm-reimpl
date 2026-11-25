using System.Numerics;
using SmNet.Extras.Executions;
using SmServer;
using SmSim;
using SmSim.Extras.Executions;
using WireOffer = SmNet.Extras.Executions.ExecutionOffer;

namespace SmNet.Tests.Extras.Executions;

/// <summary>Executions X3 on a real server over the loopback network: what tag 50 carries and to whom, and that a faithful or Musou server never sends it.</summary>
public class ExecutionServerSendTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0);

    sealed record Heard(uint ServerTick, byte Kind, ExecutionStart Start, ExecutionEnd End, WireOffer Offer, OvershieldEntry[] Shields);

    static List<Heard> Take(GameClient c)
    {
        var list = new List<Heard>();
        foreach (var s in c.TakeExtras())
        {
            if (s.Tag != ExecutionWire.Tag) continue;
            byte kind = ExecutionWire.KindOf(s.Payload);
            ExecutionWire.TryDecode(s.Payload, out ExecutionStart start);
            ExecutionWire.TryDecode(s.Payload, out ExecutionEnd end);
            ExecutionWire.TryDecode(s.Payload, out WireOffer offer);
            ExecutionWire.TryDecode(s.Payload, out OvershieldEntry[] shields);
            list.Add(new Heard(s.ServerTick, kind, start, end, offer, shields));
        }
        return list;
    }

    static Harness Connected(int clients = 2, SimConfig? c = null)
    {
        var h = new Harness(Link, seed: 5, clients, c ?? ExecutionTestKit.Config());
        h.Run(TimeSpan.FromSeconds(1.5));
        foreach (var client in h.Clients) Assert.Equal(ClientStatus.Connected, client.Status);
        foreach (var client in h.Clients) client.TakeExtras();
        return h;
    }

    /// <summary>A run of the first client's player where it stands, starting with the server's next tick.</summary>
    static ExecutionRun Inject(Harness h, string row = "C1.chainsword")
    {
        var w = h.Server.World;
        int id = h.Clients[0].PlayerId;
        var p = w.Players[id];
        var run = new ExecutionRun(id, ExecutionTargetKind.Player, h.Clients[1].PlayerId, w.Config.Executions.WireIndexOf(row), 1.3f, p.Position, p.Position, p.Yaw, 0, w.Tick + 1, false);
        w.ExecutionsStartForTests(run);
        return ExecutionTrack.Quantize(run);
    }

    [Fact]
    public void StartSentThreeTimesToAll()
    {
        var h = Connected();
        var run = Inject(h);
        h.Run(TimeSpan.FromMilliseconds(300));   // 9 ticks
        foreach (var c in h.Clients)
        {
            var starts = Take(c).Where(x => x.Kind == ExecutionWire.StartKind && x.Start.Executor == run.Executor).ToList();
            Assert.InRange(starts.Count, 3, 4);   // the three repeats (plus at most one 2 Hz resend of the live list, which may fall in the same 9 ticks)
            Assert.All(starts, s => Assert.Equal(run, s.Start.ToRun()));
            var ticks = starts.Select(s => s.ServerTick).Distinct().Order().ToArray();
            Assert.Equal([ticks[0], ticks[0] + 1, ticks[0] + 2], ticks.Take(3));   // three consecutive messages
        }
        Assert.Equal(1, h.Server.ExecutionStartsSent);
    }

    [Fact]
    public void EndSentThreeTimes()
    {
        var h = Connected();
        var run = Inject(h);
        h.Run(TimeSpan.FromMilliseconds(200));
        h.Server.World.ExecutionsAbort(run.Executor, ExecutionEndReason.Invalid);
        h.Run(TimeSpan.FromMilliseconds(300));
        foreach (var c in h.Clients)
        {
            var ends = Take(c).Where(x => x.Kind == ExecutionWire.EndKind).ToList();
            Assert.Equal(3, ends.Count);
            Assert.All(ends, e => Assert.Equal((ushort)run.Executor, e.End.Executor));
            Assert.All(ends, e => Assert.Equal(ExecutionEndReason.Invalid, e.End.Reason));
        }
    }

    [Fact]
    public void LiveListResentTwoHz()
    {
        var h = Connected();
        var run = Inject(h);
        h.Run(TimeSpan.FromMilliseconds(400));
        foreach (var c in h.Clients) Take(c);
        h.Run(TimeSpan.FromMilliseconds(1000));   // a 2 s move: still live
        Assert.True(h.Server.World.ExecutionsLocked(run.Executor));
        var resent = Take(h.Clients[1]).Where(x => x.Kind == ExecutionWire.StartKind && x.Start.Executor == run.Executor).ToList();
        Assert.InRange(resent.Count, 2, 3);   // twice a second
        // a late joiner hears it within half a second
        var late = h.AddClient(nonce: 700);
        h.Run(TimeSpan.FromMilliseconds(700));
        Assert.Contains(Take(late), x => x.Kind == ExecutionWire.StartKind && x.Start.Executor == run.Executor);
    }

    [Fact]
    public void OvershieldListEmptyInClassic()
    {
        // Lead 40: Classic never encodes or sends the list (Warfront only, X4).
        var h = Connected();
        h.Run(TimeSpan.FromMilliseconds(1500));
        Assert.DoesNotContain(Take(h.Clients[0]), x => x.Kind == ExecutionWire.OvershieldKind);
    }

    [Fact]
    public void Refusals_before_the_choice_are_counted_not_logged()
    {
        var h = Connected();
        var log = new List<string>();
        h.Server.Log += log.Add;
        h.Run(TimeSpan.FromSeconds(1), (index, t) => new PlayerInput(t, 0, 0, 0, index == 0 && t % 2 == 0 ? InputButtons.Execute : InputButtons.None));
        Assert.True(h.Server.ExecutionRefusalsCounted > 5);
        Assert.DoesNotContain(log, l => l.Contains("did nothing"));
    }

    [Fact]
    public void OfferOnlyToKiller()
    {
        var h = Connected(3);
        var w = h.Server.World;
        for (int i = 0; i < 400 && w.State is not (MatchState.Playing or MatchState.None); i++) h.Run(TimeSpan.FromMilliseconds(50));
        int killer = h.Clients[0].PlayerId, victim = h.Clients[1].PlayerId;
        Assert.True(w.Rules is null || World.Hostile(w.Players[killer].Team, w.Players[victim].Team));
        w.ExecutionsPlaceForTests(killer, new Vector3(0f, 0f, 0f), 0f);
        w.ExecutionsPlaceForTests(victim, new Vector3(0f, 1.2f, 0f), MathF.PI);
        w.ExecutionsPlaceForTests(h.Clients[2].PlayerId, new Vector3(20f, 0f, 0f), 0f);
        foreach (var c in h.Clients) Take(c);
        bool pressed = false;
        h.Run(TimeSpan.FromMilliseconds(1500), (index, t) =>
        {
            var b = index == 0 && !pressed ? InputButtons.Melee : InputButtons.None;
            if (index == 0) pressed = true;
            return new PlayerInput(t, 0, 0, index == 0 ? 0f : MathF.PI, b);
        });
        Assert.Contains(w.KillLog, k => k.Killer == killer && k.Victim == victim && k.Cause == KillCause.Melee);
        var offers = Take(h.Clients[0]).Where(x => x.Kind == ExecutionWire.OfferKind).ToList();
        var offer = Assert.Single(offers).Offer;
        Assert.Equal((ushort)victim, offer.Victim);
        Assert.Equal(24, offer.WindowTicks);
        Assert.DoesNotContain(Take(h.Clients[1]), x => x.Kind == ExecutionWire.OfferKind);
        Assert.DoesNotContain(Take(h.Clients[2]), x => x.Kind == ExecutionWire.OfferKind);
    }

    [Fact]
    public void ClipIndexIsCatalogueIndexNotVariant()
    {
        // Three Chaos Marine rows at wire places 16, 17 and 18 (holes before them): the wire carries the place in ExecutionRows.All, never the kept rows' or the filtered list's.
        var rows = ExecutionRowSet.OfAll([ExecutionTestKit.Row("C1.chainsword"), ExecutionTestKit.Row("C2.power_axe"), ExecutionTestKit.Row("C3.thunder_hammer")]);
        Assert.Equal([16, 17, 18], new[] { "C1.chainsword", "C2.power_axe", "C3.thunder_hammer" }.Select(rows.WireIndexOf));
        var w = ExecutionTestKit.Arena(ExecutionTestKit.Config(rows), (ExecutionTestKit.Tactical, Vector3.Zero), (ExecutionTestKit.Chaos, new Vector3(0f, 1.2f, 0f)));
        uint kill = ExecutionTestKit.KillByMelee(w);
        ExecutionTestKit.Tap(w, 1, kill + 1);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        var row = rows.AtWire(run.Wire)!;
        Assert.InRange(run.Wire, 16, 18);
        Assert.Equal(rows.WireIndexOf(row.Id), run.Wire);
        Assert.NotEqual(rows.IndexOf(row.Id), run.Wire);
        Assert.True(ExecutionWire.TryDecode(ExecutionWire.Encode(ExecutionStart.Of(run)), out ExecutionStart start));
        Assert.Same(row, rows.AtWire(start.Clip));
        // a receiver whose install lacks the row draws nothing (the normal kill stands)
        Assert.Null(ExecutionRowSet.OfAll([ExecutionTestKit.Row("C1.chainsword")]).AtWire(start.Clip == 16 ? 17 : start.Clip));
    }

    [Theory]
    [InlineData(ContentExtras.None)]
    [InlineData(ContentExtras.Musou)]
    public void FaithfulAndMusouServersNeverSendTag50(ContentExtras extras)
    {
        var h = Connected(2, ExecutionTestKit.Config(extras: extras));
        h.Run(TimeSpan.FromSeconds(2), (index, t) => new PlayerInput(t, 0, 0, 0, t % 4 == 0 ? InputButtons.Execute | InputButtons.Melee : InputButtons.None));
        foreach (var c in h.Clients) Assert.Empty(Take(c));
        Assert.Equal(0, h.Server.ExecutionMessagesSent);
        Assert.Empty(h.Server.World.ExecutionsLive);
    }

    [Fact]
    public void FaithfulServerNeverSendsTag50() => FaithfulAndMusouServersNeverSendTag50(ContentExtras.None);

    [Fact]
    public void MusouServerNeverSendsTag50() => FaithfulAndMusouServersNeverSendTag50(ContentExtras.Musou);

    [Theory]
    [InlineData("extended")]
    [InlineData("orks,executions")]
    public void Warfront_dedicated_refuses_a_superset_content(string content)
    {
        Assert.False(ServerCommandLine.TryParse(["--warfront-dedicated", "--content", content], out _, out string error));
        Assert.Contains("musou or warfront", error);
        Assert.True(ServerCommandLine.TryParse(["--warfront-dedicated", "--content", "musou"], out var a, out _));
        Assert.Equal(ContentProfiles.WarfrontProfile, a.Content);
    }
}
