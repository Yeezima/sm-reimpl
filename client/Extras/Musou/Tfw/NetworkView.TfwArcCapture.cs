using System;
using System.Threading.Tasks;
using Godot;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou swing arc capture aid <c>--tfw-arc-capture</c> (before <c>--tfw-test &lt;dir&gt;</c>; server <c>SM_TFW_KEEP_HUMAN=1</c>; a windowed run): the before / after stills of the swing arc
/// (owner, 2026-11-10). The scripted player deploys on lane B, walks into the crowd and swings a light chain of three and a stun strike twice: the first time the stills come from the player's
/// own camera (<c>arc-player-*</c>), the second time from a camera high behind and beside the player (<c>arc-third-*</c>), each still <see cref="StillAfter"/> s after the predicted strike.
/// Pick the weapon with <c>--class</c> and <c>--melee</c>; <c>--musou-old-slashes</c> gives the "before" set. Every press goes through the real actions. <c>--tfw-arc-film</c> (2026-11-14, for a
/// <c>--write-movie</c> recording): only the third-person view, its camera kept on the player the whole time (no stills), the light chain of three and the stun strike twice, a pause between them.
/// </summary>
public partial class NetworkView
{
    /// <summary>Seconds after the predicted strike a still is taken (the arc has swept and holds).</summary>
    const double StillAfter = 0.13;

    double _musouLastStrikeAt = double.NegativeInfinity;   // the own predicted strike's time (MusouOwnSwingArc), for the capture
    Camera3D? _arcShotCamera;
    bool _arcShotCameraOn;

    static readonly bool ArcFilm = CmdFlag("tfw-arc-film");

    async Task TfwArcCapture()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(1.0);
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "arc: walked towards the Orks", 45.0, () => _musouState.MiddleNear(client.Predicted.Position, 22f) is not null);
        string weapon = MeleeWargear(client.Predicted) ?? "-";
        foreach (string view in ArcFilm ? new[] { "third" } : new[] { "player", "third" })
        {
            for (int run = 0; run < 4 && TfwNearCount(7f) < 5; run++)   // into the crowd until at least 5 Orks stand within 7 m
            {
                var goal = _musouState.MiddleNear(client.Predicted.Position, 30f) ?? point;
                await TfwPathTo(goal, $"arc: into the crowd ({run + 1})", 8.0, () => TfwNearCount(7f) >= 5);
            }
            for (double until = _connectedFor + 20.0; TfwNearCount(6f) < 8 && _connectedFor < until;) await TfwWait(0.1);   // let the crowd close in: every swing of the chain lands
            GD.Print($"TFWARC t {_connectedFor:F2} {view} view, {weapon}, Orks within 7 m: {TfwNearCount(7f)}");
            if (ArcFilm) _arcShotCameraOn = true;
            for (int k = 0; k < (ArcFilm ? 8 : 4); k++)
            {
                bool heavy = k % 4 == 3;
                TfwAimNearest(8f);
                double pressed = _connectedFor;
                await TfwActionTap(heavy ? GameAction.StunStrike : GameAction.Melee);
                await TfwUntil("arc: the strike", () => _musouLastStrikeAt > pressed, 1.5);
                if (view == "third") TfwArcCamera(true);
                await TfwArcWait(ArcFilm ? 0.9 : StillAfter);
                if (!ArcFilm)
                {
                    await TfwShot($"arc-{view}-{(heavy ? "heavy" : $"light{k + 1}")}");
                    TfwArcCamera(false);
                }
                if (ArcFilm) await TfwArcWait(0.3); else await TfwWait(heavy ? 1.1 : 0.45);
            }
            if (ArcFilm) await TfwArcWait(1.2); else await TfwWait(1.2);
        }
        GD.Print($"TFWARC t {_connectedFor:F2} done: {_musouArcs} arcs drawn");
        GetTree().Quit(0);
    }

    /// <summary>Waits, keeping the capture camera current (the game's camera may take the view back each frame).</summary>
    async Task TfwArcWait(double seconds)
    {
        double end = _connectedFor + seconds;
        while (_connectedFor < end)
        {
            if (_arcShotCameraOn) { if (ArcFilm) TfwArcCamera(true); else _arcShotCamera?.MakeCurrent(); }
            await TfwFrames();
        }
        if (_arcShotCameraOn) _arcShotCamera?.MakeCurrent();
    }

    /// <summary>The third-person still's camera: 8 m up, 5.5 m behind and 4 m to the left of the player at the strike, looking at a point 3 m ahead of the player.</summary>
    void TfwArcCamera(bool on)
    {
        _arcShotCameraOn = on;
        if (!on || _camera is null || _client is null)
        {
            _camera?.MakeCurrent();
            return;
        }
        var me = _client.Predicted;
        var forward = new Num.Vector3(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw), 0f);
        var left = new Num.Vector3(-MathF.Cos(me.Yaw), -MathF.Sin(me.Yaw), 0f);
        var eye = ArcFilm ? me.Position - forward * 4.2f + left * 2.6f + new Num.Vector3(0f, 0f, 2.9f) : me.Position - forward * 5.5f + left * 4f + new Num.Vector3(0f, 0f, 8f);   // film: low and close, the arcs read from the side
        var look = me.Position + forward * 3f + new Num.Vector3(0f, 0f, ArcFilm ? 1.3f : 0.8f);
        if (_arcShotCamera is null)
        {
            _arcShotCamera = new Camera3D { Name = "ArcShotCamera" };
            AddChild(_arcShotCamera);
        }
        _arcShotCamera.Fov = _camera.Fov;
        _arcShotCamera.Near = _camera.Near;
        _arcShotCamera.Far = _camera.Far;
        _arcShotCamera.GlobalPosition = ToGodot(eye);
        _arcShotCamera.LookAt(ToGodot(look), Vector3.Up);
        _arcShotCamera.MakeCurrent();
    }
}
