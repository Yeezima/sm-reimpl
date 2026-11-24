using System.Numerics;
using SmClient;
using SmNet.Extras.Executions;
using SmSim.Extras.Executions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>Executions X5a: the client's tag-50 state (client/Extras/Executions/ExecutionClientState.cs): repeats and reordering never restart a move; ends, offers and overshields expire.</summary>
public class ExecutionClientStateTests
{
    static byte[] Start(uint tick, ushort executor, ushort target = 9, ushort clip = 3) =>
        ExecutionWire.Encode(new ExecutionStart(tick, executor, ExecutionTargetKind.Player, target, clip, new Vector3(1f, 2f, 3f), 0.5f, 3.5f, 4, false));
    static byte[] End(uint tick, ushort executor, ExecutionEndReason why = ExecutionEndReason.Done) => ExecutionWire.Encode(new ExecutionEnd(tick, executor, why));

    [Fact]
    public void DuplicateStartsIgnored()
    {
        var s = new ExecutionClientState();
        s.Apply(Start(100, 4), 0.0, 1);
        int version = s.Version;
        s.Apply(Start(100, 4), 0.03, 1);
        s.Apply(Start(100, 4), 0.06, 1);
        s.Apply(Start(100, 4), 0.5, 1);   // the 2 Hz live list
        Assert.Single(s.Live);
        Assert.Equal(version, s.Version);
        Assert.Equal(3, s.Repeats);
        Assert.True(s.TryLive(4, out var run));
        Assert.Equal(100u, run.StartTick);
        Assert.Equal(3, run.Wire); Assert.Equal(3.5f, run.Distance); Assert.Equal(4, run.SlideTicks);
    }

    [Fact]
    public void EndRemovesRun()
    {
        var s = new ExecutionClientState();
        s.Apply(Start(100, 4), 0.0, 4);
        s.Apply(End(160, 4), 2.0, 4);
        Assert.Empty(s.Live);
        Assert.Single(s.Ended);
        Assert.Equal(ExecutionEndReason.Done, s.Ended[0].Reason);
        Assert.Equal(160u, s.OwnLastDoneTick);
        // Its repeats change nothing, and a late repeat of the Start does not revive it.
        s.Apply(End(160, 4), 2.03, 4);
        s.Apply(Start(100, 4), 2.06, 4);
        Assert.Empty(s.Live);
        Assert.Single(s.Ended);
        // The fade keeps it for half a second.
        s.Prune(2.4, 170);
        Assert.Single(s.Ended);
        s.Prune(2.6, 170);
        Assert.Empty(s.Ended);
    }

    [Fact]
    public void EndBeforeStartKeepsItEnded()
    {
        var s = new ExecutionClientState();
        s.Apply(End(160, 4), 0.0, 1);
        s.Apply(Start(100, 4), 0.01, 1);
        Assert.Empty(s.Live);
        s.Apply(Start(200, 4), 0.02, 1);   // a later move of the same executor plays
        Assert.True(s.TryLive(4, out var run));
        Assert.Equal(200u, run.StartTick);
    }

    [Fact]
    public void LateJoinLiveListAdds()
    {
        var s = new ExecutionClientState();
        s.Apply(Start(5000, 2, target: 11), 0.0, 7);   // heard first through the live list, long after its start
        Assert.True(s.TryLive(2, out var run));
        Assert.True(s.TryTarget(ExecutionTargetKind.Player, 11, out var byTarget));
        Assert.Equal(run, byTarget);
        Assert.False(s.TryTarget(ExecutionTargetKind.Enemy, 11, out _));
    }

    [Fact]
    public void NewerStartReplacesARunWhoseEndWasLost()
    {
        var s = new ExecutionClientState();
        s.Apply(Start(100, 4), 0.0, 1);
        s.Apply(Start(400, 4), 10.0, 1);
        Assert.True(s.TryLive(4, out var run));
        Assert.Equal(400u, run.StartTick);
        Assert.Single(s.Ended);
    }

    [Fact]
    public void OfferExpiresAfterWindow()
    {
        var s = new ExecutionClientState();
        s.Apply(ExecutionWire.Encode(new ExecutionOffer(9, 1000, 24)), 0.0, 1);
        Assert.NotNull(s.Offer);
        Assert.True(s.Offer!.Value.OpenAt(1000));
        Assert.True(s.Offer!.Value.OpenAt(1024));
        Assert.False(s.Offer!.Value.OpenAt(1025));
        Assert.False(s.Offer!.Value.OpenAt(999));
        s.Prune(0.5, 1024);
        Assert.NotNull(s.Offer);
        s.Prune(0.9, 1025);
        Assert.Null(s.Offer);
    }

    [Fact]
    public void OfferTakenByItsStart()
    {
        var s = new ExecutionClientState();
        s.Apply(ExecutionWire.Encode(new ExecutionOffer(9, 1000, 24)), 0.0, 1);
        s.Apply(Start(1010, 1, target: 9), 0.1, 1);
        Assert.Null(s.Offer);
    }

    [Fact]
    public void OvershieldExpires()
    {
        var s = new ExecutionClientState();
        s.Apply(ExecutionWire.Encode([new OvershieldEntry(3, 255), new OvershieldEntry(5, 51)]), 0.0, 1);
        Assert.Equal(1f, s.OvershieldOf(3));
        Assert.Equal(0.2f, s.OvershieldOf(5), 3);
        Assert.Equal(0f, s.OvershieldOf(4));
        s.Apply(ExecutionWire.Encode([new OvershieldEntry(5, 102)]), 1.0, 1);   // a new list replaces the old
        Assert.Equal(0f, s.OvershieldOf(3));
        s.Prune(3.4, 0);
        Assert.Equal(0.4f, s.OvershieldOf(5), 3);
        s.Prune(3.6, 0);
        Assert.Equal(0f, s.OvershieldOf(5));
    }

    [Fact]
    public void EndAllAndForget()
    {
        var s = new ExecutionClientState();
        s.Apply(Start(100, 4), 0.0, 1);
        s.Apply(Start(100, 6), 0.0, 1);
        s.Forget(4, 0.1);
        Assert.False(s.TryLive(4, out _));
        s.EndAll(0.2);
        Assert.Empty(s.Live);
        Assert.Equal(2, s.Ended.Count);
        Assert.All(s.Ended, e => Assert.Equal(ExecutionEndReason.Disconnect, e.Reason));
    }

    [Fact]
    public void MalformedNeverThrows()
    {
        var s = new ExecutionClientState();
        var random = new Random(50);
        for (int i = 0; i < 10000; i++)
        {
            var b = new byte[random.Next(0, 40)];
            random.NextBytes(b);
            if (b.Length > 0 && random.Next(2) == 0) b[0] = (byte)random.Next(0, 6);
            s.Apply(b, i * 0.01, 1);
            s.Prune(i * 0.01, i);
        }
        Assert.True(s.Malformed > 0);
        s.Apply(ReadOnlySpan<byte>.Empty, 0.0, 1);
    }
}
