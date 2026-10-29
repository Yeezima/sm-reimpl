using System.Numerics;

namespace SmContent;

/// <summary>Why a sprint stop ended (<see cref="SprintStopTracker"/>).</summary>
public enum StopEnd
{
    /// <summary>Still playing (or none).</summary>
    None,
    /// <summary>The clip reached its end (its branch or the end of its root motion): the navigation takes over with the stop's exit blend.</summary>
    Done,
    /// <summary>The marine left the ground, aimed, acted or died, or the weapon came up (a shot): the stop gives way.</summary>
    Interrupted,
    /// <summary>The stop was not shown for a while (an action clip took over): it does not resume later.</summary>
    Stale,
    /// <summary>The simulation's stop ended with the body still moving (a movement key, Jump, Fire or Aim cancels it), or the body sped up again.</summary>
    Cancelled,
}

/// <summary>One frame of what the sprint stop sees (<see cref="SprintStopTracker.Update"/>).</summary>
/// <param name="Eligible">On the ground, alive, not aiming, not in an action or a fall.</param>
/// <param name="Ready">The weapon is up (a shot, a charge, the raise): the stop gives way to the ready posture.</param>
/// <param name="SimStopping">The simulation's own sprint stop flag (<c>PlayerState.SprintStop</c>).</param>
/// <param name="Speed">The body's ground speed (m/s).</param>
/// <param name="Forward">Its part along the facing (m/s).</param>
/// <param name="Decel">How fast it is slowing (m/s², over the last ~0.1 s).</param>
/// <param name="Frame">The rendered frame's number (qc/reference-pass, review S-2): a stop not placed on the frame before this one was hidden (an action clip took the body). -1: no frame
/// numbers (only the time backstop <see cref="SprintStopTracker.StaleBackstopSeconds"/> applies).</param>
public readonly record struct StopFrame(double Now, bool Eligible, bool Ready, bool SimStopping, float Speed, float Forward, float Decel, long Frame = -1);

/// <summary>
/// The sprint stop's state machine (animation pass 3; engine-free, tested): when the stop clip starts, the clip time each frame and when it ends. The client
/// (<c>NetworkView.SprintEdges.cs</c>) calls <see cref="SprintShown"/> each frame it shows the sprint loop and <see cref="Update"/> each frame it places the locomotion, and poses the clip at
/// <see cref="Time"/> while <see cref="Active"/>. Rules: see <see cref="Update"/>; the thresholds are ours (spec "Transitions, connecting clips ... (animation pass 3)").
/// </summary>
public sealed class SprintStopTracker
{
    /// <summary>A stop is armed for this long after the sprint was last shown (s).</summary>
    public const double ArmSeconds = 0.5;
    /// <summary>Without the simulation's flag: a sprinter slower than this share of the run speed and slowing is stopping.</summary>
    public const float BelowRun = 0.85f;
    /// <summary>Without the flag: the slowing it needs (m/s²).</summary>
    public const float MinDecel = 12f;
    /// <summary>The body counts as at rest below this speed (m/s).</summary>
    public const float RestSpeed = 0.3f;
    /// <summary>The clip runs at most this many times real time while it follows the body's speed.</summary>
    public const float MaxRate = 4f;
    /// <summary>Without the flag: a stop still faster than <see cref="GiveUpSpeed"/> this long after it began is not one (s).</summary>
    public const double GiveUpSeconds = 0.45;
    public const float GiveUpSpeed = 1.2f;
    /// <summary>
    /// A stop not placed for more than <see cref="SprintEdgeStaleness.MaxSkippedFrames"/> frames in a row (<see cref="StopFrame.Frame"/>) is over: an action clip took the body (review M1).
    /// Frame-based since qc/reference-pass (review S-2: a single frame longer than the old 0.15 s time limit, a hitch, ended a stop for good). This time backstop (s; ours) applies only
    /// when no frame numbers are given.
    /// </summary>
    public const double StaleBackstopSeconds = 0.5;
    /// <summary>The simulation's flag dropping with the body still faster than this (m/s) ends the stop (the stop was cancelled).</summary>
    public const float CancelSpeed = 1f;

    public bool Active { get; private set; }
    public float Time { get; private set; }
    public double SprintAt { get; private set; } = double.NegativeInfinity;
    bool _used, _rest, _withFlag;
    float _min, _end;
    double _startAt, _frameAt;
    long _lastFrame = -1;

