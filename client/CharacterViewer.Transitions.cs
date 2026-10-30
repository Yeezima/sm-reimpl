using System;
using Godot;
using SmContent;
using NQuat = System.Numerics.Quaternion;
using NVec = System.Numerics.Vector3;

namespace SmClient;

/// <summary>
/// Animation pass 3 (the owner, 2026-10-29: "it flicks between animation states in a jarring fashion"): how a cross-fade leaves the pose it starts from. The maths are engine-free and
/// tested (<see cref="PoseBlendMath"/>).
/// <list type="bullet">
/// <item><b>Moving snapshot</b> ("dead blending"). A cross-fade (a clip taking over, M40-T3, or the stances taking over from a clip) faded from a frozen copy of the pose shown at that
/// moment: every bone stopped dead in the first frame of the fade. Now the copy keeps moving: each bone carries on with the velocity it had between the last two poses applied (local
/// translation and rotation), slowing with a time constant of <see cref="SnapshotDecaySeconds"/> (ours). The velocity is measured between the last two applied poses with the time between
/// them (review S1: a clip posed from the networked view's placement is applied after the frame's own pose work, so a copy of the skeleton at the start of the frame gave it no velocity).
/// Only for characters within <see cref="DeadBlendRange"/> of the camera (review S3: the bone read-back is the cost; farther away the motion is too small to see).</item>
/// <item><b>Smooth blend curve</b>. The weights of the clip entries and of the stance cross-fades follow a smoothstep (3x² - 2x³) of the linear progress. The graphs' transition effects carry
/// a blend curve the readers do not decode; Havok's default for a blending transition is the smooth curve (<b>interpretation</b>).</item>
/// <item><b>Arc-continuous entry slerp</b> (<see cref="ContinuousSlerp"/>).</item>
/// </list>
/// The networked Ork's Stance / Pose entries go through the same code, so they get the curve, the floors and the moving snapshot too. <c>--no-p3</c> (test aid) restores the frozen snapshot
/// and the linear fades (with the other pass-3 changes) for before / after runs; <c>--no-dead-blend</c> only these.
/// </summary>
public partial class CharacterViewer
{
    /// <summary><c>--no-p3</c>: every animation pass 3 change off (before / after runs). <c>--no-dead-blend</c>: the frozen snapshot and linear fades only.</summary>
    public static readonly bool NoP3 = OS.GetCmdlineUserArgs().Contains("--no-p3");
    static readonly bool NoDeadBlend = NoP3 || OS.GetCmdlineUserArgs().Contains("--no-dead-blend");

    /// <summary>How fast a snapshot's carried-on motion dies away (s; ours): after 0.06 s the bone has gone 63 % of the way it will drift, 0.06 s times its velocity at the switch.</summary>
    public static readonly float SnapshotDecaySeconds = ArgFloat("--dead-blend-decay", 0.06f);
    /// <summary>The fastest a snapshot bone may carry on (ours): a rotation of at most 720 degrees a second and a translation of at most 3 m/s (a pop just before the switch is not extrapolated).
    /// <c>--dead-blend-cap &lt;deg/s&gt;</c> (test aid) changes the rotation cap.</summary>
    static readonly float MaxSnapAngular = ArgFloat("--dead-blend-cap", 720f) * MathF.PI / 180f;
    const float MaxSnapLinear = 3f;
    /// <summary>The moving snapshot is kept for characters within this distance of the camera (m; ours); farther away the copy is frozen as before.</summary>
    public const float DeadBlendRange = 30f;

    /// <summary>
    /// The shortest clip entry on the smooth curve (s; ours): the smoothstep's steepest slope is 1.5 times the line's, so the M40-T3 floor (0.125 s, a bone turning at most 24 degrees a
    /// frame) is lengthened by 1.5 to keep that peak rate. The graphs' 0.1 s and 0.167 s entries become 0.1875 s.
    /// </summary>
    public const float SmoothMinEntryBlendSeconds = MinEntryBlendSeconds * 1.5f;
    /// <summary>The shortest stance cross-fade on the smooth curve (s; ours): the graphs' 0.1 s ('Default 3 Frame Transition') times 1.5, the same steepest rate as the old linear 0.1 s.</summary>
    public const float SmoothMinStanceFadeSeconds = 0.15f;

