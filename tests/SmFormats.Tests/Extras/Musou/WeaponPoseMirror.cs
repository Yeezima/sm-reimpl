using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SmContent.Extras.Musou;
using SmFormats.Havok;

namespace SmFormats.Tests;

/// <summary>
/// A Godot-free mirror of the client's keyframed weapon overlay (<c>client/Extras/Musou/Weapons/CharacterViewer.WeaponPose.cs</c>, <c>WeaponPoseOverlay</c>) on the install's rig, for the
/// spear's clearance test: the base clip laid under the keys (untracked bones at rest), the stance's frame read from the thighs, every bone's turn and aim channels composed and laid parents
/// first with the pelvis carrying the spine as one piece, the shifts, the hand-over fade from the previous clip, and the two-bone leg solve onto the planted feet. The same arithmetic in
/// System.Numerics (Godot's and System.Numerics' quaternions compose and rotate alike: <c>a * b</c> applies <c>b</c> first). Keep it in step with the client's overlay; the preview stage's
/// <c>--clearance</c> prints the same measurement from the client's own overlay, which is how the two were compared (2026-11-10).
/// </summary>
sealed class WeaponPoseMirror
{
    readonly HavokSkeleton _sk;
    readonly int _n;
    readonly Vector3[] _pos, _scl;
    readonly Quaternion[] _rot;
    readonly Vector3[] _gpos, _gscl;
    readonly Quaternion[] _grot;
    bool _dirty = true;
    readonly int[] _order, _carried;
    readonly Dictionary<string, int> _index;
    readonly int _lThigh, _lCalf, _lFoot, _rThigh, _rCalf, _rFoot, _lHand, _rHand;

