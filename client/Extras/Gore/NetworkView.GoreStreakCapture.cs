using System;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using SmClient.ImpactRules;
using SmSim;
using Num = System.Numerics;
using static SmClient.ImpactRules.ClassicHitDirRules;

namespace SmClient;

/// <summary>
/// gore-streak capture aid (dev only; a *placeholder demonstration* of the drawing, not of the confirmation path): <c>--gore-streak-capture &lt;case&gt;</c> in a Classic Extended match
/// draws one streak of the case on the other player nearest the camera (use one <c>--bot-idle</c> bot and <c>--observe-side 4.5</c>), through the same path a confirmed hit takes
/// (<see cref="ClassicStreak"/>, or the explosion's radial streak), then holds it at each key time of the table (<see cref="GoreStreakView.AgeOverride"/>) and saves the view as
/// <c>0.png .. 10.png</c> plus <c>capture.json</c> (wound, body height and exit axis on screen, case, SHA; the scoring tools' contract) into <c>--gore-streak-frames &lt;dir&gt;</c>
/// (default %LOCALAPPDATA%\sm-reimpl\tmp\gore-streak\&lt;case&gt;), and quits. Cases: <c>sweep-lr</c>, <c>sweep-rl</c>, <c>overhead</c>, <c>thrust</c>, <c>bolt</c>,
/// <c>lascannon</c>, <c>explosion</c>, <c>kill</c> (a lethal left-to-right chainsword sweep). <c>--capture-backdrop grey</c> puts an unshaded grey wall 6 m behind the victim.
/// <c>--gore-streak-sha &lt;sha&gt;</c> is written into capture.json. Needs a rendering (windowed) client: <c>godot.cmd --windowed</c>.
/// </summary>
public partial class NetworkView
{
    static readonly string? StreakCaptureCase = CmdFlag("gore-streak-capture") ? (CmdArg("gore-streak-capture") ?? "sweep-lr") : null;
    GoreStreakTicker? _streakCaptureTicker;
    int _streakCapturePhase = -1, _streakCaptureFrame, _streakCaptureWait;
    double _streakCaptureAt;
    int _streakCaptureVictim;
    Vector3 _lastStreakOrigin, _lastStreakAxis;
    float _lastStreakUnit;
    readonly StringBuilder _streakCaptureFrames = new();

    /// <summary>Called from the Classic prewarm (NetworkView.ClassicBlood.cs): starts the capture once when asked.</summary>
    void StreakCaptureStart()
    {
        if (StreakStressRate > 0f && _streakCaptureTicker is null)
        {
            _streakCaptureTicker = new GoreStreakTicker { Name = "GoreStreakStress", Tick = StreakStressTick };
            AddChild(_streakCaptureTicker);
            _streakStressStart = _connectedFor;
            GD.Print($"gore-streak-stress: {StreakStressRate:F0} streaks a second for {StreakStressSeconds:F0} s after a 3 s settle");
            return;
        }
        if (StreakCaptureCase is null || _streakCaptureTicker is not null) return;
        _streakCaptureTicker = new GoreStreakTicker { Name = "GoreStreakCapture", Tick = StreakCaptureTick };
        AddChild(_streakCaptureTicker);
        _streakCapturePhase = 0;
        _streakCaptureAt = _connectedFor;
        GD.Print($"gore-streak-capture: case {StreakCaptureCase}, frames to {StreakCaptureDir()}");
    }

