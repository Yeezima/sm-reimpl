using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Godot;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Test aid <c>--tfw-claw-capture</c> (with <c>--class assault --melee "lightning claw" --content musou</c>, before <c>--tfw-test &lt;dir&gt;</c>, a <c>--windowed</c> run for the PNGs; server
/// <c>SM_TFW_KEEP_HUMAN=1</c>): the lightning claw's gauntlet (<see cref="ClawGauntlet"/>) in a real Musou match, in the open on Ashfall: the player deploys on lane B, turns by
/// <c>--tfw-claw-turn &lt;degrees&gt;</c> and walks <c>--tfw-claw-walk &lt;seconds&gt;</c> (default 3) away from the landing, then stands, runs and strikes (light presses, a still at each hit) seen from the game's camera and
/// from orbit views round the marine (<c>claw-&lt;view&gt;-...</c>), and from close views that ride the hand bone (<c>claw-hand-&lt;side&gt;-...</c>: the back of the hand, its side, the front, a three-quarter view).
/// <c>--tfw-claw-views player,front,side,back,hand</c> picks the sets. A <c>CLAWPOSE</c> line per still. Quits 0.
/// </summary>
public partial class NetworkView
{
    /// <summary>The capture's camera this frame: 0 the game's, 1 an orbit round the marine, 2 a close view riding the right hand.</summary>
    int _clawCam;
    float _clawAzimuth, _clawDistance = 3.0f, _clawHeight = 1.5f, _clawHandDistance = 1.1f;
    Vector3 _clawHandDirection = Vector3.Zero, _clawFace = new(0f, 0f, -1f);

    async Task TfwClawCapture()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(2.0);
        TfwCheck("the lightning claw is the melee weapon", MeleeWargear(client.Predicted)?.Equals(ArsenalRules.LightningClaw, StringComparison.OrdinalIgnoreCase) == true, MeleeWargear(client.Predicted) ?? "none");
        _yaw += (CmdArg("tfw-claw-turn") is { } turn && float.TryParse(turn, NumberStyles.Float, CultureInfo.InvariantCulture, out var deg) ? deg : 0f) * MathF.PI / 180f;
        await TfwWait(0.5);
        TfwKey(Key.W, true);
        await TfwWait(CmdArg("tfw-claw-walk") is { } walk && double.TryParse(walk, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : 3.0);
        TfwKey(Key.W, false);
        await TfwWait(1.0);
        float facing = _yaw;
        string views = CmdArg("tfw-claw-views") is { Length: > 0 } v ? v : "player,front,side,hand";
        foreach (var (cam, az, view) in new[] { (0, 0f, "player"), (1, 35f, "front"), (1, 90f, "side"), (1, 180f, "back") })
        {
            if (!views.Contains(view, StringComparison.OrdinalIgnoreCase)) continue;
            _yaw = facing; _clawCam = cam; _clawAzimuth = az;
            await TfwWait(1.0);
            await ClawStill($"claw-{view}-1-idle");
            TfwKey(Key.W, true);
            await TfwWait(1.2);
            await ClawStill($"claw-{view}-2-run");
            TfwKey(Key.W, false);
            await TfwWait(0.6);
            await ClawReturn(facing);
            for (int k = 1; k <= 3; k++)
            {
                await ClawPress(GameAction.Melee);
                await ClawAtHit($"claw-{view}-{2 + k}-strike{k}");
                await TfwWait(0.7);
            }
            await TfwWait(1.5);
            await ClawReturn(facing);
        }
        if (views.Contains("hand", StringComparison.OrdinalIgnoreCase) && WeaponRig._clawFrame is { } frame)
        {
            _clawCam = 2;
            foreach (var (name, dir) in new[]
            {
                ("back", frame.Back - frame.Along * 0.3f), ("side", frame.Across), ("front", frame.Along + frame.Back * 0.6f), ("quarter", frame.Back * 0.6f + frame.Across * 0.8f + frame.Along * 0.4f),
            })
            {
                _clawHandDirection = dir.Normalized();
                _yaw = facing;
                await TfwWait(0.8);
                await ClawStill($"claw-hand-{name}-1-idle");
                TfwKey(Key.W, true);
                await TfwWait(1.2);
                await ClawStill($"claw-hand-{name}-2-run");
                TfwKey(Key.W, false);
                await TfwWait(0.6);
                await ClawReturn(facing);
                await ClawPress(GameAction.Melee);
                await TfwWait(0.12);
                await ClawStill($"claw-hand-{name}-3-strike-windup");
                await ClawAtHit($"claw-hand-{name}-4-strike-hit");
                await TfwWait(0.15);
                await ClawStill($"claw-hand-{name}-5-strike-follow");
                await TfwWait(2.0);
                await ClawReturn(facing);
            }
        }
        _clawCam = 0;
        await TfwWait(0.5);
        GetTree().Quit(0);
    }

    /// <summary>Back to roughly where the capture's views stand (a short walk backwards).</summary>
    async Task ClawReturn(float facing)
    {
        _yaw = facing;
        TfwKey(Key.S, true);
        await TfwWait(1.3);
        TfwKey(Key.S, false);
        await TfwWait(0.8);
    }

    async Task ClawPress(GameAction action) { TfwAction(action, true); await TfwWait(0.12); TfwAction(action, false); await TfwFrames(2); }

    /// <summary>Waits for the own light strike to reach its hit, then takes the still (a CLAWPOSE line even when the strike does not begin).</summary>
    async Task ClawAtHit(string name)
    {
        var client = _client!;
        if (!await TfwUntil($"claw: {name} begins", () => client.Predicted.MeleeStep > 0, 3.0)) { GD.Print($"CLAWPOSE {name}: the strike did not begin"); return; }
        var hit = _config is not null && Combat.MeleeStepOf(_config, client.Predicted) is { } s ? s.HitTime : 0.5f;
        await TfwUntil($"claw: {name} reaches its hit", () => client.Predicted.MeleeTime >= hit - 0.02f || client.Predicted.MeleeStep == 0, 3.0);
        await ClawStill(name);
    }

    async Task ClawStill(string name)
    {
        string where = _local?.Skeleton is { } sk && sk.FindBone("RIG_R_Hand") is var h and >= 0 ? $"hand {(sk.GlobalTransform * sk.GetBoneGlobalPose(h).Origin)}" : "no hand";
        GD.Print($"CLAWPOSE {name} t {_connectedFor:F3} posed {_local?.PosedClip ?? "stances"} melee step {_client!.Predicted.MeleeStep} at {_client.Predicted.MeleeTime:F3} s; {where}; frame {Engine.GetFramesDrawn()}");
        if (_tfwShots is null || DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        if (_clawCam == 1 && ClawCrop(image.GetSize()) is { } rect) image = image.GetRegion(rect);
        string path = Path.Combine(_tfwShots, name + ".png");
        GD.Print(image.SavePng(path) == Error.Ok ? $"tfw shot -> {path}" : $"tfw shot failed: {path}");
    }

    Rect2I? ClawCrop(Vector2I size)
    {
        if (_camera is not { } camera || _local?.Skeleton is not { } sk) return null;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int b = 0; b < sk.GetBoneCount(); b++)
        {
            var world = sk.GlobalTransform * sk.GetBoneGlobalPose(b).Origin;
            if (camera.IsPositionBehind(world)) continue;
            var p = camera.UnprojectPosition(world);
            minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y); maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y);
        }
        if (minX > maxX) return null;
        float pad = MathF.Max(maxX - minX, maxY - minY) * 0.22f + 24f;
        int x0 = Math.Clamp((int)(minX - pad), 0, size.X - 1), y0 = Math.Clamp((int)(minY - pad), 0, size.Y - 1);
        int x1 = Math.Clamp((int)(maxX + pad), x0 + 1, size.X), y1 = Math.Clamp((int)(maxY + pad), y0 + 1, size.Y);
        return new Rect2I(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>The capture's camera, after the game's: an orbit round the marine (the game's facing turned by the azimuth) or a close view riding the right hand; nothing without the aid.</summary>
    void ClawCamFrame()
    {
        if (_camera is not { } camera || _local?.Skeleton is not { } sk) return;
        if (_clawCam == 0)
        {
            var fwd = -camera.GlobalBasis.Z; fwd.Y = 0f;
            if (fwd.LengthSquared() > 1e-6f) _clawFace = fwd.Normalized();
            return;
        }
        if (_clawCam == 1)
        {
            var dir = _clawFace.Rotated(Vector3.Up, Mathf.DegToRad(-_clawAzimuth));
            var feet = _local.GlobalPosition;
            camera.GlobalTransform = Transform3D.Identity.Translated(feet + dir * _clawDistance + Vector3.Up * _clawHeight).LookingAt(feet + Vector3.Up * 1.15f, Vector3.Up);
            return;
        }
        int hand = sk.FindBone("RIG_R_Hand");
        if (hand < 0 || _clawHandDirection == Vector3.Zero || WeaponRig._clawFrame is not { } frame) return;
        var pose = sk.GlobalTransform * sk.GetBoneGlobalPose(hand);
        var origin = pose.Origin + pose.Basis * frame.Along * 0.18f;
        var look = pose.Basis * _clawHandDirection;
        var eye = origin + look.Normalized() * _clawHandDistance;
        var toHand = (origin - eye).Normalized();
        camera.GlobalTransform = Transform3D.Identity.Translated(eye).LookingAt(origin, MathF.Abs(toHand.Dot(Vector3.Up)) > 0.97f ? Vector3.Forward : Vector3.Up);
    }
}
