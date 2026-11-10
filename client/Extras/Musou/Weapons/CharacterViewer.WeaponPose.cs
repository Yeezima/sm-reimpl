using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent.Extras.Musou;
using SmFormats.Havok;

namespace SmClient;

/// <summary>
/// What a character shows of a Musou weapon's keyframed strike this frame (added content, Musou Mode only; <c>docs/claw-spear-animations.md</c>). Set by the owner of the character
/// (the preview stage, or the networked view for a Musou player with the power spear: <c>Arsenal/Spear/NetworkView.SpearPose.cs</c>), read by <see cref="CharacterViewer.ExtrasPoseOverlay"/>.
/// Null on every other character.
/// </summary>
public sealed class WeaponPoseState
{
    /// <summary>The clip shown, or null (the stance alone).</summary>
    public WeaponPoseClip? Clip;
    /// <summary>Seconds into <see cref="Clip"/> (in a match the strike's drawn time, <see cref="WeaponPoseClip.TimeAt"/>).</summary>
    public float Time;
    /// <summary>The overlay's blend weight, 0 to 1.</summary>
    public float Weight = 1f;
    /// <summary>A clip this one took over from (a chained strike): its pose at the hand-over fades out over <see cref="BlendSeconds"/> so the overlay never pops.</summary>
    public WeaponPoseClip? From;
    public float FromTime, FromElapsed;
    /// <summary>How long a chained strike fades from the one before it (s; ours, about the stance cross-fade).</summary>
    public const float BlendSeconds = 0.14f;
    /// <summary>
    /// The install clip the shown clip was authored over (its <see cref="WeaponPoseClip.BaseClip"/>), laid under the keys at <see cref="BaseTime"/> while the viewer is posed with a clip (not in
    /// the stances), so the keys sit on the pose they were authored on whatever clip the game posed (in a match the spear's strikes keep the hammer's clips posed for their sound tags and timing,
    /// and show the hammer's idle under the keys, as the preview stage does). Null: the keys over the pose as it is (the stage, the carry over the stances).
    /// </summary>
    public string? Base;
    public float BaseTime;

    /// <summary>Shows <paramref name="clip"/> at <paramref name="time"/>; a change of clip keeps the old one's pose at its last time as the fade's start.</summary>
    public void Show(WeaponPoseClip? clip, float time)
    {
        if (!ReferenceEquals(clip, Clip))
        {
            if (Clip is not null) { From = Clip; FromTime = Time; FromElapsed = 0f; }
            Clip = clip;
        }
        Time = time;
    }

    /// <summary>
    /// Advances the hand-over fade from <see cref="From"/> by <paramref name="delta"/> seconds of real time (a negative delta counts as 0) and drops <see cref="From"/> once
    /// <see cref="BlendSeconds"/> have passed. The owner of the state calls it once a frame (never twice: the fade would run twice as fast), before or after <see cref="Show"/>;
    /// <see cref="Time"/> is not advanced (the owner sets it from its own clock: the stage's timeline, or the strike's drawn time in a match). The overlay reads the fade as <see cref="FromShare"/>.
    /// </summary>
    public void Advance(float delta)
    {
        if (From is null) return;
        FromElapsed += MathF.Max(0f, delta);
        if (FromElapsed >= BlendSeconds) From = null;
    }

    /// <summary>How much of <see cref="From"/>'s pose is still shown: 1 at the hand-over, 0 after <see cref="BlendSeconds"/>, on the smooth curve.</summary>
    public float FromShare => From is null ? 0f : 1f - CharacterViewer.Smooth(Mathf.Clamp(FromElapsed / BlendSeconds, 0f, 1f));
}

