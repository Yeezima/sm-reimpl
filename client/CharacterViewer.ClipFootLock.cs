using System;
using Godot;
using SmNet;

namespace SmClient;

/// <summary>
/// anim/melee-flow: the clip foot lock (rules and measurements in <see cref="ClipFootLockRules"/>; ours). A clip posed with <see cref="ClipFootLock"/> on (the networked view sets it for
/// the melee strikes and the charge on the ground) holds each foot the clip puts down at the place in the world where it was put down, by two-bone IK of the leg
/// (<see cref="SolveLeg"/>; the foot keeps the clip's orientation), for as long as the clip keeps it down; a lifted foot goes back to the clip's own over
/// <see cref="ClipFootLockRules.ReleaseSeconds"/>. A held ankle the leg cannot reach is dragged on the edge of the reach. Applied after the clip and its layers are posed
/// (<see cref="ApplyPose"/>), so the distance variants and the entry cross-fade are included. The pins are dropped when the lock is off for a frame, when the body jumps more
/// than a metre in a frame, and in the stances. <c>--no-clip-foot-lock</c> (test aid) or <c>--no-melee-flow</c> switch it off.
/// </summary>
public partial class CharacterViewer
{
    /// <summary><c>--no-melee-flow</c>: every anim/melee-flow change off (before / after runs on the same demo).</summary>
    public static readonly bool NoMeleeFlow = OS.GetCmdlineUserArgs().Contains("--no-melee-flow");
    /// <summary><c>--no-clip-foot-lock</c> (test aid): the clip foot lock alone off.</summary>
    public static readonly bool NoClipFootLock = NoMeleeFlow || OS.GetCmdlineUserArgs().Contains("--no-clip-foot-lock");

    /// <summary>Set by the caller before <see cref="Pose"/>: hold the feet the clip puts down where they were put down (the body may not travel the clip's root motion). Off for every other pose.</summary>
    public bool ClipFootLock { get; set; }

    /// <summary>Set by the caller with <see cref="ClipFootLock"/>: the clip's extracted root speed forward now (m/s per second of the clip; NaN: unknown). A foot starts a hold only
    /// when it stands still where the clip was authored (<see cref="ClipFootLockRules.AuthoredStill"/>).</summary>
    public float ClipRootSpeed { get; set; } = float.NaN;

    struct ClipPin
    {
        public bool Held, Releasing;
        public Vector3 World;        // the held ankle, world space
        public double ReleasedAt;    // _lockClock when the clip lifted the foot
        public float ReleaseFor;     // how long the release takes (ClipFootLockRules.ReleaseDuration)
    }

    readonly ClipPin[] _clipPins = new ClipPin[2];
    double _lockClock;
    ulong _lockFrame = ulong.MaxValue;
    Vector3 _lockOrigin;
    readonly Vector3[] _lockPrevToe = new Vector3[2];   // the clip's toes (skeleton space) at the last application, for their speed in the clip
    readonly bool[] _lockPrevStill = [true, true];      // the last decision, kept through an application at the same clip time
    float _lockPrevTime = float.NaN;
    string? _lockPrevClip;

    /// <summary>Test aid counters (the transition probe prints them): feet put down under a lock, ankles dragged on the edge of the reach (frames), pins dropped on a teleport.</summary>
    public static int ClipPinsHeld, ClipPinsDragged, ClipPinsTeleport;

    void AdvanceLockClock(double delta) => _lockClock += Math.Clamp(delta, 0.0, 0.25);

    /// <summary>Drops the pins and the toes' history (the next lock starts with no speed in the clip: no stale velocity from another clip or place).</summary>
    void DropClipPins()
    {
        _clipPins[0] = default; _clipPins[1] = default;
        _lockPrevClip = null; _lockPrevTime = float.NaN;
        _lockPrevToe[0] = _lockPrevToe[1] = default;
        _lockPrevStill[0] = _lockPrevStill[1] = true;
    }

