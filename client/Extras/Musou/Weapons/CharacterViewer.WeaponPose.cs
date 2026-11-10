using System;
using System.Collections.Generic;
using Godot;
using SmContent.Extras.Musou;

namespace SmClient;

/// <summary>
/// What a character shows of a Musou weapon's keyframed strike this frame (added content, Musou Mode only; <c>docs/claw-spear-animations.md</c>). Set by the owner of the character
/// (the preview stage now; the networked view once the weapons are simulated), read by <see cref="CharacterViewer.ExtrasPoseOverlay"/>. Null on every other character.
/// </summary>
public sealed class WeaponPoseState
{
    /// <summary>The clip shown, or null (the stance alone).</summary>
    public WeaponPoseClip? Clip;
    /// <summary>Seconds into <see cref="Clip"/> (the simulation's attack time once the weapons are simulated).</summary>
    public float Time;
    /// <summary>The overlay's blend weight, 0 to 1.</summary>
    public float Weight = 1f;
    /// <summary>A clip this one took over from (a chained strike): its pose at the hand-over fades out over <see cref="BlendSeconds"/> so the overlay never pops.</summary>
    public WeaponPoseClip? From;
    public float FromTime, FromElapsed;
    /// <summary>How long a chained strike fades from the one before it (s; ours, about the stance cross-fade).</summary>
    public const float BlendSeconds = 0.14f;

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

    /// <summary>Advances the hand-over fade.</summary>
    public void Advance(float delta)
    {
        if (From is null) return;
        FromElapsed += MathF.Max(0f, delta);
        if (FromElapsed >= BlendSeconds) From = null;
    }
}

/// <summary>
/// The keyframed strike overlay (<see cref="WeaponPoseClip"/>), laid over the pose the stances, clips and layers gave, parents before children, each bone slerped from that pose by
/// the weight. A <see cref="PoseChannelKind.Turn"/> channel turns the bone about its origin (yaw about up, pitch about the body's right, roll about its front, in the stance's frame read
/// from the hips before anything is moved). An <see cref="PoseChannelKind.Aim"/> channel points the bone's segment (toward its child) at an elevation and azimuth in that frame by the
/// shortest turn from where the pose under it points, as far as the key's weight. A <see cref="PoseChannelKind.Move"/> channel shifts the bone's origin (the pelvis: drops, weight shifts).
/// The legs (third pass, owner 2026-11-10: "putting his entire body into the strokes", "add pelvis now"): whenever a clip is laid, each leg is solved (two bones, thigh and calf) so the
/// foot stands where the stance planted it plus its <see cref="PoseChannelKind.Foot"/> offset (a step lifts and replants it), the foot keeping the stance's orientation; so the pelvis and
/// the spine may move freely and a planted foot never slides. The root is never moved. A chained strike fades from the previous clip's last pose (<see cref="WeaponPoseState.From"/>).
/// Restore switch: <c>--no-weapon-pose</c> (the stance alone).
/// </summary>
public partial class CharacterViewer
{
    /// <summary>The Musou weapon strike shown on this character, or null (every other character).</summary>
    public WeaponPoseState? WeaponPose { get; set; }

    /// <summary>The stance's flat front and right (skeleton space) read from the hips before the overlay moved anything this frame; the stage faces its cameras by them.</summary>
    public Vector3 WeaponPoseBaseFront { get; private set; } = new(1f, 0f, 0f);
    public Vector3 WeaponPoseBaseRight { get; private set; } = new(0f, -1f, 0f);

    static readonly bool NoWeaponPose = OS.GetCmdlineUserArgs().Contains("--no-weapon-pose");

