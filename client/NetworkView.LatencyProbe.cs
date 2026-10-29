using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--latency-probe</c> (feel/controls test aid, a human client, headless): plays a script of key, mouse-button and mouse-motion events through
/// Godot's own input queue (<see cref="Input.ParseInputEvent"/>: delivered at the next frame like a real device event) and measures, per action,
/// the time from the frame the event is delivered in to (a) the first frame whose predicted state reacts and (b) the first frame that issues the
/// visible or audible feedback (the drawn own model moving, the camera turning, the shot's muzzle flash and sound, the clip starting). Times are
/// frame start to frame start (Godot's ticks); the rendering and presentation after the feedback frame (a frame and the swap chain) come on top
/// and are not measured headless. Also: lost taps (a press and release delivered in one frame), the converged shot's direction against the camera
/// of the same frame while the view pans, and how often the own attack clip's drawn time stands still between frames.
/// <para>
/// Options: <c>--latency-trials N</c> (12), <c>--latency-actions a,b,..</c> (pan, turn, move, aim, fire, firetap, reload, switch, melee, meletap,
/// stun, evade, grenade, jump; default all but jump, which needs a jump-pack class; for <c>--camera-probe</c> runs also sprinttaps and jumptaps:
/// sprints and jump-pack jumps with Fire taps), <c>--latency-seed n</c>. Prints <c>LATENCY</c> lines and quits.
/// Start Godot with <c>--max-fps 60</c> or <c>144</c> before <c>--</c>. Test aid, ours; the mouse counts as captured.
/// </para>
/// </summary>
public partial class NetworkView
{
    static readonly string[] ProbeArgs = OS.GetCmdlineUserArgs();
    static readonly bool LatencyProbeOn = ProbeArgs.Contains("--latency-probe");

    static string? ProbeArg(string name) => Array.IndexOf(ProbeArgs, name) is var i && i >= 0 && i + 1 < ProbeArgs.Length ? ProbeArgs[i + 1] : null;

    sealed class ProbeTrial
    {
        public string Kind = "";
        public long InjectFrame;
        public double? Delivered, Sim, Feedback;
        public float BaseSpeed, BaseYaw;
        public System.Numerics.Vector3 BaseDrawn;
        public Vector3 BaseCameraForward;
        public double Deadline;
    }

    readonly List<(double At, Action Do)> _probeOps = new();
    readonly List<ProbeTrial> _probeDone = new();
    ProbeTrial? _probeTrial;
    int _probeOpIndex;
    double _probeStart = -1, _probeNowUs;
    long _probeFrame;
    double _probeFrameSum; long _probeFrames, _probeLongFrames;
    float _probePanRate;   // radians per second while panning, 0 otherwise
    System.Numerics.Vector3? _probeShotDir; long _probeShotFrame = -1;
    readonly List<double> _probeStaleDeg = new();
    System.Numerics.Vector3 _probeLastDrawn; bool _probeHasLastDrawn;
    float _ownClipShown = -1f, _probeLastClip = -1f; long _probeClipFrames, _probeClipStill;
    int _probeEarlyAtStart;

