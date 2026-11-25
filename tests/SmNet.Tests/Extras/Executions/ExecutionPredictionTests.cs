using System.Numerics;
using SmClient;
using SmSim;
using SmSim.Extras.Executions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>
/// Executions X6: the own executor's prediction (client/Extras/Executions/ExecutionPrediction.cs) on a real <see cref="GameClient"/> over the loopback harness. The server's pin is X3's
/// (<c>World.ExecutionsPin</c>: <see cref="ExecutionTrack.Pose"/> on ticks StartTick + 1 to the end); where a test needs the server to pin, the client's reconcile is handed the server's pinned record
/// through <see cref="GameClient.ExtrasCarry"/> (the record the snapshot carries once X3 pins), so these tests stand on the shared track, not on X3's state machine.
/// </summary>
public class ExecutionPredictionTests
{
    static SimConfig Config(ContentExtras extras = ContentExtras.Executions) => new() { Extras = extras };

    static readonly ExecutionRow Row = ExecutionTestKit.Row("C1.chainsword", gap: 1.6f, seconds: 3f, kill: 1.5f, end: 2.8f);
    static readonly ExecutionRowSet Set = ExecutionRowSet.OfAll([Row]);
    static int Wire => Set.WireIndexOf(Row.Id);

    /// <summary>A run of <see cref="Row"/> for <paramref name="executor"/> started at <paramref name="start"/>: a 0.8 m slide over 8 ticks, then the clip's 0.7 m walk.</summary>
    static ExecutionRun Run(int executor, uint start, Vector3 from, float yaw = 0.4f) =>
        new(executor, ExecutionTargetKind.Player, 99, Wire, 1.6f, from, from + new Vector3(0.8f, 0f, 0f), yaw, 8, start, false);

    static (Harness H, GameClient C, ExecutionClientState S, ExecutionPrediction P) Connected(LinkConditions link, SimConfig? config = null)
    {
        config ??= Config();
        var h = new Harness(link, 3, 0, config);
        var c = h.AddClient(nonce: 1);
        var s = new ExecutionClientState();
        var p = new ExecutionPrediction(s, () => Set, () => c.Status == ClientStatus.Connected ? c.PlayerId : -1, config.TickRate);
        Assert.True(ExecutionPrediction.Install(c, config, p));
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal(ClientStatus.Connected, c.Status);
        return (h, c, s, p);
    }

    /// <summary>The server's pinned own record (what X3's snapshots carry) for the reconcile: the track on the run's held ticks.</summary>
    static Func<PlayerState, uint, PlayerState> ServerPin(Func<ExecutionRun?> run, int rate) => (record, tick) =>
        run() is { } r && record.Alive && ExecutionPrediction.HoldsTick(r, ExecutionTrack.EndTick(r, Row, rate), tick) ? ExecutionPrediction.PinTo(record, r, Row, tick, rate) : record;

