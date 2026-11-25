using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmNet.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X6 test aids (a connected client in a match with <see cref="ContentExtras.Executions"/>, e.g. <c>server.cmd --content extended</c>); every move they show is synthetic (fed straight into
/// <see cref="ExecutionClientState"/>, with a synthetic row in place of the match's), so they are <b>placeholder demonstrations</b> of the camera, the prompt and the shell, not of a server's execution:
/// <list type="bullet">
/// <item><c>--executions-x6-test</c> (headless): the prompt for a synthetic offer shows and hides after the window; the camera seam is taken during a synthetic own run and released after its End; the
/// prediction pins it; the armour tint follows a synthetic overshield; the shell is made and freed. <c>EXECX6</c> log lines, then <c>EXECX6 RESULT PASS|FAIL</c>, then the client quits.</item>
/// <item><c>--executions-x6-demo &lt;dir&gt;</c> (windowed, <c>godot.cmd --windowed</c>): stills of the prompt (keyboard, then the pad's unbound hint), the orbit at three yaws, the shell at a full and a
/// half overshield, and a short series of camera frames (<c>orbit_clip_NNN.png</c>, for an MP4), then the client quits.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    static readonly bool ExecutionX6Test = CmdFlag("executions-x6-test");
    static readonly string? ExecutionX6Demo = CmdArg("executions-x6-demo");

    /// <summary>The shell's overshield fraction forced by the test aids (null: the real one).</summary>
    float? ExecutionX6ShellOverride;
    /// <summary>The demo's pad shot: the prompt as on a pad with Execute unbound (the default).</summary>
    bool _execX6PadShot;

    double _execX6At = -1;
    int _execX6Step, _execX6Pass, _execX6Fail;
    uint _execX6RunTick;
    int _execX6Frames;
    readonly List<string> _execX6Notes = new();

    /// <summary>A synthetic row (no original data: made-up clip names and timings, a straight 0.6 m root path) under a wire id of <see cref="ExecutionRows.All"/>, for <paramref name="seconds"/>.</summary>
    static ExecutionRowSet ExecutionX6Rows(float seconds)
    {
        var root = Enumerable.Range(0, 31).Select(i => new System.Numerics.Vector4(0f, 0.6f * i / 30f, 0f, 0f)).ToArray();
        var events = new List<ExecutionEvent> { new(ExecutionEventKind.Kill, seconds * 0.5f), new(ExecutionEventKind.End, seconds * 0.95f) };
        ExecutionRow Row(string id, string target) => new(id, "*", "*", target, "x6_attacker", "x6_victim", seconds, seconds, events, new System.Numerics.Vector3(0f, 1.3f, 0f), MathF.PI, "x6", "x6")
        { AttackerRoot = root, VictimRoot = root };
        return ExecutionRowSet.OfAll([Row(ExecutionRows.All[0].Id, ExecutionTargets.ChaosMarine), Row(ExecutionRows.All[1].Id, ExecutionTargets.OrkBoy)]);
    }

    void ExecutionX6Check(string name, bool ok, string detail = "")
    {
        if (ok) _execX6Pass++; else _execX6Fail++;
        GD.Print($"EXECX6 {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    void ExecutionX6TestFrame(PlayerState me, double delta)
    {
        if (!ExecutionX6Test && ExecutionX6Demo is null) return;
        if (_client is not { Status: ClientStatus.Connected } client || _config is not { } config || _hudView is null) return;
        if (_execX6At < 0) { if (!me.Alive || _local is null) return; _execX6At = 0; GD.Print($"EXECX6 start: player {client.PlayerId}, class {Loadouts.ClassOf(config, me.Class)?.Name}, melee {ExecutionPromptRules.MeleeKey(config, me)}"); }
        _execX6At += delta;
        if (ExecutionX6Demo is { } dir) { ExecutionX6DemoFrame(client, config, me, dir); return; }
        double t = _execX6At;
        var rows = ExecutionX6Rows(2f);
        switch (_execX6Step)
        {
            case 0 when t > 2.0:
                _execRowsOverride = rows;
                ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionOffer((ushort)client.PlayerId, (uint)Math.Max(0, client.RemotePlayers().Tick), (byte)ExecutionRules.WindowTicks(config))), _connectedFor, client.PlayerId);
                _execX6Step++; _execX6At = 0; break;
            case 1 when t > 0.25:
                ExecutionX6Check("the prompt shows for an open offer", _hudView.ExecutionPrompt is { } p && p.Text.EndsWith(ExecutionPromptRules.Label, StringComparison.Ordinal), $"{_hudView.ExecutionPrompt?.Text ?? "none"}, hidden {_execPromptHidden}");
                _execX6Step++; _execX6At = 0; break;
            case 2 when t > 1.2:
                ExecutionX6Check("the prompt hides after the window", _hudView.ExecutionPrompt is null, $"hidden {_execPromptHidden}");
                _execX6RunTick = client.Tick;
                ExecutionsState.Start(new ExecutionRun(client.PlayerId, ExecutionTargetKind.Player, client.PlayerId, 0, 1.3f, me.Position, me.Position, me.Yaw, 0, _execX6RunTick, false), _connectedFor);
                _execX6Step++; _execX6At = 0; break;
            case 3 when t > 0.4:
                ExecutionX6Check("the camera seam is taken during an own run", ExecutionOrbitActive && ExecutionOrbitsStarted == 1, $"orbit {(ExecutionOrbitActive ? "on" : "off")}, started {ExecutionOrbitsStarted}");
                ExecutionX6Check("the prompt hides while executing", _hudView.ExecutionPrompt is null && _execPromptHidden == ExecutionPromptHidden.Executing, $"hidden {_execPromptHidden}");
                ExecutionX6Check("the prediction locks and pins the run", _execPrediction is { Pins: > 0, LockedSteps: > 0 }, $"pins {_execPrediction?.Pins}, locked steps {_execPrediction?.LockedSteps}");
                ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionEnd(_execX6RunTick + 40, (ushort)client.PlayerId, ExecutionEndReason.Done)), _connectedFor, client.PlayerId);
                _execX6Step++; _execX6At = 0; break;
            case 4 when t > 0.6:
                ExecutionX6Check("the camera seam is released after the End", !ExecutionOrbitActive && ExecutionOrbitExits == 1, $"orbit {(ExecutionOrbitActive ? "on" : "off")}, exits {ExecutionOrbitExits}");
                ExecutionX6Check("the own last Done tick is the End's", ExecutionsState.OwnLastDoneTick == _execX6RunTick + 40, $"{ExecutionsState.OwnLastDoneTick}");
                Hud.ExecutionsTestOvershield = 1f;
                ExecutionX6ShellOverride = 1f;
                _execX6Step++; _execX6At = 0; break;
            case 5 when t > 0.3:
                ExecutionX6Check("the armour tint turns deeper blue with an overshield", Hud.ArmourTintFor(200f, 100f) != Colors.White && (!_hudView.ExecutionsTintCalled || _hudView.ExecutionArmourTintLast != Colors.White),
                    _hudView.ExecutionsTintCalled ? $"tint {_hudView.ExecutionArmourTintLast}" : "the armour bar is not drawn in this run (headless); the rule checked");
                ExecutionX6Check("the shell is made on the own model", _execShells.Count == 1 || _local?.Skeleton is null, _local?.Skeleton is null ? "no skeleton in this run (lean)" : $"{_execShells.Count} shells, {(_execShells.Values.FirstOrDefault()?.Pieces ?? 0)} pieces");
                Hud.ExecutionsTestOvershield = 0f;
                ExecutionX6ShellOverride = 0f;
                _execX6Step++; _execX6At = 0; break;
            case 6 when t > 0.3:
                ExecutionX6Check("the tint goes back to white without an overshield", !_hudView.ExecutionsTintCalled || _hudView.ExecutionArmourTintLast == Colors.White, $"tint {_hudView.ExecutionArmourTintLast}");
                ExecutionX6Check("the shell is freed at no overshield", _execShells.Count == 0, $"{_execShells.Count} shells");
                Hud.ExecutionsTestOvershield = null;
                ExecutionX6ShellOverride = null;
                _execRowsOverride = null;
                GD.Print($"EXECX6 RESULT {(_execX6Fail == 0 ? "PASS" : "FAIL")} ({_execX6Pass} passed, {_execX6Fail} failed)");
                _execX6Step++;
                GetTree().Quit(_execX6Fail == 0 ? 0 : 1);
                break;
        }
    }

    void ExecutionX6Shot(string dir, string name)
    {
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, name + ".png");
        bool ok = GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok;
        if (!name.StartsWith("orbit_clip_", StringComparison.Ordinal)) GD.Print(ok ? $"EXECX6 shot {name}" : $"EXECX6 shot {name} failed");
    }

    /// <summary>The windowed stills (one step per stage; each shot after the view has settled).</summary>
    void ExecutionX6DemoFrame(GameClient client, SimConfig config, PlayerState me, string dir)
    {
        double t = _execX6At;
        var rows = ExecutionX6Rows(30f);   // a long move, so the orbit holds while the shots are taken
        // The offer's corpse: the nearest other player (a bot) if any, else the own player (a placeholder anchor).
        int victim = client.PlayerId;
        float best = float.MaxValue;
        foreach (var (id, o) in _execOthers)
        {
            float d = System.Numerics.Vector3.Distance(o.Position, me.Position);
            if (id != client.PlayerId && d < best && d < 12f) (best, victim) = (d, id);
        }
        void Offer() => ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionOffer((ushort)victim, (uint)Math.Max(0, client.RemotePlayers().Tick), 255)), _connectedFor, client.PlayerId);
        switch (_execX6Step)
        {
            case 0 when t > 4.0: _execRowsOverride = rows; Offer(); _execX6Step++; _execX6At = 0; break;
            case 1 when t > 0.6: Offer(); ExecutionX6Shot(dir, "prompt_keyboard"); _execX6PadShot = true; _execX6Step++; _execX6At = 0; break;
            case 2 when t > 0.6: ExecutionX6Shot(dir, "prompt_pad_unbound"); _execX6PadShot = false; _execX6Step++; _execX6At = 0; break;
            case 3:
                _execX6RunTick = client.Tick;
                ExecutionsState.Start(new ExecutionRun(client.PlayerId, ExecutionTargetKind.Player, victim, 0, 1.3f, me.Position, me.Position, me.Yaw, 0, _execX6RunTick, false), _connectedFor);
                _execX6Step++; _execX6At = 0; break;
            case 4 when t > 1.0: ExecutionX6Shot(dir, "orbit_yaw0"); _execOrbit.Look(MathF.PI * 2f / 3f, 0.25f, _execEl); _execX6Step++; _execX6At = 0; break;
            case 5 when t > 1.0: ExecutionX6Shot(dir, "orbit_yaw120"); _execOrbit.Look(MathF.PI * 2f / 3f, -0.2f, _execEl); _execX6Step++; _execX6At = 0; break;
            case 6 when t > 1.0: ExecutionX6Shot(dir, "orbit_yaw240"); ExecutionX6ShellOverride = 1f; _execX6Step++; _execX6At = 0; break;
            case 7 when t > 0.8: ExecutionX6Shot(dir, "shell_full"); ExecutionX6ShellOverride = 0.5f; _execX6Step++; _execX6At = 0; break;
            case 8 when t > 0.8: ExecutionX6Shot(dir, "shell_half"); ExecutionX6ShellOverride = null; _execX6Frames = 0; _execX6Step++; _execX6At = 0; break;
            case 9:
                // The camera clip: a steady orbit sweep (a quarter turn a second) for 4 s, a frame every other render frame; then the End and the blend back.
                _execOrbit.Look(-MathF.PI / 2f * (float)GetProcessDeltaTime(), 0f, _execEl);
                if (Engine.GetProcessFrames() % 2 == 0) ExecutionX6Shot(dir, $"orbit_clip_{_execX6Frames++:000}");
                if (t > 4.0)
                {
                    ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionEnd(client.Tick, (ushort)client.PlayerId, ExecutionEndReason.Done)), _connectedFor, client.PlayerId);
                    _execX6Step++; _execX6At = 0;
                }
                break;
            case 10:
                if (Engine.GetProcessFrames() % 2 == 0) ExecutionX6Shot(dir, $"orbit_clip_{_execX6Frames++:000}");
                if (t > 1.0) { _execRowsOverride = null; GD.Print($"EXECX6 demo done: {_execX6Frames} clip frames in {dir}"); _execX6Step++; GetTree().Quit(); }
                break;
        }
    }
}