    /// <summary>After a clip pose (and its layers) is applied: holds the put-down feet (see the class comment).</summary>
    void ApplyClipFootLock()
    {
        if (!ClipFootLock || NoClipFootLock || _skeleton is null) { DropClipPins(); return; }
        ResolveLegs();
        if (_legs is null || _legs.Length != 2) return;
        if (_toeBones[0] < 0 || _toeBones[1] < 0) _toeBones = [_skeleton.FindBone("RIG_L_Toe"), _skeleton.FindBone("RIG_R_Toe")];
        if (_toeBones[0] < 0 || _toeBones[1] < 0) return;
        // A lock that was off for more than a frame starts afresh (the pins are of another clip and another place); so does one after a teleport.
        ulong frame = Engine.GetProcessFrames();
        var skeletonGlobal = _skeleton.GlobalTransform;
        var origin = skeletonGlobal.Origin;
        if (_lockFrame == ulong.MaxValue || frame > _lockFrame + 2) DropClipPins();
        else if (FootPinRules.Teleported(origin.X - _lockOrigin.X, origin.Y - _lockOrigin.Y, origin.Z - _lockOrigin.Z) && (_clipPins[0].Held || _clipPins[1].Held)) { DropClipPins(); ClipPinsTeleport++; }
        _lockFrame = frame; _lockOrigin = origin;
        var toSkeleton = skeletonGlobal.AffineInverse();
        bool sameClip = _lockPrevClip == _animationName && float.IsFinite(_lockPrevTime);
        float clipDt = sameClip ? _time - _lockPrevTime : 0f;
        for (int i = 0; i < 2; i++)
        {
            var leg = _legs[i];
            var footPose = _skeleton.GetBoneGlobalPose(leg.Foot);   // skeleton space: Z up, the feet's plane at 0
            var toePos = _skeleton.GetBoneGlobalPose(_toeBones[i]).Origin;
            var toeToAnkle = footPose.Origin - toePos;   // the clip's foot: the ankle follows the held toe, so the clip's heel lift and the body's turn pivot on the toe
            ref var pin = ref _clipPins[i];
            bool down = ClipFootLockRules.Planted(pin.Held && !pin.Releasing, toePos.Z, footPose.Origin.Z);
            // A new hold only for a foot that stands still in the authored world (not one the clip drags or skims along the ground).
            float vx = clipDt > 1e-4f ? (toePos.X - _lockPrevToe[i].X) / clipDt : float.NaN, vy = clipDt > 1e-4f ? (toePos.Y - _lockPrevToe[i].Y) / clipDt : float.NaN;
            bool still = clipDt > 1e-4f ? ClipFootLockRules.AuthoredStill(vx, vy, ClipRootSpeed) : sameClip ? _lockPrevStill[i] : !float.IsFinite(ClipRootSpeed);
            _lockPrevToe[i] = toePos; _lockPrevStill[i] = still;
            if (down && !still && (!pin.Held || pin.Releasing)) down = false;
            if (LockTrace is not null && Name == LockTrace)
                GD.Print($"CLIPLOCK f {frame} {Name} {(i == 0 ? "L" : "R")} clip {_animationName} t {_time:F3} toe {toePos.Z:F3} ankle {footPose.Origin.Z:F3} down {down} still {still} root {ClipRootSpeed:F2} v {vx:F2},{vy:F2} held {pin.Held} rel {pin.Releasing}");
            if (down && (!pin.Held || pin.Releasing))
            {
                // Put down (again): the toe held from where the pose shows it now (mid-release: where the release has brought it).
                var start = toePos;
                if (pin.Held && pin.Releasing)
                {
                    float keep = ClipFootLockRules.ReleaseHold((float)(_lockClock - pin.ReleasedAt), pin.ReleaseFor);
                    start = toePos.Lerp(toSkeleton * pin.World, keep);
                }
                pin = new ClipPin { Held = true, World = skeletonGlobal * start };
                ClipPinsHeld++;
            }
            else if (!down && pin.Held && !pin.Releasing)
            {
                pin.Releasing = true; pin.ReleasedAt = _lockClock;
                pin.ReleaseFor = ClipFootLockRules.ReleaseDuration((toSkeleton * pin.World + toeToAnkle).DistanceTo(footPose.Origin));   // the farther from its own pose, the longer
            }
            if (!pin.Held) continue;
            float hold = pin.Releasing ? ClipFootLockRules.ReleaseHold((float)(_lockClock - pin.ReleasedAt), pin.ReleaseFor) : 1f;
            if (hold <= 0f) { pin = default; continue; }
            var wanted = footPose.Origin.Lerp(toSkeleton * pin.World + toeToAnkle, hold);
            var hip = _skeleton.GetBoneGlobalPose(leg.Thigh).Origin;
            var knee = _skeleton.GetBoneGlobalPose(leg.Calf).Origin;
            float legLength = hip.DistanceTo(knee) + knee.DistanceTo(footPose.Origin);
            var reach = wanted - hip;
            float r = ClipFootLockRules.Reach(reach.Length(), legLength, out bool dragged);
            if (dragged && reach.LengthSquared() > 1e-8f)
            {
                wanted = hip + reach.Normalized() * r;
                if (!pin.Releasing) pin.World = skeletonGlobal * (wanted - toeToAnkle);   // the foot is dragged along: held from here on
                ClipPinsDragged++;
            }
            SolveLeg(leg, wanted);
        }
        _lockPrevClip = _animationName; _lockPrevTime = _time;
    }

    /// <summary><c>--clip-lock-trace &lt;character name&gt;</c> (test aid): one line per foot and application of the lock on that character (e.g. Player5).</summary>
    static readonly string? LockTrace = Array.IndexOf(OS.GetCmdlineUserArgs(), "--clip-lock-trace") is var li and >= 0 && li + 1 < OS.GetCmdlineUserArgs().Length ? OS.GetCmdlineUserArgs()[li + 1] : null;
}
