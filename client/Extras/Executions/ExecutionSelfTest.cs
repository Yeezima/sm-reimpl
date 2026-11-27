using System;
using System.Collections.Generic;
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
    /// <summary><c>--execution-end killed</c>: the self-test ends its move Killed (the Classic hand-over to a ragdoll; the proxy goes within 6.5 s).</summary>
    static readonly bool ExecSelfTestKilled = string.Equals(CmdArg("execution-end"), "killed", StringComparison.OrdinalIgnoreCase);
    /// <summary><c>--execution-end-at s</c>: the self-test ends its move this many seconds after it starts (default 1; exec-decap: past a row's head cut, with <c>--execution-end killed</c>, the head must go to the ragdoll).</summary>
    static readonly double ExecSelfTestEndAt = double.TryParse(CmdArg("execution-end-at"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var endAt) ? endAt : 1.0;
    static readonly string[] ExecDemoRows = (CmdArg("execution-demo") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    /// <summary>
    /// <c>--execution-demo-enemy Boy,ShootaBoy,..</c> (Warfront capture aid, exec-decap): each demo move takes the nearest drawn crowd enemy of the next listed type (within 400 m) as its victim
    /// (TargetKind Enemy: the crowd's look and scale, the crowd hiding the member), in place of the synthetic player; a move waits for such an enemy. Client only: the server's Ork lives on.
    /// </summary>
    static readonly string[] ExecDemoEnemies = (CmdArg("execution-demo-enemy") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    ExecutionRun? _execTestRun;
    ExecutionRow? _execTestRow;
    double _execTestAt, _execTestNext = 4.0, _execTestEndedAt = -1;
    int _execTestIndex, _execTestFails;
    bool _execTestChecked, _execTestDone;

    /// <summary><c>--execution-bot-press</c>: the client taps Execute once, 0.2 s after it hears each own Classic offer (a test aid for the real server path).</summary>
    static readonly bool ExecBotPress = CmdFlag("execution-bot-press");
    uint _execPressedKill = uint.MaxValue;
    double _execOfferHeardAt = -1;
    bool _execPressHeld;

    double _execDotSince = -1, _execWarfrontPressedAt = -100;

    partial void ExtrasExecutionTestPress(ref PlayerInput input)
    {
        if (!ExecBotPress || !ExecutionsOn) return;
        if (_execPressHeld) { _execPressHeld = false; return; }   // one tick down, then up: a tap
        if (_config is { } config && config.Extras.HasFlag(ContentExtras.Warfront))
        {
            // X4b (Warfront, Orks or Chaos): a tap once the red dot has stood over the same enemy for 0.2 s, at most one every 1.5 s (the server's cooldown refuses the rest; a test aid, not a rule).
            if (!ExecutionMarkerShown || ExecutionPickedEnemy == 0) { _execDotSince = -1; return; }
            if (_execDotSince < 0) { _execDotSince = _connectedFor; return; }
            if (_connectedFor - _execDotSince < 0.2 || _connectedFor - _execWarfrontPressedAt < 1.5) return;
            _execWarfrontPressedAt = _connectedFor;
            _execDotSince = -1;
            _execPressHeld = true;
            input = input with { Buttons = input.Buttons | InputButtons.Execute };
            GD.Print($"EXECPRESS t {_connectedFor:F2} s: Execute tapped at tick {input.Tick} on enemy {ExecutionPickedEnemy} (type {ExecutionPickedType}) under the red dot");
            return;
        }
        if (ExecutionsState.Offer is not { } offer || offer.KillTick == _execPressedKill) { _execOfferHeardAt = -1; return; }
        if (_execOfferHeardAt < 0) { _execOfferHeardAt = _connectedFor; return; }
        if (_connectedFor - _execOfferHeardAt < 0.2) return;
        _execPressedKill = offer.KillTick;
        _execOfferHeardAt = -1;
        _execPressHeld = true;
        input = input with { Buttons = input.Buttons | InputButtons.Execute };
        GD.Print($"EXECPRESS t {_connectedFor:F2} s: Execute tapped at tick {input.Tick} for the offer on P{offer.Victim} (kill tick {offer.KillTick}, window {offer.WindowTicks})");
    }

    /// <summary>
    /// <c>--execution-live-shots &lt;dir&gt;</c> (windowed capture aid, X4b): during each own live execution on an enemy, stills at 35, 60 and 85 % of the move (<c>&lt;row&gt;_&lt;type&gt;_&lt;enemy&gt;_&lt;n&gt;.png</c>),
    /// at most <c>--execution-live-shots-most</c> moves (default 6) and two of one enemy type; an <c>EXECSHOT</c> line per still and per start with the engine frame (to cut a <c>--write-movie</c> recording).
    /// </summary>
    static readonly string? ExecLiveShots = CmdArg("execution-live-shots");
    static readonly int ExecLiveShotsMost = int.TryParse(CmdArg("execution-live-shots-most"), out int most) ? most : 6;
    readonly System.Collections.Generic.Dictionary<int, int> _execShotTypes = new();
    (uint Start, int Taken, string Name)? _execShotRun;
    int _execShotMoves, _execFrameIndex;
    double _execFrameAt = -100;
    static readonly double ExecLiveFrames = double.TryParse(CmdArg("execution-live-frames"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fps) ? fps : 0;
    static readonly string? ExecLiveFramesType = CmdArg("execution-live-frames-type");

    void ExecutionLiveShotsFrame()
    {
        if (ExecLiveShots is not { } dir || _client is not { } client || _config is not { } config) return;
        // X4b review: a Classic Extended finisher on a player too (the executor's hands: pistol holstered), named <row>_player_<id>.
        if (!ExecutionsState.TryLive(client.PlayerId, out var run) || ExecutionRowsOf(config).AtWire(run.Wire) is not { } row) { _execShotRun = null; return; }
        if (_execShotRun is not { } s || s.Start != run.StartTick)
        {
            if (_execShotMoves >= ExecLiveShotsMost) return;
            int type = run.TargetKind == ExecutionTargetKind.Player ? -2 : -1;
            if (run.TargetKind == ExecutionTargetKind.Enemy) foreach (var e in _tfwShown) if (e.Id == run.Target) { type = (int)e.Type; break; }
            if (_execShotTypes.GetValueOrDefault(type) >= 2) { _execShotRun = (run.StartTick, 3, ""); return; }
            _execShotTypes[type] = _execShotTypes.GetValueOrDefault(type) + 1;
            _execShotMoves++;
            string name = $"{row.Id}_{(type == -2 ? "player" : type < 0 ? "unknown" : ((SmSim.Extras.Musou.MusouEnemyType)type).ToString())}_{run.Target}";
            _execShotRun = s = (run.StartTick, 0, name);
            GD.Print($"EXECSHOT start {name} at frame {Engine.GetFramesDrawn()} (t {_connectedFor:F2} s, tick {run.StartTick}), {row.Seconds:F3} s");
        }
        float clip = ExecutionPresentMath.ClipSeconds(run, OwnDrawTick(), ExecTickRate);
        // --execution-live-frames <fps> [--execution-live-frames-type <MusouEnemyType>]: a frame sequence of the move as well (for an MP4), at most 12 s of it.
        if (ExecLiveFrames > 0 && s.Name.Length > 0 && (ExecLiveFramesType is null || s.Name.Contains("_" + ExecLiveFramesType + "_", StringComparison.Ordinal))
            && clip <= row.Seconds + 0.5f && clip < 12f && _connectedFor - _execFrameAt >= 1.0 / ExecLiveFrames)
        {
            _execFrameAt = _connectedFor;
            ExecutionX6Shot(System.IO.Path.Combine(dir, "frames_" + s.Name), $"f{_execFrameIndex++:D4}", log: false);
        }
        if (s.Taken >= 3) return;
        float at = row.Seconds * (s.Taken switch { 0 => 0.35f, 1 => 0.6f, _ => 0.85f });
        if (clip < at) return;
        string shot = $"{s.Name}_{s.Taken + 1}";
        ExecutionX6Shot(dir, shot, log: false);
        string hands = _local?.Weapons is { } rig ? $"; hands: pistol {(rig.PistolInHand ? "IN HAND" : "holstered")}, melee {(rig.MeleeHeld ? "in hand" : "away")}" : "";
        GD.Print($"EXECSHOT {shot} at frame {Engine.GetFramesDrawn()}: clip {clip:F2} of {row.Seconds:F2} s{hands}");
        _execShotRun = s with { Taken = s.Taken + 1 };
    }

    void ExecutionSelfTestFrame(PlayerState me, System.Collections.Generic.IReadOnlyList<PlayerState> others)
    {
        ExecutionLiveShotsFrame();
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
            var kind = ExecutionTargetKind.Player;
            int target = DemoTarget;
            if (ExecDemoEnemies.Length > 0)
            {
                string type = ExecDemoEnemies[(_execTestIndex - 1) % ExecDemoEnemies.Length];
                int best = -1; float bestD = 400f * 400f;
                for (int i = 0; i < _tfwShown.Count; i++)
                {
                    var e = _tfwShown[i];
                    if (e.State is SmSim.Extras.Musou.MusouEnemyState.Dying || !string.Equals(e.Type.ToString(), type, StringComparison.OrdinalIgnoreCase)) continue;
                    float d = System.Numerics.Vector2.DistanceSquared(new(e.Position.X, e.Position.Y), new(me.Position.X, me.Position.Y));
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best < 0) { _execTestIndex--; _execTestNext = _connectedFor + 1.0; GD.Print($"EXECSELFTEST demo: no drawn {type} within 400 m yet ({_tfwShown.Count} enemies shown)"); return; }
                kind = ExecutionTargetKind.Enemy;
                target = _tfwShown[best].Id;
                GD.Print($"EXECSELFTEST demo: victim enemy {target} ({type}) {MathF.Sqrt(bestD):F1} m away");
            }
            var run = new ExecutionRun(me.PlayerId, kind, target, wire, row.Offset.Y, me.Position, me.Position, me.Yaw, 0, _client.Tick, false);
            _execTestRun = ExecutionTrack.Quantize(run);
            _execTestRow = row;
            _execTestAt = _connectedFor;
            _execTestChecked = false;
            ExecutionsState.Start(_execTestRun.Value, _connectedFor, announced: false);   // synthetic: no live list re-announces it
            GD.Print($"EXECSELFTEST start {row.Id} (wire {wire}) at tick {_client.Tick}: {row.AttackerClip} / {row.VictimClip}, {row.Seconds:F3} s");
            if (ExecSelfTest && ExecSelfTestKilled) _aliveSeen[DemoTarget] = false;   // the synthetic victim is dead (as a real one is), so the Killed hand-over's death makes a ragdoll (exec-decap review 1)
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
            if (ExecSelfTest ? _connectedFor - _execTestAt > ExecSelfTestEndAt : _client.Tick >= end + 2)
            {
                var why = ExecSelfTest ? (ExecSelfTestKilled ? ExecutionEndReason.Killed : ExecutionEndReason.Invalid) : ExecutionEndReason.Done;
                ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionEnd(_client.Tick, (ushort)r.Executor, why)), _connectedFor, me.PlayerId);
                _execTestEndedAt = _connectedFor;
                GD.Print($"EXECSELFTEST end {testRow.Id} {why} at tick {_client.Tick}");
            }
            return;
        }
        if (ExecSelfTest)
        {
            if (_connectedFor - _execTestEndedAt < (ExecSelfTestKilled ? 6.5 : 0.8)) return;
            if (ExecutionProxyCount != 0) { _execTestFails++; GD.PrintErr($"EXECSELFTEST: {ExecutionProxyCount} proxies left after the end"); }
            if (ExecutionsState.TryLive(r.Executor, out _)) { _execTestFails++; GD.PrintErr("EXECSELFTEST: the run is still live"); }
            GD.Print($"EXECSELFTEST decap: cuts {ExecutionDecapCount}, carried to a ragdoll {ExecutionDecapCarried}, loose heads now {ExecutionLooseHeads}");
            if (ExecSelfTestKilled && ExecutionDecapCount > 0 && (ExecutionDecapCarried == 0 || ExecutionLooseHeads == 0)) { _execTestFails++; GD.PrintErr("EXECSELFTEST: the cut head did not go to the ragdoll"); }
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