/// <summary>
/// The keyframed strike overlay (<see cref="WeaponPoseClip"/>), laid over the pose the stances, clips and layers gave, parents before children, each bone slerped from that pose by
/// the weight. A <see cref="PoseChannelKind.Turn"/> channel turns the bone about its origin (yaw about up, pitch about the body's right, roll about its front, in the stance's frame read
/// from the hips before anything is moved). An <see cref="PoseChannelKind.Aim"/> channel points the bone's segment (toward its child) at an elevation and azimuth in that frame by the
/// shortest turn from where the pose under it points, as far as the key's weight. A <see cref="PoseChannelKind.Move"/> channel shifts the bone's origin (the pelvis: drops, weight shifts).
/// The legs (third pass, owner 2026-11-10: "putting his entire body into the strokes", "add pelvis now"): whenever a clip is laid, each leg is solved (two bones, thigh and calf) so the
/// foot stands where the stance planted it plus its <see cref="PoseChannelKind.Foot"/> offset (a step lifts and replants it), the foot keeping the stance's orientation; so the pelvis and
/// the spine may move freely and a planted foot never slides. The legs are the core's (<c>ResolveLegs</c>: the marine's <c>RIG_L_Thigh</c> ..., the Ork's <c>RIG_LLeg_Thigh</c> ...). The root
/// is never moved. A chained strike fades from the previous clip's last pose (<see cref="WeaponPoseState.From"/>). Nothing is allocated per frame, and a viewer that never shows a clip
/// allocates nothing for it. Restore switch: <c>--no-weapon-pose</c> (the stance alone).
/// </summary>
public partial class CharacterViewer
{
    /// <summary>The Musou weapon strike shown on this character, or null (every other character).</summary>
    public WeaponPoseState? WeaponPose { get; set; }

    /// <summary>The stance's flat front and right (skeleton space) read from the hips before the overlay moved anything this frame; the stage faces its cameras by them.</summary>
    public Vector3 WeaponPoseBaseFront { get; private set; } = new(1f, 0f, 0f);
    public Vector3 WeaponPoseBaseRight { get; private set; } = new(0f, -1f, 0f);

    /// <summary><c>--no-weapon-pose</c>: no keyframed weapon clip anywhere (the stances alone); the networked view's spear posing reads it too (<c>SpearPosesOn</c>), so a match then shows W3's mounts alone.</summary>
    internal static readonly bool NoWeaponPose = OS.GetCmdlineUserArgs().Contains("--no-weapon-pose");

    // Lazy (review, 2026-11-10): made the first time this viewer lays a clip or is asked for a carry, so the viewers that never are (every Classic PvP character) carry none of it.
    Dictionary<string, int>? _weaponPoseBones;
    int[] _weaponOrder = Array.Empty<int>();
    Dictionary<WeaponPoseClip, Dictionary<int, List<PoseChannel>>>? _weaponChannels;
    HashSet<string>? _weaponPoseMissing;
    /// <summary>Where the stance plants each foot this frame and how it turns it (per core leg, <c>_legs</c>), read before the overlay moves the body.</summary>
    Vector3[]? _weaponPlantedAt;
    Quaternion[]? _weaponPlantedRot;
    int _weaponHandL = -2, _weaponHandR = -2;

    /// <summary>Bones a clip named that the rig lacks (noted once each).</summary>
    public IReadOnlySet<string> WeaponPoseMissingBones => _weaponPoseMissing ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clip the viewer is posed with now (clip mode: an attack, a jump, a reaction ...), or null in the stances; and its time (s). Musou's spear reads them (which clip its pose lies over).</summary>
    public string? PosedClip => _stanceMode ? null : _animation?.Name;
    public float PosedClipTime => _time;

