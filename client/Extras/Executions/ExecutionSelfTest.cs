using System;
using Godot;
using SmNet.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X5 test aids (client only, in a match with the executions flag; nothing reaches the server). Both feed <see cref="ExecutionsState"/> a synthetic Start of the own
/// player on a victim that is not a player (id <see cref="DemoTarget"/>, standing where the row puts it), so the real presenter draws a move without a server-side execution:
/// <list type="bullet">
/// <item><c>--execution-selftest [--execution-row id]</c> (default <c>C1.chainsword</c>): one move; checks the proxy exists and the own character stands where
/// <see cref="ExecutionTrack"/> puts it, then ends it (Invalid) and checks the proxy is gone within 0.8 s; prints <c>EXECSELFTEST PASS</c> or <c>FAIL</c> and quits (0 / 1).</item>
/// <item><c>--execution-demo id[,id..]</c> (e.g. <c>C1.chainsword,O3.chainsword</c>): the rows in turn, each played whole and ended Done, 2 s apart, forever (a capture aid).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    const int DemoTarget = 60000;
    static readonly bool ExecSelfTest = CmdFlag("execution-selftest");
    static readonly string[] ExecDemoRows = (CmdArg("execution-demo") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    ExecutionRun? _execTestRun;
    ExecutionRow? _execTestRow;
    double _execTestAt, _execTestNext = 4.0, _execTestEndedAt = -1;
    int _execTestIndex, _execTestFails;
    bool _execTestChecked, _execTestDone;

    void ExecutionSelfTestFrame(PlayerState me, System.Collections.Generic.IReadOnlyList<PlayerState> others)
    {
        if (!ExecSelfTest && ExecDemoRows.Length == 0) return;
        if (_execTestDone || _client is null || _execLib.Catalogue is not { } catalogue || !me.Alive || _connectedFor < _execTestNext) return;
        if (_execTestRun is null)
        {
            string id = ExecSelfTest ? CmdArg("execution-row") ?? "C1.chainsword" : ExecDemoRows[_execTestIndex++ % ExecDemoRows.Length];
            var row = catalogue.Find(id);
            int wire = catalogue.WireIndexOf(id);
            if (row is null || wire < 0)
            {
                GD.PrintErr($"EXECSELFTEST: row '{id}' is not in this install's catalogue ({catalogue.Rows.Count} rows)");
                if (ExecSelfTest) { _execTestDone = true; GetTree().Quit(1); }
                _execTestNext = _connectedFor + 2.0;
                return;
            }
            var run = new ExecutionRun(me.PlayerId, ExecutionTargetKind.Player, DemoTarget, wire, row.Offset.Y, me.Position, me.Position, me.Yaw, 0, _client.Tick, false);
            _execTestRun = ExecutionTrack.Quantize(run);
            _execTestRow = row;
            _execTestAt = _connectedFor;
            _execTestChecked = false;
            ExecutionsState.Start(_execTestRun.Value, _connectedFor);
            GD.Print($"EXECSELFTEST start {row.Id} (wire {wire}) at tick {_client.Tick}: {row.AttackerClip} / {row.VictimClip}, {row.Seconds:F3} s");
            return;
        }
        var r = _execTestRun.Value;
        var testRow = _execTestRow!;
        if (ExecSelfTest && !_execTestChecked && _connectedFor - _execTestAt > 0.5)
        {
            _execTestChecked = true;
            var pose = ExecutionPresentMath.Pose(r, testRow, OwnDrawTick(), ExecTickRate);
            var drawn = _local?.Transform.Origin ?? Vector3.Zero;
            float off = new System.Numerics.Vector2(drawn.X - pose.Feet.X, drawn.Y - pose.Feet.Y).Length();
            bool proxy = ExecutionProxyCount > 0;
            GD.Print($"EXECSELFTEST check: proxies {ExecutionProxyCount}, own drawn {drawn.X:F3},{drawn.Y:F3} track {pose.Feet.X:F3},{pose.Feet.Y:F3} (off {off:F3} m), clip {pose.ClipSeconds:F3} s");
            if (!proxy) { _execTestFails++; GD.PrintErr("EXECSELFTEST: no victim proxy"); }
            if (off > 0.1f) { _execTestFails++; GD.PrintErr("EXECSELFTEST: the own character is not where the track puts it"); }
        }
        if (_execTestEndedAt < 0)
        {
            uint end = ExecutionTrack.EndTick(r, testRow, ExecTickRate);
            if (ExecSelfTest ? _connectedFor - _execTestAt > 1.0 : _client.Tick >= end + 2)
            {
                var why = ExecSelfTest ? ExecutionEndReason.Invalid : ExecutionEndReason.Done;
                ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionEnd(_client.Tick, (ushort)r.Executor, why)), _connectedFor, me.PlayerId);
                _execTestEndedAt = _connectedFor;
                GD.Print($"EXECSELFTEST end {testRow.Id} {why} at tick {_client.Tick}");
            }
            return;
        }
        if (ExecSelfTest)
        {
            if (_connectedFor - _execTestEndedAt < 0.8) return;
            if (ExecutionProxyCount != 0) { _execTestFails++; GD.PrintErr($"EXECSELFTEST: {ExecutionProxyCount} proxies left after the end"); }
            if (ExecutionsState.TryLive(r.Executor, out _)) { _execTestFails++; GD.PrintErr("EXECSELFTEST: the run is still live"); }
            GD.Print(_execTestFails == 0 ? "EXECSELFTEST PASS" : $"EXECSELFTEST FAIL ({_execTestFails})");
            _execTestDone = true;
            GetTree().Quit(_execTestFails == 0 ? 0 : 1);
            return;
        }
        // The demo: the next row 2 s after this one's end.
        _execTestRun = null;
        _execTestEndedAt = -1;
        _execTestNext = _connectedFor + 2.0;
    }
}
