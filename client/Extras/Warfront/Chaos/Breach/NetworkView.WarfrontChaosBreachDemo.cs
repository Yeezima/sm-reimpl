using System;
using Godot;

using SmClient.Extras.Warfront.Chaos.Breach;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos;

namespace SmClient;

/// <summary>
/// <c>--wf-breach-demo</c> (capture aid for a windowed human client in a Chaos battle; a test script, not a game rule): once a breach is open the player turns to the nearest one and walks until
/// <c>--wf-breach-demo-range</c> m (default 9) from its centre, waits until at least <c>--wf-breach-demo-daemons</c> (default 3) of its Daemons are out (or 15 s), then throws the equipment item
/// at its centre, on the low arc of the item's speed and gravity, plus <c>--wf-breach-demo-lift</c> radians (default 0). It presses the bound actions (Move Forward, Grenade) through Godot's input map, as a hand on
/// the keys would. One <c>WFBREACHDEMO</c> line per step.
/// </summary>
public partial class NetworkView
{
    static readonly bool BreachDemoOn = CmdFlag("wf-breach-demo");
    enum BreachDemoStep : byte { Idle, Walk, Wait, Thrown, Done }
    BreachDemoStep _bdStep;
    byte _bdTarget;
    double _bdSince, _bdReleaseAt = -1, _bdDoneAt = -1, _bdNextShot;
    int _bdShots;
    static readonly string? BreachDemoFrames = CmdArg("wf-breach-demo-frames");

    /// <summary><c>--wf-breach-demo-frames &lt;dir&gt;</c>: a JPG of the view every 1/12 s of wall time from the walk until 6 s after the last step (<c>f00000.jpg</c> up; ffmpeg <c>-framerate 12</c> joins them). Real time, unlike <c>--write-movie</c>, whose fixed clock slows a networked client on a loaded machine.</summary>
    void BreachDemoShoot()
    {
        if (BreachDemoFrames is not { Length: > 0 } dir || _bdStep == BreachDemoStep.Idle) return;
        double wall = Time.GetTicksMsec() / 1000.0;
        if (_bdDoneAt >= 0 && wall > _bdDoneAt + 6.0) return;
        if (wall < _bdNextShot) return;
        _bdNextShot = Math.Max(_bdNextShot + 1.0 / 12.0, wall - 0.2);
        if (GetViewport()?.GetTexture()?.GetImage() is not { } image) return;
        System.IO.Directory.CreateDirectory(dir);
        image.SaveJpg(System.IO.Path.Combine(dir, $"f{_bdShots++:D5}.jpg"), 0.85f);
    }

    void BreachDemoDone(double now, string what) { BreachDemoSay(now, what + $" ({_bdShots} frames so far)"); _bdStep = BreachDemoStep.Done; _bdDoneAt = Time.GetTicksMsec() / 1000.0; }

    void BreachDemoSay(double now, string what) => GD.Print($"t {now:F1} s (frame {Engine.GetFramesDrawn()}): WFBREACHDEMO {what}");   // the frame number places the step in a --write-movie recording

    /// <summary>The map went (C4c's frame node left): a walk in progress lets go of Move Forward (lead 40, C4c review), so the next map does not start with the key held.</summary>
    void WarfrontBreachDemoGone()
    {
        if (!BreachDemoOn || _bdStep is not (BreachDemoStep.Walk or BreachDemoStep.Wait)) return;
        Input.ActionRelease(ControlsInput.Name(GameAction.MoveForward));
        BreachDemoDone(_connectedFor, "the map went mid-walk; Move Forward released");
    }

    void WarfrontBreachDemoFrame(double now)
    {
        if (!BreachDemoOn || _bot || _client?.Predicted is not { } me || _config is null) return;
        if (_bdReleaseAt >= 0 && now >= _bdReleaseAt) { Input.ActionRelease(ControlsInput.Name(GameAction.Grenade)); _bdReleaseAt = -1; }
        BreachDemoShoot();
        if (_bdStep == BreachDemoStep.Done) return;
        WarfrontChaosWire.Breach? target = null;
        foreach (var b in _wfBreach.Breaches)
            if (_bdStep == BreachDemoStep.Idle ? b.State == WarfrontBreachState.Open && _wfBreach.Standing(b) : b.Id == _bdTarget) target = target is { } t && Dist(t) <= Dist(b) ? t : b;
        float Dist(in WarfrontChaosWire.Breach b) => new System.Numerics.Vector2(b.Centre.X - me.Position.X, b.Centre.Y - me.Position.Y).Length();
        if (target is not { } breach) { if (_bdStep != BreachDemoStep.Idle) { Release(); BreachDemoDone(now, "the breach is gone"); } return; }
        float range = FloatArg("wf-breach-demo-range") ?? 9f, lift = FloatArg("wf-breach-demo-lift") ?? 0f;
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
                _pitch = Math.Clamp(BreachDemoPitch(me, flat, dz) + lift, -MaxPitchDown, MaxPitchUp);
                Input.ActionPress(ControlsInput.Name(GameAction.Grenade));
                _bdReleaseAt = now + 0.15;
                _bdStep = BreachDemoStep.Thrown; _bdSince = now;
                BreachDemoSay(now, $"throws at breach {breach.Id} from {flat:F1} m, pitch {_pitch:F2} rad; {breach.Alive} Daemons out; grenades {me.Grenades}");
                break;
            case BreachDemoStep.Thrown:
                if (!_wfBreach.Standing(breach)) { BreachDemoDone(now, $"breach {breach.Id} closed {now - _bdSince:F1} s after the throw"); }
                else if (now - _bdSince > 6.0) { BreachDemoDone(now, $"breach {breach.Id} still open 6 s after the throw"); }
                break;
        }

        static void Release() => Input.ActionRelease(ControlsInput.Name(GameAction.MoveForward));
    }

    /// <summary>The view pitch whose throw lands on a point <paramref name="flat"/> m away and <paramref name="dz"/> m above the eye: the low ballistic arc of the item's speed and first-segment gravity, less its extra aim angle; the straight line when there is no item or no arc.</summary>
    float BreachDemoPitch(SmSim.PlayerState me, float flat, float dz)
    {
        float straight = MathF.Atan2(dz, MathF.Max(flat, 0.5f));
        if (SmSim.Loadouts.Equipment(_config!, me)?.Projectile is not { } p || p.Speed <= 0f) return straight;
        float g = _config!.Gravity * SmSim.Combat.GravityFactor(p.Gravity, 0f), v2 = p.Speed * p.Speed, x = MathF.Max(flat, 0.5f);
        float extra = p.ExtraAimAngle * MathF.PI / 180f;
        if (g <= 1e-3f) return straight - extra;
        float root = v2 * v2 - g * (g * x * x + 2f * dz * v2);
        if (root < 0f) return MathF.PI / 4f - extra;
        float theta = MathF.Atan((v2 - MathF.Sqrt(root)) / (g * x));
        GD.Print($"WFBREACHDEMO arc: speed {p.Speed:F1} m/s, gravity {g:F2} m/s2, extra aim {p.ExtraAimAngle:F1} deg, launch {theta * 180f / MathF.PI:F1} deg");
        return theta - extra;
    }
}
