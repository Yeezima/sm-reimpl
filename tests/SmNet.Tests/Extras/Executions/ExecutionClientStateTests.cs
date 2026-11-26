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

    /// <summary>
    /// X4b review (bolt pistol during finishers): the hands rule's condition, shared by <c>ExtrasExecutionMeleeShown</c> and <c>ExtrasExecutionPistolStowed</c>: only an alive player with a live run
    /// holds its melee weapon with the pistol holstered; the victim, a dead executor and everyone after the End do not.
    /// </summary>
    [Fact]
    public void LiveExecutorOnlyWhileAliveAndExecuting()
    {
        var s = new ExecutionClientState();
        Assert.False(s.LiveExecutor(4, alive: true));
        s.Apply(Start(100, 4, target: 9), 0.0, 1);
        Assert.True(s.LiveExecutor(4, alive: true));
        Assert.False(s.LiveExecutor(4, alive: false));   // killed mid-move: the death pose keeps the normal hands
        Assert.False(s.LiveExecutor(9, alive: true));    // the victim is not an executor
        s.Apply(End(160, 4), 2.0, 1);
        Assert.False(s.LiveExecutor(4, alive: true));    // after the move: the pistol comes back
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
        s.Prune(2.4, 170, 170);
        Assert.Single(s.Ended);
        s.Prune(2.6, 170, 170);
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
        s.Prune(0.5, 1024, 1024);
        Assert.NotNull(s.Offer);
        s.Prune(0.9, 1025, 1025);
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
        s.Prune(3.4, 0, 0);
        Assert.Equal(0.4f, s.OvershieldOf(5), 3);
        s.Prune(3.6, 0, 0);
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
            s.Prune(i * 0.01, i, i);
        }
        Assert.True(s.Malformed > 0);
        s.Apply(ReadOnlySpan<byte>.Empty, 0.0, 1);
    }

    /// <summary>
    /// X5 review X5-1 / re-review 2: a run whose Ends were all lost ends Invalid once the 2 Hz live list stops announcing it for 1.5 s while snapshots keep arriving; a same-start repeat from the
    /// live list revives it (the server still plays it), an End settles it.
    /// </summary>
    [Fact]
    public void LostEndExpiresWhenTheLiveListStopsAndTheLiveListRevivesIt()
    {
        var s = new ExecutionClientState();
        s.Prune(0.0, 97, 100);
        s.Apply(Start(100, 4), 0.0, 1);
        for (int i = 1; i <= 6; i++) { s.Apply(Start(100, 4), i * 0.5, 1); s.Prune(i * 0.5, 97 + i * 15, 100 + i * 15); }   // the live list keeps it for 3 s
        Assert.True(s.TryLive(4, out _));
        s.Prune(4.4, 97 + 132, 100 + 132);   // 1.4 s since the last announcement
        Assert.True(s.TryLive(4, out _));
        s.Prune(4.6, 97 + 138, 100 + 138);   // 1.6 s, and snapshots moved on as far
        Assert.False(s.TryLive(4, out _));
        Assert.Equal(ExecutionEndReason.Invalid, Assert.Single(s.Ended).Reason);
        Assert.Equal(1, s.Expired);
        s.Apply(Start(100, 4), 4.7, 1);   // the live list again: revived, its fade entry gone
        Assert.True(s.TryLive(4, out _));
        Assert.Empty(s.Ended);
        Assert.Equal(1, s.Revived);
        s.Apply(End(260, 4), 5.0, 1);
        Assert.False(s.TryLive(4, out _));
        s.Apply(Start(100, 4), 5.1, 1);   // after the End: a straggler revives nothing
        Assert.False(s.TryLive(4, out _));
    }

    /// <summary>
    /// X5 re-review 2, on the real clock path: through a receive stall the view tick (GameClient's render tick: the newest snapshot's tick plus the wall time since) runs on while the newest snapshot
    /// tick stands still. That is not silence: the run keeps playing, however long the stall, and the next announcement keeps it.
    /// </summary>
    [Fact]
    public void StallIsNotSilenceWhileTheViewTickRunsOn()
    {
        var s = new ExecutionClientState();
        s.Prune(0.0, 97, 100);
        s.Apply(Start(100, 4), 0.0, 1);
        for (int f = 1; f <= 90; f++) s.Prune(f / 30.0, 97 + f, 100);   // 3 s of frames: the view tick advances 90 ticks, no snapshot arrives
        Assert.True(s.TryLive(4, out _));
        s.Apply(Start(100, 4), 3.05, 1);   // the stall ends: snapshots and the live list resume together
        s.Prune(3.1, 190, 193);
        Assert.True(s.TryLive(4, out _));
        Assert.Equal(0, s.Expired);
    }

    /// <summary>
    /// X5 re-review N1: after a 3 s receive stall, the first snapshot jumps the snapshot tick by 90 one prune before the live list's repeat arrives; the silence clock only advanced by that frame,
    /// so the run is not closed (and not revived later).
    /// </summary>
    [Fact]
    public void FirstSnapshotAfterAStallIsNotSilence()
    {
        var s = new ExecutionClientState();
        s.Prune(0.0, 97, 100);
        s.Apply(Start(100, 4), 0.0, 1);
        for (int f = 1; f <= 90; f++) s.Prune(f / 30.0, 97 + f, 100);   // 3 s, no snapshot
        s.Prune(3.0 + 1 / 30.0, 188, 190);                              // the first snapshot after the stall, without the live list
        Assert.True(s.TryLive(4, out _));
        s.Apply(Start(100, 4), 3.1, 1);                                 // the live list next
        s.Prune(3.1, 190, 192);
        Assert.True(s.TryLive(4, out _));
        Assert.Equal(0, s.Expired);
        Assert.Equal(0, s.Revived);
    }

    /// <summary>X5 review X5-1 / re-review 3: a run is over a second past its own last tick, even if announced, for good; the own run's last tick then starts the cooldown. A synthetic run (announced: false) expires only that way.</summary>
    [Fact]
    public void RunPastItsEndTickExpiresAndStartsTheOwnCooldown()
    {
        var s = new ExecutionClientState();
        s.Apply(ExecutionWire.Encode(new ExecutionOffer(60000, 1, 1)), 0.0, 4);   // the own id (4) is known from the payloads
        uint LastTick(ExecutionRun r) => r.StartTick + 60;
        s.Start(new ExecutionRun(4, ExecutionTargetKind.Player, 60000, 0, 1f, default, default, 0f, 0, 100, false), 0.0, announced: false);
        s.Prune(10.0, 189, 189, LastTick, 30);   // silent for 10 s, but synthetic: kept; 29 ticks past its end (< 30)
        Assert.True(s.TryLive(4, out _));
        s.Prune(10.1, 191, 191, LastTick, 30);
        Assert.False(s.TryLive(4, out _));
        Assert.Equal(ExecutionEndReason.Invalid, Assert.Single(s.Ended).Reason);
        Assert.Equal(160u, s.OwnLastDoneTick);
        s.Start(new ExecutionRun(4, ExecutionTargetKind.Player, 60000, 0, 1f, default, default, 0f, 0, 100, false), 10.2);
        Assert.False(s.TryLive(4, out _));   // past its end: final
    }

    /// <summary>X5 re-review 3: a Done heard after this client already ended the own run (silence) still starts the cooldown.</summary>
    [Fact]
    public void LateOwnDoneStillStartsTheCooldown()
    {
        var s = new ExecutionClientState();
        s.Prune(0.0, 100, 100);
        s.Apply(Start(100, 4), 0.0, 4);
        s.Prune(2.0, 160, 160);   // silence: ended here
        Assert.False(s.TryLive(4, out _));
        Assert.Null(s.OwnLastDoneTick);
        s.Apply(End(170, 4), 2.1, 4);
        Assert.Equal(170u, s.OwnLastDoneTick);
    }

    /// <summary>X6 review X6-10: the server's press takes the latest kill and, among equal kill ticks, the lowest victim; the marker keeps the same offer. A spent offer is not taken again.</summary>
    [Fact]
    public void OfferKeepsTheServersPickAndSpendHidesIt()
    {
        var s = new ExecutionClientState();
        s.Apply(ExecutionWire.Encode(new ExecutionOffer(7, 1000, 24)), 0.0, 1);
        s.Apply(ExecutionWire.Encode(new ExecutionOffer(9, 1000, 24)), 0.0, 1);   // the same tick, a higher victim: the first stays
        Assert.Equal(7, s.Offer!.Value.Victim);
        s.Apply(ExecutionWire.Encode(new ExecutionOffer(3, 1000, 24)), 0.0, 1);   // the same tick, a lower victim: the server's pick
        Assert.Equal(3, s.Offer!.Value.Victim);
        s.Apply(ExecutionWire.Encode(new ExecutionOffer(8, 990, 24)), 0.0, 1);    // an older kill heard late
        Assert.Equal(3, s.Offer!.Value.Victim);
        s.Apply(ExecutionWire.Encode(new ExecutionOffer(8, 1005, 24)), 0.0, 1);   // a later kill
        Assert.Equal(8, s.Offer!.Value.Victim);
        s.SpendOffer();
        Assert.Null(s.Offer);
        s.Apply(ExecutionWire.Encode(new ExecutionOffer(8, 1005, 24)), 0.1, 1);   // a repeat of the spent one
        Assert.Null(s.Offer);
    }
}