    /// <summary>
    /// The bones' order for the overlay and the rigid carries, built once per rig: parents before children (sorted by depth), and within a depth by index, except that a bone carried with
    /// another (<see cref="RigidWith"/>) always comes right after its carrier, whatever their indices (review, 2026-11-10), so the carrier's move is laid on it before its own keys.
    /// </summary>
    void EnsureWeaponOrder(Skeleton3D s)
    {
        int n = s.GetBoneCount();
        if (_weaponOrder.Length == n) return;
        var depth = new int[n];
        for (int b = 0; b < n; b++) { int d = 0; for (int p = s.GetBoneParent(b); p >= 0; p = s.GetBoneParent(p)) d++; depth[b] = d; }
        var order = Enumerable.Range(0, n).OrderBy(b => depth[b]).ThenBy(b => b).ToList();
        _weaponCarried = new int[n];
        Array.Fill(_weaponCarried, -1);
        foreach (var (carrier, carried) in RigidWith)
        {
            int c = Bone(s, carrier), f = Bone(s, carried);
            if (c < 0 || f < 0) continue;
            if (s.GetBoneParent(c) != s.GetBoneParent(f)) { GD.Print($"weapon pose: {carried} is not a sibling of {carrier}; it is not carried with it"); continue; }
            _weaponCarried[c] = f;
            order.Remove(f);
            order.Insert(order.IndexOf(c) + 1, f);   // siblings share a depth, so the carried bone stays before every bone deeper than it
        }
        _weaponOrder = order.ToArray();
    }

    /// <summary>The bone carried rigidly with <paramref name="carrier"/> (<see cref="RigidWith"/>), or -1. Used by the overlay and by the Musou weight pass's settle (its pelvis drop).</summary>
    int RigidCarriedWith(Skeleton3D s, int carrier)
    {
        EnsureWeaponOrder(s);
        return (uint)carrier < (uint)_weaponCarried.Length ? _weaponCarried[carrier] : -1;
    }