    static string StreakCaptureDir() => CmdArg("gore-streak-frames") is { Length: > 0 } d ? d
        : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "tmp", "gore-streak", StreakCaptureCase ?? "case");

    void StreakCaptureTick()
    {
        if (_client is null || GetViewport().GetCamera3D() is not { } camera) return;
        switch (_streakCapturePhase)
        {
            case 0:   // wait for a victim, the fixed side camera and two seconds to settle
                if (ObserveSide > 0f && _sideCamera is null) return;
                if (_connectedFor - _streakCaptureAt < 2.0) return;
                int first = 0; float best = float.MaxValue;
                foreach (var (pid, c) in _remotes)
                {
                    if (!GodotObject.IsInstanceValid(c) || !c.IsInsideTree() || !c.Visible) continue;
                    float d = c.GlobalPosition.DistanceSquaredTo(camera.GlobalPosition);
                    if (d < best) { best = d; first = pid; }
                }
                if (first == 0) return;
                _streakCaptureVictim = first;
                if (CmdArg("capture-backdrop") == "grey") StreakBackdrop(camera, first);
                _streakCapturePhase = 1; _streakCaptureAt = _connectedFor;
                return;
            case 1:   // the backdrop drawn for half a second, then the streak
                if (_connectedFor - _streakCaptureAt < 0.5) return;
                if (!StreakCaseDraw(camera)) { GD.Print("gore-streak-capture: the case drew nothing (is this Classic Extended?)"); GetTree().Quit(1); return; }
                Directory.CreateDirectory(StreakCaptureDir());
                _streakCapturePhase = 2; _streakCaptureFrame = 0; _streakCaptureWait = 0;
                GoreStreaks().AgeOverride = GoreStreakRules.Keys[0].T + 0.0005f;
                return;
            case 2:   // each key time: hold it for four frames (the shader and any temporal filter settle), save, next
                if (++_streakCaptureWait < 5) return;
                var img = GetViewport().GetTexture().GetImage();
                string path = Path.Combine(StreakCaptureDir(), $"{_streakCaptureFrame}.png");
                img.SavePng(path);
                AppendStreakFrame(camera);
                _streakCaptureFrame++; _streakCaptureWait = 0;
                if (_streakCaptureFrame < GoreStreakRules.Keys.Length) { GoreStreaks().AgeOverride = GoreStreakRules.Keys[_streakCaptureFrame].T + 0.0005f; return; }
                WriteStreakCaptureJson(camera, img.GetWidth(), img.GetHeight());
                GoreStreaks().AgeOverride = null;
                GD.Print($"gore-streak-capture: {GoreStreakRules.Keys.Length} frames and capture.json written to {StreakCaptureDir()}");
                _streakCapturePhase = 3;
                GetTree().Quit();
                return;
        }
    }

    /// <summary>
    /// Perf aid <c>--gore-streak-stress &lt;per second&gt;</c> (Classic Extended): after a 3 s settle, starts that many streaks a second (seeded sources: sweeps, shots, explosions, kills) in a
    /// 6 m ring 8 m in front of the camera for <c>--gore-streak-stress-seconds</c> (default 20), then prints a <c>GORESTRESS</c> line (frame ms mean, p95, p99, worst; live streaks; the
    /// view's CPU) and quits. Bounded by its rate and time.
    /// </summary>
    static readonly float StreakStressRate = float.TryParse(CmdArg("gore-streak-stress"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sr) ? Math.Clamp(sr, 0f, 400f) : 0f;
    static readonly float StreakStressSeconds = float.TryParse(CmdArg("gore-streak-stress-seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ss) ? Math.Clamp(ss, 2f, 120f) : 20f;
    double _streakStressStart, _streakStressOwed;
    readonly System.Collections.Generic.List<float> _streakStressMs = new();
    int _streakStressLivePeak;

    void StreakStressTick()
    {
        if (GetViewport().GetCamera3D() is not { } camera) return;
        double t = _connectedFor - _streakStressStart;
        if (t < 3.0) return;
        float dt = (float)GetProcessDeltaTime();
        if (t >= 3.0 + StreakStressSeconds)
        {
            _streakStressMs.Sort();
            int n = _streakStressMs.Count;
            float mean = 0f; foreach (var m in _streakStressMs) mean += m; mean /= Math.Max(1, n);
            float P(float q) => n == 0 ? 0f : _streakStressMs[Math.Clamp((int)(q * (n - 1)), 0, n - 1)];
            var v = GoreStreaks();
            GD.Print($"GORESTRESS {StreakStressRate:F0}/s for {StreakStressSeconds:F0} s ({RenderingServer.GetCurrentRenderingMethod()}): frames {n}, frame ms mean {mean:F2} p95 {P(0.95f):F2} p99 {P(0.99f):F2} worst {P(1f):F2}; live peak {_streakStressLivePeak}; {v.Counters()}; view cpu {v.TakeCpuMs():F2} ms");
            GetTree().Quit();
            _streakStressStart = double.PositiveInfinity;
            return;
        }
        _streakStressMs.Add(dt * 1000f);
        _streakStressOwed += StreakStressRate * dt;
        var fwd = -camera.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
        var centre = camera.GlobalPosition + fwd * 8f;
        while (_streakStressOwed >= 1.0)
        {
            _streakStressOwed -= 1.0;
            int seed = NextStreakSeed();
            float a = GoreStreakRules.Hash01(seed, 70) * MathF.Tau, r = 6f * MathF.Sqrt(GoreStreakRules.Hash01(seed, 71));
            var at = centre + new Vector3(MathF.Cos(a) * r, 0.2f + GoreStreakRules.Hash01(seed, 72), MathF.Sin(a) * r);
            var src = (GoreStreakRules.Source)(seed % 4 == 3 ? 3 : seed % 3);
            var axis = new Vector3(MathF.Cos(a * 3f), 0.1f, MathF.Sin(a * 3f)).Normalized();
            bool lethal = seed % 5 == 0;
            SpawnStreak(at, axis, StreakPlane(src, axis, seed), GoreStreakRules.ClassicBodyHeight, GoreStreakRules.Scale(src == GoreStreakRules.Source.Explosion ? GoreStreakRules.Weight.Heavy : GoreStreakRules.Weight.Normal, lethal), 0f, 0f, lethal, false, seed);
        }
        _streakStressLivePeak = Math.Max(_streakStressLivePeak, GoreStreaks().Live);
    }

    /// <summary>Draws the case's streak on the victim; false when nothing was drawn.</summary>
    bool StreakCaseDraw(Camera3D camera)
    {
        PlayerState? v = null;
        foreach (var p in _lastOthers) if (p.PlayerId == _streakCaptureVictim) { v = p; break; }
        if (v is not { } victim) return false;
        var chest = ToGodot(victim.Position + new Num.Vector3(0f, 0f, 1.25f));
        var toCam = camera.GlobalPosition - chest; toCam.Y = 0f;
        toCam = toCam.LengthSquared() > 1e-4f ? toCam.Normalized() : Vector3.Forward;
        var right = Vector3.Up.Cross(toCam).Normalized();   // the screen's right as seen from the camera
        string kind = StreakCaptureCase ?? "sweep-lr";
        int me = _client!.PlayerId;
        if (kind == "explosion")
        {
            // a frag going off at his feet, a metre to the screen's left and a little toward the camera
            var blast = ToGodot(victim.Position) - right * 1.0f + toCam * 0.4f;
            ExtrasClassicBlast(blast, "");
            bool drawn = false;
            ExtrasClassicUnpaired(victim, false, true, false, ref drawn);
            return drawn;
        }
        ClassicContact c;
        switch (kind)
        {
            case "bolt":
            case "lascannon":
            {
                var dir = (right - toCam * 0.25f).Normalized();   // across the view, a little away from the camera
                c = new ClassicContact(victim.PlayerId, me, chest + dir * BodySurface, dir, HitVisual.Blood, true, false, false, false, 0.3f, 1f, 0.0, null, 0, null,
                    Wargear: kind == "bolt" ? "sm_mp_ranged_bolter" : "sm_mp_ranged_lascannon");
                break;
            }
            case "thrust":
            {
                var dir = (right * 0.6f - toCam * 0.8f).Normalized();   // a thrust driven through him, out the far side and to the screen's right
                c = new ClassicContact(victim.PlayerId, me, chest + dir * BodySurface, dir, HitVisual.Blood, false, false, false, false, 0.2f, 1f, 0.0, null, 0, null, Exit: true, Wargear: "sm_mp_melee_chainsword");
                break;
            }
            default:
            {
                // a sweep through the chest: the weapon's recorded path, an arc across the screen bowing toward the camera (the swing plane tilts down toward the exit side)
                float sign = kind == "sweep-rl" ? -1f : 1f;
                bool overhead = kind == "overhead";
                var along = overhead ? (Vector3.Down * 0.9f + right * 0.3f).Normalized() : (right * sign + Vector3.Down * 0.3f).Normalized();
                var hist = new (double T, Vector3 P)[9];
                for (int i = 0; i < hist.Length; i++)
                {
                    double t = (i - 4) * 0.04;
                    float ft = (float)t;
                    hist[i] = (t, chest + along * (ft * 7f) - toCam * (ft * ft * 25f));
                }
                bool lethal = kind == "kill";
                c = new ClassicContact(victim.PlayerId, me, chest, along, HitVisual.Blood, false, false, lethal, false, 0.6f, 1f, 0.0, hist, hist.Length, null, Wargear: "sm_mp_melee_chainsword");
                break;
            }
        }
        return ClassicStreak(c);
    }

    /// <summary>An unshaded mid-grey wall 6 m behind the victim, facing the camera (the scoring tools' backdrop).</summary>
    void StreakBackdrop(Camera3D camera, int victim)
    {
        if (!_remotes.TryGetValue(victim, out var model)) return;
        var at = model.GlobalPosition + Vector3.Up * 1.2f;
        var away = at - camera.GlobalPosition; away.Y = 0f;
        away = away.LengthSquared() > 1e-4f ? away.Normalized() : Vector3.Forward;
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.5f, 0.5f, 0.5f), CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var wall = new MeshInstance3D { Name = "GoreStreakBackdrop", Mesh = new QuadMesh { Size = new Vector2(40f, 24f) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(wall);
        wall.GlobalTransform = new Transform3D(Basis.LookingAt(away, Vector3.Up), at + away * 6f);
        // a floor of the same grey under him too, so the spray never shows against the level
        var floor = new MeshInstance3D { Name = "GoreStreakBackdropFloor", Mesh = new QuadMesh { Size = new Vector2(40f, 40f) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(floor);
        floor.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(-Mathf.Pi / 2f, 0f, 0f)), model.GlobalPosition + Vector3.Up * 0.02f);
    }

    (float X, float Y) StreakPx(Camera3D camera, Vector3 world) { var p = camera.UnprojectPosition(world); return (p.X, p.Y); }

    (float[] Wound, float H, float Axis) StreakScreen(Camera3D camera)
    {
        var w = StreakPx(camera, _lastStreakOrigin);
        var tip = StreakPx(camera, _lastStreakOrigin + _lastStreakAxis * 0.5f);
        float axis = MathF.Atan2(-(tip.Y - w.Y), tip.X - w.X) * 180f / MathF.PI;
        float h = 0f;
        if (_remotes.TryGetValue(_streakCaptureVictim, out var model))
        {
            var feet = StreakPx(camera, model.GlobalPosition);
            var head = StreakPx(camera, model.GlobalPosition + Vector3.Up * GoreStreakRules.ClassicBodyHeight);
            h = MathF.Sqrt((feet.X - head.X) * (feet.X - head.X) + (feet.Y - head.Y) * (feet.Y - head.Y));
        }
        return (new[] { w.X, w.Y }, h, axis);
    }

    static string J(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    void AppendStreakFrame(Camera3D camera)
    {
        var (w, h, a) = StreakScreen(camera);
        if (_streakCaptureFrames.Length > 0) _streakCaptureFrames.Append(",\n    ");
        _streakCaptureFrames.Append($"{{\"wound_px\": [{J(w[0])}, {J(w[1])}], \"h_px\": {J(h)}, \"axis_deg\": {J(a)}, \"t\": {J(GoreStreakRules.Keys[_streakCaptureFrame].T)}}}");
    }

    void WriteStreakCaptureJson(Camera3D camera, int width, int height)
    {
        var (w, h, a) = StreakScreen(camera);
        string renderer = RenderingServer.GetCurrentRenderingMethod();
        string sha = CmdArg("gore-streak-sha") ?? "unknown";
        string json = $$"""
{
  "wound_px": [{{J(w[0])}}, {{J(w[1])}}],
  "h_px": {{J(h)}},
  "axis_deg": {{J(a)}},
  "case": "{{StreakCaptureCase}}",
  "sha": "{{sha}}",
  "renderer": "{{renderer}}",
  "size": [{{width}}, {{height}}],
  "streak_unit_m": {{J(_lastStreakUnit)}},
  "frames": [
    {{_streakCaptureFrames}}
  ]
}
""";
        File.WriteAllText(Path.Combine(StreakCaptureDir(), "capture.json"), json);
    }
}