    public WeaponPoseMirror(HavokSkeleton skeleton)
    {
        _sk = skeleton; _n = skeleton.BoneNames.Count;
        _pos = new Vector3[_n]; _scl = new Vector3[_n]; _rot = new Quaternion[_n];
        _gpos = new Vector3[_n]; _gscl = new Vector3[_n]; _grot = new Quaternion[_n];
        _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _n; i++) _index[skeleton.BoneNames[i]] = i;
        // The overlay's order (EnsureWeaponOrder): by depth, then index; a carried bone right after its carrier.
        var depth = new int[_n];
        for (int b = 0; b < _n; b++) { int d = 0; for (int p = _sk.Parents[b]; p >= 0; p = _sk.Parents[p]) d++; depth[b] = d; }
        var order = Enumerable.Range(0, _n).OrderBy(b => depth[b]).ThenBy(b => b).ToList();
        _carried = Enumerable.Repeat(-1, _n).ToArray();
        int carrier = Bone("RIG_Pelvis"), carried = Bone("RIG_SpineA");
        if (carrier >= 0 && carried >= 0 && _sk.Parents[carrier] == _sk.Parents[carried])
        {
            _carried[carrier] = carried;
            order.Remove(carried);
            order.Insert(order.IndexOf(carrier) + 1, carried);
        }
        _order = order.ToArray();
        _lThigh = Bone("RIG_L_Thigh"); _lCalf = Bone("RIG_L_Calf"); _lFoot = Bone("RIG_L_Foot");
        _rThigh = Bone("RIG_R_Thigh"); _rCalf = Bone("RIG_R_Calf"); _rFoot = Bone("RIG_R_Foot");
        // The hands' grip sockets (SpearHold.RightGripBone / LeftGripBone, in the palms: the client's WeaponPoseHands), else the hands' origins.
        _lHand = Bone(SpearHold.LeftGripBone); _rHand = Bone(SpearHold.RightGripBone);
        if (_lHand < 0 || _rHand < 0) { _lHand = Bone("RIG_L_Hand"); _rHand = Bone("RIG_R_Hand"); }
    }

    public int Bone(string name) => _index.TryGetValue(name, out int b) ? b : -1;

    /// <summary>The client's <c>WeaponPoseState.FistWeight</c>: how far the carry's fist solve is laid (0 standing, 1 walking, running, sprinting).</summary>
    public float FistWeight;

    /// <summary>Sets every bone to its rest (the skeleton's reference pose).</summary>
    public void Rest()
    {
        for (int b = 0; b < _n; b++) { var r = _sk.ReferencePose[b]; _pos[b] = r.Translation; _rot[b] = Quaternion.Normalize(r.Rotation); _scl[b] = r.Scale; }
        _dirty = true;
    }

    /// <summary>Lays <paramref name="clip"/> at <paramref name="time"/> as the pose (the stances; a bone the clip has no track for at its rest, as <c>LayWeaponBase</c> does).</summary>
    public void Lay(HavokBoundAnimation clip, float time)
    {
        Rest();
        var sample = clip.Animation.Sample(time);
        for (int t = 0; t < sample.Length; t++)
        {
            int b = clip.TrackToBone[t];
            if ((uint)b >= (uint)_n) continue;
            _pos[b] = sample[t].Translation; _rot[b] = Quaternion.Normalize(sample[t].Rotation); _scl[b] = sample[t].Scale;
        }
        _dirty = true;
    }

    void Update()
    {
        if (!_dirty) return;
        for (int b = 0; b < _n; b++)
        {
            int p = _sk.Parents[b];
            if (p < 0) { _gpos[b] = _pos[b]; _grot[b] = _rot[b]; _gscl[b] = _scl[b]; continue; }
            _gpos[b] = _gpos[p] + Vector3.Transform(_gscl[p] * _pos[b], _grot[p]);
            _grot[b] = Quaternion.Normalize(_grot[p] * _rot[b]);
            _gscl[b] = _gscl[p] * _scl[b];
        }
        _dirty = false;
    }

    public Vector3 Origin(int b) { Update(); return _gpos[b]; }
    public Quaternion Rotation(int b) { Update(); return _grot[b]; }
    public (Vector3, Quaternion)? Pose(string bone) { int b = Bone(bone); if (b < 0) return null; Update(); return (_gpos[b], _grot[b]); }
    public Vector3 RightHand => Origin(_rHand);
    public Vector3 LeftHand => Origin(_lHand);

    /// <summary>The front the spear is placed against with <paramref name="clip"/> shown (<c>WeaponPoseSpearFront</c>): the facing for a clip read in it (the carry), else <see cref="FrontNow"/>.</summary>
    public Vector3 SpearFront(WeaponPoseClip clip) => WeaponPoseClip.FacingFront;

    /// <summary>The flat front from the thighs as the pose stands now (the stance's frame, as the overlay reads it before it moves anything).</summary>
    public Vector3 FrontNow()
    {
        var across = Origin(_rThigh) - Origin(_lThigh); across.Z = 0f;
        return across.LengthSquared() > 1e-6f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, Vector3.Normalize(across))) : Vector3.UnitX;
    }

    /// <summary>
    /// The overlay (<c>WeaponPoseOverlay</c>) over the pose as it stands: <paramref name="clip"/> at <paramref name="time"/>, faded from <paramref name="from"/> at <paramref name="fromTime"/> by
    /// <paramref name="fromShare"/> (1 at the hand-over, 0 after it), at weight <paramref name="weight"/>.
    /// </summary>
    public void Overlay(WeaponPoseClip? clip, float time, WeaponPoseClip? from = null, float fromTime = 0f, float fromShare = 0f, float weight = 1f) => Overlay(clip, time, from, fromTime, fromShare, weight, null, Matrix4x4.Identity, 0f, false);

    /// <summary>
    /// The overlay with the client's world-space foot pin (<see cref="FootPin"/>, the networked view's spear strikes: <c>WeaponPoseState.PinFeet</c>): each leg's target goes through
    /// <paramref name="pin"/> with the skeleton's <paramref name="toWorld"/> this frame, as <c>WeaponPoseOverlay</c> does.
    /// </summary>
    public void Overlay(WeaponPoseClip? clip, float time, WeaponPoseClip? from, float fromTime, float fromShare, float weight, FootPin? pin, Matrix4x4 toWorld, float dt, bool pinActive)
    {
        float w = Math.Clamp(weight, 0f, 1f);
        if (w <= 1e-3f || (clip is null && from is null)) return;
        Vector3 right = Vector3.UnitX, front = Vector3.UnitY, up = Vector3.UnitZ;
        var across = Origin(_rThigh) - Origin(_lThigh); across.Z = 0f;
        if (across.LengthSquared() > 1e-6f) { right = Vector3.Normalize(across); front = Vector3.Normalize(Vector3.Cross(up, right)); }
        var plantedAt = new[] { Origin(_lFoot), Origin(_rFoot) };
        var plantedRot = new[] { Rotation(_lFoot), Rotation(_rFoot) };
        var now = clip is null ? null : ChannelsOf(clip);
        Dictionary<int, List<PoseChannel>>? before = from is not null && fromShare > 1e-3f ? ChannelsOf(from) : null;
        if (before is null) fromShare = 0f;
        // Each clip's own frame (WeaponPoseClip.FacingFrame: the skeleton's +Y and +X; else the stance's).
        var (nowRight, nowFront) = clip?.FacingFrame == true ? (WeaponPoseClip.FacingRight, WeaponPoseClip.FacingFront) : (right, front);
        var (fromRight, fromFront) = from?.FacingFrame == true ? (WeaponPoseClip.FacingRight, WeaponPoseClip.FacingFront) : (right, front);
        bool anyLaid = false;
        foreach (int b in _order)
        {
            List<PoseChannel>? nowList = null, beforeList = null;
            bool any = (now?.TryGetValue(b, out nowList) ?? false) | (before?.TryGetValue(b, out beforeList) ?? false);
            if (!any) continue;
            if (b == _lFoot || b == _rFoot) continue;
            anyLaid = true;
            var turn = nowList is not null ? TurnOf(b, nowList, time, nowRight, nowFront, up) : Quaternion.Identity;
            var shift = nowList is not null ? ShiftOf(nowList, time, nowRight, nowFront, up) : Vector3.Zero;
            if (beforeList is not null && fromShare > 0f)
            {
                var old = TurnOf(b, beforeList, fromTime, fromRight, fromFront, up);
                if (Quaternion.Dot(old, turn) < 0f) old = -old;
                turn = Quaternion.Normalize(SlerpGodot(turn, old, fromShare));
                shift = Vector3.Lerp(shift, ShiftOf(beforeList, fromTime, fromRight, fromFront, up), fromShare);
            }
            var pivot = Origin(b);
            MoveBone(b, turn, shift, pivot, w);
            if (_carried[b] >= 0) MoveBone(_carried[b], turn, shift, pivot, w);
        }
        if (!anyLaid) return;
        // The carry's fist (WeaponPoseClip.FistTarget): the right arm solved so its grip socket stands there, faded with the clip that has it (WeaponPoseFist).
        float fistShare = (clip?.FistTarget is not null ? 1f - fromShare : 0f) + (from?.FistTarget is not null ? fromShare : 0f);
        if ((clip?.FistTarget ?? from?.FistTarget) is { } fist && fistShare * w * FistWeight > 1e-3f)
        {
            var (fr, ff) = clip?.FistTarget is not null ? (nowRight, nowFront) : (fromRight, fromFront);
            SolveFist(fist, fr, ff, up, fistShare * w * FistWeight);
        }
        int[] feet = [_lFoot, _rFoot];
        (int, int, int)[] legs = [(_lThigh, _lCalf, _lFoot), (_rThigh, _rCalf, _rFoot)];
        for (int i = 0; i < 2; i++)
        {
            var offset = Vector3.Zero;
            if (now is not null && now.TryGetValue(feet[i], out var footNow)) offset = ShiftOf(footNow, time, nowRight, nowFront, up);
            if (before is not null && fromShare > 0f && before.TryGetValue(feet[i], out var footBefore)) offset = Vector3.Lerp(offset, ShiftOf(footBefore, fromTime, fromRight, fromFront, up), fromShare);
            var target = plantedAt[i] + offset * w;
            if (pin is not null)
            {
                var (th, ca, fo) = legs[i];
                float reach = (Vector3.Distance(Origin(th), Origin(ca)) + Vector3.Distance(Origin(ca), Origin(fo))) * 0.99f;
                target = Vector3.Lerp(target, pin.Place(i, target, offset.Z * w < FootPin.PlantedBelow, pinActive, toWorld, dt, Origin(th), reach), w);
            }
            SolveLeg(legs[i], target, plantedRot[i], front);
        }
    }

    readonly Dictionary<WeaponPoseClip, Dictionary<int, List<PoseChannel>>> _channels = new(ReferenceEqualityComparer.Instance);

    Dictionary<int, List<PoseChannel>> ChannelsOf(WeaponPoseClip clip)
    {
        if (_channels.TryGetValue(clip, out var map)) return map;
        map = new();
        foreach (var ch in clip.Channels)
        {
            int b = Bone(ch.Bone);
            if (b < 0 || (ch.Kind == PoseChannelKind.Aim && Bone(ch.Toward ?? "") < 0)) continue;
            if (!map.TryGetValue(b, out var list)) map[b] = list = new();
            list.Add(ch);
        }
        _channels[clip] = map;
        return map;
    }

    Quaternion TurnOf(int bone, List<PoseChannel> channels, float t, Vector3 right, Vector3 front, Vector3 up)
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
                q = Quaternion.Normalize(Axis(up, k.Yaw * deg) * Axis(right, k.Pitch * deg) * Axis(front, k.Roll * deg) * q);
                continue;
            }
            float weight = Math.Clamp(k.Weight, 0f, 1f);
            if (weight <= 1e-4f) continue;
            int child = Bone(ch.Toward!);
            var from = Origin(bone);
            var dir = Vector3.Transform(Origin(child) - from, q);
            if (dir.LengthSquared() < 1e-8f) continue;
            dir = Vector3.Normalize(dir);
            float el = k.Elevation * deg, az = k.Azimuth * deg;
            var want = Vector3.Normalize(front * (MathF.Cos(el) * MathF.Cos(az)) + right * (MathF.Cos(el) * MathF.Sin(az)) + up * MathF.Sin(el));
            var arc = ArcBetween(dir, want);
            if (weight < 1f) arc = Quaternion.Normalize(SlerpGodot(Quaternion.Identity, arc, weight));
            q = Quaternion.Normalize(arc * q);
        }
        return q;
    }

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

    void MoveBone(int bone, Quaternion turn, Vector3 shift, Vector3 pivot, float w)
    {
        int parent = _sk.Parents[bone];
        var parentRot = parent >= 0 ? Rotation(parent) : Quaternion.Identity;
        var parentScale = parent >= 0 ? (Update2(parent)) : Vector3.One;
        var origin = Origin(bone);
        bool turns = MathF.Abs(turn.W) <= 0.9999995f;
        if (turns)
        {
            var local = _rot[bone];
            var target = Quaternion.Normalize(Quaternion.Inverse(parentRot) * turn * parentRot * local);
            if (Quaternion.Dot(local, target) < 0f) target = -target;
            _rot[bone] = w >= 0.999f ? target : Quaternion.Normalize(SlerpGodot(local, target, w));
            _dirty = true;
        }
        var moved = (turns ? pivot + Vector3.Transform(origin - pivot, turn) : origin) + shift;
        if (Vector3.DistanceSquared(moved, origin) > 1e-12f)
        {
            var delta = (moved - origin) * w;
            var localShift = Vector3.Transform(delta, Quaternion.Inverse(parentRot));
            localShift = new Vector3(localShift.X / MathF.Max(parentScale.X, 1e-4f), localShift.Y / MathF.Max(parentScale.Y, 1e-4f), localShift.Z / MathF.Max(parentScale.Z, 1e-4f));
            _pos[bone] += localShift;
            _dirty = true;
        }
    }

    Vector3 Update2(int b) { Update(); return _gscl[b]; }

    void SolveLeg((int Thigh, int Calf, int Foot) leg, Vector3 target, Quaternion plantedRot, Vector3 front)
    {
        var H = Origin(leg.Thigh); var K = Origin(leg.Calf); var A = Origin(leg.Foot);
        var thighRot = Rotation(leg.Thigh); var kneeRot = Rotation(leg.Calf); var footRot = Rotation(leg.Foot);
        float a = Vector3.Distance(H, K), b = Vector3.Distance(K, A);
        if (a < 1e-3f || b < 1e-3f) return;
        if (Vector3.DistanceSquared(A, target) < 1e-8f) return;
        var toT = target - H;
        float dist = toT.Length();
        if (dist < 1e-4f) return;
        var dir = toT / dist;
        float d = Math.Clamp(dist, MathF.Abs(a - b) + 1e-3f, (a + b) * 0.995f);
        var along = A - H;
        var pole = along.LengthSquared() > 1e-8f ? K - H - Vector3.Normalize(along) * Vector3.Dot(K - H, Vector3.Normalize(along)) : Vector3.Zero;
        if (pole.LengthSquared() < 1e-6f) pole = front;
        pole -= dir * Vector3.Dot(pole, dir);
        if (pole.LengthSquared() < 1e-8f) pole = Vector3.Cross(Vector3.UnitZ, dir);
        pole = Vector3.Normalize(pole);
        float x = (a * a - b * b + d * d) / (2f * d);
        float h = MathF.Sqrt(MathF.Max(0f, a * a - x * x));
        var knee = H + dir * x + pole * h;
        var foot = H + dir * d;
        int hipParent = _sk.Parents[leg.Thigh];
        var hipParentRot = hipParent >= 0 ? Rotation(hipParent) : Quaternion.Identity;
        var thighNew = Quaternion.Normalize(ArcBetween(Vector3.Normalize(K - H), Vector3.Normalize(knee - H)) * thighRot);
        _rot[leg.Thigh] = Quaternion.Normalize(Quaternion.Inverse(hipParentRot) * thighNew);
        var calfRel = Quaternion.Normalize(Quaternion.Inverse(thighRot) * kneeRot);
        var calfRot = Quaternion.Normalize(thighNew * calfRel);
        var footNow = knee + Vector3.Transform(Vector3.Transform(A - K, Quaternion.Inverse(kneeRot)), calfRot);
        var calfNew = Quaternion.Normalize(ArcBetween(Vector3.Normalize(footNow - knee), Vector3.Normalize(foot - knee)) * calfRot);
        _rot[leg.Calf] = Quaternion.Normalize(Quaternion.Inverse(thighNew) * calfNew);
        _rot[leg.Foot] = Quaternion.Normalize(Quaternion.Inverse(calfNew) * plantedRot);
        _ = footRot;
        _dirty = true;
    }

    /// <summary>
    /// The two-bone solve of the right arm (<c>WeaponPoseFist</c>): the upper arm and the forearm turned so the right grip socket reaches the middle of the shoulders plus <paramref name="offset"/>
    /// (right, forward, up in the clip's frame), the elbow toward <see cref="WeaponPoseClip.FistElbowHint"/>; three passes for the socket's offset from the hand; then each bone slerped from its
    /// pose before the solve by <paramref name="weight"/>.
    /// </summary>
    void SolveFist(Vector3 offset, Vector3 right, Vector3 front, Vector3 up, float weight)
    {
        int sh = Bone("RIG_R_UpperArm"), lsh = Bone("RIG_L_UpperArm"), fore = Bone("RIG_R_Forearm"), hand = Bone("RIG_R_Hand");
        if (sh < 0 || lsh < 0 || fore < 0 || hand < 0) return;
        var before = (_rot[sh], _rot[fore]);
        var mid = (Origin(sh) + Origin(lsh)) * 0.5f;
        var target = mid + right * offset.X + front * offset.Y + up * offset.Z;
        var h0 = WeaponPoseClip.FistElbowHint;
        var hint = Vector3.Normalize(right * h0.X + front * h0.Y + up * h0.Z);
        for (int it = 0; it < 3; it++)
        {
            var S = Origin(sh); var E = Origin(fore); var H = Origin(hand); var P = Origin(_rHand);
            var ht = target - (P - H);
            float a = Vector3.Distance(S, E), b = Vector3.Distance(E, H);
            var toT = ht - S; float dist = toT.Length();
            if (dist < 1e-4f || a < 1e-4f || b < 1e-4f) break;
            var dir = toT / dist; float d = Math.Clamp(dist, MathF.Abs(a - b) + 1e-3f, (a + b) * 0.999f);
            var pole = hint - dir * Vector3.Dot(hint, dir);
            pole = pole.LengthSquared() > 1e-6f ? Vector3.Normalize(pole) : Vector3.Normalize(Vector3.Cross(dir, up));
            float x = (a * a - b * b + d * d) / (2f * d), hh = MathF.Sqrt(MathF.Max(0f, a * a - x * x));
            var elbow = S + dir * x + pole * hh;
            MoveBone(sh, ArcBetween(Vector3.Normalize(E - S), Vector3.Normalize(elbow - S)), Vector3.Zero, S, 1f);
            E = Origin(fore); H = Origin(hand);
            MoveBone(fore, ArcBetween(Vector3.Normalize(H - E), Vector3.Normalize(S + dir * d - E)), Vector3.Zero, E, 1f);
        }
        if (weight < 0.999f)
        {
            _rot[sh] = Quaternion.Normalize(SlerpGodot(before.Item1, _rot[sh], weight));
            _rot[fore] = Quaternion.Normalize(SlerpGodot(before.Item2, _rot[fore], weight));
            _dirty = true;
        }
    }

    static Quaternion Axis(Vector3 axis, float angle) => Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);

    /// <summary>Godot's <c>Quaternion.Slerp</c> (the short way round).</summary>
    static Quaternion SlerpGodot(Quaternion a, Quaternion b, float t) => Quaternion.Slerp(a, b, t);

    static Quaternion ArcBetween(Vector3 from, Vector3 to)
    {
        float d = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (d > 0.99999f) return Quaternion.Identity;
        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-10f) axis = MathF.Abs(from.X) < 0.9f ? Vector3.Cross(from, Vector3.UnitX) : Vector3.Cross(from, Vector3.UnitY);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(d));
    }
}