    void WeaponPoseOverlay()
    {
        if (WeaponPose is not { } state || _skeleton is null || NoWeaponPose) return;
        if (state.Clip is null && state.From is null) return;
        float w = Mathf.Clamp(state.Weight, 0f, 1f);
        if (w <= 1e-3f) return;
        var s = _skeleton;
        EnsureWeaponOrder(s);
        WeaponPoseBaseLaid = null;
        if (state.Base is { } baseName && !_stanceMode && _assets?.Animation(baseName) is { } baseClip) { LayWeaponBase(s, baseClip, state.BaseTime, w); WeaponPoseBaseLaid = baseClip.Name; }
        ResolveLegs();
        var legs = _legs is { Length: 2 } two ? two : null;
        if (legs is not null && _weaponPlantedAt is null) { _weaponPlantedAt = new Vector3[2]; _weaponPlantedRot = new Quaternion[2]; }

        // The stance's frame from the hips (before anything is moved): its right from the left thigh to the right one (flat), its front square to that, up the skeleton's Z.
        Vector3 right = new(1f, 0f, 0f), front = new(0f, 1f, 0f), up = new(0f, 0f, 1f);
        int lt = legs?[0].Thigh ?? -1, rt = legs?[1].Thigh ?? -1;
        if (lt < 0 || rt < 0) { lt = Bone(s, "RIG_L_UpperArm"); rt = Bone(s, "RIG_R_UpperArm"); }
        if (lt >= 0 && rt >= 0)
        {
            var across = s.GetBoneGlobalPose(rt).Origin - s.GetBoneGlobalPose(lt).Origin;
            across.Z = 0f;
            if (across.LengthSquared() > 1e-6f) { right = across.Normalized(); front = up.Cross(right).Normalized(); }
        }
        WeaponPoseBaseFront = front; WeaponPoseBaseRight = right;
        // Where the stance plants the feet this frame, and how it turns them: the legs are solved back onto these after the body has moved.
        if (legs is not null)
            for (int i = 0; i < 2; i++)
            {
                var foot = s.GetBoneGlobalPose(legs[i].Foot);
                _weaponPlantedAt![i] = foot.Origin; _weaponPlantedRot![i] = foot.Basis.Orthonormalized().GetRotationQuaternion();
            }

        var now = state.Clip is { } clip ? ChannelsOf(s, clip) : null;
        float fromShare = state.FromShare;
        Dictionary<int, List<PoseChannel>>? before = null;
        if (state.From is { } from && fromShare > 1e-3f) before = ChannelsOf(s, from); else fromShare = 0f;
        // Each clip's own frame (WeaponPoseClip.FacingFrame: the way the player faces, the skeleton's +Y; else the stance's from the hips).
        var (nowRight, nowFront) = state.Clip?.FacingFrame == true ? (FacingRight, FacingFront) : (right, front);
        var (fromRight, fromFront) = state.From?.FacingFrame == true ? (FacingRight, FacingFront) : (right, front);
        int footL = legs?[0].Foot ?? -1, footR = legs?[1].Foot ?? -1;

        bool anyLaid = false;
        foreach (int b in _weaponOrder)
        {
            List<PoseChannel>? nowList = null, beforeList = null;
            bool any = (now?.TryGetValue(b, out nowList) ?? false) | (before?.TryGetValue(b, out beforeList) ?? false);
            if (!any) continue;
            if (b == footL || b == footR) continue;   // the feet are placed by the leg solve below
            anyLaid = true;
            // The turn, then the shift, each blended from the previous clip's.
            var turn = nowList is not null ? TurnOf(s, b, nowList, state.Time, nowRight, nowFront, up) : Quaternion.Identity;
            var shift = nowList is not null ? ShiftOf(nowList, state.Time, nowRight, nowFront, up) : Vector3.Zero;
            if (beforeList is not null && fromShare > 0f)
            {
                var old = TurnOf(s, b, beforeList, state.FromTime, fromRight, fromFront, up);
                if (old.Dot(turn) < 0f) old = -old;
                turn = turn.Slerp(old, fromShare).Normalized();
                shift = shift.Lerp(ShiftOf(beforeList, state.FromTime, fromRight, fromFront, up), fromShare);
            }
            var pivot = s.GetBoneGlobalPose(b).Origin;
            MoveBone(s, b, turn, shift, pivot, w);
            // The marine rig hangs the spine beside the pelvis under the root, not under it, so a pelvis moved or turned on its own left the torso behind and the legs came away from the body
            // (the owner's review of 2026-11-10). The carried bone takes the same turn about the carrier's origin and the same shift, as one rigid piece; its own channels are laid on top when
            // its turn comes (it is ordered right after its carrier, EnsureWeaponOrder).
            int carried = _weaponCarried[b];
            if (carried >= 0) MoveBone(s, carried, turn, shift, pivot, w);
        }
        if (anyLaid && legs is not null)
            for (int i = 0; i < 2; i++)
            {
                int foot = legs[i].Foot;
                var offset = Vector3.Zero;
                if (now is not null && now.TryGetValue(foot, out var footNow)) offset = ShiftOf(footNow, state.Time, nowRight, nowFront, up);
                if (before is not null && fromShare > 0f && before.TryGetValue(foot, out var footBefore)) offset = offset.Lerp(ShiftOf(footBefore, state.FromTime, fromRight, fromFront, up), fromShare);
                SolveWeaponLeg(s, legs[i], _weaponPlantedAt![i] + offset * w, _weaponPlantedRot![i], front);
            }
        _layersDirty = true;   // a held stance is applied again next frame, so the strike moves on
    }

    HavokTransform[]? _weaponBaseSample;
    /// <summary>Test aid: the base clip laid under the keys the last time the overlay ran (<see cref="WeaponPoseState.Base"/>), or null.</summary>
    public string? WeaponPoseBaseLaid { get; private set; }

    bool[]? _weaponBaseTracked;

