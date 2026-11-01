using System;
using Godot;
using SmFormats.Havok;
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
    /// <summary><c>--no-feel</c> (test aid): every change of the feel pass (anim/feel-pass, <c>NetworkView.Feel.cs</c>) off, for before / after runs on one build.</summary>
    public static readonly bool NoFeel = OS.GetCmdlineUserArgs().Contains("--no-feel");
    /// <summary><c>--old-hammer-legs</c> (test aid, anim/hammer-swing): no stride scale (the clip foot lock as before).</summary>
    public static readonly bool OldHammerLegs = OS.GetCmdlineUserArgs().Contains("--old-hammer-legs");
    /// <summary><c>--no-clip-foot-lock</c> (test aid): the clip foot lock alone off.</summary>
    public static readonly bool NoClipFootLock = NoMeleeFlow || OS.GetCmdlineUserArgs().Contains("--no-clip-foot-lock");

    /// <summary>Set by the caller before <see cref="Pose"/>: hold the feet the clip puts down where they were put down (the body may not travel the clip's root motion). Off for every other pose.</summary>
    public bool ClipFootLock { get; set; }

    /// <summary>Set by the caller with <see cref="ClipFootLock"/>: the clip's extracted root speed forward now (m/s per second of the clip; NaN: unknown). A foot starts a hold only
    /// when it stands still where the clip was authored (<see cref="ClipFootLockRules.AuthoredStill"/>).</summary>
    public float ClipRootSpeed { get; set; } = float.NaN;

    /// <summary>
    /// anim/hammer-swing: set by the caller with <see cref="ClipFootLock"/> for a strike whose body travels more than its clip's root motion (the simulation's lunge scale; 1: it travels what the
    /// clip did). The clip's feet are authored for the root motion it has: a foot planted in the authored world moves back along the body at the root's speed. The body here goes this
    /// many times as far, so the foot is held where it was put down by moving it back this many times as fast: the clip's feet are stretched along the body's heading by this factor
    /// (their forward offset from the character times it, by two-bone IK, faded in with the clip's share of the entry cross-fade), so a held foot stays within reach and a free foot lands
    /// where the longer step puts it. Before, the held foot was left behind at the clip's distance and the leg stretched out straight and was dragged along (<c>--old-hammer-legs</c>).
    /// </summary>
    public float ClipStrideScale { get; set; } = 1f;
    /// <summary>The strike's last sampled local pose (for the stride shift: where the clip's own ankle is).</summary>
    HavokTransform[]? _lockSampled;
    HavokBoundAnimation? _lockSampledAnimation;
    /// <summary>Test aid: the pins of the two feet as letters (H held, r releasing, - none) and the stride in use.</summary>
    public string ClipLockDebug() => $"{(_clipPins[0].Held ? (_clipPins[0].Releasing ? 'r' : 'H') : '-')}{(_clipPins[1].Held ? (_clipPins[1].Releasing ? 'r' : 'H') : '-')} stride {ClipStrideScale:F2}";
    readonly bool[] _reachOut = new bool[2];
    /// <summary>The farthest a stretched stride puts the ankle from the hip, of the leg's length (ours: under the 0.995 the lock's drag stood at, which showed as a straight leg).</summary>
    public const float StrideReach = 0.95f;
    /// <summary>Test aid counter (the report): held feet let go for being out of reach under a stretched stride.</summary>
    public static int StrideLetGo;
    /// <summary>Test aid counter (the report): feet shifted by the stride scale (frames).</summary>
    public static int StrideFeet;

    /// <summary>The clip's own ankle for <paramref name="leg"/> in skeleton space, from forward kinematics of the sampled pose (the same space as the bone poses); null without a sample.</summary>
    Vector3? ClipAnkle(int leg)
    {
        if (_lockSampled is not { } sampled || _lockSampledAnimation is not { } clip || _legs is null || _assets is null) return null;
        var sk = _assets.Model.Rig.Skeleton;
        if (!TrackOfBone.TryGetValue(clip, out var trackOf))
        {
            trackOf = new int[sk.BoneNames.Count];
            Array.Fill(trackOf, -1);
            for (int t = 0; t < clip.TrackToBone.Length; t++) if ((uint)clip.TrackToBone[t] < (uint)trackOf.Length) trackOf[clip.TrackToBone[t]] = t;
            TrackOfBone.Add(clip, trackOf);
        }
        System.Numerics.Matrix4x4 model = System.Numerics.Matrix4x4.Identity;
        foreach (int b in _legs[leg].Chain)
        {
            var local = trackOf[b] >= 0 ? sampled[trackOf[b]] : sk.ReferencePose[b];
            model = local.ToMatrix() * model;
        }
        return new Vector3(model.M41, model.M42, model.M43);
    }

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
        // anim/hammer-swing: the stride shift, in skeleton space along the heading (Y): the clip's ankle's forward offset times the stride's extra, by the clip's share of the entry.
        float stride = ClipStrideScale > 1.001f && float.IsFinite(ClipStrideScale) && !OldHammerLegs ? (ClipStrideScale - 1f) * (1f - _clipEntryKeep) : 0f;
        for (int i = 0; i < 2; i++)
        {
            var leg = _legs[i];
            var footPose = _skeleton.GetBoneGlobalPose(leg.Foot);   // skeleton space: Z up, the feet's plane at 0
            var toeRaw = _skeleton.GetBoneGlobalPose(_toeBones[i]).Origin;
            var shift = Vector3.Zero;
            var hipNow = _skeleton.GetBoneGlobalPose(leg.Thigh).Origin;
            var kneeNow = _skeleton.GetBoneGlobalPose(leg.Calf).Origin;
            float legLength = hipNow.DistanceTo(kneeNow) + kneeNow.DistanceTo(footPose.Origin);   // (the leg's bones are rigid: the shifted ankle is as far from the knee)
            if (stride > 1e-3f && ClipAnkle(i) is { } clipAnkle)
            {
                // The stretched foot, kept within StrideReach of the leg (the clip's own pose when that is already farther): a leg never shows straighter than the clip's.
                var wantedShift = new Vector3(0f, stride * clipAnkle.Y, 0f);
                var target = footPose.Origin + wantedShift - hipNow;
                float cap = MathF.Max(StrideReach * legLength, (footPose.Origin - hipNow).Length());
                if (target.Length() > cap) target = target.Normalized() * cap;
                shift = hipNow + target - footPose.Origin;
                StrideFeet++;
            }
            var toePos = toeRaw + shift;
            var ankleClip = footPose.Origin + shift;
            var toeToAnkle = ankleClip - toePos;   // the clip's foot: the ankle follows the held toe, so the clip's heel lift and the body's turn pivot on the toe
            ref var pin = ref _clipPins[i];
            bool down = ClipFootLockRules.Planted(pin.Held && !pin.Releasing, toeRaw.Z, footPose.Origin.Z);
            // A new hold only for a foot that stands still in the authored world (not one the clip drags or skims along the ground).
            float vx = clipDt > 1e-4f ? (toeRaw.X - _lockPrevToe[i].X) / clipDt : float.NaN, vy = clipDt > 1e-4f ? (toeRaw.Y - _lockPrevToe[i].Y) / clipDt : float.NaN;
            bool still = clipDt > 1e-4f ? ClipFootLockRules.AuthoredStill(vx, vy, ClipRootSpeed) : sameClip ? _lockPrevStill[i] : !float.IsFinite(ClipRootSpeed);
            _lockPrevToe[i] = toeRaw; _lockPrevStill[i] = still;
            if (down && !still && (!pin.Held || pin.Releasing)) down = false;
            if (LockTrace is not null && Name == LockTrace)
                GD.Print($"CLIPLOCK f {frame} {Name} {(i == 0 ? "L" : "R")} clip {_animationName} t {_time:F3} toe {toePos.Z:F3} ankle {ankleClip.Z:F3} down {down} still {still} root {ClipRootSpeed:F2} v {vx:F2},{vy:F2} held {pin.Held} rel {pin.Releasing}");
            if (!down) _reachOut[i] = false;
            if (down && (!pin.Held || pin.Releasing) && !_reachOut[i])
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
                pin.ReleaseFor = stride > 1e-3f ? ClipFootLockRules.ReleaseSeconds : ClipFootLockRules.ReleaseDuration((toSkeleton * pin.World + toeToAnkle).DistanceTo(ankleClip));   // the farther from its own pose, the longer; a stretched stride (the hammer): the foot flies on at once, as the clip's does
            }
            if (!pin.Held) { if (shift != Vector3.Zero) SolveLeg(leg, ankleClip); continue; }
            // A stretched stride (the hammer): a foot held out of reach lets go (the clip's own foot, kept within reach, takes over) instead of lying dragged behind the body.
            if (stride > 1e-3f && !pin.Releasing && (toSkeleton * pin.World + toeToAnkle - hipNow).Length() > StrideReach * legLength)
            {
                pin.Releasing = true; pin.ReleasedAt = _lockClock; pin.ReleaseFor = ClipFootLockRules.ReleaseSeconds; _reachOut[i] = true; StrideLetGo++;
            }
            float hold = pin.Releasing ? ClipFootLockRules.ReleaseHold((float)(_lockClock - pin.ReleasedAt), pin.ReleaseFor) : 1f;
            if (hold <= 0f) { pin = default; if (shift != Vector3.Zero) SolveLeg(leg, ankleClip); continue; }
            var wanted = ankleClip.Lerp(toSkeleton * pin.World + toeToAnkle, hold);
            var hip = hipNow;
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
