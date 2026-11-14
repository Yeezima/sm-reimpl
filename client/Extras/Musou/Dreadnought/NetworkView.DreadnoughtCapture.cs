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
        // the guns: walk towards the Orks, aim at the nearest and hold Fire (the cannon), then Switch and fire the melta
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "walked towards the Orks", 90.0, () => _musouState.MiddleNear(client.Predicted.Position, 26f) is not null);
        for (int run = 0; run < 3 && !AutocannonAim(); run++) await TfwWait(0.5);
        PlayerState me = client.Predicted;
        string held = _config is { } cfg && Loadouts.InHand(cfg, me) is { } hw ? hw.Name : "?";
        GD.Print($"DNCAPTURE t {_connectedFor:F2} in hand {held}, clip {me.Clip}, reserve {me.Reserve}");
        TfwCheck("the cannon is in hand", held == DreadnoughtRules.CannonWargear, held);
        await TfwShot("dn-player-gun-ready");
        int clip0 = me.Clip;
        TfwAction(GameAction.Fire, true);
        double begun = _connectedFor;
        await TfwWait(0.5);
        await DnStill(1, "dn-side-fire-1");
        while (_connectedFor < begun + 1.2) { AutocannonAim(); await TfwFrames(); }
        await TfwShot("dn-player-fire");
        await DnStill(2, "dn-rear-fire");
        while (_connectedFor < begun + 3.0) { AutocannonAim(); await TfwFrames(); }
        TfwAction(GameAction.Fire, false);
        GD.Print($"DNCAPTURE t {_connectedFor:F2} after {(_connectedFor - begun):F1} s of fire: clip {client.Predicted.Clip} (was {clip0}), reload {client.Predicted.ReloadLeft:F2}");
        TfwCheck("rounds were fired", client.Predicted.Clip < clip0 || client.Predicted.ReloadLeft > 0f, $"{clip0} -> {client.Predicted.Clip}");
        await TfwWait(0.3);
        // the melta: Switch, then fire it at the crowd twice
        await TfwActionTap(GameAction.NextWeapon);
        await TfwWait(1.2);
        held = _config is { } cfg2 && Loadouts.InHand(cfg2, client.Predicted) is { } mw ? mw.Name : "?";
        GD.Print($"DNCAPTURE t {_connectedFor:F2} in hand {held}, clip {client.Predicted.Clip}");
        TfwCheck("Switch brought the melta", held == DreadnoughtRules.MeltaWargear, held);
        await DnStill(1, "dn-side-melta-ready");
        for (int shot = 0; shot < 2; shot++)
        {
            AutocannonAim();
            TfwAction(GameAction.Fire, true);
            await TfwFrames(3);
            int clip = client.Predicted.Clip;
            if (shot == 0) { await TfwShot("dn-player-melta"); await DnStill(1, "dn-side-melta"); }
            TfwAction(GameAction.Fire, false);
            GD.Print($"DNCAPTURE melta shot {shot}: clip {clip}, after {client.Predicted.Clip}");
            await TfwWait(1.5);
        }
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