    /// <summary>
    /// The base clip's pose at <paramref name="time"/>, as far as <paramref name="w"/> (<see cref="WeaponPoseState.Base"/>; the buffers are kept, nothing is allocated per frame). A bone the base clip
    /// has no track for goes to its rest pose, as on the stage, where nothing else poses it (in a match it would keep the posed strike clip's bend: the torso leant over in hew 3).
    /// </summary>
    void LayWeaponBase(Skeleton3D s, HavokBoundAnimation clip, float time, float w)
    {
        int tracks = clip.Animation.TrackCount, count = s.GetBoneCount();
        if (_weaponBaseSample is null || _weaponBaseSample.Length < tracks) _weaponBaseSample = new HavokTransform[tracks];
        if (_weaponBaseTracked is null || _weaponBaseTracked.Length != count) _weaponBaseTracked = new bool[count];
        Array.Clear(_weaponBaseTracked);
        clip.Animation.SampleInto(time, _weaponBaseSample);
        for (int t = 0; t < tracks; t++) if ((uint)clip.TrackToBone[t] < (uint)count) _weaponBaseTracked[clip.TrackToBone[t]] = true;
        for (int b = 0; b < count; b++)
        {
            if (_weaponBaseTracked[b]) continue;
            var rest = s.GetBoneRest(b);
            SetWeaponBaseBone(s, b, rest.Origin, rest.Basis.Orthonormalized().GetRotationQuaternion(), rest.Basis.Scale, w);
        }
        for (int t = 0; t < tracks; t++)
        {
            int bone = clip.TrackToBone[t];
            if ((uint)bone >= (uint)count) continue;
            var l = _weaponBaseSample[t];
            SetWeaponBaseBone(s, bone, new Vector3(l.Translation.X, l.Translation.Y, l.Translation.Z), new Quaternion(l.Rotation.X, l.Rotation.Y, l.Rotation.Z, l.Rotation.W).Normalized(),
                new Vector3(l.Scale.X, l.Scale.Y, l.Scale.Z), w);
        }
    }

    static void SetWeaponBaseBone(Skeleton3D s, int bone, Vector3 pos, Quaternion rot, Vector3 scale, float w)
    {
        if (w < 0.999f)
        {
            var cur = s.GetBonePoseRotation(bone);
            if (cur.Dot(rot) < 0f) rot = -rot;
            rot = cur.Slerp(rot, w).Normalized();
            pos = s.GetBonePosePosition(bone).Lerp(pos, w);
            scale = s.GetBonePoseScale(bone).Lerp(scale, w);
        }
        s.SetBonePosePosition(bone, pos);
        s.SetBonePoseRotation(bone, rot);
        s.SetBonePoseScale(bone, scale);
    }

    /// <summary>
    /// The flat front of the body now (skeleton space), from the thighs as the pose stands at the call (after the overlay, so a pelvis turn turns it): the spear's carry and blade edge
    /// (<see cref="SpearHold.Place"/>). The skeleton's +X when the rig has no legs.
    /// </summary>
    /// <summary>The facing frame of <see cref="WeaponPoseClip.FacingFrame"/> in skeleton space (+Y front, +X right).</summary>
    static readonly Vector3 FacingFront = new(0f, 1f, 0f), FacingRight = new(1f, 0f, 0f);

    /// <summary>
    /// The front the spear is placed against (<see cref="SpearHold.Place"/>): the way the player faces, the skeleton's +Y (second follow-up pass, 2026-11-11: the carry's lean and the edge are read
    /// against the facing in every clip, so a strike begun from the carry starts with the shaft where the carry holds it, and the swung edge faces the target ahead; before, the hips' front).
    /// </summary>
    public Vector3 WeaponPoseSpearFront() => FacingFront;

    public Vector3 WeaponPoseFrontNow()
    {
        if (_skeleton is null) return new Vector3(1f, 0f, 0f);
        ResolveLegs();
        if (_legs is not { Length: 2 } legs) return new Vector3(1f, 0f, 0f);
        var across = _skeleton.GetBoneGlobalPose(legs[1].Thigh).Origin - _skeleton.GetBoneGlobalPose(legs[0].Thigh).Origin;
        across.Z = 0f;
        return across.LengthSquared() > 1e-6f ? new Vector3(0f, 0f, 1f).Cross(across.Normalized()).Normalized() : new Vector3(1f, 0f, 0f);
    }