    /// <summary>Whether a stop could start now (review S-1: the client skips the stop's work for a marine with no stop under way and none armed).</summary>
    public bool CanArm(double now) => !_used && SprintAt < now && now - SprintAt <= ArmSeconds;

    /// <summary>
    /// Whether the stop was hidden since the last update: more than <see cref="SprintEdgeStaleness.MaxSkippedFrames"/> placement frames skipped (<see cref="StopFrame.Frame"/>), or, only
    /// without frame numbers, more than <see cref="StaleBackstopSeconds"/> passed.
    /// </summary>
    bool Skipped(in StopFrame f) => SprintEdgeStaleness.Stale(f.Now, _frameAt, f.Frame, _lastFrame, StaleBackstopSeconds);

    /// <summary>The sprint loop (or its start) is shown this frame: a stop may follow it.</summary>
    public void SprintShown(double now) { SprintAt = now; _used = false; }

    /// <summary>A charge ended this frame: a stop may follow it as one follows the sprint (the graphs' Sprint Attack → Sprint to Idle).</summary>
    public void ChargeEnded(double now) { SprintAt = now - 1e-3; _used = false; }

    /// <summary>Forgets a stop under way (an action took the body).</summary>
    public void Drop() => Active = false;

    /// <summary>
    /// One frame. Starts a stop when none plays, the sprint was shown within <see cref="ArmSeconds"/> (not this frame), the marine is <see cref="StopFrame.Eligible"/>, the weapon is down and
    /// either the simulation's flag is on, or (no flag) the body moves forward slower than <see cref="BelowRun"/> x run speed, faster than <see cref="RestSpeed"/>, slowing at
    /// <see cref="MinDecel"/> or more; one stop per sprint. A stop under way ends (<see cref="StopEnd"/>): stale when a placement frame was skipped (<see cref="StopFrame.Frame"/>) or after <see cref="StaleBackstopSeconds"/> unseen; interrupted when not eligible or the
    /// weapon comes up; cancelled when the flag it began with drops while the body still moves faster than <see cref="CancelSpeed"/>, or (no flag) the body speeds up by 1 m/s or is still
    /// above <see cref="GiveUpSpeed"/> after <see cref="GiveUpSeconds"/>; done when the clip time reaches the later of its branch and its rest (at most its length). The clip time is where the
    /// clip's root, past its top speed, has slowed to the body's speed, between half and <see cref="MaxRate"/> times real time; from rest the clip runs at its own speed. Returns how it ended this frame
    /// (<see cref="StopEnd.None"/> while it plays or when none starts).
    /// </summary>
    public StopEnd Update(in StopFrame f, SprintEdgeClip? clip, float runSpeed)
    {
        if (Active)
        {
            double sinceSeen = f.Now - _frameAt;
            if (Skipped(f)) { Active = false; return StopEnd.Stale; }
            float dt = (float)Math.Clamp(sinceSeen, 0.0, 0.1);
            _frameAt = f.Now; _lastFrame = f.Frame;
            _min = MathF.Min(_min, f.Speed);
            if (!f.Eligible || f.Ready) { Active = false; return StopEnd.Interrupted; }
            if (_withFlag ? !f.SimStopping && f.Speed > CancelSpeed
                          : f.Speed > _min + 1f || (f.Now - _startAt > GiveUpSeconds && f.Speed > GiveUpSpeed)) { Active = false; return StopEnd.Cancelled; }
            if (clip is null) { Active = false; return StopEnd.Interrupted; }
            if (f.Speed <= RestSpeed) _rest = true;
            float target = _rest ? Time + dt : clip.TimeAtSlowingSpeed(f.Speed);
            Time = _rest ? target : Math.Clamp(target, Time + 0.5f * dt, Time + MaxRate * dt);
            if (Time >= _end) { Active = false; return StopEnd.Done; }
            return StopEnd.None;
        }
        if (clip is null || !f.Eligible || f.Ready || _used || f.Now - SprintAt > ArmSeconds || SprintAt >= f.Now || clip.RootForward.Count < 2) return StopEnd.None;
        if (!f.SimStopping && (f.Speed >= BelowRun * runSpeed || f.Speed <= RestSpeed || f.Forward < 0.5f * f.Speed || f.Decel < MinDecel)) return StopEnd.None;
        _used = true; Active = true; _withFlag = f.SimStopping; _rest = f.Speed <= RestSpeed;
        Time = clip.TimeAtSlowingSpeed(f.Speed); _min = f.Speed; _startAt = _frameAt = f.Now; _lastFrame = f.Frame;
        _end = MathF.Min(clip.Length, MathF.Max(clip.BranchAt, clip.RestAt));
        return StopEnd.None;
    }
}

