using System;
using System.Threading.Tasks;
using Godot;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// W1 test aid <c>--tfw-autocannon-capture</c> (with <c>--class "sm - devastator" --primary sm_mp_exotic_auto_cannon --content musou</c>, before <c>--tfw-test &lt;dir&gt;</c>, a <c>--windowed</c> run for the PNGs; server
/// <c>SM_TFW_KEEP_HUMAN=1</c>): the screenshot gate of the Musou autocannon (docs/new-weapons-design.md section 11). The player deploys on lane B, walks into the crowd, and fires the autocannon at the nearest
/// Ork through the real actions for <see cref="CaptureSeconds"/> s; PNGs of the player's own view (<c>ac-player-*</c>), and of a third-person view (<c>ac-third-side-*</c> from beside him, <c>ac-third-rear-*</c>
/// from behind and above: the camera stands off the player for those views only, <see cref="AutocannonCamFrame"/>): each view once with the gun up before the fire (<c>-ready</c>, <c>-idle</c>) and
/// twice in it, each held for a sixth of the fire with its still on the frame after a round leaves (W1c). <c>ACCAPTURE</c> lines carry the marks (cut a Movie Maker clip at them) and the weapon in hand. Quits 0.
/// </summary>
public partial class NetworkView
{
    /// <summary>Seconds of fire in the capture.</summary>
    const double CaptureSeconds = 6.0;

    /// <summary>The third-person view the camera is put in this frame: 0 none, 1 beside the player (his weapon in profile), 2 behind and above (the crowd past him).</summary>
    int _acCam;