    Dictionary<string, int>? _weaponPoseBones;
    int[] _weaponOrder = Array.Empty<int>();
    readonly Dictionary<WeaponPoseClip, Dictionary<int, List<PoseChannel>>> _weaponChannels = new(ReferenceEqualityComparer.Instance);
    /// <summary>Bones a clip named that the rig lacks (noted once each).</summary>
    public HashSet<string> WeaponPoseMissingBones { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One leg for the solve: its bones and the foot's place and orientation in the stance this frame.</summary>
    sealed class LegChain
    {
        public int Thigh = -1, Calf = -1, Foot = -1;
        public Vector3 PlantedAt;
        public Quaternion PlantedRot;
        public bool Ok => Thigh >= 0 && Calf >= 0 && Foot >= 0;
    }
    readonly LegChain _legL = new(), _legR = new();
    bool _weaponLegsResolved;

    void WeaponPoseOverlay()
    {
        if (WeaponPose is not { } state || _skeleton is null || NoWeaponPose) return;
        if (state.Clip is null && state.From is null) return;
        float w = Math.Clamp(state.Weight, 0f, 1f);
        if (w <= 1e-3f) return;
        var s = _skeleton;
        int n = s.GetBoneCount();
        if (_weaponOrder.Length != n)
        {
            _weaponOrder = new int[n];
            // Parents before children: the skeleton's bones are stored with parents first (docs/discovery.md), but sort by depth to be sure.
            var depth = new int[n];
            for (int b = 0; b < n; b++) { int d = 0; for (int p = s.GetBoneParent(b); p >= 0; p = s.GetBoneParent(p)) d++; depth[b] = d; }
            for (int b = 0; b < n; b++) _weaponOrder[b] = b;
            Array.Sort(depth, _weaponOrder);
        }
        _weaponPoseBones ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!_weaponLegsResolved)
        {
            _weaponLegsResolved = true;
            foreach (var (leg, side) in new[] { (_legL, "L"), (_legR, "R") })
            { leg.Thigh = Bone(s, $"RIG_{side}_Thigh"); leg.Calf = Bone(s, $"RIG_{side}_Calf"); leg.Foot = Bone(s, $"RIG_{side}_Foot"); }
        }

        // The stance's frame from the hips (before anything is moved): its right from the left thigh to the right one (flat), its front square to that, up the skeleton's Z.
        Vector3 right = new(1f, 0f, 0f), front = new(0f, 1f, 0f), up = new(0f, 0f, 1f);
        int lt = Bone(s, "RIG_L_Thigh"), rt = Bone(s, "RIG_R_Thigh");
        if (lt < 0 || rt < 0) { lt = Bone(s, "RIG_L_UpperArm"); rt = Bone(s, "RIG_R_UpperArm"); }
        if (lt >= 0 && rt >= 0)
        {
            var across = s.GetBoneGlobalPose(rt).Origin - s.GetBoneGlobalPose(lt).Origin;
            across.Z = 0f;
            if (across.LengthSquared() > 1e-6f) { right = across.Normalized(); front = up.Cross(right).Normalized(); }
        }
        WeaponPoseBaseFront = front; WeaponPoseBaseRight = right;
        // Where the stance plants the feet this frame, and how it turns them: the legs are solved back onto these after the body has moved.
        foreach (var leg in new[] { _legL, _legR })
        {
            if (!leg.Ok) continue;
            var foot = s.GetBoneGlobalPose(leg.Foot);
            leg.PlantedAt = foot.Origin; leg.PlantedRot = foot.Basis.Orthonormalized().GetRotationQuaternion();
        }

        var now = state.Clip is { } clip ? ChannelsOf(s, clip) : null;
        float fromShare = 0f;
        Dictionary<int, List<PoseChannel>>? before = null;
        if (state.From is { } from)
        {
            fromShare = 1f - Smooth(Math.Clamp(state.FromElapsed / WeaponPoseState.BlendSeconds, 0f, 1f));
            if (fromShare > 1e-3f) before = ChannelsOf(s, from); else fromShare = 0f;
        }

        bool anyLaid = false;
        foreach (int b in _weaponOrder)
        {
            List<PoseChannel>? nowList = null, beforeList = null;
            bool any = (now?.TryGetValue(b, out nowList) ?? false) | (before?.TryGetValue(b, out beforeList) ?? false);
            if (!any) continue;
            if (b == _legL.Foot || b == _legR.Foot) continue;   // the feet are placed by the leg solve below
            anyLaid = true;
            // The turn, then the shift, each blended from the previous clip's.
            var turn = nowList is not null ? TurnOf(s, b, nowList, state.Time, right, front, up) : Quaternion.Identity;
            var shift = nowList is not null ? ShiftOf(nowList, state.Time, right, front, up) : Vector3.Zero;
            if (beforeList is not null && fromShare > 0f)
            {
                var old = TurnOf(s, b, beforeList, state.FromTime, right, front, up);
                if (old.Dot(turn) < 0f) old = -old;
                turn = turn.Slerp(old, fromShare).Normalized();
                shift = shift.Lerp(ShiftOf(beforeList, state.FromTime, right, front, up), fromShare);
            }
            int parent = s.GetBoneParent(b);
            var parentRot = parent >= 0 ? s.GetBoneGlobalPose(parent).Basis.Orthonormalized().GetRotationQuaternion() : Quaternion.Identity;
            if (MathF.Abs(turn.W) <= 0.9999995f)
            {
                var local = s.GetBonePoseRotation(b);
                var target = (parentRot.Inverse() * turn * parentRot * local).Normalized();
                if (local.Dot(target) < 0f) target = -target;
                s.SetBonePoseRotation(b, w >= 0.999f ? target : local.Slerp(target, w).Normalized());
            }
            if (shift.LengthSquared() > 1e-10f)
            {
                var parentScale = parent >= 0 ? s.GetBoneGlobalPose(parent).Basis.Scale : Vector3.One;
                var localShift = parentRot.Inverse() * shift;
                localShift = new Vector3(localShift.X / MathF.Max(parentScale.X, 1e-4f), localShift.Y / MathF.Max(parentScale.Y, 1e-4f), localShift.Z / MathF.Max(parentScale.Z, 1e-4f));
                s.SetBonePosePosition(b, s.GetBonePosePosition(b) + localShift * w);
            }
        }
        if (anyLaid)
            foreach (var leg in new[] { _legL, _legR })
            {
                if (!leg.Ok) continue;
                var offset = Vector3.Zero;
                if (now is not null && now.TryGetValue(leg.Foot, out var footNow)) offset = ShiftOf(footNow, state.Time, right, front, up);
                if (before is not null && fromShare > 0f && before.TryGetValue(leg.Foot, out var footBefore)) offset = offset.Lerp(ShiftOf(footBefore, state.FromTime, right, front, up), fromShare);
                SolveLeg(s, leg, leg.PlantedAt + offset * w, front);
            }
        _layersDirty = true;   // a held stance is applied again next frame, so the strike moves on
    }

