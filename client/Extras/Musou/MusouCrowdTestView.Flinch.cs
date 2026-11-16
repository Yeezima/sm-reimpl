using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Test aid <c>--musou-crowd-test --flinch stand|run|attack|burst|crowd [--kinds Boy]</c> (Ork hit reactions, owner 2026-11-13): the real crowd on the plain stage, hit by scripted shots from the front.
/// <c>stand</c>: a Boy stands and is hit once a second (a bolter, a stalker bolter, an autocannon, a heavy then a charge melee, a blast beside it); <c>run</c>: a Boy runs and takes an automatic burst (12 a second);
/// <c>attack</c>: a Boy attacks on a loop and is hit in the wind-up, the swing and the recovery; <c>burst</c>: sustained bolter fire at 12 a second for 3 s; <c>crowd</c>: 15 by 15 Orks advance under
/// sustained fire (6 hits every 50 ms). A frame is saved every step (30 fps) to <c>--out</c> as <c>&lt;--name&gt;-NNNN.png</c>; <c>--no-musou-ork-flinch</c> after the <c>--</c> gives the baseline. A FLINCHTEST line per
/// second prints the Ork's displacement and its worst foot drift. **Placeholder demonstration** around original assets read at run time.
/// </summary>
public partial class MusouCrowdTestView
{
    string? _flinchMode;
    int _flinchFrame;
    readonly List<ushort> _flinchIds = new();
    readonly Random _flinchRandom = new(7);
    float _flinchPrevT; int _driftK;
    double _flinchMs, _flinchMsMax; int _flinchMsN;

