using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Test aid <c>--bolt-smoke-scene [wargear] --bolt-smoke-dir &lt;dir&gt;</c> (fx/bolt-smoke proof, 2026-11-15; a presentation aid, client only): a deterministic scene for the before / after strips of the bolt weapons' smoke.
/// The camera stands behind the player's shoulder and looks at a fixed point 8 m ahead of him (his yaw as it is); three synthetic hits, then (Musou) kills and a bolt blast, or (Classic) hits on the
/// ground, play there at fixed times through the production paths (<c>FxShot</c>, the Ork blood, <c>FxDetonation</c>), and a still is saved every 0.1 s for 2 s (<c>scene-00</c> ...).
/// Classic: <c>--bolt-smoke-classic</c> starts it by itself on a quiet server; Musou: <c>--tfw-autocannon-capture --bolt-smoke-scene</c> (the deploy, then the scene). <c>--old-bolt-smoke</c>: the effects before the change.
/// </summary>
public partial class NetworkView
{
    const double BoltSceneStep = 0.1;
    const int BoltSceneStills = 20;
    const float BoltSceneRange = 8f;

    bool _bsStarted;
    Vector3? _bsEye, _bsLook;
    readonly List<(double At, Action Play)> _bsEvents = new();
    double _bsT0;

    /// <summary>The wargear of the scene, or null when the aid is off.</summary>
    internal string? BoltSceneGun => CmdFlag("bolt-smoke-scene") ? (CmdArg("bolt-smoke-scene") is { Length: > 0 } g && !g.StartsWith("--") ? g : "sm_mp_ranged_bolter") : null;

    void BoltSmokeSceneFrame()
    {
        if (BoltSceneGun is null || _camera is null || _client is null) return;
        if (!_bsStarted && CmdFlag("bolt-smoke-classic") && _client.Predicted.Alive && _connectedFor > 4.0) { _bsStarted = true; _ = BoltSmokeSceneRun(); }
        if (_bsEye is { } eye && _bsLook is { } look)
        {
            _camera.GlobalTransform = new Transform3D(Basis.LookingAt((look - eye).Normalized(), Vector3.Up), eye);
            _camera.Fov = 62f;
        }
        for (int i = _bsEvents.Count - 1; i >= 0; i--)
            if (_connectedFor >= _bsT0 + _bsEvents[i].At) { var play = _bsEvents[i].Play; _bsEvents.RemoveAt(i); play(); }
    }

    /// <summary>The scene's geometry in game space: the player's feet, forward and right (from his yaw), the target 8 m ahead and the camera's eye.</summary>
    internal readonly record struct BoltScene(Num.Vector3 Feet, Num.Vector3 Fwd, Num.Vector3 Right, Num.Vector3 Target, Num.Vector3 Eye);

    internal void BoltSceneAt(double at, Action play) => _bsEvents.Add((at, play));

    internal async Task BoltSmokeSceneRun()
    {
        string gun = BoltSceneGun!;
        string dir = CmdArg("bolt-smoke-dir") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "bolt-smoke-scene");
        Directory.CreateDirectory(dir);
        var feet = _client!.Predicted.Position;
        var fwd = new Num.Vector3(-MathF.Sin(_yaw), MathF.Cos(_yaw), 0f);
        var right = new Num.Vector3(MathF.Cos(_yaw), MathF.Sin(_yaw), 0f);
        var target = feet + fwd * BoltSceneRange;
        var eye = target - fwd * 4.0f + right * 2.6f + new Num.Vector3(0f, 0f, 1.9f);   // off to the side and above the retainers
        var scene = new BoltScene(feet, fwd, right, target, eye);
        _bsEye = ToGodot(eye);
        _bsLook = ToGodot(target + new Num.Vector3(0f, 0f, 1.0f));
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        for (int w = 0; w < 1800; w++)
        {
            bool ready = true;
            ExtrasBoltSceneReady(ref ready);   // EXTRAS-HOOK: the Ork blood data loads in the background
            if (ready) break;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        _bsT0 = _connectedFor + 0.5;
        GD.Print($"BOLTSCENE t {_connectedFor:F2} gun {gun} old {CmdFlag("old-bolt-smoke")} feet {feet} yaw {_yaw:F2}");
        if (!MusouScene(scene)) ClassicScene(scene, gun);
        for (int k = 0; k < BoltSceneStills; k++)
        {
            double at = _bsT0 + 0.1 + k * BoltSceneStep;
            while (_connectedFor < at) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                string path = Path.Combine(dir, $"scene-{k:00}.png");
                GD.Print(GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok ? $"BOLTSCENE still {k} t {_connectedFor - _bsT0:F2} -> {path}" : $"BOLTSCENE still failed {path}");
            }
        }
        GetTree().Quit(0);
    }

    bool MusouScene(in BoltScene s)
    {
        bool played = false;
        ExtrasBoltSmokeScene(s, BoltSceneGun!, ref played);   // EXTRAS-HOOK: Musou's Ork hits, kills and bolt blast
        return played;
    }

    partial void ExtrasBoltSmokeScene(in BoltScene scene, string gun, ref bool played);
    partial void ExtrasBoltSceneReady(ref bool ready);

    /// <summary>Classic: eight bolts into a wall point and eight into the ground, 0.1 s apart, 8 m away, through <c>FxShot</c>.</summary>
    void ClassicScene(in BoltScene s, string gun)
    {
        if (_config is null) return;
        int index = -1;
        for (int i = 0; i < _config.Weapons.Count; i++) if (_config.Weapons[i].Name.Equals(gun, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        if (index < 0 || FxWeapon(gun) is not { } w) { GD.Print($"BOLTSCENE no weapon '{gun}'"); return; }
        var spec = _config.Weapons[index];
        var chest = ToGodot(s.Target - s.Right * 1.5f + new Num.Vector3(0f, 0f, 1.4f));
        var ground = ToGodot(s.Target + s.Right * 1.5f);
        var from = ToGodot(s.Eye);
        for (int k = 0; k < 8; k++)
        {
            double at = k * 0.1;
            BoltSceneAt(at, () => ClassicShot(spec, w, index, from, chest, ShotHit.World, Vector3.Zero));   // a wall: its normal back toward the shooter
            BoltSceneAt(at, () => ClassicShot(spec, w, index, from, ground, ShotHit.World, Vector3.Up));
        }
    }

    void ClassicShot(WeaponSpec spec, WeaponEffects w, int index, Vector3 from, Vector3 end, ShotHit hit, Vector3 normalIn)
    {
        var dir = (end - from).Normalized();
        var normal = normalIn == Vector3.Zero ? -dir : normalIn;
        FxShot(-999, spec, w, index, 0, end, hit, -1, normal, dir, ToGame(from), own: false, victim: -1, armourUp: false, rayOrigin: from, rayDir: dir);
    }
}