    /// <summary>
    /// Two-bone solve of a leg so the foot's origin reaches <paramref name="target"/> (skeleton space): the thigh aims at the new knee, the calf at the target, the knee bending the way it
    /// already bends (else forward), the foot keeping the orientation the stance gave it. A target out of reach straightens the leg toward it (the foot then stops short on the line).
    /// </summary>
    void SolveLeg(Skeleton3D s, LegChain leg, Vector3 target, Vector3 front)
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
        float d = Math.Clamp(dist, MathF.Abs(a - b) + 1e-3f, (a + b) * 0.995f);
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
        s.SetBonePoseRotation(leg.Foot, (calfNew.Inverse() * leg.PlantedRot).Normalized());
    }

    int Bone(Skeleton3D s, string name)
    {
        if (!_weaponPoseBones!.TryGetValue(name, out int b)) { b = s.FindBone(name); _weaponPoseBones[name] = b; }
        return b;
    }

    /// <summary>A clip's channels by bone index (missing bones noted once and left out).</summary>
    Dictionary<int, List<PoseChannel>> ChannelsOf(Skeleton3D s, WeaponPoseClip clip)
    {
        if (_weaponChannels.TryGetValue(clip, out var map)) return map;
        map = new Dictionary<int, List<PoseChannel>>();
        foreach (var ch in clip.Channels)
        {
            int b = Bone(s, ch.Bone);
            if (b < 0 || (ch.Kind == PoseChannelKind.Aim && Bone(s, ch.Toward ?? "") < 0))
            {
                if (WeaponPoseMissingBones.Add(b < 0 ? ch.Bone : ch.Toward ?? "")) GD.Print($"weapon pose: the rig has no bone '{(b < 0 ? ch.Bone : ch.Toward)}' ({clip.Name})");
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
            float weight = Math.Clamp(k.Weight, 0f, 1f);
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
            float weight = Math.Clamp(k.Weight, 0f, 1f);
            if (weight <= 1e-4f || k.IsZero) continue;
            shift += (front * k.Forward + right * k.Right + up * k.Up) * weight;
        }
        return shift;
    }

    /// <summary>The shortest rotation taking unit vector <paramref name="from"/> to <paramref name="to"/>.</summary>
    static Quaternion ArcBetween(Vector3 from, Vector3 to)
    {
        float d = Math.Clamp(from.Dot(to), -1f, 1f);
        if (d > 0.99999f) return Quaternion.Identity;
        var axis = from.Cross(to);
        if (axis.LengthSquared() < 1e-10f) axis = MathF.Abs(from.X) < 0.9f ? from.Cross(new Vector3(1f, 0f, 0f)) : from.Cross(new Vector3(0f, 1f, 0f));
        return new Quaternion(axis.Normalized(), MathF.Acos(d));
    }
}
