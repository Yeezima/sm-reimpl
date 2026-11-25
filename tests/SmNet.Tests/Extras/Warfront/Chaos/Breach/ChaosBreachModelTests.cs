using SmClient.Extras.Warfront.Chaos.Breach;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos;

namespace SmNet.Tests.Extras.Warfront.Chaos.Breach;

/// <summary>Chaos Warfront C4c: the client's breach model (tag 51 kinds 1 and 2), the route of C5's kinds, and the envelope's one forward line.</summary>
public class ChaosBreachModelTests
{
    static WarfrontChaosWire.Breach B(byte id, WarfrontBreachState state, byte alive = 0, short x = 100, short y = -200, short z = 30) => new(id, x, y, z, state, alive);

    static byte[] List(params WarfrontChaosWire.Breach[] list) => WarfrontChaosWire.EncodeBreaches(list);
    static byte[] Closure(uint tick, byte id, ushort closer) => WarfrontChaosWire.EncodeClosure(new WarfrontChaosWire.Closure(tick, id, closer));

    [Fact]
    public void ApplyListAndClosure()
    {
        var m = new ChaosBreachModel();
        Assert.True(m.Apply(List(B(1, WarfrontBreachState.Open, 5), B(2, WarfrontBreachState.Opening)), 10.0, out var none));
        Assert.Null(none);
        Assert.Equal(2, m.Breaches.Count);
        Assert.Equal(5, m.Breaches[0].Alive);
        Assert.Equal(ChaosBreachMarkerKind.Open, m.Marker(m.Breaches[0], 10.0));
        Assert.True(m.Standing(m.Breaches[1]));

        Assert.True(m.Apply(Closure(900, 1, 3), 11.0, out var first));
        Assert.NotNull(first);
        Assert.Equal((byte)1, first!.Value.Breach);
        Assert.Equal((ushort)3, first.Value.Closer);
        Assert.Equal(m.Breaches[0].Centre, first.Value.Centre);   // the list's last centre (foot + 1.5 m)
        // the two repeats of the same (tick, id) are kept quiet
        Assert.True(m.Apply(Closure(900, 1, 3), 11.1, out var again));
        Assert.Null(again);
        Assert.True(m.Apply(Closure(900, 1, 3), 11.2, out again));
        Assert.Null(again);
        Assert.Equal(1, m.ClosuresTaken);
        Assert.Equal(2, m.Repeats);
        // the closure stands down the breach before the list says so, and the list's Closed keeps it down
        Assert.False(m.Standing(m.Breaches[0]));
        m.Apply(List(B(1, WarfrontBreachState.Closed, 5), B(2, WarfrontBreachState.Open)), 11.5, out _);
        Assert.False(m.Standing(m.Breaches[0]));
        Assert.Equal(ChaosBreachMarkerKind.Closed, m.Marker(m.Breaches[0], 11.5));
        Assert.Equal(ChaosBreachMarkerKind.Open, m.Marker(m.Breaches[1], 11.5));
        // the end of the battle (an empty list) forgets the closures: the next battle's ids start again
        m.Apply(List(), 20.0, out _);
        Assert.Empty(m.Breaches);
        Assert.Null(m.ClosureOf(1));
        Assert.True(m.Apply(Closure(900, 1, 3), 21.0, out var later));
        Assert.NotNull(later);
        Assert.Null(later!.Value.Centre);   // a closure of a breach no list named: no place for its show
    }

    [Fact]
    public void ClosureShownThreeSeconds()
    {
        var m = new ChaosBreachModel();
        m.Apply(List(B(4, WarfrontBreachState.Open, 2)), 0.0, out _);
        m.Apply(Closure(50, 4, 1), 1.0, out _);
        m.Apply(List(B(4, WarfrontBreachState.Closed, 2)), 1.0, out _);
        var b = m.Breaches[0];
        Assert.Equal(ChaosBreachMarkerKind.Closed, m.Marker(b, 1.0));
        Assert.Equal(ChaosBreachMarkerKind.Closed, m.Marker(b, 3.99));
        Assert.Equal(ChaosBreachMarkerKind.Hidden, m.Marker(b, 4.0));
        Assert.Equal(ChaosBreachMarkerKind.Hidden, m.Marker(b, 60.0));
        // C5's silent close (Closed in the list, no closure): no marker at all
        var silent = new ChaosBreachModel();
        silent.Apply(List(B(1, WarfrontBreachState.Closed)), 0.0, out _);
        Assert.Equal(ChaosBreachMarkerKind.Hidden, silent.Marker(silent.Breaches[0], 0.0));
        Assert.False(silent.Standing(silent.Breaches[0]));
    }