/// <summary>
/// The sprint start's state machine (animation pass 3; engine-free, tested): a sprint shown within <see cref="ArmSeconds"/> of standing plays the start clip, posed at the distance the body has
/// gone since it stood (between real time and <see cref="MaxRate"/> times it), until its branch or the end of its root motion; then the loop takes over at its right foot
/// (<see cref="LoopSeedFraction"/>). Not resumed once left (a placement frame without it, or <see cref="StaleBackstopSeconds"/>).
/// </summary>
public sealed class SprintStartTracker
{
    public const double ArmSeconds = 0.4;
    public const float MaxRate = 3f;
    /// <summary>The time backstop of the start's staleness (s; ours), used only without frame numbers: with them a start is left after more than <see cref="SprintEdgeStaleness.MaxSkippedFrames"/> placed frames without it.</summary>
    public const double StaleBackstopSeconds = 0.5;
    /// <summary>The loop's right foot (<c>EVT_Right_Foot_Rush</c> at 0.9444 of the loop); the start ends on its right foot.</summary>
    public const float LoopSeedFraction = 0.9444f;
    /// <summary>Below this ground speed (m/s) the marine stands.</summary>
    public const float StandSpeed = 1f;

    public bool Active { get; private set; }
    public float Time { get; private set; }
    public float Travel { get; private set; }
    public double StandAt { get; private set; } = double.NegativeInfinity;
    bool _used;
    double _frameAt = double.NegativeInfinity;
    long _placeFrame = -1;

    /// <summary>
    /// Every placed frame, before <see cref="Place"/>: the body's ground speed and how far it went forward along the facing since the last frame (m). <paramref name="frame"/> is the rendered
    /// frame's number (-1: none): a start that was not placed on the frame before is left.
    /// </summary>
    public void Track(double now, float speed, bool grounded, float movedForward, long frame = -1)
    {
        if (speed < StandSpeed && grounded) { StandAt = now; Travel = 0f; _used = false; }
        else Travel += MathF.Max(0f, movedForward);
        if (Active && SprintEdgeStaleness.Stale(now, _frameAt, frame, _placeFrame, StaleBackstopSeconds)) Active = false;
    }

    /// <summary>
    /// A frame the sprint is shown: true when the start clip shows (at <see cref="Time"/>), false when it does not or has just ended (then <paramref name="seedFraction"/> is set for the
    /// loop's first frame). <paramref name="clip"/> null: the family has no start.
    /// </summary>
    public bool Place(double now, SprintEdgeClip? clip, out float? seedFraction, long frame = -1)
    {
        seedFraction = null;
        if (!Active)
        {
            if (_used || now - StandAt > ArmSeconds) return false;
            _used = true;
            if (clip is null || clip.RootForward.Count < 2) return false;
            Active = true; _frameAt = double.NegativeInfinity; Time = 0f;
        }
        float target = clip!.TimeAtDistance(Travel);
        bool first = double.IsNegativeInfinity(_frameAt);
        float dt = first ? 0f : (float)Math.Clamp(now - _frameAt, 0.0, 0.1);
        Time = first ? target : Math.Clamp(target, Time + dt, Time + MaxRate * dt);
        _frameAt = now; _placeFrame = frame;
        if (Time >= clip.BranchAt - 1e-3f || Travel >= clip.RootForward[^1]) { Active = false; seedFraction = LoopSeedFraction; return false; }
        return true;
    }

    /// <summary>The start has given way to something else this frame.</summary>
    public void Drop() => Active = false;
}

/// <summary>
/// When a sprint stop or start under way is over because the networked view stopped placing it (qc/reference-pass; engine-free, tested): more than <see cref="MaxSkippedFrames"/> rendered
/// frames in a row without it (an action clip took the body: a roll, a reaction, a throw or a charge lasts far longer), or, only when no frame numbers are given, longer than the backstop.
/// A remote marine's one-snapshot flag (a one-tick reaction or throw seen for a frame or two) skips a frame or two and does not end it; a hitch between consecutive frames never does.
/// </summary>
public static class SprintEdgeStaleness
{
    /// <summary>The most placement frames in a row a stop or start may miss and go on (ours: a one-snapshot flicker, 33 ms at the 30 Hz tick, is 2 to 3 frames at 60 to 90 fps).</summary>
    public const int MaxSkippedFrames = 3;