    void FlinchFrame(MusouEnemyType kind, MusouLooks.Look look, float dt)
    {
        MusouOrkFlinchRules.TestScale = Arg(_args, "flinch-scale", 1f);
        MusouOrkFlinchPose.DebugPlant = _args.ContainsKey("plant-debug");
        _flinchMode ??= _args.GetValueOrDefault("flinch") ?? "stand";
        float t = _t += dt;
        _shown.Clear();
        var front = new System.Numerics.Vector2(-1f, 0f);   // the hit comes from the front (+X) and pushes the Ork back (-X)
        float speed = SpeedOf(kind);
        float seconds = _flinchMode switch { "stand" => 8f, "drift" => 11f, "charge" => 5f, "blast" => 4f, "run" => 5f, "attack" => 6f, "burst" => 5f, _ => 6f };
        var centre = Vector3.Zero;
        void Hit(float at, ushort id, float strength) { if (_flinchPrevT < at && t >= at) _crowd!.Hit(id, front, strength, kind); }
        switch (_flinchMode)
        {
            case "stand":
                _shown.Add(new MusouShown(_id, V(0, 0, 0), -MathF.PI / 2f, kind, MusouEnemyState.Seek, t));
                Hit(1.0f, _id, MusouOrkFlinchRules.ShotStrength("sm_mp_ranged_bolter"));
                Hit(2.0f, _id, MusouOrkFlinchRules.ShotStrength("sm_mp_ranged_sniper_rifle"));
                Hit(3.0f, _id, MusouOrkFlinchRules.ShotStrength("sm_mp_exotic_auto_cannon"));
                Hit(4.0f, _id, MusouOrkFlinchRules.MeleeStrength("sm_mp_melee_power_sword", true, false));
                Hit(5.0f, _id, MusouOrkFlinchRules.MeleeStrength("sm_mp_melee_power_sword", true, true));
                if (_flinchPrevT < 6f && t >= 6f) _crowd!.Blast(_id, front, 1f, 8f, kind);
                break;
            case "drift":   // the feet-planted check: a hit of each strength, the worst ankle drift of each window printed
            {
                _shown.Add(new MusouShown(_id, V(0, 0, 0), -MathF.PI / 2f, kind, MusouEnemyState.Seek, t));
                float[] strengths = [1f, 1.5f, 2.5f, 1f, 1.5f, 2.5f];
                for (int k = 0; k < strengths.Length; k++)
                {
                    float at = 1f + 1.5f * k;
                    if (_flinchPrevT < at && t >= at) { MusouOrkFlinchPose.WorstFootDrift = 0f; _crowd!.Hit(_id, front, strengths[k], kind); _driftK = k; }
                    if (_flinchPrevT < at + 1.2f && t >= at + 1.2f) GD.Print($"FLINCHDRIFT strength {strengths[k]:0.0} (size {MusouOrkFlinchRules.Amplitude(strengths[k], kind):0.0}): worst foot drift {MusouOrkFlinchPose.WorstFootDrift * 1000f:F1} mm");
                }
                break;
            }
            case "charge":   // a melee charge (2.5x) on a running Ork, then a heavy hammer strike
            {
                var at = new Vector3(-4f + speed * MathF.Min(t, 5f) * 0.4f, 0f, 0f);
                _shown.Add(new MusouShown(_id, V(at.X, at.Y, at.Z), -MathF.PI / 2f, kind, MusouEnemyState.Seek, t));
                centre = at;
                Hit(1.2f, _id, MusouOrkFlinchRules.MeleeStrength("sm_mp_melee_power_sword", true, true));
                Hit(3.0f, _id, MusouOrkFlinchRules.MeleeStrength("sm_mp_melee_2hand_thunder_hammer", true, false));
                break;
            }
            case "blast":   // 25 Orks standing about a frag's blast (radius 8 m), the centre in front of the middle one
            {
                if (_flinchIds.Count == 0) for (int i = 0; i < 25; i++) _flinchIds.Add((ushort)(100 + i));
                var c0 = new Vector3(0f, 0f, 0f);
                for (int i = 0; i < 25; i++) _shown.Add(new MusouShown(_flinchIds[i], V((i % 5 - 2) * 1.6f, (i / 5 - 2) * 1.6f, 0f), -MathF.PI / 2f, i % 7 == 6 ? MusouEnemyType.ArdBoy : kind, MusouEnemyState.Seek, t));
                if (_flinchPrevT < 1.2f && t >= 1.2f)
                    for (int i = 0; i < 25; i++)
                    {
                        var d = new System.Numerics.Vector2((i % 5 - 2) * 1.6f + 1.2f, (i / 5 - 2) * 1.6f);
                        _crowd!.Blast(_flinchIds[i], new System.Numerics.Vector2(-d.X, -d.Y) * -1f, d.Length(), 6f, kind);
                    }
                centre = c0;
                break;
            }
            case "run":
            {
                var at = new Vector3(-6f + speed * MathF.Min(t, 5f) * 0.5f, 0f, 0f);
                _shown.Add(new MusouShown(_id, V(at.X, at.Y, at.Z), -MathF.PI / 2f, kind, MusouEnemyState.Seek, t));
                centre = at;
                for (float k = 1.5f; k < 3.0f; k += 1f / 12f) Hit(k, _id, 1f);
                break;
            }
            case "attack":
            {
                float c = t % 1.4f;
                var state = c < 0.7f ? MusouEnemyState.WindUp : c < 0.9f ? MusouEnemyState.Strike : MusouEnemyState.Recover;
                _shown.Add(new MusouShown(_id, V(0, 0, 0), -MathF.PI / 2f, kind, state, c));
                for (int k = 0; k < 4; k++) { Hit(k * 1.4f + 0.3f, _id, 1f); Hit(k * 1.4f + 0.8f, _id, 1.3f); Hit(k * 1.4f + 1.15f, _id, 1f); }
                break;
            }
            case "burst":
                _shown.Add(new MusouShown(_id, V(0, 0, 0), -MathF.PI / 2f, kind, MusouEnemyState.Seek, t));
                for (float k = 1f; k < 4f; k += 1f / 12f) Hit(k, _id, 1f);
                break;
            default:   // crowd
                int count = (int)Arg(_args, "flinch-n", 225f), perFrame = (int)Arg(_args, "flinch-hits", 6f), across = Math.Max(1, (int)MathF.Sqrt(count * 0.6f));
                if (_flinchIds.Count == 0) for (int i = 0; i < count; i++) _flinchIds.Add((ushort)(100 + i));
                float x0 = -6f + 2f * MathF.Min(t, 6f);
                for (int i = 0; i < count; i++) _shown.Add(new MusouShown(_flinchIds[i], V(x0 + (i % across) * 1.4f, (i / across - across / 2) * 1.4f, 0f), -MathF.PI / 2f, i % 7 == 6 ? MusouEnemyType.ArdBoy : kind, MusouEnemyState.Seek, t));
                if (t > 1f) for (int n = 0; n < perFrame; n++) _crowd!.Hit(_flinchIds[_flinchRandom.Next(count)], front, 1f, kind);
                centre = new Vector3(x0 + across * 0.7f, 0f, 0f);
                break;
        }
        _flinchPrevT = t;
        float height = MathF.Max(2f, LookHeight(look));
        float dist = Arg(_args, "cam-dist", _flinchMode == "crowd" ? 20f : _flinchMode == "blast" ? 9f : _flinchMode is "run" or "charge" ? 7f : height * 2.4f);
        var target = new Vector3(centre.X, 0f, _flinchMode == "crowd" ? 1f : height * 0.5f);
        var eye = target + (_args.ContainsKey("cam-front") ? new Vector3(dist, 0f, height * 0.2f) : new Vector3(0f, -dist, _flinchMode == "crowd" ? 6f : height * 0.2f));   // --cam-front: from the side the hit comes from
        _camera!.Position = GameToGodot * eye;
        _camera.LookAt(GameToGodot * target, Vector3.Up);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _crowd!.Frame(_shown, eye, dt);
        if (t > 2f) { _flinchMs += watch.Elapsed.TotalMilliseconds; _flinchMsN++; _flinchMsMax = Math.Max(_flinchMsMax, watch.Elapsed.TotalMilliseconds); }
        if (_flinchMode != "crowd" || true)
        {
            System.IO.Directory.CreateDirectory(_outDir);
            if (!_args.ContainsKey("no-shots")) GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_outDir, $"{_name}-{_flinchFrame:0000}.png"));
        }
        if (_flinchFrame % 6 == 0) GD.Print($"FLINCHTEST t {t:F2} flinching {_crowd.Flinching} kicks {_crowd.Kicks} foot drift {MusouOrkFlinchPose.LastFootDrift * 1000f:F1} mm");
        _flinchFrame++;
        if (t >= seconds) { GD.Print($"FLINCHTEST done: {_flinchFrame} frames, kicks {_crowd.Kicks}, Frame() mean {_flinchMs / Math.Max(1, _flinchMsN):F2} ms, worst {_flinchMsMax:F2} ms, shown skinned {_crowd.ShownSkinned} baked {_crowd.ShownVat}, worst foot drift {MusouOrkFlinchPose.WorstFootDrift * 1000f:F1} mm"); GetTree().Quit(0); }
    }

    static System.Numerics.Vector3 V(float x, float y, float z) => new(x, y, z);
}