    /// <summary>
    /// Where the hands grip (skeleton space) as posed now: the rig's own grip sockets, <c>RIG_Prop_RHand</c> and <c>RIG_Prop_LHand</c> (the hands' children every held weapon's handle sits on,
    /// in the palm), else the hands' origins (<c>RIG_R_Hand</c>, <c>RIG_L_Hand</c>, the wrists); false when the rig lacks them. The spear's shaft runs through these points
    /// (<see cref="SpearHold.Place"/>; owner, 2026-11-11: "the spear is always attached to the inside palm of the hand"; before, through the wrists, about 0.1 m off the palm).
    /// </summary>
    public bool WeaponPoseHands(out Vector3 rightHand, out Vector3 leftHand)
    {
        rightHand = leftHand = default;
        if (_skeleton is null) return false;
        if (_weaponHandR == -2)
        {
            _weaponHandR = _skeleton.FindBone(SpearHold.RightGripBone); _weaponHandL = _skeleton.FindBone(SpearHold.LeftGripBone);
            if (_weaponHandR < 0 || _weaponHandL < 0) { _weaponHandR = _skeleton.FindBone("RIG_R_Hand"); _weaponHandL = _skeleton.FindBone("RIG_L_Hand"); }
        }
        if (_weaponHandR < 0 || _weaponHandL < 0) return false;
        rightHand = _skeleton.GetBoneGlobalPose(_weaponHandR).Origin;
        leftHand = _skeleton.GetBoneGlobalPose(_weaponHandL).Origin;
        return true;
    }

    /// <summary>
    /// Bones that move as one rigid piece with another bone that is not their parent: the marine's <c>RIG_SpineA</c> hangs under <c>RIG_Root</c> beside <c>RIG_Pelvis</c>, so the torso must be
    /// carried along whenever the pelvis is turned or shifted, or the legs come away from the body.
    /// </summary>
    static readonly (string Carrier, string Carried)[] RigidWith = [("RIG_Pelvis", "RIG_SpineA")];
    int[] _weaponCarried = Array.Empty<int>();

    /// <summary>
    /// Turns <paramref name="bone"/> by the skeleton-space rotation <paramref name="turn"/> about <paramref name="pivot"/> and shifts it by <paramref name="shift"/>, as far as <paramref name="w"/>; a
    /// pivot at the bone's own origin leaves its origin where it was (its own turn), another pivot carries it round that point.
    /// </summary>
    static void MoveBone(Skeleton3D s, int bone, Quaternion turn, Vector3 shift, Vector3 pivot, float w)
    {
        int parent = s.GetBoneParent(bone);
        var parentPose = parent >= 0 ? s.GetBoneGlobalPose(parent) : Transform3D.Identity;
        var parentRot = parentPose.Basis.Orthonormalized().GetRotationQuaternion();
        var origin = s.GetBoneGlobalPose(bone).Origin;
        bool turns = MathF.Abs(turn.W) <= 0.9999995f;
        if (turns)
        {
            var local = s.GetBonePoseRotation(bone);
            var target = (parentRot.Inverse() * turn * parentRot * local).Normalized();
            if (local.Dot(target) < 0f) target = -target;
            s.SetBonePoseRotation(bone, w >= 0.999f ? target : local.Slerp(target, w).Normalized());
        }
        var moved = (turns ? pivot + turn * (origin - pivot) : origin) + shift;
        if (moved.DistanceSquaredTo(origin) > 1e-12f)
        {
            var delta = (moved - origin) * w;
            var parentScale = parentPose.Basis.Scale;
            var localShift = parentRot.Inverse() * delta;
            localShift = new Vector3(localShift.X / MathF.Max(parentScale.X, 1e-4f), localShift.Y / MathF.Max(parentScale.Y, 1e-4f), localShift.Z / MathF.Max(parentScale.Z, 1e-4f));
            s.SetBonePosePosition(bone, s.GetBonePosePosition(bone) + localShift);
        }
    }