    /// <summary>Lead 40 (C4s review): a lost closure burst still plays: the list's open-to-closed change is a closure; the late kind 2 is then a repeat. C5c's silent close shows nothing.</summary>
    [Fact]
    public void ListChangeClosesWhenTheClosureIsLost()
    {
        var m = new ChaosBreachModel();
        m.Apply(List(B(1, WarfrontBreachState.Open, 3), B(2, WarfrontBreachState.Open)), 0.0, out _);
        Assert.True(m.Apply(List(B(1, WarfrontBreachState.Closed, 3), B(2, WarfrontBreachState.Open)), 1.0, out var derived));
        Assert.NotNull(derived);
        Assert.Single(m.NewClosures);
        Assert.Equal((byte)1, derived!.Value.Breach);
        Assert.Equal(m.Breaches[0].Centre, derived.Value.Centre);
        Assert.Equal(ChaosBreachMarkerKind.Closed, m.Marker(m.Breaches[0], 1.5));
        Assert.True(m.Apply(Closure(77, 1, 4), 1.1, out var late));   // the burst arrives after all: no second show
        Assert.Null(late);
        Assert.Empty(m.NewClosures);
        Assert.True(m.Apply(List(B(1, WarfrontBreachState.Closed, 3), B(2, WarfrontBreachState.Open)), 2.0, out var again));
        Assert.Null(again);   // a closed breach closes once
        Assert.Equal(1, m.ClosuresTaken);

        var finale = new ChaosBreachModel { SilentCloses = true };
        finale.Apply(List(B(1, WarfrontBreachState.Open)), 0.0, out _);
        finale.Apply(List(B(1, WarfrontBreachState.Closed)), 1.0, out var quiet);
        Assert.Null(quiet);
        Assert.Equal(ChaosBreachMarkerKind.Hidden, finale.Marker(finale.Breaches[0], 1.0));
        Assert.False(finale.Standing(finale.Breaches[0]));
    }

    [Fact]
    public void DuelKindsForwarded()
    {
        for (int k = WarfrontChaosWire.DuelKindFirst; k <= WarfrontChaosWire.DuelKindLast; k++)
            Assert.Equal(ChaosSectionRoute.Duel, ChaosBreachModel.Route([(byte)k, 1, 2, 3]));
        Assert.Equal(ChaosSectionRoute.Breach, ChaosBreachModel.Route(List()));
        Assert.Equal(ChaosSectionRoute.Breach, ChaosBreachModel.Route(Closure(1, 1, 1)));
        foreach (byte k in new byte[] { 0, 3, 15, 32, 255 }) Assert.Equal(ChaosSectionRoute.Refused, ChaosBreachModel.Route([k]));
        Assert.Equal(ChaosSectionRoute.Refused, ChaosBreachModel.Route([]));
        // the model never takes a duel kind as a breach section
        var m = new ChaosBreachModel();
        Assert.False(m.Apply([16, 0, 0], 0.0, out _));
        Assert.Equal(1, m.Dropped);
    }

    [Fact]
    public void MalformedSectionsChangeNothing()
    {
        var m = new ChaosBreachModel();
        m.Apply(List(B(1, WarfrontBreachState.Open, 1)), 0.0, out _);
        byte[] bad = List(B(1, WarfrontBreachState.Open, 1));
        bad[2 + 7] = 3;   // a state above 2
        Assert.False(m.Apply(bad, 1.0, out _));
        Assert.False(m.Apply([WarfrontChaosWire.KindClosure, 1, 2], 1.0, out _));
        Assert.False(m.Apply([WarfrontChaosWire.KindBreaches, 9], 1.0, out _));
        Assert.Single(m.Breaches);
        Assert.Equal(0.0, m.ListAt);
        Assert.Equal(3, m.Dropped);
        var rng = new Random(51);
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[rng.Next(0, 40)];
            rng.NextBytes(junk);
            m.Apply(junk, 2.0, out _);   // never throws
        }
    }

    [Fact]
    public void StaleListClears()
    {
        var m = new ChaosBreachModel();
        m.Apply(List(B(1, WarfrontBreachState.Open)), 0.0, out _);
        Assert.False(m.Tick(ChaosBreachLook.StaleSeconds));
        Assert.True(m.Tick(ChaosBreachLook.StaleSeconds + 0.01));
        Assert.Empty(m.Breaches);
        Assert.False(m.Tick(100.0));
    }

    [Fact]
    public void LookNumbers()
    {
        Assert.Equal("WARP BREACH  52 m", ChaosBreachLook.OpenLine(51.6f));
        Assert.Equal("WARP BREACH  0 m", ChaosBreachLook.OpenLine(float.NaN));
        Assert.Equal(2.0, ChaosBreachLook.ArcsDelay(0.0));
        Assert.Equal(5.0, ChaosBreachLook.ArcsDelay(1.0));
        Assert.Equal(5.0, ChaosBreachLook.ArcsDelay(7.0));
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found above the test assembly");
    }

    /// <summary>The envelope's tag 51 forward is one case to <c>WarfrontChaosSection</c> (pinned: the C4c owned edit), and the breach file forwards C5's kinds to the declared <c>WarfrontChaosDuelSection</c>.</summary>
    [Fact]
    public void EnvelopeForwardIsPinned()
    {
        string root = RepoRoot();
        var envelope = File.ReadAllLines(Path.Combine(root, "client/Extras/Warfront/Stage/NetworkView.WarfrontEnvelope.cs"));
        int at = Array.FindIndex(envelope, l => l.Contains("case SmNet.Extras.Warfront.WarfrontChaosWire.Tag:", StringComparison.Ordinal));
        Assert.True(at >= 0, "the envelope has no tag 51 case");
        Assert.Contains("WarfrontChaosSection(tag, payload, ref handled);", envelope[at + 1]);
        Assert.Equal(1, envelope.Count(l => l.Contains("WarfrontChaosWire", StringComparison.Ordinal)));
        Assert.Equal(1, envelope.Count(l => l.TrimStart().StartsWith("partial void WarfrontChaosSection(", StringComparison.Ordinal)));
        string breach = File.ReadAllText(Path.Combine(root, "client/Extras/Warfront/Chaos/Breach/NetworkView.WarfrontChaosBreach.cs"));
        Assert.Contains("partial void WarfrontChaosDuelSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled);", breach);
        Assert.Contains("WarfrontChaosDuelSection(tag, payload, ref handled);", breach);
    }
}
