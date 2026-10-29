using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The run start (M40-T5; the M40 audit's D9). A rifle marine that was standing and runs off forward plays the graph's start clip, <c>SM_TRN_BO_IdleAim_to_RunF</c> (0.7 s),
/// as the graph does when the requested speed is 3 or more ("Idle Relaxed To Run Relaxed", "Idle Ready To Run Ready"; the pistol and the heavy bolter blend straight into the run).
/// The clip's extracted root motion carries the body 4.43 m, and the simulation brings the body to the run in 0.16 s and 4.15 m in the same 0.7 s, so the clip is driven by the
/// distance the body has travelled, not by time: the clip's time is where its root has gone as far as the body (<see cref="StartClip.TimeAtDistance"/>), which keeps its planted feet
/// still on the ground. At the end of the clip (the <c>BRANCH_Idle_To_Run_End</c> window) the run blend takes over at the foot the clip ended on (<see cref="StartMatch"/>, the
/// nearest of the run cycle's poses by the toes). The clip is whole-body: the aim pitch is not followed for its 0.5 s. A walk (the speed never reaches 3 within
/// <see cref="RunStartRules.ArmSeconds"/>), a run that is not forward within <see cref="RunStartRules.ForwardCone"/> degrees, a sprint, an action or aim mode do not play it. <b>Off by default</b>
/// (review M1): <c>--run-start</c> turns it on, <c>--no-run-start</c> (test aid) wins over it. The decisions are <see cref="RunStartRules"/> (engine-free, tested).
/// The clip has no footstep tags; the footsteps of the run take over with the blend. Which speed starts it, the heading cone and the hand-over match are interpretation.
/// </summary>
public partial class NetworkView
{
    /// <summary>
    /// <c>--run-start</c> opts in (review M1, 2026-10-28: the clip is off by default; measured, it cut the relaxed start slide from 50 to 26 cm but left the ready start about as it was and added
    /// pose pops at its entry and hand-over). <c>--no-run-start</c> (test aid) stays and wins: the run blend alone.
    /// </summary>
    static readonly bool RunStartOn = OS.GetCmdlineUserArgs().Contains("--run-start") && !OS.GetCmdlineUserArgs().Contains("--no-run-start");

    /// <summary>One marine's run start.</summary>
    sealed class RunStartState
    {
        public bool Armed, Active;
        public double ArmedAt, At = double.NegativeInfinity;
        public float Travel;            // metres the body has gone forward since it left standing
        public float Speed;             // the speed when the marine left standing, for the acceleration test
        public float LastX, LastY;      // the drawn position of the last frame
        public bool HavePos;
        public bool Ready;              // the posture the start was armed in
        public void Reset() { Armed = Active = false; Travel = 0f; HavePos = false; }
    }

    readonly Dictionary<bool, float?> _startPhases = new();   // by ready: the run cycle's phase the start ends on (null: not computed or not possible)
    readonly HashSet<bool> _startPhaseTried = new();
    readonly Dictionary<bool, float> _startCycle = new();   // by ready: the length (s) of the run cycle the start hands over to

    /// <summary>
    /// Poses the run start when it applies: true when the start clip shows (the caller skips the locomotion), false otherwise. Called by <c>PlaceLocomotion</c> after aim mode and
    /// before the ready and relaxed poses, with the shown body's yaw in <paramref name="s"/>.
    /// </summary>
    static readonly bool RunStartLog = OS.GetCmdlineUserArgs().Contains("--run-start-log");

    void RunStartNote(PlayerState s, RunStartState rs, string what)
    {
        if (RunStartLog && s.PlayerId == _client?.PlayerId) GD.Print($"RUNSTART t {_connectedFor:F3} {what} (speed {MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y):F2}, travel {rs.Travel:F2})");
    }

    bool RunStartPlace(CharacterViewer character, PlayerState s, HipTrack hip, bool own)
    {
        var t = LocoOf(s.PlayerId);
        var rs = t.Start;
        if (!RunStartOn && !rs.Armed && !rs.Active) return false;   // off by default: nothing is armed, nothing to drop
        if (!RunStartOn || NoTurns || TurnData() is not { } data || HipFamilyOf(s) is not { } family || data.For(family) is not { } set || s.Aiming || TurnInAction(s, null)) { DropStart(character, rs); return false; }
        bool ready = HipReady(s);
        var clip = ready ? set.ReadyStart : set.RelaxedStart;
        if (clip is null || !RigHasClip(clip.Clip)) { DropStart(character, rs); return false; }
        double now = _connectedFor;
        float dt = double.IsFinite(rs.At) ? (float)Math.Clamp(now - rs.At, 0.0, 0.25) : 0f;
        rs.At = now;
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        float fx = -MathF.Sin(s.Yaw), fy = MathF.Cos(s.Yaw);
        float forward = s.Velocity.X * fx + s.Velocity.Y * fy;
        // The distance the body has gone along its facing is measured on the drawn position (smooth at the frame rate), not by integrating the simulation's velocity, which steps with the ticks.
        float moved = rs.HavePos && dt > 0f ? (s.Position.X - rs.LastX) * fx + (s.Position.Y - rs.LastY) * fy : 0f;
        rs.LastX = s.Position.X; rs.LastY = s.Position.Y; rs.HavePos = dt > 0f || rs.HavePos;
        if (MathF.Abs(moved) > 2f) moved = 0f;   // a respawn or a correction, not a step
        float heading = speed > 0.1f ? MathF.Acos(Math.Clamp(forward / speed, -1f, 1f)) * 180f / MathF.PI : 180f;

        if (!rs.Active)
        {
            // Armed when the marine leaves standing (BodyYawFor saw it stand last frame and not now); the travel counts from there.
            if (!rs.Armed)
            {
                if (!RunStartRules.ShouldArm(t.PrevStanding, t.WasStanding, speed, RunThreshold)) return false;
                rs.Armed = true; rs.ArmedAt = now; rs.Travel = 0f; rs.Ready = ready; rs.Speed = speed; RunStartNote(s, rs, "armed"); character.BeginSettle(StartHoldSeconds, 0.2f);   // the idle feet stay put on the ground while the body sets off
            }
            rs.Travel += MathF.Max(0f, moved);
            // The simulation's speed steps with its 30 Hz ticks, so the acceleration is the average since the marine left standing.
            double elapsed = now - rs.ArmedAt;
            float acceleration = elapsed > 1e-3 ? (float)((speed - rs.Speed) / elapsed) : float.MaxValue;
            float sprintAbove = _config is { } c ? c.RunSpeed + 0.25f * (c.SprintSpeed - c.RunSpeed) : float.MaxValue;
            // The decision is RunStartRules.Armed (tested): a stop, a long wait, a posture change, a sprint or a turned heading reset it; a run still accelerating
            // (the simulation accelerates at 40 m/s squared) waits for the clip's speed, a walk that has levelled off below it is dropped.
            switch (RunStartRules.Armed(speed, elapsed, acceleration, ready != rs.Ready, sprintAbove, heading, clip.EnterSpeed, RunThreshold))
            {
                case RunStartVerdict.Reset:
                    RunStartNote(s, rs, $"reset (heading {heading:F0}, acceleration {acceleration:F1})"); DropStart(character, rs); return false;
                case RunStartVerdict.Hold:
                    // Until the clip starts the marine keeps its idle (a walk blend for the 0.1 s it takes would slide the planted foot).
                    return HoldIdleForStart(character, s, hip, ready);
            }
            rs.Active = true; RunStartNote(s, rs, "clip starts"); character.CancelSettle();   // the clip's feet are where the idle's were
        }
        else
        {
            rs.Travel += MathF.Max(0f, moved);
            // Over: the body stopped or turned away, the sprint began, or the clip reached its end window / the end of its root motion.
            float sprintAbove = _config is { } c ? c.RunSpeed + 0.25f * (c.SprintSpeed - c.RunSpeed) : float.MaxValue;
            if (RunStartRules.Aborted(speed, heading, sprintAbove, ready != rs.Ready, RunThreshold)) { RunStartNote(s, rs, "clip aborted"); DropStart(character, rs); return false; }
        }

        float time = clip.TimeAtDistance(rs.Travel);
        if (time >= clip.Length * clip.EndFraction - 1e-3f || rs.Travel >= clip.RootForward[^1])
        {
            // Hand over to the run blend at the foot the clip ends on.
            // The blend layer advances its phase by one frame before it is first drawn, so the seed is one frame behind the clip's last pose.
            if (StartPhaseFor(clip, ready) is { } phase) character.SeedBlendPhase(GaitPhase.Wrap(phase - dt * (_startCycle.GetValueOrDefault(ready, 0.867f) > 0f ? 1f / _startCycle.GetValueOrDefault(ready, 0.867f) : 1.15f), 1f));
            RunStartNote(s, rs, "clip done, run takes over");
            DropStart(character, rs);
            return false;
        }
        if (!character.Pose(clip.Clip, time, MathF.Min(clip.BlendIn, MaxStartEntryBlend), lockFeet: true)) { DropStart(character, rs); return false; }
        hip.Kind = ready ? PoseKind.Hip : PoseKind.Normal;
        if (own && _showInfo) _locoNowText = $"run start {clip.Clip} at {time:F2} s (body travelled {rs.Travel:F2} m of {clip.RootForward[^1]:F2})";
        return true;
    }

    /// <summary>
    /// Ends a start for any reason: the pins armed for it are dropped too (review S1: before, the guard at the top of <see cref="RunStartPlace"/> and the failed pose reset the state and
    /// left the pins holding the idle feet while the marine ran). Pins that belong to a stop (not armed here) are left alone.
    /// </summary>
    static void DropStart(CharacterViewer character, RunStartState rs)
    {
        if (rs.Armed && !rs.Active) character.CancelSettle();
        rs.Reset();
    }

    /// <summary>The pins that hold the idle feet while a start is armed never time out before the start is decided (seconds; the arm window is 0.3 s).</summary>
    const float StartHoldSeconds = 1f;

    /// <summary>The idle pose while a start is armed and the clip has not begun (the body is still, the marine is on its way): the aim-offset idle of the posture, else not handled (false: the locomotion shows its blend).</summary>
    bool HoldIdleForStart(CharacterViewer character, PlayerState s, HipTrack hip, bool ready)
    {
        if (TurnData() is not { } data || TurnPostureOf(s, data) is not { } posture || TurnRigOf(posture) is not { } rig) return false;
        return IdlePlace(character, s, hip, ready, ready ? posture.StopBlend : HipRelaxFade(s, hip), posture, rig);
    }

    /// <summary>The cross-fade into the start clip is at most this long, seconds (the graph's "Idle To Nav" is 0.333 s, which would hide the clip for most of its 0.5 s; ours).</summary>
    const float MaxStartEntryBlend = 0.15f;

    /// <summary>The shared gait phase of the run cycle that matches the start clip's last pose (cached per posture; null when it cannot be computed).</summary>
    float? StartPhaseFor(StartClip clip, bool ready)
    {
        if (_startPhases.TryGetValue(ready, out var cached)) return cached;
        if (!_startPhaseTried.Add(ready) || _assets is null) return null;
        float? phase = null;
        try
        {
            // The forward run clip of the posture's run blend: the relaxed set's 0 degree run clip, the ready set's level-aim run clip.
            string? runClip = null;
            if (!ready && _locoAnims?.RelaxedFor(HipFireWeaponClass.Rifle)?.Directions.FirstOrDefault(d => Math.Abs(d.AngleDegrees) < 1f)?.Run is { } run) runClip = run.Clip;
            else if (ready && _hipAnims?.For(HipFireWeaponClass.Rifle)?.Directions.FirstOrDefault(d => Math.Abs(d.AngleDegrees) < 1f)?.Run?.Clips.MinBy(c => MathF.Abs(c.PitchDegrees)) is { } level) runClip = level.Clip;
            if (runClip is not null && GaitOf(runClip) is { } gait && _assets.Animation(clip.Clip) is { } startAnim && _assets.Animation(runClip) is { } runAnim)
                { phase = StartMatch.PhaseAfter(_assets.Model.Rig.Skeleton, startAnim, clip.Length * clip.EndFraction, runAnim, gait); _startCycle[ready] = gait.Duration; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { GD.PrintErr("run start phase: " + ex.Message); }
        _startPhases[ready] = phase;
        if (phase is { } p) GD.Print($"run start: the {(ready ? "ready" : "relaxed")} start clip hands over at gait phase {p:F2}");
        return phase;
    }
}
