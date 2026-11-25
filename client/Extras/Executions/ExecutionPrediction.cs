using System;
using System.Numerics;
using SmNet;
using SmSim;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X6: the own executor's prediction, locked and pinned exactly as the server's state machine (X3) locks and pins it (engine-free, tested in <c>ExecutionPredictionTests</c>).
/// <list type="bullet">
/// <item>A run started at <see cref="ExecutionRun.StartTick"/> holds ticks <c>StartTick + 1</c> to its end tick: the server's step that starts it runs after that tick's pin, and the step of the end
/// tick pins before it ends the run. The end tick is the track's (<see cref="ExecutionTrack.EndTick"/>) while the run is live, the End's tick once the server announced one.</item>
/// <item>Input (<see cref="GameClient.ExtrasPredictInput"/>): a held tick steps with <see cref="MatchFlow.Locked"/> (no stick, no buttons, so no Execute bit; the view kept), the first prediction and every
/// replay alike. The stored and sent input stays raw (the server locks it itself).</item>
/// <item>Pin (<see cref="GameClient.ExtrasPin"/>): a held tick's predicted state takes <see cref="ExecutionTrack.Pose"/>'s feet and yaw, no velocity, grounded; a dead state is never pinned (the server ends
/// that run Killed).</item>
/// <item>No speculative pin before the Start arrives: the reconcile after it replays the history with the pin (<c>GameClient.ProcessSnapshot</c>), so the correction is a replay, not a snap.</item>
/// </list>
/// A row this install lacks (<see cref="ExecutionRowSet.AtWire"/> null) predicts nothing: the server's snapshots carry the pinned place and the prediction follows them.
/// </summary>
public sealed class ExecutionPrediction
{
    readonly ExecutionClientState _state;
    readonly Func<ExecutionRowSet> _rows;
    readonly Func<int> _ownId;
    readonly int _tickRate;

    /// <param name="state">The client's tag-50 state (the own live run and the runs that ended lately).</param>
    /// <param name="rows">The row set the wire index resolves in (the match config's).</param>
    /// <param name="ownId">The own player id (-1 before the welcome).</param>
    /// <param name="tickRate">The simulation's tick rate (Hz).</param>
    public ExecutionPrediction(ExecutionClientState state, Func<ExecutionRowSet> rows, Func<int> ownId, int tickRate)
    {
        _state = state;
        _rows = rows;
        _ownId = ownId;
        _tickRate = Math.Max(1, tickRate);
    }

    /// <summary>Locked ticks stepped (the first prediction and the replays; a test and log counter).</summary>
    public int LockedSteps { get; private set; }
    /// <summary>States pinned (the first prediction and the replays).</summary>
    public int Pins { get; private set; }

    /// <summary>The own run holding <paramref name="tick"/> (live, or ended at or after it), with its row; false when the tick is free.</summary>
    public bool Holds(uint tick, out ExecutionRun run, out ExecutionRow row)
    {
        int id = _ownId();
        row = null!;
        if (id < 0) { run = default; return false; }
        var rows = _rows();
        if (_state.TryLive(id, out run) && rows.AtWire(run.Wire) is { } liveRow)
        {
            row = liveRow;
            if (HoldsTick(run, ExecutionTrack.EndTick(run, row, _tickRate), tick)) return true;
        }
        // A run the server ended lately: the ticks up to its end were pinned on the server too (a replay of an unacknowledged tick must agree).
        var ended = _state.Ended;
        for (int i = ended.Count - 1; i >= 0; i--)
        {
            var e = ended[i];
            if (e.Run.Executor != id || rows.AtWire(e.Run.Wire) is not { } endedRow) continue;
            uint end = Math.Min(e.EndTick, ExecutionTrack.EndTick(e.Run, endedRow, _tickRate));
            if (HoldsTick(e.Run, end, tick)) { run = e.Run; row = endedRow; return true; }
        }
        run = default;
        return false;
    }

    /// <summary>Whether a run that started at its start tick and ended at <paramref name="endTick"/> holds <paramref name="tick"/> (<c>StartTick &lt; tick &lt;= endTick</c>).</summary>
    public static bool HoldsTick(in ExecutionRun run, uint endTick, uint tick) => tick > run.StartTick && tick <= endTick;

    /// <summary><see cref="GameClient.ExtrasPredictInput"/>: a held tick's input locked as the server locks it.</summary>
    public PlayerInput Input(PlayerInput input, uint tick)
    {
        if (!Holds(tick, out _, out _)) return input;
        LockedSteps++;
        return Lock(input);
    }

    /// <summary>The server's lock: <see cref="MatchFlow.Locked"/> (no stick, no buttons: the Execute bit with them; yaw and pitch kept).</summary>
    public static PlayerInput Lock(PlayerInput input) => MatchFlow.Locked(input) with { Buttons = InputButtons.None };

    /// <summary><see cref="GameClient.ExtrasPin"/>: a held tick's predicted state pinned to the track.</summary>
    public PlayerState Pin(PlayerState state, uint tick)
    {
        if (!state.Alive || !Holds(tick, out var run, out var row)) return state;
        Pins++;
        return PinTo(state, run, row, tick, _tickRate);
    }

    /// <summary>The server's pin: the track's feet and yaw at <paramref name="tick"/>, no velocity, grounded; nothing else touched.</summary>
    public static PlayerState PinTo(PlayerState state, in ExecutionRun run, ExecutionRow row, uint tick, int tickRate)
    {
        var pose = ExecutionTrack.Pose(run, row, tick, tickRate);
        return state with { Position = pose.Feet, Yaw = pose.Yaw, Velocity = Vector3.Zero, Grounded = true };
    }

    /// <summary>
    /// Sets the client's prediction delegates to <paramref name="prediction"/> when <paramref name="config"/> carries <see cref="ContentExtras.Executions"/>; otherwise clears them (the faithful game
    /// and Musou Mode never run them). True when they were set.
    /// </summary>
    public static bool Install(GameClient client, SimConfig config, ExecutionPrediction prediction)
    {
        if (!config.Extras.HasFlag(ContentExtras.Executions)) { Uninstall(client); return false; }
        client.ExtrasPredictInput = prediction.Input;
        client.ExtrasPin = prediction.Pin;
        return true;
    }

    /// <summary>Clears the prediction delegates (a disconnect, the view leaving).</summary>
    public static void Uninstall(GameClient client)
    {
        client.ExtrasPredictInput = null;
        client.ExtrasPin = null;
    }
}
