using System;
using System.Threading.Tasks;
using Godot;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// D2+ test aid <c>--tfw-dreadnought-capture</c> (with <c>--content musou --class "sm - dreadnought"</c>, before <c>--tfw-test &lt;dir&gt;</c>, a <c>godot.cmd --windowed</c> run for the PNGs; server <c>SM_TFW_KEEP_HUMAN=1</c>,
/// <c>--bind 127.0.0.1</c>): deploys on lane B (the hub plays the Tactical stand-in; he is the Dreadnought from the landing) and photographs him from the player's view and from third-person views (<see cref="_dnCam"/>:
/// beside, behind and above, in front): the entrance, standing, walking, running, strafing, sprinting. <c>DNCAPTURE</c> lines carry what was seen (class, speed, the clip the pose picked). Quits 0.
/// </summary>
public partial class NetworkView
{
    /// <summary>The third-person view the camera is put in: 0 none, 1 beside, 2 behind and above, 3 in front.</summary>
    int _dnCam;
    string _dnDir = "";

    async Task DnStill(int cam, string name)
    {
        _dnCam = cam;
        await TfwFrames(4);
        await TfwShot(name);
        _dnCam = 0;
    }

    async Task DnViews(string tag)
    {
        await TfwShot($"dn-player-{tag}");
        await DnStill(1, $"dn-side-{tag}");
        await DnStill(2, $"dn-rear-{tag}");
        await DnStill(3, $"dn-front-{tag}");
    }

    async Task TfwDreadnoughtCapture()
    {
        var client = _client!;
        if (!await AutocannonDeploy()) return;
        TfwCheck("he is the Dreadnought after the landing", IsDreadnoughtClass(client.Predicted.Class), client.Predicted.Class.ToString());
        GD.Print($"DNCAPTURE t {_connectedFor:F2} landed, class {client.Predicted.Class}, health {client.Predicted.Health:F0}, locked {DreadnoughtEntryText()}");
        await TfwWait(0.6);
        await DnStill(1, "dn-side-entrance-1");
        await TfwWait(1.4);
        await DnStill(2, "dn-rear-entrance-2");
        await TfwWait(3.2);
        await DnViews("stand");
        // forward: W is the run; Shift the sprint; A strafes (the keys the human uses)
        TfwKey(Key.W, true);
        await TfwWait(1.2);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} run, speed {SpeedOf(client.Predicted):F1}");
        await DnViews("run");
        TfwKey(Key.Shift, true);
        await TfwWait(1.2);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} sprint, speed {SpeedOf(client.Predicted):F1}");
        await DnViews("sprint");
        TfwKey(Key.Shift, false);
        TfwKey(Key.W, false);
        TfwKey(Key.A, true);
        await TfwWait(1.0);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} strafe, speed {SpeedOf(client.Predicted):F1}");
        await DnViews("strafe");
        TfwKey(Key.A, false);
        TfwKey(Key.S, true);
        await TfwWait(0.9);
        await DnStill(1, "dn-side-back");
        TfwKey(Key.S, false);
        await TfwWait(0.5);
        GD.Print("DNCAPTURE done");
        GetTree().Quit(0);
    }

    static float SpeedOf(PlayerState s) => MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
    string DreadnoughtEntryText() => _client is { } c ? $"yaw {c.Predicted.Yaw:F2}" : "";

    /// <summary>The camera off the player for the capture's third-person views, after the game's own camera; nothing without the aid. The player is 3 m tall: 7 m off.</summary>
    void DreadnoughtCamFrame()
    {
        if (_dnCam == 0 || _camera is not { } camera || _gameSpace is null || _client is null) return;
        var p = _client.Predicted.Position;
        var fwd = new Vector3(-MathF.Sin(_yaw), MathF.Cos(_yaw), 0f);
        var right = new Vector3(MathF.Cos(_yaw), MathF.Sin(_yaw), 0f);
        var feet = new Vector3(p.X, p.Y, p.Z);
        var look = feet + Vector3.Back * 1.6f;
        var eye = _dnCam switch
        {
            1 => feet + right * 7f + Vector3.Back * 2.2f,
            2 => feet - fwd * 7f + right * 1.0f + Vector3.Back * 4.0f,
            _ => feet + fwd * 7f + right * 1.5f + Vector3.Back * 2.2f,
        };
        camera.GlobalTransform = Transform3D.Identity.Translated(_gameSpace.ToGlobal(eye)).LookingAt(_gameSpace.ToGlobal(look), Vector3.Up);
    }
}
