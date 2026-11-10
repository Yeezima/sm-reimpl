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
/// from behind and above: the camera stands off the player for those frames only, <see cref="AutocannonCamFrame"/>). <c>ACCAPTURE</c> lines carry the marks (cut a Movie Maker clip at them) and the weapon in hand. Quits 0.
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
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(1.0);
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "walked towards the Orks", 45.0, () => _musouState.MiddleNear(client.Predicted.Position, 26f) is not null);
        string held = _config is { } c && Loadouts.InHand(c, client.Predicted) is { } w ? w.Name : "?";
        GD.Print($"ACCAPTURE t {_connectedFor:F2} in hand: {held}");
        TfwCheck("the autocannon is in hand", held == SmSim.Extras.Musou.Arsenal.ArsenalRules.Autocannon, held);
        for (int run = 0; run < 3 && !TfwAimNearest(30f); run++) await TfwWait(0.5);
        await TfwShot("ac-player-00-ready");
        GD.Print($"ACCAPTURE mark fire t {_connectedFor:F3} frame {Engine.GetProcessFrames()}");
        int blasts = _tfwBoltBlastsShown;
        TfwAction(GameAction.Fire, true);
        double begun = _connectedFor;
        var stills = new (double At, string Name, int Cam)[] { (0.6, "ac-player-01-fire", 0), (1.4, "ac-third-side-00", 1), (2.2, "ac-third-rear-00", 2), (3.0, "ac-player-02-fire", 0), (3.8, "ac-third-side-01", 1), (4.6, "ac-third-rear-01", 2) };
        int next = 0;
        while (_connectedFor < begun + CaptureSeconds)
        {
            TfwAimNearest(30f);
            await TfwWait(0.1);
            if (next < stills.Length && _connectedFor - begun >= stills[next].At)
            {
                _acCam = stills[next].Cam;
                await TfwFrames(3);
                await TfwShot(stills[next].Name);
                _acCam = 0;
                next++;
            }
        }
        TfwAction(GameAction.Fire, false);
        GD.Print($"ACCAPTURE mark done t {_connectedFor:F3} frame {Engine.GetProcessFrames()}; blasts seen {_tfwBoltBlastsShown - blasts}, drawn this run {_tfwBoltBlastsDrawn}, burst of the heat {client.Predicted.Heat:F1} overheated {client.Predicted.Overheated}");
        TfwCheck("blasts came back", _tfwBoltBlastsShown > blasts, (_tfwBoltBlastsShown - blasts).ToString());
        await TfwWait(1.0);
        GetTree().Quit(0);
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
        var eye = _acCam == 1 ? feet + right * 3.4f + fwd * 0.8f + Vector3.Back * 1.6f : feet - fwd * 3.6f + right * 0.8f + Vector3.Back * 3.0f;
        var look = _acCam == 1 ? feet + fwd * 0.4f + Vector3.Back * 1.35f : feet + fwd * 6f + Vector3.Back * 1.2f;
        camera.GlobalTransform = Transform3D.Identity.Translated(_gameSpace.ToGlobal(eye)).LookingAt(_gameSpace.ToGlobal(look), Vector3.Up);
    }
}
