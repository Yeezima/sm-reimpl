using System;
using System.Threading.Tasks;
using Godot;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// <c>--dn-pass-film</c> (the 2026-11-17 Dreadnought pass's proof; Musou Mode; a test aid, with <c>--tfw-dreadnought-capture</c>, <c>--write-movie f.avi --fixed-fps 30</c>, server <c>SM_TFW_KEEP_HUMAN=1</c> and <c>SM_DN_TEST_GUARD=1</c>):
/// the crosshair is laid on an Ork's chest from the camera (not the eye: his camera sits high and behind) and the cannon held (the rounds go through the crosshair, the sustained shake), then he walks to the crowd and mashes
/// Melee (the swing chain, the 1.5 times cleave, the hit shakes) and stomps. <c>DNPASS</c> lines carry the frames to cut by and what the shake did. With <c>--show-shot-rays</c> the crosshair's and the damaging ray are drawn.
/// </summary>
public partial class NetworkView
{
    /// <summary>Lays the view so the camera's own ray goes through the chest of the nearest Ork within <paramref name="within"/> m of the Dreadnought; false with none.</summary>
    bool DnAimCrosshair(float within)
    {
        if (_camera is null || _client is null) return false;
        var me = _client.Predicted.Position;
        if (_musouState.NearestTo(me, within) is not { } at) return false;
        var cam = ToGame(_camera.GlobalPosition);
        var to = new Num.Vector3(at.X, at.Y, at.Z + 1.1f) - cam;
        float flat = MathF.Max(0.5f, MathF.Sqrt(to.X * to.X + to.Y * to.Y));
        _yaw = MathF.Atan2(-to.X, to.Y);
        _pitch = MathF.Atan2(to.Z, flat);
        return true;
    }

    /// <summary>The crosshair's meeting point against the round the server's rules fire for it (<see cref="Converged"/>, <see cref="Combat.ShotOrigin"/>): how far the round passes from the point, and how far under it.</summary>
    void DnAimProbe(string tag)
    {
        if (_camera is null || _client is null || _config is null) return;
        var me = _client.Predicted;
        var (point, _, _, _) = CenterRayTarget(me, _config, FreshCenterRay());
        var input = Converged(new PlayerInput(0, 0f, 0f, me.Yaw, InputButtons.Fire, me.Pitch));
        var origin = Combat.ShotOrigin(me, input, _config, _client.Collision);
        var ray = Combat.ShotDirection(input, _config, me);
        var rel = point - origin;
        float along = Num.Vector3.Dot(rel, ray);
        var foot = origin + ray * along;
        float miss = (point - foot).Length(), below = point.Z - foot.Z;
        var cam = ToGame(_camera.GlobalPosition);
        GD.Print($"DNPASS aimprobe {tag}: the crosshair meets the world {Num.Vector3.Distance(point, cam):F1} m from the camera; the round's ray passes {miss:F2} m from that point ({below:F2} m below it); origin {Num.Vector3.Distance(origin, Combat.EyePosition(me, _config)):F2} m from the eye (cut {_config.MaxShotOriginOffset:F1} m)");
    }

    /// <summary>Turns the view until the camera's ray meets level geometry 12 to 45 m ahead (a wall to leave an impact on); false after a full turn.</summary>
    async Task<bool> DnAimWall()
    {
        if (_client is null || _config is null) return false;
        float yaw0 = _yaw;
        for (int k = 0; k < 24; k++)
        {
            _yaw = yaw0 + k * MathF.PI / 12f; _pitch = -0.03f;
            await TfwFrames(8);
            var me = _client.Predicted;
            var (_, _, start, direction) = CenterRayTarget(me, _config, FreshCenterRay());
            if (_client.Collision?.Weapons.Raycast(start, direction, 60f) is { } wall && wall.Distance is > 12f and < 45f) return true;
        }
        return false;
    }