    static float ArgFloat(string name, float fallback)
    {
        var a = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length && float.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) && v >= 0f ? v : fallback;
    }

    // The last two poses applied (local translation, rotation) and the character clock when each was applied.
    Vector3[] _lastPos = Array.Empty<Vector3>(), _prevPos = Array.Empty<Vector3>();
    Quaternion[] _lastRot = Array.Empty<Quaternion>(), _prevRot = Array.Empty<Quaternion>();
    bool _haveLast, _havePrev;
    double _clock, _lastAt, _prevAt;   // review S-7: doubles (a float clock loses sub-millisecond steps after hours)
    ulong _lastFrame = ulong.MaxValue, _nearFrame = ulong.MaxValue;
    bool _near;
    Vector3[] _snapVel = Array.Empty<Vector3>(), _snapAngular = Array.Empty<Vector3>();
    bool _snapMoving;
    float _snapAge, _snapDrift;   // seconds since the snapshot; how far it has drifted (in seconds of its velocity), once per frame

    /// <summary>The smooth blend curve (smoothstep) of a linear progress 0..1.</summary>
    public static float Smooth(float x)
    {
        if (NoDeadBlend) return Math.Clamp(x, 0f, 1f);
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>The linear progress whose <see cref="Smooth"/> is <paramref name="y"/> (the inverse of the smoothstep; sim/switch-cancel).</summary>
    public static float SmoothInverse(float y)
    {
        y = Math.Clamp(y, 0f, 1f);
        return NoDeadBlend ? y : 0.5f - MathF.Sin(MathF.Asin(1f - 2f * y) / 3f);
    }

    /// <summary>At the start of a frame's pose work: the character's clock and the snapshot's age.</summary>
    void AdvanceClock(double delta)
    {
        if (NoDeadBlend) return;
        double dt = Math.Clamp(delta, 0.0, 0.25);
        _clock += dt;
        _snapAge += (float)dt;
        _snapDrift = PoseBlendMath.Drift(_snapAge, SnapshotDecaySeconds);
    }

    /// <summary>Whether this character is near enough the camera for the moving snapshot (decided once a frame).</summary>
    bool NearCamera()
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame == _nearFrame) return _near;
        _nearFrame = frame;
        var camera = GetViewport()?.GetCamera3D();
        _near = camera is null || camera.GlobalPosition.DistanceSquaredTo(GlobalPosition) <= DeadBlendRange * DeadBlendRange;
        return _near;
    }

    /// <summary>
    /// After a pose was applied: keeps it (and the one before, from an earlier frame) for the velocity a snapshot takes. A second application in the same frame replaces the frame's pose. Only
    /// near the camera (<see cref="DeadBlendRange"/>).
    /// </summary>
    void RecordApplied()
    {
        if (NoDeadBlend || _skeleton is null) return;
        if (!NearCamera()) { _haveLast = _havePrev = false; return; }
        int n = _skeleton.GetBoneCount();
        if (_lastPos.Length != n) { _lastPos = new Vector3[n]; _prevPos = new Vector3[n]; _lastRot = new Quaternion[n]; _prevRot = new Quaternion[n]; _haveLast = _havePrev = false; }
        ulong frame = Engine.GetProcessFrames();
        if (_haveLast && frame != _lastFrame)
        {
            (_prevPos, _lastPos) = (_lastPos, _prevPos);
            (_prevRot, _lastRot) = (_lastRot, _prevRot);
            _prevAt = _lastAt; _havePrev = true;
        }
        for (int b = 0; b < n; b++) { _lastPos[b] = _skeleton.GetBonePosePosition(b); _lastRot[b] = _skeleton.GetBonePoseRotation(b); }
        _lastAt = _clock; _lastFrame = frame; _haveLast = true;
    }

    /// <summary>Called by <see cref="TakeSnapshot"/> once the pose is copied: each bone's velocity between the pose applied before the last one and the snapshot (zero when there is none).</summary>
    void SnapshotVelocities()
    {
        _snapAge = 0f; _snapDrift = 0f;
        _snapMoving = false;
        if (NoDeadBlend || _skeleton is null || _snapPos is null || _snapRot is null) return;
        int n = _snapPos.Length;
        if (_snapVel.Length != n) { _snapVel = new Vector3[n]; _snapAngular = new Vector3[n]; }
        float dt = (float)(_lastAt - _prevAt);
        // Review S-5: the last applied pose must be recent too (a character not posed for a while carries no velocity into the snapshot).
        if (!_havePrev || _prevPos.Length != n || !(dt > 1e-4f) || dt > 0.1f || _clock - _lastAt > 0.1) { Array.Clear(_snapVel); Array.Clear(_snapAngular); SnapshotsFrozen++; return; }
        SnapshotsMoving++;
        for (int b = 0; b < n; b++)
        {
            var v = PoseBlendMath.Capped(N(_snapPos[b] - _prevPos[b]) / dt, MaxSnapLinear);
            _snapVel[b] = new Vector3(v.X, v.Y, v.Z);
            var w = PoseBlendMath.AngularVelocity(N(_prevRot[b]), N(_snapRot[b]), dt, MaxSnapAngular);
            _snapAngular[b] = new Vector3(w.X, w.Y, w.Z);
        }
        _snapMoving = true;
    }

    /// <summary>Test aid (the transition probe's report): snapshots taken with a measured velocity, and without one (far from the camera, no earlier applied pose, or dead blending off).</summary>
    public static int SnapshotsMoving, SnapshotsFrozen;

    /// <summary>The snapshot's bone <paramref name="b"/> now: carried on by its velocity, slowing (see the class comment); the frozen copy without dead blending.</summary>
    void SnapBone(int b, out Vector3 pos, out Quaternion rot)
    {
        pos = _snapPos![b]; rot = _snapRot![b];
        if (!_snapMoving || b >= _snapVel.Length || _snapDrift <= 0f) return;
        pos += _snapVel[b] * _snapDrift;
        var w = _snapAngular[b];
        float speed = w.Length();
        if (speed > 1e-5f) rot = (new Quaternion(w.Normalized(), speed * _snapDrift) * rot).Normalized();
    }

    /// <summary>
    /// Animation pass 3 (reported by the jump-pack work for the air slash): the clip entry's slerp from the snapshot to the clip's rotation, kept on the arc the entry began on: the target is
    /// put in the hemisphere of its own last frame and the slerp follows it (<see cref="PoseBlendMath.ArcSlerp"/>), so it never flips to the other side when the moving target passes 180
    /// degrees from the start. <c>--no-p3</c> / <c>--no-dead-blend</c>: the plain shortest-arc slerp.
    /// </summary>
    Quaternion ContinuousSlerp(int bone, Quaternion from, Quaternion to, float t)
    {
        if (NoDeadBlend || (uint)bone >= (uint)_entryTarget.Length) return from.Slerp(to, t).Normalized();
        var (result, target) = PoseBlendMath.ArcSlerp(N(from), N(to), t, _entryTargetValid[bone] ? N(_entryTarget[bone]) : null);
        _entryTarget[bone] = G(target); _entryTargetValid[bone] = true;
        return G(result);
    }

    Quaternion[] _entryTarget = Array.Empty<Quaternion>();
    bool[] _entryTargetValid = Array.Empty<bool>();

    /// <summary>At a clip entry: the arc of every bone is chosen afresh (<see cref="ContinuousSlerp"/>).</summary>
    void ResetEntryArcs()
    {
        int n = _skeleton?.GetBoneCount() ?? 0;
        if (_entryTarget.Length != n) { _entryTarget = new Quaternion[n]; _entryTargetValid = new bool[n]; }
        else Array.Clear(_entryTargetValid);
    }

    static NQuat N(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    static NVec N(Vector3 v) => new(v.X, v.Y, v.Z);
    static Quaternion G(NQuat q) => new(q.X, q.Y, q.Z, q.W);
}