    /// <param name="frame">This frame's number, -1 when none.</param>
    /// <param name="lastFrame">The frame it was last placed on, -1 when none.</param>
    public static bool Stale(double now, double lastAt, long frame, long lastFrame, double backstopSeconds) =>
        frame >= 0 && lastFrame >= 0 ? frame - lastFrame - 1 > MaxSkippedFrames : now - lastAt > backstopSeconds;
}

/// <summary>
/// The pose maths of animation pass 3's cross-fades (engine-free, tested): the velocity a bone carries into a cross-fade ("dead blending"), how far it drifts, and the slerp that keeps the arc
/// it began on (no flip when the target passes 180 degrees from the start).
/// </summary>
public static class PoseBlendMath
{
    /// <summary>The angular velocity (radians per second, as an axis times the rate) that turns <paramref name="previous"/> into <paramref name="current"/> in <paramref name="dt"/> seconds, capped at <paramref name="maxRate"/>.</summary>
    public static Vector3 AngularVelocity(Quaternion previous, Quaternion current, float dt, float maxRate)
    {
        if (!(dt > 1e-5f)) return Vector3.Zero;
        var dq = Quaternion.Normalize(current * Quaternion.Inverse(previous));
        if (dq.W < 0f) dq = Quaternion.Negate(dq);
        float angle = 2f * MathF.Acos(Math.Clamp(dq.W, -1f, 1f));
        var axis = new Vector3(dq.X, dq.Y, dq.Z);
        float s = axis.Length();
        if (s < 1e-6f || angle < 1e-6f) return Vector3.Zero;
        var w = axis / s * (angle / dt);
        float rate = w.Length();
        return rate > maxRate ? w / rate * maxRate : w;
    }

    /// <summary>A linear velocity capped at <paramref name="max"/>.</summary>
    public static Vector3 Capped(Vector3 v, float max) { float l = v.Length(); return l > max ? v / l * max : v; }

    /// <summary>How far (in seconds of the velocity) a snapshot has drifted <paramref name="age"/> s after it was taken, slowing with time constant <paramref name="decay"/>: decay x (1 - e^(-age/decay)).</summary>
    public static float Drift(float age, float decay) => decay > 1e-4f ? decay * (1f - MathF.Exp(-MathF.Max(0f, age) / decay)) : 0f;

    /// <summary>A rotation carried on by angular velocity <paramref name="w"/> for <paramref name="drift"/> seconds (applied in the parent's frame, as the velocity was measured).</summary>
    public static Quaternion Carry(Quaternion rotation, Vector3 w, float drift)
    {
        float rate = w.Length();
        if (rate < 1e-5f || drift <= 0f) return rotation;
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(w / rate, rate * drift) * rotation);
    }

    /// <summary>
    /// A slerp from <paramref name="from"/> to <paramref name="to"/> along the arc they give, with <paramref name="to"/> first put in the hemisphere of <paramref name="lastTarget"/> (its own
    /// value last frame) when there is one, else of <paramref name="from"/> (the shorter arc). Returns the result and the target as used (the caller keeps it for the next frame). When the two
    /// are antipodal (no defined arc) the start is kept.
    /// </summary>
    public static (Quaternion Result, Quaternion Target) ArcSlerp(Quaternion from, Quaternion to, float t, Quaternion? lastTarget)
    {
        if (lastTarget is { } last) { if (Quaternion.Dot(to, last) < 0f) to = Quaternion.Negate(to); }
        else if (Quaternion.Dot(from, to) < 0f) to = Quaternion.Negate(to);
        float cos = Math.Clamp(Quaternion.Dot(from, to), -1f, 1f);
        float angle = MathF.Acos(cos);
        float sin = MathF.Sin(angle);
        if (sin < 1e-5f)
            return (cos < 0f ? from : Quaternion.Normalize(Quaternion.Lerp(from, to, t)), to);   // antipodal: no arc (review nit); equal: a plain lerp
        float wa = MathF.Sin((1f - t) * angle) / sin, wb = MathF.Sin(t * angle) / sin;
        return (Quaternion.Normalize(new Quaternion(from.X * wa + to.X * wb, from.Y * wa + to.Y * wb, from.Z * wa + to.Z * wb, from.W * wa + to.W * wb)), to);
    }
}