    async Task DnPassFilm()
    {
        var client = _client!;
        void Mark(string what) => GD.Print($"DNPASS {what} frame {Engine.GetFramesDrawn()} t {_connectedFor:F1}");
        float shakePeak = 0f, shakeDeg = 0f;
        async Task Watch(double seconds, bool aim = false)
        {
            double until = _connectedFor + seconds, log = _connectedFor + 0.5;
            while (_connectedFor < until)
            {
                if (aim && client.Predicted.Alive) DnAimCrosshair(45f);
                await TfwFrames();
                shakePeak = MathF.Max(shakePeak, DreadShakeNow.Offset.Length());
                shakeDeg = MathF.Max(shakeDeg, DreadShakeNow.Degrees.Length());
                if (_connectedFor >= log) { log += 0.5; GD.Print($"DNPASS shake t {_connectedFor:F1} peak offset {shakePeak:F3} m degrees {shakeDeg:F2} sustain {_dreadSustain:F2}"); shakePeak = shakeDeg = 0f; }
            }
        }
        Mark("start");
        await TfwWait(5.4);   // the entrance's lock is over
        // the open landing ground, no Ork yet: the crosshair on a wall, the cannon held; the impact stands where the crosshair is
        Mark("wall");
        bool wall = await DnAimWall();
        GD.Print($"DNPASS wall found {wall}");
        await TfwWait(0.3);
        await TfwShot("dnpass-wall-ready");
        DnAimProbe("wall");
        TfwAction(GameAction.Fire, true);
        await Watch(0.9);
        DnAimProbe("wall, firing");
        await TfwShot("dnpass-wall-fire");
        await Watch(1.4);
        TfwAction(GameAction.Fire, false);
        await Watch(1.0);
        var point = _tfwState.Objectives[0].Position;
        Mark("walk");
        await TfwPathTo(point, "pass film: walked towards the Orks", 90.0, () => _musouState.MiddleNear(client.Predicted.Position, 30f) is not null);
        for (int k = 0; k < 40 && !DnAimCrosshair(45f); k++) await TfwWait(0.25);
        Mark("aim");
        await Watch(0.4, aim: true);
        await TfwShot("dnpass-aim-ready");
        DnAimProbe("Ork");
        TfwAction(GameAction.Fire, true);
        await Watch(1.2, aim: true);
        DnAimProbe("Ork, firing");
        await TfwShot("dnpass-aim-fire-1");
        await Watch(0.8, aim: true);
        await TfwShot("dnpass-aim-fire-2");
        await Watch(2.0, aim: true);
        TfwAction(GameAction.Fire, false);
        await Watch(1.0);   // the sustained shake falls away
        Mark("melee");
        // into the crowd: walk until some Orks stand within 6 m, then mash Melee (a tap every 8 frames, as a human's)
        for (double stop = _connectedFor + 150.0; _connectedFor < stop && TfwNearCount(6f) < 4 && client.Predicted.Alive;)
        {
            var me = client.Predicted.Position;
            if (_musouState.NearestTo(me, 90f) is { } ork)
            {
                _yaw = MathF.Atan2(-(ork.X - me.X), ork.Y - me.Y); _pitch = 0f;
                TfwAction(GameAction.MoveForward, TfwFlat(ork, me) > 3.5f);
            }
            else TfwAction(GameAction.MoveForward, false);
            await TfwFrames(6);
        }
        TfwAction(GameAction.MoveForward, false);
        Mark("spam");
        double spamStart = _connectedFor;
        for (double stop = _connectedFor + 8.0; _connectedFor < stop;)
        {
            if (_musouState.NearestTo(client.Predicted.Position, 30f) is { } near) { var me2 = client.Predicted.Position; _yaw = MathF.Atan2(-(near.X - me2.X), near.Y - me2.Y); _pitch = 0f; }
            _dnCam = _connectedFor < spamStart + 4.0 ? 1 : 0;   // the first half from the side (the chain), the second from the player's view (the cleave, the shake)
            await TfwActionTap(GameAction.Melee);
            shakePeak = MathF.Max(shakePeak, DreadShakeNow.Offset.Length()); shakeDeg = MathF.Max(shakeDeg, DreadShakeNow.Degrees.Length());
        }
        _dnCam = 0;
        GD.Print($"DNPASS spam done: crowd near {TfwNearCount(12f)}, peak shake offset {shakePeak:F3} m degrees {shakeDeg:F2}");
        await TfwShot("dnpass-melee-spam");
        Mark("stomp");
        TfwFaceCrowd(30f);
        await TfwActionTap(GameAction.StunStrike);
        await Watch(1.6);
        await TfwShot("dnpass-stomp");
        Mark("end");
        GetTree().Quit(0);
    }
}