    /// <summary>Frame start (before the client update): the frame's time, then the script's due events.</summary>
    void LatencyProbeBeforeUpdate()
    {
        if (!LatencyProbeOn || _client is null) return;
        _probeNowUs = Time.GetTicksUsec() / 1000.0;
        _probeFrame++;
        if (_probeStart < 0)
        {
            if (_client.Status != ClientStatus.Connected || !_client.Predicted.Alive || _connectedFor < 3.0) return;
            _probeStart = _connectedFor;
            _probeEarlyAtStart = _client.EarlyTicks;
            BuildProbeScript();
            GD.Print($"LATENCY probe starts: {_probeOps.Count} script steps; tap latch {(NoTapLatch ? "off" : "on")}, early tick {(NoEarlyTick ? "off" : "on")}, fresh aim {(NoFreshAim ? "off" : "on")}, own clip smoothing {(NoOwnClipSmoothing ? "off" : "on")}");
        }
        double t = _connectedFor - _probeStart;
        while (_probeOpIndex < _probeOps.Count && _probeOps[_probeOpIndex].At <= t) _probeOps[_probeOpIndex++].Do();
        if (_probePanRate != 0f && _camera is not null)
        {
            float px = _probePanRate * (float)GetProcessDeltaTime() / (MouseYawPerPixel * MathF.Max(_mouseScale, 1e-3f));
            Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(px, 0f) });
        }
        if (_probeOpIndex >= _probeOps.Count && _probeTrial is null) { LatencyProbeReport(); GetTree().Quit(0); }
    }

    /// <summary>From <c>_UnhandledInput</c>: the script's event arrived (the delivery frame).</summary>
    void LatencyProbeEvent(InputEvent e)
    {
        if (!LatencyProbeOn || _probeTrial is not { Delivered: null } trial || e is InputEventMouseMotion && trial.Kind != "turn") return;
        trial.Delivered = Time.GetTicksUsec() / 1000.0;   // input is dispatched at the frame's start, before _Process
    }

    /// <summary>From the input sample: the converged shot direction this frame (for the pan's staleness check).</summary>
    void LatencyProbeShot(System.Numerics.Vector3 direction)
    {
        if (!LatencyProbeOn) return;
        _probeShotDir = direction; _probeShotFrame = _probeFrame;
        LagProbeShot(direction);   // ui/hud-fidelity camera lag (temporary, NetworkView.CameraLag.cs)
    }

    /// <summary>End of the frame: the reaction and feedback checks.</summary>
    void LatencyProbeFrameEnd(PlayerState me, double delta)
    {
        if (!LatencyProbeOn || _client is null || _probeStart < 0 || _local is null || _camera is null || _gameSpace is null) return;
        _probeFrameSum += delta; _probeFrames++;
        if (_probeFrames > 10 && delta > 2.0 * _probeFrameSum / _probeFrames) _probeLongFrames++;
        var o = _local.GlobalTransform.Origin;
        var inverse = _gameSpace.Transform.AffineInverse();
        var g = inverse * o;
        var drawn = new System.Numerics.Vector3(g.X, g.Y, g.Z);
        float drawnSpeed = _probeHasLastDrawn && delta > 1e-5 ? System.Numerics.Vector2.Distance(new(drawn.X, drawn.Y), new(_probeLastDrawn.X, _probeLastDrawn.Y)) / (float)delta : 0f;
        _probeLastDrawn = drawn; _probeHasLastDrawn = true;
        var camForward = -_camera.GlobalBasis.Z;

        // Staleness of the converged shot while panning: the sampled direction against the camera this frame shows.
        if (_probePanRate != 0f && _probeShotFrame == _probeFrame && _probeShotDir is { } sd)
        {
            var cf = inverse.Basis * camForward;
            _probeStaleDeg.Add(Mathf.RadToDeg(new Vector3(sd.X, sd.Y, sd.Z).AngleTo(cf)));
        }
        // The own attack clip's drawn time: frames it stands still.
        if (_ownClipShown >= 0f)
        {
            if (_probeLastClip >= 0f && _ownClipShown >= _probeLastClip && _ownClipShown > 1e-4f)
            {
                _probeClipFrames++;
                if (_ownClipShown - _probeLastClip < 1e-5f) _probeClipStill++;
            }
            _probeLastClip = _ownClipShown;
        }
        else _probeLastClip = -1f;
        _ownClipShown = -1f;

        if (_probeTrial is not { } t) return;
        if (t.Delivered is null && _probeFrame > t.InjectFrame + 3) t.Delivered = _probeNowUs;   // a key the view did not see (should not happen)
        if (t.Delivered is null) return;
        float speed = new System.Numerics.Vector2(me.Velocity.X, me.Velocity.Y).Length();
        bool shot = _frameOwnEvents.Any(x => x.Events.Fired);
        bool sim, fb;
        switch (t.Kind)
        {
            case "move": sim = speed > 0.3f; fb = System.Numerics.Vector3.Distance(drawn, t.BaseDrawn) > 0.005f; break;
            case "stop": sim = speed < 0.85f * t.BaseSpeed; fb = drawnSpeed < 0.85f * t.BaseSpeed; break;
            case "turn": sim = MathF.Abs(ViewSmoothing.WrapPi(me.Yaw - t.BaseYaw)) > 0.01f; fb = camForward.AngleTo(t.BaseCameraForward) > 0.01f; break;
            case "aim": sim = me.Aiming; fb = _aimBlend > 0f; break;
            case "fire" or "firetap": sim = shot; fb = shot; break;   // the drained own shot: muzzle flash, shells, tracer and sound in this frame (UpdateFx, UpdateSound)
            case "reload": sim = me.ReloadLeft > 0f; fb = sim; break;
            case "switch": sim = me.SwitchLeft > 0f; fb = sim; break;
            case "melee" or "meleetap" or "stun": sim = me.MeleeStep > 0; fb = sim && AttackClip(me) is not null; break;
            case "evade": sim = Evade.Evading(me); fb = EvadePose(me) is not null; break;
            case "jump": sim = me.Jump != JumpPhase.None; fb = sim; break;
            case "grenade": sim = me.Throwing; fb = sim; break;
            default: sim = fb = true; break;
        }
        if (sim && t.Sim is null) t.Sim = _probeNowUs;
        if (fb && t.Feedback is null) t.Feedback = _probeNowUs;
        if ((t.Sim is not null && t.Feedback is not null) || _connectedFor > t.Deadline)
        {
            if (ProbeArgs.Contains("--latency-verbose"))
                GD.Print($"LATENCY trial {t.Kind}: sim {(t.Sim - t.Delivered)?.ToString("F1") ?? "-"} feedback {(t.Feedback - t.Delivered)?.ToString("F1") ?? "-"} ms; grounded {me.Grounded} evade {me.EvadeTicks}/{me.EvadeLock} melee {me.MeleeStep} jump {me.Jump} alive {me.Alive} early ticks {_client.EarlyTicks}");
            _probeDone.Add(t); _probeTrial = null;
        }
    }

    void ProbeBegin(string kind)
    {
        if (_probeTrial is { } open) _probeDone.Add(open);   // a trial still open is closed unanswered
        var me = _client!.Predicted;
        var g = _gameSpace!.Transform.AffineInverse() * _local!.GlobalTransform.Origin;
        _probeTrial = new ProbeTrial
        {
            Kind = kind, InjectFrame = _probeFrame, BaseSpeed = new System.Numerics.Vector2(me.Velocity.X, me.Velocity.Y).Length(), BaseYaw = me.Yaw,
            BaseDrawn = new System.Numerics.Vector3(g.X, g.Y, g.Z), BaseCameraForward = -_camera!.GlobalBasis.Z, Deadline = _connectedFor + 1.5,
        };
    }

    static void ProbeKey(Key key, bool pressed) => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = pressed });
    static void ProbeMouse(MouseButton button, bool pressed) => Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = pressed });

    void BuildProbeScript()
    {
        int trials = int.TryParse(ProbeArg("--latency-trials"), out var n) && n > 0 ? n : 12;
        var rng = new Random(int.TryParse(ProbeArg("--latency-seed"), out var seed) ? seed : 7);
        var actions = (ProbeArg("--latency-actions") ?? "pan,turn,move,aim,fire,firetap,reload,switch,melee,meleetap,stun,evade,grenade").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        double t = 0.5;
        void At(double gap, Action a) { t += gap; _probeOps.Add((t, a)); }
        // A random gap phase against the 33 ms tick (and the frame) per trial.
        double Gap(double baseSeconds) => baseSeconds + rng.NextDouble() * 0.1;
        void Tap(Key key, string kind, double hold) { At(0, () => { ProbeKey(key, true); ProbeBegin(kind); }); At(hold, () => ProbeKey(key, false)); }
        void Click(MouseButton b, string kind, double hold)
        {
            if (hold <= 0) At(0, () => { ProbeMouse(b, true); ProbeMouse(b, false); ProbeBegin(kind); });   // pressed and let go before the next frame
            else { At(0, () => { ProbeMouse(b, true); ProbeBegin(kind); }); At(hold, () => ProbeMouse(b, false)); }
        }
        if (actions.Contains("pan")) { At(0, () => _probePanRate = 300f * MathF.PI / 180f); At(2.0, () => _probePanRate = 0f); At(0.5, () => { }); }
        if (actions.Contains("turn")) for (int i = 0; i < trials; i++) { At(Gap(0.4), () => { Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(40f, 0f) }); ProbeBegin("turn"); }); }
        if (actions.Contains("move"))
            for (int i = 0; i < trials; i++)
            {
                At(Gap(0.7), () => { ProbeKey(Key.W, true); ProbeBegin("move"); });
                At(Gap(0.9), () => { ProbeKey(Key.W, false); ProbeBegin("stop"); });
                At(0.3, () => { ProbeKey(Key.S, true); });   // walk back so the runs stay in the room
                At(0.9, () => { ProbeKey(Key.S, false); });
            }
        if (actions.Contains("aim")) for (int i = 0; i < trials; i++) { At(Gap(0.6), () => { }); Tap(Key.Ctrl, "aim", 0.4); }
        if (actions.Contains("fire"))
        {
            At(0.6, () => ProbeMouse(MouseButton.Left, true)); At(0.04, () => ProbeMouse(MouseButton.Left, false));   // raises the weapon (the data's raise delay is not ours to measure)
            for (int i = 0; i < trials; i++) { At(Gap(0.45), () => { }); Click(MouseButton.Left, "fire", 0.04); }
        }
        if (actions.Contains("firetap")) for (int i = 0; i < trials; i++) { At(Gap(0.45), () => { }); Click(MouseButton.Left, "firetap", 0); }
        if (actions.Contains("reload"))
            for (int i = 0; i < Math.Min(trials, 6); i++)
            {
                At(Gap(0.5), () => ProbeMouse(MouseButton.Left, true)); At(0.04, () => ProbeMouse(MouseButton.Left, false));
                At(Gap(0.5), () => { }); Tap(Key.R, "reload", 0.04); At(3.0, () => { });
            }
        if (actions.Contains("switch")) for (int i = 0; i < trials; i++) { At(Gap(1.6), () => { }); Tap(Key.X, "switch", 0.04); }
        if (actions.Contains("melee")) for (int i = 0; i < trials; i++) { At(Gap(1.8), () => { }); Click(MouseButton.Right, "melee", 0.04); }
        if (actions.Contains("meleetap")) for (int i = 0; i < trials; i++) { At(Gap(1.8), () => { }); Click(MouseButton.Right, "meleetap", 0); }
        if (actions.Contains("stun")) for (int i = 0; i < trials; i++) { At(Gap(2.2), () => { }); Tap(Key.F, "stun", 0.04); }
        if (actions.Contains("evade"))
            for (int i = 0; i < trials; i++)
            {
                // Roll forward and back in turn (repeated rolls one way end against a wall, where a roll does not start).
                var dir = i % 2 == 0 ? Key.W : Key.S;
                At(Gap(1.5), () => ProbeKey(dir, true)); At(0.3, () => { }); Tap(Key.Space, "evade", 0.04); At(0.3, () => ProbeKey(dir, false));
            }
        if (actions.Contains("jump")) for (int i = 0; i < trials; i++) { At(Gap(3.0), () => { }); Tap(Key.Space, "jump", 0.04); }
        // Camera checks (with --camera-probe): sprints with Fire taps every 0.12 s while the view turns (early ticks while moving fast, into the
        // spawn room's walls), and jump-pack jumps with taps around the lift-off (jump-pack classes).
        if (actions.Contains("sprinttaps"))
            for (int i = 0; i < trials; i++)
            {
                float pan = (i % 2 == 0 ? 1 : -1) * 90f * MathF.PI / 180f;
                At(Gap(0.5), () => { ProbeKey(Key.W, true); ProbeKey(Key.Shift, true); _probePanRate = pan; });
                for (int k = 0; k < 12; k++) { At(0.12, () => { ProbeMouse(MouseButton.Left, true); ProbeMouse(MouseButton.Left, false); }); }
                At(0, () => { ProbeKey(Key.W, false); ProbeKey(Key.Shift, false); _probePanRate = 0f; });
            }
        if (actions.Contains("jumptaps"))
            for (int i = 0; i < trials; i++)
            {
                At(Gap(2.5), () => { ProbeKey(Key.W, true); ProbeKey(Key.Space, true); });
                for (int k = 0; k < 4; k++) At(0.07, () => { ProbeMouse(MouseButton.Left, true); ProbeMouse(MouseButton.Left, false); });
                At(0.1, () => ProbeKey(Key.Space, false));
                At(0.8, () => ProbeKey(Key.W, false));
            }
        if (actions.Contains("grenade")) for (int i = 0; i < Math.Min(trials, 2); i++) { At(Gap(2.2), () => { }); Tap(Key.G, "grenade", 0.04); }
        At(2.0, () => { });
    }

    void LatencyProbeReport()
    {
        double fps = _probeFrames > 0 ? _probeFrames / _probeFrameSum : 0;
        GD.Print($"LATENCY summary at {fps:F0} fps (mean frame {1000.0 / Math.Max(fps, 1e-6):F1} ms, frames over 2x the mean: {_probeLongFrames}); early ticks {_client!.EarlyTicks - _probeEarlyAtStart}; times from the delivery frame's start, ms (mean / sd / min / max):");
        foreach (var group in _probeDone.GroupBy(x => x.Kind))
        {
            var all = group.ToList();
            var sims = all.Where(x => x.Sim is not null && x.Delivered is not null).Select(x => x.Sim!.Value - x.Delivered!.Value).ToList();
            var fbs = all.Where(x => x.Feedback is not null && x.Delivered is not null).Select(x => x.Feedback!.Value - x.Delivered!.Value).ToList();
            GD.Print($"LATENCY {group.Key,-9} n {all.Count,2}: (a) predicted {Stats(sims)}; (b) feedback {Stats(fbs)}; no reaction in 1.5 s: {all.Count - sims.Count}");
        }
        if (_probeStaleDeg.Count > 0)
            GD.Print($"LATENCY while panning: converged shot against the camera of the same frame: mean {_probeStaleDeg.Average():F2} max {_probeStaleDeg.Max():F2} deg over {_probeStaleDeg.Count} samples");
        if (LagProbeSummary() is { } lag) GD.Print(lag);   // ui/hud-fidelity camera lag (temporary)
        if (_probeClipFrames > 0)
            GD.Print($"LATENCY own attack clip: drawn time stood still in {_probeClipStill} of {_probeClipFrames} frames ({100.0 * _probeClipStill / _probeClipFrames:F0}%)");

        static string Stats(List<double> v)
        {
            if (v.Count == 0) return "none";
            double mean = v.Average(), sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
            var sorted = v.OrderBy(x => x).ToList();
            double median = sorted[sorted.Count / 2], p90 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.9))];
            return $"{mean,5:F1} / {sd,4:F1} / {v.Min(),5:F1} / {v.Max(),5:F1} (median {median:F1}, p90 {p90:F1})";
        }
    }
}
