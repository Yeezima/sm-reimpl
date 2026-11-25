using System;
using Godot;

using SmClient.Extras.Warfront.Chaos.Breach;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos;

namespace SmClient;

/// <summary>
/// <c>--wf-breach-demo</c> (capture aid for a windowed human client in a Chaos battle; a test script, not a game rule): once a breach is open the player turns to the nearest one and walks until
/// <c>--wf-breach-demo-range</c> m (default 9) from its centre, waits until at least <c>--wf-breach-demo-daemons</c> (default 3) of its Daemons are out (or 15 s), then throws the equipment item
/// at its centre, aimed <c>--wf-breach-demo-lift</c> radians (default 0.12) above the straight line. It presses the bound actions (Move Forward, Grenade) through Godot's input map, as a hand on
/// the keys would. One <c>WFBREACHDEMO</c> line per step.
/// </summary>
public partial class NetworkView
{
    static readonly bool BreachDemoOn = CmdFlag("wf-breach-demo");
    enum BreachDemoStep : byte { Idle, Walk, Wait, Thrown, Done }
    BreachDemoStep _bdStep;
    byte _bdTarget;
    double _bdSince, _bdReleaseAt = -1;

    void BreachDemoSay(double now, string what) => GD.Print($"t {now:F1} s (frame {Engine.GetFramesDrawn()}): WFBREACHDEMO {what}");   // the frame number places the step in a --write-movie recording

    void WarfrontBreachDemoFrame(double now)
    {
        if (!BreachDemoOn || _bot || _client?.Predicted is not { } me || _config is null) return;
        if (_bdReleaseAt >= 0 && now >= _bdReleaseAt) { Input.ActionRelease(ControlsInput.Name(GameAction.Grenade)); _bdReleaseAt = -1; }
        if (_bdStep == BreachDemoStep.Done) return;
        WarfrontChaosWire.Breach? target = null;
        foreach (var b in _wfBreach.Breaches)
            if (_bdStep == BreachDemoStep.Idle ? b.State == WarfrontBreachState.Open && _wfBreach.Standing(b) : b.Id == _bdTarget) target = target is { } t && Dist(t) <= Dist(b) ? t : b;
        float Dist(in WarfrontChaosWire.Breach b) => new System.Numerics.Vector2(b.Centre.X - me.Position.X, b.Centre.Y - me.Position.Y).Length();
        if (target is not { } breach) { if (_bdStep != BreachDemoStep.Idle) { Release(); BreachDemoSay(now, "the breach is gone"); _bdStep = BreachDemoStep.Done; } return; }
        float range = FloatArg("wf-breach-demo-range") ?? 9f, lift = FloatArg("wf-breach-demo-lift") ?? 0.12f;
        int daemons = (int)(FloatArg("wf-breach-demo-daemons") ?? 3f);
        var c = breach.Centre;
        float dx = c.X - me.Position.X, dy = c.Y - me.Position.Y, flat = MathF.Sqrt(dx * dx + dy * dy);
        if (me.Alive) _yaw = MathF.Atan2(-dx, dy);
        switch (_bdStep)
        {
            case BreachDemoStep.Idle:
                _bdTarget = breach.Id; _bdStep = BreachDemoStep.Walk; _bdSince = now;
                BreachDemoSay(now, $"walks to breach {breach.Id} at {flat:F0} m");
                break;
            case BreachDemoStep.Walk:
                if (flat > range) Input.ActionPress(ControlsInput.Name(GameAction.MoveForward));
                else { Release(); _bdStep = BreachDemoStep.Wait; _bdSince = now; BreachDemoSay(now, $"stands {flat:F1} m from breach {breach.Id}; {breach.Alive} Daemons out"); }
                break;
            case BreachDemoStep.Wait:
                if (flat > range + 3f) { _bdStep = BreachDemoStep.Walk; break; }
                if (breach.Alive < daemons && now - _bdSince < 15.0) break;
                if (now - _bdSince < 1.5) break;
                float dz = c.Z - (me.Position.Z + _config.EyeHeight);
                _pitch = Math.Clamp(MathF.Atan2(dz, MathF.Max(flat, 0.5f)) + lift, -MaxPitchDown, MaxPitchUp);
                Input.ActionPress(ControlsInput.Name(GameAction.Grenade));
                _bdReleaseAt = now + 0.15;
                _bdStep = BreachDemoStep.Thrown; _bdSince = now;
                BreachDemoSay(now, $"throws at breach {breach.Id} from {flat:F1} m, pitch {_pitch:F2} rad; {breach.Alive} Daemons out; grenades {me.Grenades}");
                break;
            case BreachDemoStep.Thrown:
                if (!_wfBreach.Standing(breach)) { BreachDemoSay(now, $"breach {breach.Id} closed {now - _bdSince:F1} s after the throw"); _bdStep = BreachDemoStep.Done; }
                else if (now - _bdSince > 6.0) { BreachDemoSay(now, $"breach {breach.Id} still open 6 s after the throw"); _bdStep = BreachDemoStep.Done; }
                break;
        }

        static void Release() => Input.ActionRelease(ControlsInput.Name(GameAction.MoveForward));
    }
}