    /// <summary>
    /// Two-bone solve of a leg so the foot's origin reaches <paramref name="target"/> (skeleton space): the thigh aims at the new knee, the calf at the target, the knee bending the way it
    /// already bends (else forward), the foot keeping the orientation the stance gave it (<paramref name="plantedRot"/>). A target out of reach straightens the leg toward it (the foot then stops
    /// short on the line). The core's <c>SolveLeg</c> differs in the straight leg's bend (forward here) and in leaving the foot to the caller.
    /// </summary>
    void SolveWeaponLeg(Skeleton3D s, Leg leg, Vector3 target, Quaternion plantedRot, Vector3 front)
    {
        var hipG = s.GetBoneGlobalPose(leg.Thigh); var kneeG = s.GetBoneGlobalPose(leg.Calf); var footG = s.GetBoneGlobalPose(leg.Foot);
        var H = hipG.Origin; var K = kneeG.Origin; var A = footG.Origin;
        float a = H.DistanceTo(K), b = K.DistanceTo(A);
        if (a < 1e-3f || b < 1e-3f) return;
        if (A.DistanceSquaredTo(target) < 1e-8f) return;
        var toT = target - H;
        float dist = toT.Length();
        if (dist < 1e-4f) return;
        var dir = toT / dist;
        float d = Mathf.Clamp(dist, MathF.Abs(a - b) + 1e-3f, (a + b) * 0.995f);
        // The knee's bend direction: where the knee sits off the hip-to-foot line now; forward when the leg is straight.
        var along = (A - H);
        var pole = along.LengthSquared() > 1e-8f ? K - H - along.Normalized() * (K - H).Dot(along.Normalized()) : Vector3.Zero;
        if (pole.LengthSquared() < 1e-6f) pole = front;
        pole -= dir * pole.Dot(dir);
        if (pole.LengthSquared() < 1e-8f) pole = new Vector3(0f, 0f, 1f).Cross(dir);
        pole = pole.Normalized();
        float x = (a * a - b * b + d * d) / (2f * d);
        float h = MathF.Sqrt(MathF.Max(0f, a * a - x * x));
        var knee = H + dir * x + pole * h;
        var foot = H + dir * d;

        int hipParent = s.GetBoneParent(leg.Thigh);
        var hipParentRot = hipParent >= 0 ? s.GetBoneGlobalPose(hipParent).Basis.Orthonormalized().GetRotationQuaternion() : Quaternion.Identity;
        var thighRot = hipG.Basis.Orthonormalized().GetRotationQuaternion();
        var thighNew = (ArcBetween((K - H).Normalized(), (knee - H).Normalized()) * thighRot).Normalized();
        s.SetBonePoseRotation(leg.Thigh, (hipParentRot.Inverse() * thighNew).Normalized());
        // The calf's global rotation after the thigh turned: the same relative rotation on the new thigh.
        var calfRel = (thighRot.Inverse() * kneeG.Basis.Orthonormalized().GetRotationQuaternion()).Normalized();
        var calfRot = (thighNew * calfRel).Normalized();
        var footRel = (kneeG.Basis.Orthonormalized().GetRotationQuaternion().Inverse() * footG.Basis.Orthonormalized().GetRotationQuaternion()).Normalized();
        var footNow = knee + calfRot * (kneeG.Basis.Orthonormalized().GetRotationQuaternion().Inverse() * (A - K));
        var calfNew = (ArcBetween((footNow - knee).Normalized(), (foot - knee).Normalized()) * calfRot).Normalized();
        s.SetBonePoseRotation(leg.Calf, (thighNew.Inverse() * calfNew).Normalized());
        // The foot keeps the stance's orientation (the sole stays as it was), whatever the leg did.
        s.SetBonePoseRotation(leg.Foot, (calfNew.Inverse() * plantedRot).Normalized());
    }