    async Task TfwAutocannonCapture()
    {
        var client = _client!;
        if (!await AutocannonDeploy()) return;
        await TfwWait(1.0);
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "walked towards the Orks", 150.0, () => _musouState.MiddleNear(client.Predicted.Position, 26f) is not null);
        string held = _config is { } c && Loadouts.InHand(c, client.Predicted) is { } w ? w.Name : "?";
        GD.Print($"ACCAPTURE t {_connectedFor:F2} in hand: {held}");
        AutocannonEffectsLine();   // W1c: the effects as the client loaded them (the loyalist flash, tracer and impact; NetworkView.AutocannonImpactCapture.cs)
        TfwCheck("the autocannon is in hand", held == SmSim.Extras.Musou.Arsenal.ArsenalRules.Autocannon, held);
        for (int run = 0; run < 3 && !AutocannonAim(); run++) await TfwWait(0.5);
        // W1c: the gun up and not firing, from each view (the owner's stills of the shorter barrel).
        await TfwShot("ac-player-00-ready");
        await AutocannonStill(1, "ac-third-side-idle");
        await AutocannonStill(2, "ac-third-rear-idle");
        await AutocannonStill(3, "ac-close-right-idle");   // zoom-barrel pass: the barrel's join with the receiver, close
        await AutocannonStill(4, "ac-close-left-idle");
        await AutocannonStill(5, "ac-close-top-idle");
        GD.Print($"ACCAPTURE mark fire t {_connectedFor:F3} frame {Engine.GetProcessFrames()}");
        int blasts = _tfwBoltBlastsShown;
        TfwAction(GameAction.Fire, true);
        double begun = _connectedFor;
        // Each view holds the camera for its share of the fire (a recording shows several rounds from it); its still is the frame after a round leaves, so the muzzle flash and the tracer's
        // start are in it (W1c; before, a still at a fixed time mostly fell between rounds).
        var views = new (string Name, int Cam)[] { ("ac-player-01-fire", 0), ("ac-third-side-00", 1), ("ac-third-rear-00", 2), ("ac-player-02-fire", 0), ("ac-third-side-01", 1), ("ac-third-rear-01", 2) };
        double window = CaptureSeconds / views.Length;
        for (int v = 0; v < views.Length; v++)
        {
            double until = begun + window * (v + 1);
            _acCam = views[v].Cam;
            await TfwFrames(3);
            int shots = _fxOwnShots;
            while (_fxOwnShots == shots && _connectedFor < until - 0.1) { AutocannonAim(); await TfwFrames(); }
            bool fresh = _fxOwnShots != shots;
            await TfwFrames(1);
            await TfwShot(views[v].Name);
            GD.Print($"ACCAPTURE still {views[v].Name} t {_connectedFor:F3}: {(fresh ? "a frame after a round" : "no round in its window")}");
            while (_connectedFor < until) { AutocannonAim(); await TfwFrames(); }
        }
        _acCam = 0;
        TfwAction(GameAction.Fire, false);
        GD.Print($"ACCAPTURE mark done t {_connectedFor:F3} frame {Engine.GetProcessFrames()}; blasts seen {_tfwBoltBlastsShown - blasts}, drawn this run {_tfwBoltBlastsDrawn}, burst of the heat {client.Predicted.Heat:F1} overheated {client.Predicted.Overheated}");
        TfwCheck("blasts came back", _tfwBoltBlastsShown > blasts, (_tfwBoltBlastsShown - blasts).ToString());
        await TfwWait(1.0);
        GetTree().Quit(0);
    }

    /// <summary>A still from a third-person view (<see cref="_acCam"/>), the camera back on the player after it.</summary>
    async Task AutocannonStill(int cam, string name)
    {
        _acCam = cam;
        await TfwFrames(3);
        await TfwShot(name);
        _acCam = 0;
    }

    bool _acAimLogged;

    /// <summary>
    /// The capture's aim (W1c): the nearest Ork within 45 m that the eye sees (no level geometry on the line, <c>Collision.Weapons</c>), so the rounds and their tracers cross open ground; else the
    /// nearest within 30 m as before (W1b's run stood at a wall with the nearest Ork behind it: every round burst on the wall 8 m away and its tracer was hardly drawn).
    /// </summary>
    bool AutocannonAim()
    {
        var me = _client!.Predicted.Position;
        var eye = me + new Num.Vector3(0f, 0f, 1.6f);
        var world = _client.Collision;
        bool Seen(Num.Vector3 at)
        {
            var to = at + new Num.Vector3(0f, 0f, 1.0f) - eye;
            float length = to.Length();
            return world is null || length < 1f || world.Weapons.Raycast(eye, to / length, length - 0.5f) is null;
        }
        if (_musouState.NearestTo(me, 45f, Seen) is not { } at)
        {
            if (!_acAimLogged) { _acAimLogged = true; GD.Print("ACCAPTURE aim: no Ork in sight within 45 m; the nearest within 30 m"); }
            return TfwAimNearest(30f);
        }
        if (!_acAimLogged) { _acAimLogged = true; GD.Print($"ACCAPTURE aim: an Ork in sight {TfwFlat(at, me):F1} m away"); }
        _yaw = MathF.Atan2(-(at.X - me.X), at.Y - me.Y);
        _pitch = MathF.Atan2(at.Z + 1.0f - (me.Z + 1.6f), MathF.Max(0.5f, TfwFlat(at, me)));
        return true;
    }

    /// <summary>
    /// <c>TfwTestDeploy</c> for a Movie Maker run: the same steps, but the Stormbird's landing is waited for 90 s, not 10 (a recording renders slower than the server's clock and the blink between the screens costs frames).
    /// </summary>
    async Task<bool> AutocannonDeploy()
    {
        await TfwTestPanelsAway();
        if (!await TfwWalkTo(TfwHub!.DestinationStation, "walked to Destination")) return false;
        await TfwTap(Key.E);
        if (!await TfwUntil("E at Destination opens the war map", () => _tfwPanel is not null, 6.0)) return false;
        await TfwFrames(4);
        _tfwPanel!.DeployButton(TfwLane.B)!.GrabFocus();
        await TfwFrames(2);
        await TfwTap(Key.Enter);
        if (!await TfwUntil("the server echoes it: travel", () => TfwNow.Phase == TfwRunPhase.Travel, 10.0)) return false;
        return await TfwUntil("the Stormbird lands: Battle", () => TfwNow.Phase == TfwRunPhase.Battle && _tfwState.Objectives.Count == TfwBattle.CommanderObjective + 1, 90.0);
    }

    /// <summary>
    /// Puts the camera off the player for a capture's third-person frames (<see cref="_acCam"/>), after the game's own camera; nothing without the aid. Game space: the player's yaw gives his forward
    /// (-sin, cos) and right (cos, sin).
    /// </summary>
    void AutocannonCamFrame()
    {
        if (_acCam == 0 || _camera is not { } camera || _gameSpace is null || _client is null) return;
        var p = _client.Predicted.Position;
        var fwd = new Vector3(-MathF.Sin(_yaw), MathF.Cos(_yaw), 0f);
        var right = new Vector3(MathF.Cos(_yaw), MathF.Sin(_yaw), 0f);
        var feet = new Vector3(p.X, p.Y, p.Z);
        Vector3 eye, look;
        if (_acCam >= 3)
        {
            // the close-ups (zoom-barrel pass): on the barrel's join with the receiver (about 1.1 m ahead of the chest, 1.3 m up), 0.8 m off it: 3 from the right, 4 from the left, 5 from above
            look = feet + fwd * 1.15f + Vector3.Back * 1.3f;
            eye = look + (_acCam == 3 ? right * 0.8f + fwd * 0.25f + Vector3.Back * 0.15f : _acCam == 4 ? -right * 0.8f + fwd * 0.25f + Vector3.Back * 0.15f : Vector3.Back * 0.8f + fwd * 0.2f);
        }
        else
        {
            eye = _acCam == 1 ? feet + right * 3.4f + fwd * 0.8f + Vector3.Back * 1.6f : feet - fwd * 3.6f + right * 0.8f + Vector3.Back * 3.0f;
            look = _acCam == 1 ? feet + fwd * 0.4f + Vector3.Back * 1.35f : feet + fwd * 6f + Vector3.Back * 1.2f;
        }
        camera.GlobalTransform = Transform3D.Identity.Translated(_gameSpace.ToGlobal(eye)).LookingAt(_gameSpace.ToGlobal(look), Vector3.Up);
    }
}
