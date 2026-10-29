using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The own marine's facing, position and aim pitch in the networked view (M36; the owner's playtest 2026-10-28: "the player model must turn
/// smoothly when the camera pans left/right with the mouse"; review: the position and the camera still stepped).
/// <para>
/// Cause: the camera looks along the view yaw and pitch (<c>_yaw</c>, <c>_pitch</c>), which the mouse changes every frame; the simulation takes
/// them once per tick (30 Hz, the sampled input) and the predicted state changes only then. The own model was placed at the predicted state, so on
/// a 60 Hz or faster display it turned in steps of one tick's turn every second or third frame, lagged the camera, and (running at 6.5 m/s: 0.22 m
/// a tick) moved in steps too - and the camera, placed from the same state, stepped with it. Remote players are interpolated between snapshots
/// and are smooth as they are.
/// </para>
/// Fix (client only; the simulation and the protocol are unchanged), all in <see cref="OwnShownState"/> and <see cref="DisplayYaw"/>, with the
/// rules in <see cref="ViewSmoothing"/> (engine-free, tested):
/// <list type="bullet">
/// <item>Yaw and pitch: drawn at the view's while the predicted state's follows the last sampled input (so it is the angle the next tick takes);
/// when the simulation holds another heading (a charge's locked one) the state's is kept.</item>
/// <item>Position (model, and the camera placed from it): drawn between the last two predicted ticks by the fraction of a tick elapsed
/// (<see cref="GameClient.DrawPair"/>; feel/controls: after an early tick the pair before it), so it shows the state one tick (33 ms) late but moves
/// every frame; a reconciliation correction fades out over <see cref="ViewSmoothing.CorrectionSeconds"/> instead of snapping; a step longer than
/// <see cref="ViewSmoothing.TeleportDistance"/> (a respawn) is not crossed. The simulation's position stays the one for collision, shots, the
/// camera's wall casts and everything else.</item>
/// </list>
/// **Client presentation, ours.**
/// <para>
/// Test aids: <c>--pan-rate &lt;deg/s&gt;</c> turns the view at that rate (like holding Q); <c>--yaw-probe</c> prints, every 3 s, the per-frame yaw
/// steps of the view, of the old drawing (the predicted state's yaw) and of the shown model, and the per-frame position steps of the simulation's
/// position (the old drawing), the shown model and the camera while the marine runs; <c>--no-view-smoothing</c> draws the old position and pitch
/// (for before and after numbers).
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--pan-rate &lt;deg/s&gt;</c> (test aid, human client): turns the view at this rate every frame, as holding Q or the mouse would.</summary>
    public float PanRate { get; init; }
    /// <summary><c>--yaw-probe</c> (test aid): logs the own model's yaw and position steps per frame against the view's and the old (tick) drawing's.</summary>
    public bool YawProbe { get; init; }
    /// <summary><c>--no-view-smoothing</c> (test aid): draws the own model and camera at the predicted tick state (position and pitch) as before the review fix.</summary>
    public bool NoViewSmoothing { get; init; }

    float _sampledYaw, _sampledPitch;   // the view yaw and pitch the last input sample took (every sampler sets them first)
    readonly CorrectionOffset _correction = new();
    int _correctionSerial;
    System.Numerics.Vector3 _simPosition;   // the simulation's own position this frame: the camera's wall casts fall back to it (feel/controls; --sim-camera-cast: always)

    /// <summary>The yaw to draw the own model at: the view's while the simulation's follows the input, else the predicted state's (<see cref="ViewSmoothing.FollowAngle"/>).</summary>
    float DisplayYaw(PlayerState state) =>
        !state.Alive || OwnDead ? state.Yaw : ViewSmoothing.FollowAngle(state.Yaw, _sampledYaw, _yaw);

    /// <summary>
    /// The own player's state as drawn this frame: the position between the last two predicted ticks plus the fading correction, and the pitch
    /// at the view's while the simulation's follows the input. The yaw stays the state's (the movement poses compare it with the velocity;
    /// the model itself uses <see cref="DisplayYaw"/>). Dead, without a previous tick, or with <c>--no-view-smoothing</c>: the state itself.
    /// </summary>
    PlayerState OwnShownState(PlayerState me, double delta)
    {
        _simPosition = me.Position;
        TurnSweepView(delta);   // M40-T5 test aid (--bot-turn-sweep): the bot's view, per frame
        if (_client is null || NoViewSmoothing || !me.Alive || OwnDead) { _correction.Reset(); _correctionSerial = _client?.CorrectionSerial ?? 0; return me; }
        if (_client.CorrectionSerial != _correctionSerial) { _correctionSerial = _client.CorrectionSerial; _correction.Add(_client.LastCorrection); }
        // feel/controls: after an early tick the pair before it is drawn until its scheduled time (GameClient.DrawPair), so the position keeps its timeline.
        var (previous, current, fraction) = _client.DrawPair();
        var position = previous.Alive && previous.PlayerId == me.PlayerId && current.Alive && current.PlayerId == me.PlayerId
            ? ViewSmoothing.Interpolate(previous.Position, current.Position, fraction) : me.Position;
        position += _correction.Step((float)delta);
        return me with { Position = position, Pitch = ViewSmoothing.FollowAngle(me.Pitch, _sampledPitch, _pitch) };
    }

    // ---- --yaw-probe ----
    sealed class YawStats
    {
        public int Frames, Moving;
        public double Seconds;
        public double ViewSum, ViewMax, TickSum, TickMax, ShownSum, ShownMax;
        public int TickStalled, ShownStalled;
        public double TickLagSum, TickLagMax, ShownLagSum, ShownLagMax;
        public double TickStepSq, ShownStepSq;
    }

    /// <summary>Per-frame steps of a position, as a multiple of the ideal step (speed x frame time), while the marine runs.</summary>
    sealed class PosStats
    {
        public int Frames, Stalled;
        public double Sum, SumSq, Max;
        public void Add(double normalized) { Frames++; Sum += normalized; SumSq += normalized * normalized; Max = Math.Max(Max, normalized); if (normalized < 0.05) Stalled++; }
        public string Text()
        {
            if (Frames == 0) return "no samples";
            double mean = Sum / Frames, sd = Math.Sqrt(Math.Max(0, SumSq / Frames - mean * mean));
            return $"step/ideal mean {mean:F2} sd {sd:F2} max {Max:F2}, no movement in {100.0 * Stalled / Frames:F0}% of {Frames} frames";
        }
        public void Reset() { Frames = Stalled = 0; Sum = SumSq = Max = 0; }
    }

    readonly YawStats _yawStats = new();
    readonly PosStats _posSim = new(), _posShown = new(), _posCamera = new();
    float _probeViewYaw, _probeTickYaw, _probeShownYaw;
    System.Numerics.Vector3 _probeSimPos, _probeShownPos;
    Vector3 _probeCameraPos;
    bool _probeInit, _probePosInit;
    double _probeLogAt;

    /// <summary>Per frame after the own character is placed: records the yaw probe (and logs every 3 s).</summary>
    void YawProbeFrame(double delta, PlayerState me)
    {
        if (!YawProbe || _local is null) return;
        float view = _yaw, tick = me.Yaw;
        var b = _local.Transform.Basis;
        float shown = MathF.Atan2(b.X.Y, b.X.X);
        if (_probeInit && delta > 0)
        {
            var st = _yawStats;
            double dv = Math.Abs(ViewSmoothing.WrapPi(view - _probeViewYaw)), dt = Math.Abs(ViewSmoothing.WrapPi(tick - _probeTickYaw)), ds = Math.Abs(ViewSmoothing.WrapPi(shown - _probeShownYaw));
            st.Frames++; st.Seconds += delta;
            if (dv > 1e-5)
            {
                st.Moving++;
                st.ViewSum += dv; st.ViewMax = Math.Max(st.ViewMax, dv);
                st.TickSum += dt; st.TickMax = Math.Max(st.TickMax, dt); st.TickStepSq += (dt - dv) * (dt - dv);
                st.ShownSum += ds; st.ShownMax = Math.Max(st.ShownMax, ds); st.ShownStepSq += (ds - dv) * (ds - dv);
                if (dt < 1e-6) st.TickStalled++;
                if (ds < 1e-6) st.ShownStalled++;
            }
            double lagTick = Math.Abs(ViewSmoothing.WrapPi(view - tick)), lagShown = Math.Abs(ViewSmoothing.WrapPi(view - shown));
            st.TickLagSum += lagTick; st.TickLagMax = Math.Max(st.TickLagMax, lagTick);
            st.ShownLagSum += lagShown; st.ShownLagMax = Math.Max(st.ShownLagMax, lagShown);
        }
        _probeInit = true; _probeViewYaw = view; _probeTickYaw = tick; _probeShownYaw = shown;
        if (_connectedFor - _probeLogAt < 3.0) return;
        _probeLogAt = _connectedFor;
        var s = _yawStats;
        if (s.Moving > 5)
        {
            const double Deg = 180.0 / Math.PI;
            double n = s.Moving;
            GD.Print($"YAWPROBE {s.Frames} frames in {s.Seconds:F1} s ({s.Frames / Math.Max(s.Seconds, 1e-6):F0} fps, {s.Moving} with the view turning); " +
                     $"view step mean {s.ViewSum / n * Deg:F3} max {s.ViewMax * Deg:F3} deg; " +
                     $"tick-yaw drawing (old): step mean {s.TickSum / n * Deg:F3} max {s.TickMax * Deg:F3} deg, no change in {100.0 * s.TickStalled / n:F0}% of the frames, step error rms {Math.Sqrt(s.TickStepSq / n) * Deg:F3} deg, behind the view mean {s.TickLagSum / Math.Max(s.Frames, 1) * Deg:F3} max {s.TickLagMax * Deg:F3} deg; " +
                     $"shown (new): step mean {s.ShownSum / n * Deg:F3} max {s.ShownMax * Deg:F3} deg, no change in {100.0 * s.ShownStalled / n:F0}%, step error rms {Math.Sqrt(s.ShownStepSq / n) * Deg:F3} deg, behind the view mean {s.ShownLagSum / Math.Max(s.Frames, 1) * Deg:F3} max {s.ShownLagMax * Deg:F3} deg");
        }
        else GD.Print($"YAWPROBE view not turning ({s.Frames} frames): use --pan-rate <deg/s>");
        if (_posSim.Frames > 5)
            GD.Print($"POSPROBE{(NoViewSmoothing ? " (--no-view-smoothing)" : "")} while moving above {PosProbeSpeed:F0} m/s: simulation position (old drawing) {_posSim.Text()}; shown model {_posShown.Text()}; camera {_posCamera.Text()}");
        else GD.Print("POSPROBE the own marine is not moving fast enough (needs a bot with --bot-gait, or a player running)");
        _yawStats.Frames = _yawStats.Moving = 0; _yawStats.Seconds = 0;
        _yawStats.ViewSum = _yawStats.ViewMax = _yawStats.TickSum = _yawStats.TickMax = _yawStats.ShownSum = _yawStats.ShownMax = 0;
        _yawStats.TickStalled = _yawStats.ShownStalled = 0;
        _yawStats.TickLagSum = _yawStats.TickLagMax = _yawStats.ShownLagSum = _yawStats.ShownLagMax = _yawStats.TickStepSq = _yawStats.ShownStepSq = 0;
        _posSim.Reset(); _posShown.Reset(); _posCamera.Reset();
    }

    /// <summary>The position probe only counts frames while the simulation's speed is above this (m/s).</summary>
    const float PosProbeSpeed = 3f;

    /// <summary>After the camera is placed: records the per-frame position steps of the simulation, the shown model and the camera.</summary>
    void PosProbeFrame(double delta, PlayerState me, PlayerState shown)
    {
        TurnAimProbeFrame(me, shown);   // M40-T5 (--anim-probe): the barrel against the aim while standing
        if (!YawProbe || _camera is null) return;
        var camera = _camera.GlobalPosition;
        if (_probePosInit && delta > 0)
        {
            double speed = new System.Numerics.Vector2(me.Velocity.X, me.Velocity.Y).Length();
            if (speed > PosProbeSpeed && me.Alive)
            {
                double ideal = speed * delta;
                _posSim.Add(System.Numerics.Vector3.Distance(me.Position, _probeSimPos) / ideal);
                _posShown.Add(System.Numerics.Vector3.Distance(shown.Position, _probeShownPos) / ideal);
                _posCamera.Add(camera.DistanceTo(_probeCameraPos) / ideal);
            }
        }
        _probePosInit = true; _probeSimPos = me.Position; _probeShownPos = shown.Position; _probeCameraPos = camera;
    }

    /// <summary><c>--pan-rate</c>: turns the view this frame (a human client; with the key and mouse turns).</summary>
    void PanView(double delta)
    {
        if (PanRate == 0f || _bot || OwnDead) return;
        _yaw += PanRate * MathF.PI / 180f * (float)delta;
        _yaw %= 2 * MathF.PI;
    }

    // ---- M40-T5: the barrel against the aim while the own marine stands (--anim-probe) ----
    sealed class AimErrorStat { public readonly List<float> Total = new(), Yaw = new(), Pitch = new(); }
    readonly Dictionary<string, AimErrorStat> _turnAim = new();
    double _turnAimLogAt;

    /// <summary>
    /// With <c>--anim-probe</c>: per frame while the own marine stands (speed under 0.5 m/s), the angle between its barrel (<see cref="MuzzleWorld"/>) and the aim
    /// (the view's yaw and the drawn pitch), and its yaw and pitch parts away from the aim, by the angle between the aim and the body (0-30, 30-60, 60-90, over 90
    /// degrees) and the posture. Printed (<c>TURNAIM</c>) every 30 s and at exit.
    /// </summary>
    void TurnAimProbeFrame(PlayerState me, PlayerState shown)
    {
        if (!TurnProbe.Enabled || _client is null || _local is null || _gameSpace is null || !me.Alive || OwnDead) return;
        float speed = MathF.Sqrt(me.Velocity.X * me.Velocity.X + me.Velocity.Y * me.Velocity.Y);
        if (_connectedFor > 10.0 && _connectedFor - _turnAimLogAt >= 30.0 && _turnAim.Count > 0) { _turnAimLogAt = _connectedFor; PrintTurnAim("TURNAIM"); }
        if (speed > RunThreshold || me.SwitchLeft > 0f || me.MeleeStep > 0 || me.Throwing || MuzzleWorld(_client.PlayerId) is not { } m) return;
        var t = LocoOf(_client.PlayerId);
        float aimYaw = t.AimYaw;
        float twist = MathF.Abs(ViewSmoothing.WrapPi(aimYaw - t.Body.Yaw)) * 180f / MathF.PI;
        string bucket = twist < 30f ? "0-30" : twist < 60f ? "30-60" : twist < 90f ? "60-90" : ">90";
        string posture = (t.Stand is not null ? "turn rules " : "old idle ") + (me.Aiming ? "aim mode" : HipReady(me) ? "ready" : "relaxed");
        var aim = Combat.AimDirection(aimYaw, shown.Pitch);
        var aimWorld = (_gameSpace.Basis * new Vector3(aim.X, aim.Y, aim.Z)).Normalized();
        float total = Mathf.RadToDeg(m.Direction.AngleTo(aimWorld));
        // The barrel in the frame of the aim's heading (game space: +Y along the aim, +Z up).
        var local = new Basis(new Vector3(0, 0, 1), aimYaw).Inverse() * (_gameSpace.Basis.Inverse() * m.Direction);
        float yawErr = Mathf.RadToDeg(MathF.Atan2(local.X, local.Y));
        float pitchErr = Mathf.RadToDeg(MathF.Asin(Math.Clamp(local.Z / MathF.Max(local.Length(), 1e-6f), -1f, 1f))) - Mathf.RadToDeg(shown.Pitch);
        string key = $"{posture}, aim {bucket} deg off the body";
        if (!_turnAim.TryGetValue(key, out var stat)) _turnAim[key] = stat = new AimErrorStat();
        stat.Total.Add(total); stat.Yaw.Add(yawErr); stat.Pitch.Add(pitchErr);
    }

    void PrintTurnAim(string tag)
    {
        static float Median(List<float> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
        static float P90(List<float> v) { var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)(s.Count * 0.9))]; }
        foreach (var (key, st) in _turnAim.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            GD.Print($"{tag} barrel against the aim, {key}: {st.Total.Count} frames, off the aim median {Median(st.Total):F1} p90 {P90(st.Total):F1} max {st.Total.Max():F1} deg; yaw error median {Median(st.Yaw):+0.0;-0.0} (+ right), pitch error median {Median(st.Pitch):+0.0;-0.0} (+ up)");
    }

    /// <summary>At exit with <c>--anim-probe</c>: the last <c>TURNAIM</c> lines.</summary>
    void TurnAimProbeFinal() { if (TurnProbe.Enabled && _turnAim.Count > 0) PrintTurnAim("TURNAIM final"); }
}