    [Fact]
    public void PredictedPinMatchesServerPin()
    {
        var (h, c, s, p) = Connected(new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.Zero, 0));
        int rate = h.Config.TickRate;
        var from = c.Predicted.Position;
        uint start = c.Tick + 3;   // the Start is known before its first held tick (a client far behind the server learns it this way)
        var run = Run(c.PlayerId, start, from);
        c.ExtrasCarry = ServerPin(() => run, rate);
        s.Start(run, 0);
        uint end = ExecutionTrack.EndTick(run, Row, rate);
        int compared = 0;
        uint seen = c.Tick;
        h.Run(TimeSpan.FromSeconds(3.5), after: () =>
        {
            if (c.Tick == seen) return;
            seen = c.Tick;
            if (!ExecutionPrediction.HoldsTick(run, end, c.Tick)) return;
            var pose = ExecutionTrack.Pose(run, Row, c.Tick, rate);
            Assert.True(Vector3.Distance(pose.Feet, c.Predicted.Position) < 1e-4f, $"tick {c.Tick}: predicted {c.Predicted.Position}, track {pose.Feet}");
            Assert.Equal(pose.Yaw, c.Predicted.Yaw, 5);
            Assert.Equal(Vector3.Zero, c.Predicted.Velocity);
            compared++;
        });
        Assert.True(compared > (int)(end - start) - 4, $"{compared} ticks compared of {end - start}");
        // The first held tick is the one after the start (the server's step that starts a run runs after that tick's pin).
        Assert.False(ExecutionPrediction.HoldsTick(run, end, start));
        Assert.True(ExecutionPrediction.HoldsTick(run, end, start + 1));
        Assert.True(ExecutionPrediction.HoldsTick(run, end, end));
        Assert.False(ExecutionPrediction.HoldsTick(run, end, end + 1));
    }

    [Fact]
    public void CorrectionOnStartIsReplayNotSnap()
    {
        var (h, c, s, p) = Connected(new LinkConditions(TimeSpan.FromMilliseconds(60), TimeSpan.Zero, 0));
        int rate = h.Config.TickRate;
        var from = c.Predicted.Position;
        uint start = c.Tick - 2;   // the server started it two ticks before the client's newest tick: the client predicted those unpinned
        var run = Run(c.PlayerId, start, from);
        c.ExtrasCarry = ServerPin(() => run, rate);   // the server pins from start + 1 whatever the client knows
        h.Run(TimeSpan.FromMilliseconds(120));        // the Start is still on its way: the client predicts free (no speculative pin)
        Assert.Equal(0, p.Pins);
        s.Start(run, 0);                              // the Start arrives
        var corrections = new List<float>();
        int serial = c.CorrectionSerial;
        uint end = ExecutionTrack.EndTick(run, Row, rate);
        h.Run(TimeSpan.FromSeconds(1.5), after: () =>
        {
            if (c.CorrectionSerial == serial) return;
            serial = c.CorrectionSerial;
            corrections.Add(c.LastCorrection.Length());
            if (ExecutionPrediction.HoldsTick(run, end, c.Tick))
                Assert.True(Vector3.Distance(ExecutionTrack.Pose(run, Row, c.Tick, rate).Feet, c.Predicted.Position) < 1e-4f, $"tick {c.Tick}: the replay left the prediction off the track");
        });
        Assert.True(corrections.Count > 10, $"{corrections.Count} reconciles");
        Assert.True(p.Pins > 0);
        // After the replay that took the Start in, the prediction and the server agree: no rubber band.
        Assert.All(corrections.Skip(1), x => Assert.True(x < 0.05f, $"a correction of {x:F3} m after the Start"));
    }

    [Fact]
    public void LockedPredictedInputHasNoButtons()
    {
        var state = new ExecutionClientState();
        var p = new ExecutionPrediction(state, () => Set, () => 4, 30);
        var run = Run(4, 100, Vector3.Zero);
        state.Start(run, 0);
        var raw = new PlayerInput(101, 1f, -0.5f, 1.25f, InputButtons.Fire | InputButtons.Execute | InputButtons.Jump | InputButtons.Melee) { Pitch = 0.3f };
        var locked = p.Input(raw, 101);
        Assert.Equal(InputButtons.None, locked.Buttons);
        Assert.Equal(0f, locked.MoveX);
        Assert.Equal(0f, locked.MoveY);
        Assert.Equal(raw.Yaw, locked.Yaw);
        Assert.Equal(raw.Pitch, locked.Pitch);
        Assert.Equal(raw, p.Input(raw with { Tick = 100 }, 100) with { Tick = 101 });   // the start tick itself is free
        uint end = ExecutionTrack.EndTick(run, Row, 30);
        Assert.Equal(raw, p.Input(raw, end + 1));                                          // after the end: free
        Assert.Equal(InputButtons.None, p.Input(raw, end).Buttons);                       // the end tick is held
        Assert.Equal(2, p.LockedSteps);
        // Another player's run never locks the own input.
        var other = new ExecutionPrediction(state, () => Set, () => 5, 30);
        Assert.Equal(raw, other.Input(raw, 110));
    }

    [Fact]
    public void StoredInputStaysRaw()
    {
        var (h, c, s, p) = Connected(LinkConditions.Perfect);
        int rate = h.Config.TickRate;
        var from = c.Predicted.Position;
        var serverFrom = h.Server.World.Players[c.PlayerId].Position;
        var run = Run(c.PlayerId, c.Tick, from);
        s.Start(run, 0);
        // The player pushes the stick through the move: the prediction holds it on the track, the input sent is raw (this harness's server has no lock: it walks).
        h.Run(TimeSpan.FromSeconds(1.2), input: (_, t) => new PlayerInput(t, 0f, 1f, 0f, InputButtons.None));
        var server = h.Server.World.Players[c.PlayerId].Position;
        Assert.True(Vector3.Distance(server, serverFrom) > 1f, $"the server's player moved {Vector3.Distance(server, serverFrom):F2} m: the stick reached it");
        var pose = ExecutionTrack.Pose(run, Row, c.Tick, rate);
        Assert.True(Vector3.Distance(pose.Feet, c.Predicted.Position) < 1e-4f, $"predicted {c.Predicted.Position}, track {pose.Feet}");
        Assert.True(p.LockedSteps > 0);
    }

    [Fact]
    public void NoDelegatesWithoutFlag()
    {
        foreach (var extras in new[] { ContentExtras.None, ContentExtras.Musou, ContentExtras.All & ~ContentExtras.Executions })
        {
            var config = Config(extras);
            var h = new Harness(LinkConditions.Perfect, 1, 0, config);
            var c = h.AddClient(nonce: 2);
            c.ExtrasPin = (st, _) => st;   // left over from before: cleared
            var p = new ExecutionPrediction(new ExecutionClientState(), () => Set, () => c.PlayerId, config.TickRate);
            Assert.False(ExecutionPrediction.Install(c, config, p));
            Assert.Null(c.ExtrasPredictInput);
            Assert.Null(c.ExtrasPin);
        }
        var on = Config();
        var client = new Harness(LinkConditions.Perfect, 1, 0, on).AddClient(nonce: 3);
        Assert.True(ExecutionPrediction.Install(client, on, new ExecutionPrediction(new ExecutionClientState(), () => Set, () => 1, on.TickRate)));
        Assert.NotNull(client.ExtrasPredictInput);
        Assert.NotNull(client.ExtrasPin);
        ExecutionPrediction.Uninstall(client);
        Assert.Null(client.ExtrasPredictInput);
        Assert.Null(client.ExtrasPin);
    }

    [Fact]
    public void EndedRunPinsUpToItsEndAndMissingRowPredictsNothing()
    {
        var state = new ExecutionClientState();
        var p = new ExecutionPrediction(state, () => Set, () => 4, 30);
        var run = Run(4, 100, Vector3.Zero);
        state.Start(run, 0);
        state.Apply(SmNet.Extras.Executions.ExecutionWire.Encode(new SmNet.Extras.Executions.ExecutionEnd(120, 4, ExecutionEndReason.Invalid)), 0.1, 4);
        var s = new PlayerState(4, new Vector3(9f, 9f, 0f), Vector3.One, 0f, false) { Alive = true };
        Assert.Equal(ExecutionTrack.Pose(run, Row, 115, 30).Feet, p.Pin(s, 115).Position);   // an unacknowledged tick the server pinned before the End
        Assert.Equal(ExecutionTrack.Pose(run, Row, 120, 30).Feet, p.Pin(s, 120).Position);
        Assert.Equal(s, p.Pin(s, 121));                                                          // after the End: free
        Assert.Equal(s with { Alive = false }, p.Pin(s with { Alive = false }, 110));             // a dead state is never pinned
        // A row this install lacks: nothing predicted (the server's snapshots carry the place).
        var lacking = new ExecutionPrediction(state, () => ExecutionRowSet.Empty, () => 4, 30);
        state.Start(run with { StartTick = 200 }, 1.0);
        Assert.Equal(s, lacking.Pin(s, 205));
        Assert.Equal(ExecutionTrack.Pose(run with { StartTick = 200 }, Row, 205, 30).Feet, p.Pin(s, 205).Position);   // with the row: pinned
    }
}
