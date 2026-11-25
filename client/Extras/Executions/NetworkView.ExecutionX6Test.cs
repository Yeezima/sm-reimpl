using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SmNet;
using SmNet.Extras.Executions;
using SmSim;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X6 test aids (a connected client in a match with <see cref="ContentExtras.Executions"/>, e.g. <c>server.cmd --content extended</c> or a Warfront stage). Synthetic offers, runs and
/// overshields (fed straight into <see cref="ExecutionClientState"/>, with a synthetic row in place of the match's) are <b>placeholder demonstrations</b>; the Warfront marker shot waits for a real pick.
/// <list type="bullet">
/// <item><c>--executions-x6-test</c> (headless): the marker for a synthetic offer shows and hides after the window; the camera seam is taken during a synthetic own run and released after its End; the
/// prediction pins it; the armour tint follows a synthetic overshield; the shell is made and freed. <c>EXECX6</c> lines, then <c>EXECX6 RESULT PASS|FAIL</c>, then the client quits.</item>
/// <item><c>--executions-x6-demo &lt;dir&gt; --executions-x6-shots dot|ork|shell|orbit</c> (windowed, <c>godot.cmd --windowed</c>): <c>dot</c> the marker over a synthetic Classic offer's player at close
/// and mid range; <c>ork</c> waits (up to 7 min) for the real Warfront pick and shoots it close and mid; <c>shell</c> the shell and the tint at strengths 0.6, 1 and 1.4 times full, half and low
/// overshield from one fixed camera (with <c>--executions-x6-paint &lt;scheme&gt;</c> for the armour's paint); <c>orbit</c> the orbit at three yaws and camera frames for an MP4. Then it quits.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    static readonly bool ExecutionX6Test = CmdFlag("executions-x6-test");
    static readonly string? ExecutionX6Demo = CmdArg("executions-x6-demo");
    static readonly string ExecutionX6Shots = CmdArg("executions-x6-shots") ?? "orbit";
    /// <summary><c>--executions-x6-paint &lt;scheme&gt;</c>: every marine of this client is painted with that scheme (the shell comparison's dark and light armour).</summary>
    static readonly string? ExecutionX6Paint = ApplyX6Paint();

    static string? ApplyX6Paint()
    {
        var paint = CmdArg("executions-x6-paint");
        if (paint is { Length: > 0 }) MarineAssets.PaintOverride = paint;
        return paint;
    }

    /// <summary>The shell's overshield fraction forced by the test aids (null: the real one).</summary>
    float? ExecutionX6ShellOverride;

    double _execX6At = -1;
    int _execX6Step, _execX6Pass, _execX6Fail;
    uint _execX6RunTick;
    int _execX6Frames;
    Transform3D? _execX6Camera;   // the demo's fixed camera, applied after the frame's own camera

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
        if (_execX6At < 0)
        {
            if (!me.Alive || _local is null) return;
            _execX6At = 0;
            GD.Print($"EXECX6 start: player {client.PlayerId}, class {Loadouts.ClassOf(config, me.Class)?.Name}, melee {ExecutionPromptRules.MeleeKey(config, me)}, rows {config.Executions.Rows.Count}, paint {ExecutionX6Paint ?? "default"}");
            if (CmdFlag("executions-x6-survey")) foreach (var line in OvershieldShell.Describe(_local)) GD.Print("EXECX6 mesh " + line);
        }
        _execX6At += delta;
        if (_execX6Camera is { } fixedCamera && _camera is not null) _camera.GlobalTransform = fixedCamera;
        if (ExecutionX6Demo is { } dir) { ExecutionX6DemoFrame(client, config, me, dir); return; }
        double t = _execX6At;
        switch (_execX6Step)
        {
            case 0 when t > 2.0:
                _execRowsOverride = ExecutionX6Rows(2f);
                // The kill a few ticks before the view tick and the longest window the wire carries: a loaded machine's frames can be long, and this checks the marker, not the window's length.
                ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionOffer((ushort)client.PlayerId, (uint)Math.Max(0, client.RemotePlayers().Tick - 3), 255)), _connectedFor, client.PlayerId);
                _execX6Step++; _execX6At = 0; break;
            case 1 when t > 0.1:
                ExecutionX6Check("the marker shows for an open offer", ExecutionMarkerShown, $"hidden {_execPromptHidden}, offer {ExecutionsState.Offer?.KillTick}+{ExecutionsState.Offer?.WindowTicks}, view tick {client.RemotePlayers().Tick:F1}");
                ExecutionX6Check("no text prompt is set", _hudView.ExecutionPrompt is null);
                // An offer whose window (the match's) ended before the view tick: hidden.
                ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionOffer((ushort)client.PlayerId, (uint)Math.Max(0, client.RemotePlayers().Tick - 100), (byte)ExecutionRules.WindowTicks(config))), _connectedFor, client.PlayerId);
                _execX6Step++; _execX6At = 0; break;
            case 2 when t > 0.3:
                ExecutionX6Check("the marker hides after the window", !ExecutionMarkerShown, $"hidden {_execPromptHidden}");
                _execRowsOverride = ExecutionX6Rows(30f);   // a long move: a loaded machine's frames lag the tick clock, and the run must not reach its own end tick before the checks (that expiry is a unit test)
                _execX6RunTick = client.Tick;
                ExecutionsState.Start(new ExecutionRun(client.PlayerId, ExecutionTargetKind.Player, client.PlayerId, 0, 1.3f, me.Position, me.Position, me.Yaw, 0, _execX6RunTick, false), _connectedFor);
                _execX6Step++; _execX6At = 0; break;
            case 3 when t > 0.4:
                ExecutionX6Check("the camera seam is taken during an own run", ExecutionOrbitActive && ExecutionOrbitsStarted == 1, $"orbit {(ExecutionOrbitActive ? "on" : "off")}, started {ExecutionOrbitsStarted}");
                ExecutionX6Check("the marker hides while executing", !ExecutionMarkerShown && _execPromptHidden == ExecutionPromptHidden.Executing, $"hidden {_execPromptHidden}");
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
                var shell = _execShells.Values.FirstOrDefault();
                ExecutionX6Check("the shell is made on the own model", shell is not null || _local?.Skeleton is null, _local?.Skeleton is null ? "no skeleton in this run (lean)" : $"{_execShells.Count} shells, {shell?.Pieces ?? 0} pieces");
                float full = shell?.ShownStrength ?? 0f;
                ExecutionX6ShellOverride = 0.5f;
                ExecutionsShellFrame(config, me);
                ExecutionX6Check("the shell's strength scales with the overshield", shell is null || MathF.Abs(shell.ShownStrength - full * 0.5f) < 1e-4f, $"full {full:F2}, half {shell?.ShownStrength:F2}");
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

    void ExecutionX6Shot(string dir, string name, bool log = true)
    {
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, name + ".png");
        bool ok = GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok;
        if (log) GD.Print(ok ? $"EXECX6 shot {name}" : $"EXECX6 shot {name} failed");
    }

    /// <summary>A camera <paramref name="distance"/> metres from <paramref name="target"/> (Godot space), from the side the current camera looks from, a little above, looking at it.</summary>
    Transform3D ExecutionX6CameraAt(Vector3 target, float distance, float side = 0f)
    {
        var from = _camera!.GlobalPosition - target;
        from.Y = 0f;
        if (from.LengthSquared() < 1e-4f) from = Vector3.Back;
        from = from.Normalized().Rotated(Vector3.Up, side);
        var eye = target + from * distance + Vector3.Up * MathF.Min(1.0f, distance * 0.2f);
        return new Transform3D(Basis.LookingAt((target - eye).Normalized(), Vector3.Up), eye);
    }

    void ExecutionX6Next() { _execX6Step++; _execX6At = 0; }

    void ExecutionX6DemoFrame(GameClient client, SimConfig config, PlayerState me, string dir)
    {
        switch (ExecutionX6Shots)
        {
            case "dot": ExecutionX6DotDemo(client, config, me, dir); break;
            case "ork": ExecutionX6OrkDemo(client, me, dir); break;
            case "shell": ExecutionX6ShellDemo(client, config, me, dir); break;
            default: ExecutionX6OrbitDemo(client, me, dir); break;
        }
    }

    void ExecutionX6Done(string dir, string what)
    {
        _execRowsOverride = null; _execX6Camera = null; ExecutionX6ShellOverride = null; Hud.ExecutionsTestOvershield = null; OvershieldShell.Strength = OvershieldShell.DefaultStrength;
        GD.Print($"EXECX6 demo done: {what} in {dir}");
        _execX6Step = 1000;
        GetTree().Quit();
    }

    /// <summary>Classic: a synthetic offer on the nearest other player (a bot), else the own player; the marker close and at mid range.</summary>
    void ExecutionX6DotDemo(GameClient client, SimConfig config, PlayerState me, string dir)
    {
        double t = _execX6At;
        int victim = client.PlayerId;
        float best = float.MaxValue;
        foreach (var (id, o) in _execOthers)
        {
            float d = System.Numerics.Vector3.Distance(o.Position, me.Position);
            if (id != client.PlayerId && d < best && d < 25f) (best, victim) = (d, id);
        }
        if (_execX6Step < 5) ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionOffer((ushort)victim, (uint)Math.Max(0, client.RemotePlayers().Tick), 255)), _connectedFor, client.PlayerId);
        var feet = victim == client.PlayerId ? me.Position : _execOthers[victim].Position;
        var dot = _gameSpace!.Transform * new Vector3(feet.X, feet.Y, feet.Z + MarkerOverCorpse);
        switch (_execX6Step)
        {
            case 0 when t > 4.0: _execRowsOverride = ExecutionX6Rows(2f); GD.Print($"EXECX6 dot over player {victim} ({(victim == client.PlayerId ? "own, no other player" : $"{best:F1} m away")})"); ExecutionX6Next(); break;
            case 1 when t > 0.5: _execX6Camera = ExecutionX6CameraAt(dot, 2.2f, 0.5f); ExecutionX6Next(); break;
            case 2 when t > 0.6: ExecutionX6Shot(dir, "dot_classic_close"); _execX6Camera = ExecutionX6CameraAt(dot, 7f, 0.5f); ExecutionX6Next(); break;
            case 3 when t > 0.6: ExecutionX6Shot(dir, "dot_classic_mid"); _execX6Camera = null; ExecutionX6Next(); break;
            case 4 when t > 0.6: ExecutionX6Shot(dir, "dot_classic_play_view"); ExecutionX6Next(); break;
            case 5: ExecutionX6Done(dir, "Classic marker"); break;
        }
    }

    /// <summary>Warfront: waits for the real pick (ChooseAlignedOrk on the crowd as drawn) and shoots the marker over that Ork close and at mid range.</summary>
    void ExecutionX6OrkDemo(GameClient client, PlayerState me, string dir)
    {
        double t = _execX6At;
        ushort ork = _execPickedOrk;
        Vector3? dot = null;
        if (ork != 0 && ExecutionMarkerShown)
            foreach (var e in _tfwShown) if (e.Id == ork) dot = _gameSpace!.Transform * new Vector3(e.Position.X, e.Position.Y, e.Position.Z + MarkerOverOrk);
        switch (_execX6Step)
        {
            case 0 when dot is not null: GD.Print($"EXECX6 Warfront pick: Ork {ork} after {t:F0} s"); ExecutionX6Shot(dir, "dot_warfront_play_view"); ExecutionX6Next(); break;
            case 0 when t > 420: GD.Print($"EXECX6 no Warfront pick in 7 min (hidden {_execPromptHidden}, choices {_execChoices})"); ExecutionX6Done(dir, "no Warfront marker"); break;
            case 1 when dot is { } a: _execX6Camera = ExecutionX6CameraAt(a, 2.4f, 0.4f); ExecutionX6Next(); break;
            case 2 when t > 0.15: ExecutionX6Shot(dir, "dot_warfront_close"); _execX6Camera = null; ExecutionX6Next(); break;
            case 3 when dot is { } b: _execX6Camera = ExecutionX6CameraAt(b, 7f, 0.4f); ExecutionX6Next(); break;
            case 4 when t > 0.15: ExecutionX6Shot(dir, "dot_warfront_mid"); _execX6Camera = null; ExecutionX6Next(); break;
            case 5: ExecutionX6Done(dir, "Warfront marker"); break;
            case > 0 and < 5 when t > 20: ExecutionX6Done(dir, "Warfront marker (the pick was lost before every shot)"); break;
        }
    }

    static readonly float[] X6Strengths = [0.6f, 1.0f, 1.4f];
    static readonly (string Name, float Fraction)[] X6Fractions = [("full", 1f), ("half", 0.5f), ("low", 0.15f)];

    /// <summary>The shell and tint comparison: three strengths times full, half and low overshield, one fixed camera on the own model (front-left, 3.2 m).</summary>
    void ExecutionX6ShellDemo(GameClient client, SimConfig config, PlayerState me, string dir)
    {
        double t = _execX6At;
        string paint = ExecutionX6Paint ?? "default";
        if (_execX6Step == 0)
        {
            if (t < 4.0) return;
            var chest = _gameSpace!.Transform * new Vector3(me.Position.X, me.Position.Y, me.Position.Z + 1.2f);
            var front = _gameSpace.Transform.Basis * new Vector3(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw), 0f);
            var eye = chest + front.Rotated(Vector3.Up, 0.6f).Normalized() * 3.2f + Vector3.Up * 0.3f;
            _execX6Camera = new Transform3D(Basis.LookingAt((chest - eye).Normalized(), Vector3.Up), eye);
            ExecutionX6Next();
            return;
        }
        int index = _execX6Step - 1;
        if (index >= X6Strengths.Length * X6Fractions.Length) { ExecutionX6Done(dir, $"shell comparison ({paint})"); return; }
        var (name, fraction) = X6Fractions[index % X6Fractions.Length];
        float strength = X6Strengths[index / X6Fractions.Length];
        OvershieldShell.Strength = strength;
        ExecutionX6ShellOverride = fraction;
        Hud.ExecutionsTestOvershield = fraction;
        if (t > 0.7) { ExecutionX6Shot(dir, $"shell_{paint}_s{(int)MathF.Round(strength * 100):000}_{name}"); ExecutionX6Next(); }
    }

    /// <summary>The orbit at three yaws during a synthetic own run, then camera frames of a sweep and the End's blend back (for an MP4).</summary>
    void ExecutionX6OrbitDemo(GameClient client, PlayerState me, string dir)
    {
        double t = _execX6At;
        switch (_execX6Step)
        {
            case 0 when t > 4.0:
                _execRowsOverride = ExecutionX6Rows(30f);
                _execX6RunTick = client.Tick;
                ExecutionsState.Start(new ExecutionRun(client.PlayerId, ExecutionTargetKind.Player, client.PlayerId, 0, 1.3f, me.Position, me.Position, me.Yaw, 0, _execX6RunTick, false), _connectedFor);
                ExecutionX6Next(); break;
            case 1 when t > 1.0: ExecutionX6Shot(dir, "orbit_yaw0"); _execOrbit.Look(MathF.PI * 2f / 3f, 0.25f, _execEl); ExecutionX6Next(); break;
            case 2 when t > 1.0: ExecutionX6Shot(dir, "orbit_yaw120"); _execOrbit.Look(MathF.PI * 2f / 3f, -0.2f, _execEl); ExecutionX6Next(); break;
            case 3 when t > 1.0: ExecutionX6Shot(dir, "orbit_yaw240"); _execX6Frames = 0; ExecutionX6Next(); break;
            case 4:
                _execOrbit.Look(-MathF.PI / 2f * (float)GetProcessDeltaTime(), 0f, _execEl);
                if (Engine.GetProcessFrames() % 2 == 0) ExecutionX6Shot(dir, $"orbit_clip_{_execX6Frames++:000}", log: false);
                if (t > 4.0) { ExecutionsState.Apply(ExecutionWire.Encode(new ExecutionEnd(client.Tick, (ushort)client.PlayerId, ExecutionEndReason.Done)), _connectedFor, client.PlayerId); ExecutionX6Next(); }
                break;
            case 5:
                if (Engine.GetProcessFrames() % 2 == 0) ExecutionX6Shot(dir, $"orbit_clip_{_execX6Frames++:000}", log: false);
                if (t > 1.0) ExecutionX6Done(dir, $"orbit, {_execX6Frames} clip frames");
                break;
        }
    }
}