    int Bone(Skeleton3D s, string name)
    {
        _weaponPoseBones ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!_weaponPoseBones.TryGetValue(name, out int b)) { b = s.FindBone(name); _weaponPoseBones[name] = b; }
        return b;
    }

    /// <summary>A clip's channels by bone index (missing bones noted once and left out).</summary>
    Dictionary<int, List<PoseChannel>> ChannelsOf(Skeleton3D s, WeaponPoseClip clip)
    {
        _weaponChannels ??= new Dictionary<WeaponPoseClip, Dictionary<int, List<PoseChannel>>>(ReferenceEqualityComparer.Instance);
        if (_weaponChannels.TryGetValue(clip, out var map)) return map;
        map = new Dictionary<int, List<PoseChannel>>();
        foreach (var ch in clip.Channels)
        {
            int b = Bone(s, ch.Bone);
            if (b < 0 || (ch.Kind == PoseChannelKind.Aim && Bone(s, ch.Toward ?? "") < 0))
            {
                if ((_weaponPoseMissing ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(b < 0 ? ch.Bone : ch.Toward ?? "")) GD.Print($"weapon pose: the rig has no bone '{(b < 0 ? ch.Bone : ch.Toward)}' ({clip.Name})");
                continue;
            }
            if (!map.TryGetValue(b, out var list)) map[b] = list = new List<PoseChannel>();
            list.Add(ch);
        }
        _weaponChannels[clip] = map;
        return map;
    }

    /// <summary>The skeleton-space rotation a bone's turn and aim channels ask for at <paramref name="t"/>, from the pose it has now (its parents already turned).</summary>
    Quaternion TurnOf(Skeleton3D s, int bone, List<PoseChannel> channels, float t, Vector3 right, Vector3 front, Vector3 up)
    {
        const float deg = MathF.PI / 180f;
        var q = Quaternion.Identity;
        foreach (var ch in channels)
        {
            if (ch.Kind is PoseChannelKind.Move or PoseChannelKind.Foot) continue;
            var k = ch.Sample(t);
            if (ch.Kind == PoseChannelKind.Turn)
            {
                if (k.IsZero) continue;
                q = (new Quaternion(up, k.Yaw * deg) * new Quaternion(right, k.Pitch * deg) * new Quaternion(front, k.Roll * deg) * q).Normalized();
                continue;
            }
            float weight = Mathf.Clamp(k.Weight, 0f, 1f);
            if (weight <= 1e-4f) continue;
            int child = Bone(s, ch.Toward!);
            var from = s.GetBoneGlobalPose(bone).Origin;
            var dir = (new Basis(q) * (s.GetBoneGlobalPose(child).Origin - from));
            if (dir.LengthSquared() < 1e-8f) continue;
            dir = dir.Normalized();
            float el = k.Elevation * deg, az = k.Azimuth * deg;
            var want = (front * (MathF.Cos(el) * MathF.Cos(az)) + right * (MathF.Cos(el) * MathF.Sin(az)) + up * MathF.Sin(el)).Normalized();
            var arc = ArcBetween(dir, want);
            if (weight < 1f) arc = Quaternion.Identity.Slerp(arc, weight).Normalized();
            q = (arc * q).Normalized();
        }
        return q;
    }

    /// <summary>The skeleton-space offset a bone's move or foot channels ask for at <paramref name="t"/> (metres).</summary>
    static Vector3 ShiftOf(List<PoseChannel> channels, float t, Vector3 right, Vector3 front, Vector3 up)
    {
        var shift = Vector3.Zero;
        foreach (var ch in channels)
        {
            if (ch.Kind is not (PoseChannelKind.Move or PoseChannelKind.Foot)) continue;
            var k = ch.Sample(t);
            float weight = Mathf.Clamp(k.Weight, 0f, 1f);
            if (weight <= 1e-4f || k.IsZero) continue;
            shift += (front * k.Forward + right * k.Right + up * k.Up) * weight;
        }
        return shift;
    }

    /// <summary>The shortest rotation taking unit vector <paramref name="from"/> to <paramref name="to"/>.</summary>
    static Quaternion ArcBetween(Vector3 from, Vector3 to)
    {
        float d = Mathf.Clamp(from.Dot(to), -1f, 1f);
        if (d > 0.99999f) return Quaternion.Identity;
        var axis = from.Cross(to);
        if (axis.LengthSquared() < 1e-10f) axis = MathF.Abs(from.X) < 0.9f ? from.Cross(new Vector3(1f, 0f, 0f)) : from.Cross(new Vector3(0f, 1f, 0f));
        return new Quaternion(axis.Normalized(), MathF.Acos(d));
    }
}
