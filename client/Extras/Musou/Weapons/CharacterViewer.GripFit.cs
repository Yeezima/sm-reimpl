using System;
using System.Collections.Generic;
using Godot;
using SmContent.Extras.Musou;

namespace SmClient;

/// <summary>
/// Spear pass 4b (2026-11-12, claude/spear-pass-4b): the hand on the shaft as a person grips it. The grip point in the palm (<see cref="GripPoint"/>: the rig's grip socket moved to where a closed fist
/// holds a 4.2 cm shaft), the wrist angle in the carry (the shaft crosses the palm diagonally, <see cref="GripDiagonal"/>; the hand is laid by the arm solve with its orientation fixed, so the
/// forearm and the hand keep their lengths) and a per-finger curl fitted to the shaft's radius each frame (<see cref="FitGripFingers"/>: the proximal joint, then the distal one, until they touch
/// the shaft's surface; the thumb wrapped from the side opposite the fingers). Presentation only; <c>--old-spear-pass4b</c> plays pass 4's fixed closure. Ours (the refine register).
/// </summary>
public partial class CharacterViewer
{
    /// <summary>Spear pass 4b is in force (neither rollback switch is on).</summary>
    internal static bool GripFitOn => !ClawSpearClips.OldPass4 && !ClawSpearClips.OldPass4b && !ClawSpearClips.OldPass2;

    static float EnvF(string name, float fallback) => float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>How far a finger's centre line stays off the shaft's surface (half the gauntlet finger's thickness).</summary>
    static readonly float FingerOff = EnvF("SM_FINGER_OFF", 0.010f), ThumbOff = EnvF("SM_THUMB_OFF", 0.010f);
    /// <summary>The right hand's grip point in the hand's own frame (m: along the fingers, out of the palm, across it toward the thumb); the left hand's is mirrored across the palm (z negated).</summary>
    static readonly Vector3 GripPoint = new(EnvF("SM_GRIP_GX", 0.135f), EnvF("SM_GRIP_GY", 0.026f), EnvF("SM_GRIP_GZ", 0.020f));
    /// <summary>How far the shaft crosses the palm from the hand's across axis (toward the fingers' direction), degrees: the hammer grip's diagonal.</summary>
    static readonly float GripDiagonal = EnvF("SM_GRIP_PHI", 20f);
    /// <summary>The finger tip beyond the last bone (m): the rig's fingers end in a block, there is no tip bone.</summary>
    static readonly float TipLength = EnvF("SM_TIP_LEN", 0.040f);

    static readonly bool GripTraceOn = System.Environment.GetEnvironmentVariable("SM_GRIP_TRACE") == "1";
    static readonly bool GripProbeOn = System.Environment.GetEnvironmentVariable("SM_GRIP_PROBE") == "1";
    bool _gripProbed;
    /// <summary>Test aid: the last fit's per-finger lines (SM_GRIP_PROBE=1 prints them once a second).</summary>
    public List<string> GripFitLog { get; } = new();
    float _gripLogAt;

    /// <summary>Moves the two grip sockets to <see cref="GripPoint"/> in their hands as far as <paramref name="w"/> (their pose positions are the stance's own: the hammer's grip, the pistol's).</summary>
    void LayGripSockets(Skeleton3D s, float w)
    {
        if (!GripFitOn || w <= 1e-3f || !WeaponPoseHands(out _, out _)) return;
        for (int side = 0; side < 2; side++)
        {
            int socket = side == 0 ? _weaponHandR : _weaponHandL;
            if (socket < 0) continue;
            var want = new Vector3(GripPoint.X, GripPoint.Y, side == 0 ? GripPoint.Z : -GripPoint.Z);
            var cur = s.GetBonePosePosition(socket);
            s.SetBonePosePosition(socket, w >= 0.999f ? want : cur.Lerp(want, w));
        }
    }

    /// <summary>The hand's world rotation that lays the shaft diagonally through the palm for a shaft along <paramref name="shaft"/> (blade toward the thumb), keeping the hand's roll as it is (the shortest turn).</summary>
    Quaternion WristFor(Quaternion current, Vector3 shaft)
    {
        float phi = GripDiagonal * MathF.PI / 180f;
        var across = new Vector3(MathF.Sin(phi), 0f, MathF.Cos(phi));   // the shaft's direction in the hand's frame
        var from = (current * across).Normalized();
        return (ArcBetween(from, shaft.Normalized()) * current).Normalized();
    }

    readonly record struct FingerChain(string Prox, string Dist, bool Thumb);
    static readonly FingerChain[] Chains = [new("Finger1", "Finger11", false), new("Finger2", "Finger21", false), new("Finger0", "Finger01", true)];
    int[]? _gripBones;   // [side][chain][prox, dist]

    static float LineDistance(Vector3 p, Vector3 c, Vector3 d) => (p - c).Cross(d).Length();

    /// <summary>The nearest approach of the segment a-b to the shaft's axis (c, d).</summary>
    static float SegmentDistance(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        float m = float.MaxValue;
        for (int i = 0; i <= 6; i++) m = MathF.Min(m, LineDistance(a.Lerp(b, i / 6f), c, d));
        return m;
    }

    /// <summary>
    /// Curls the fingers of one hand round the shaft (axis through <paramref name="c"/> along <paramref name="d"/>, radius <see cref="SpearHold.ShaftRadius"/>) as far as <paramref name="weight"/>: for
    /// each finger the proximal bone turns (about its local Z) until its far joint reaches a finger's thickness off the surface, the distal one until the tip does; the thumb's two bones turn
    /// until the thumb lies across the shaft from the side the fingers are not on. Returns the worst gap (a finger tip's distance from the shaft's centre line beyond the finger's own reach, m; for the tests and the log).
    /// </summary>
    float FitGripFingers(Skeleton3D s, bool left, float weight, Vector3 c, Vector3 d, List<string>? log = null)
    {
        if (weight <= 1e-3f) return 0f;
        int hand = Bone(s, left ? "RIG_L_Hand" : "RIG_R_Hand");
        if (hand < 0) return 0f;
        if (_gripBones is null)
        {
            _gripBones = new int[2 * Chains.Length * 2];
            for (int sd = 0; sd < 2; sd++)
                for (int i = 0; i < Chains.Length; i++)
                {
                    _gripBones[(sd * Chains.Length + i) * 2] = Bone(s, $"RIG_{(sd == 0 ? "R" : "L")}_{Chains[i].Prox}");
                    _gripBones[(sd * Chains.Length + i) * 2 + 1] = Bone(s, $"RIG_{(sd == 0 ? "R" : "L")}_{Chains[i].Dist}");
                }
        }
        float R = SpearHold.ShaftRadius, worst = 0f;
        var fingerTips = new List<Vector3>();
        var zAxis = new Vector3(0f, 0f, 1f);
        for (int i = 0; i < Chains.Length; i++)
        {
            int p = _gripBones[((left ? 1 : 0) * Chains.Length + i) * 2], q = _gripBones[((left ? 1 : 0) * Chains.Length + i) * 2 + 1];
            if (p < 0 || q < 0) continue;
            var chain = Chains[i];
            var parentG = s.GetBoneGlobalPose(s.GetBoneParent(p));
            var restP = s.GetBoneRest(p).Basis.Orthonormalized().GetRotationQuaternion();
            var restQ = s.GetBoneRest(q).Basis.Orthonormalized().GetRotationQuaternion();
            var posP = s.GetBonePosePosition(p); var posQ = s.GetBonePosePosition(q);
            Transform3D G0(float a) => parentG * new Transform3D(new Basis(restP * new Quaternion(zAxis, a)), posP);
            Transform3D G1(Transform3D g0, float b) => g0 * new Transform3D(new Basis(restQ * new Quaternion(zAxis, b)), posQ);
            float off = chain.Thumb ? ThumbOff : FingerOff, rf = R + off;
            float bestA = 0f, bestB = 0f;
            const float step = 2f * MathF.PI / 180f, max = 115f * MathF.PI / 180f;
            if (!chain.Thumb)
            {
                // The proximal bone: the smallest turn that brings its far joint (or any of its length) to a finger's thickness off the surface.
                float a = 0f;
                for (; a <= max; a += step)
                {
                    var g0 = G0(a);
                    if (LineDistance(g0 * posQ, c, d) <= rf || SegmentDistance(g0.Origin, g0 * posQ, c, d) <= rf) break;
                }
                bestA = MathF.Min(a, max);
                // The distal bone: the smallest turn that brings the tip to the surface (stopping short where its length would go into it).
                var g0f = G0(bestA);
                float b = 0f, lastGood = 0f;
                for (; b <= max; b += step)
                {
                    var g1 = G1(g0f, b);
                    var tip = g1 * new Vector3(TipLength, 0f, 0f);
                    if (SegmentDistance(g1.Origin, tip, c, d) < R + 0.004f) break;
                    lastGood = b;
                    if (LineDistance(tip, c, d) <= rf) break;
                }
                bestB = lastGood;
            }
            else
            {
                // The thumb: both bones searched together for the pose that puts the thumb's joint and tip on the shaft's surface, on the side the fingers are not.
                float bestCost = float.MaxValue;
                var mean = Vector3.Zero;
                foreach (var f in fingerTips) mean += f;
                if (fingerTips.Count > 0) mean /= fingerTips.Count;
                var fingers = mean - c; fingers -= d * fingers.Dot(d);
                for (float a = -40f * MathF.PI / 180f; a <= 140f * MathF.PI / 180f; a += 3f * MathF.PI / 180f)
                    for (float b = -40f * MathF.PI / 180f; b <= 140f * MathF.PI / 180f; b += 3f * MathF.PI / 180f)
                    {
                        var g0 = G0(a); var g1 = G1(g0, b);
                        var j = g1.Origin; var tip = g1 * new Vector3(TipLength, 0f, 0f);
                        float cost = MathF.Pow(LineDistance(j, c, d) - rf, 2f) + 2f * MathF.Pow(LineDistance(tip, c, d) - rf, 2f);
                        if (SegmentDistance(g0.Origin, j, c, d) < R + 0.004f) cost += 0.01f;
                        if (SegmentDistance(j, tip, c, d) < R + 0.004f) cost += 0.01f;
                        var side = tip - c; side -= d * side.Dot(d);
                        if (side.LengthSquared() > 1e-8f && fingers.LengthSquared() > 1e-8f) cost += 0.002f * (1f + side.Normalized().Dot(fingers.Normalized()));
                        if (cost < bestCost) { bestCost = cost; bestA = a; bestB = b; }
                    }
            }
            var rotP = (restP * new Quaternion(zAxis, bestA)).Normalized();
            var rotQ = (restQ * new Quaternion(zAxis, bestB)).Normalized();
            var curP = s.GetBonePoseRotation(p); var curQ = s.GetBonePoseRotation(q);
            if (curP.Dot(rotP) < 0f) rotP = -rotP;
            if (curQ.Dot(rotQ) < 0f) rotQ = -rotQ;
            s.SetBonePoseRotation(p, weight >= 0.999f ? rotP : curP.Slerp(rotP, weight).Normalized());
            s.SetBonePoseRotation(q, weight >= 0.999f ? rotQ : curQ.Slerp(rotQ, weight).Normalized());
            var fg0 = G0(bestA); var fg1 = G1(fg0, bestB);
            var fTip = fg1 * new Vector3(TipLength, 0f, 0f);
            if (!chain.Thumb) fingerTips.Add(fTip);
            float gap = LineDistance(fTip, c, d) - rf;
            if (gap > worst) worst = gap;
            log?.Add($"{(left ? 'L' : 'R')} {chain.Prox}: a {bestA * 180f / MathF.PI:F0} b {bestB * 180f / MathF.PI:F0} knuckle {LineDistance(fg0.Origin, c, d) * 100f:F1} joint {LineDistance(fg1.Origin, c, d) * 100f:F1} tip {LineDistance(fTip, c, d) * 100f:F1} cm (surface {R * 100f:F1}, finger centre {rf * 100f:F1})");
        }
        return worst;
    }

    static readonly bool PopProbeOn = System.Environment.GetEnvironmentVariable("SM_POP_PROBE") == "1";
    static readonly string[] PopBones = ["RIG_Pelvis", "RIG_SpineA", "RIG_SpineD", "RIG_R_Clavicle", "RIG_R_UpperArm", "RIG_R_Forearm", "RIG_R_Hand", "RIG_L_Clavicle", "RIG_L_UpperArm", "RIG_L_Forearm", "RIG_L_Hand", "RIG_R_Thigh", "RIG_R_Calf", "RIG_L_Thigh", "RIG_L_Calf"];
    Quaternion[]? _popPrev;
    ulong _popFrame, _popMs;

    /// <summary>Test aid (<c>SM_POP_PROBE=1</c>): a POP line per frame with the fastest bone's angular speed (rad/s), the fastest arm bone's, and the clip's names, to find pops in hand-overs.</summary>
    void PopProbe(Skeleton3D s, float delta, string tag)
    {
        if (!PopProbeOn) return;
        ulong frame = Engine.GetProcessFrames();
        if (frame == _popFrame) return;   // one reading a frame (the stage's overlay can run twice)
        _popFrame = frame;
        delta = (Time.GetTicksMsec() - _popMs) * 0.001f; _popMs = Time.GetTicksMsec();
        if (delta <= 1e-4f) return;
        _popPrev ??= new Quaternion[PopBones.Length];
        float best = 0f, arm = 0f; string bestName = "", armName = "";
        for (int i = 0; i < PopBones.Length; i++)
        {
            int b = Bone(s, PopBones[i]);
            if (b < 0) continue;
            var q = s.GetBonePoseRotation(b);
            var prev = _popPrev[i];
            if (prev.LengthSquared() > 0.5f)
            {
                float ang = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(prev.Dot(q)))), v = ang / delta;
                if (v > best) { best = v; bestName = PopBones[i]; }
                if (i is >= 3 and <= 10 && v > arm) { arm = v; armName = PopBones[i]; }
            }
            _popPrev[i] = q;
        }
        GD.Print($"POP {tag} best {best:F1} {bestName} arm {arm:F1} {armName}");
    }

    void GripProbe(Skeleton3D s)
    {
        if (!GripProbeOn || _gripProbed) return;
        _gripProbed = true;
        int hand = Bone(s, System.Environment.GetEnvironmentVariable("SM_GRIP_SIDE") == "L" ? "RIG_L_Hand" : "RIG_R_Hand");
        if (hand < 0) return;
        var hp = s.GetBoneGlobalPose(hand);
        GD.Print($"GRIPPROBE hand origin {hp.Origin} basisX {hp.Basis.X} Y {hp.Basis.Y} Z {hp.Basis.Z}");
        void Dump(int b, int depth)
        {
            var g = s.GetBoneGlobalPose(b); var rest = s.GetBoneRest(b);
            var local = hp.AffineInverse() * g;
            GD.Print($"GRIPPROBE {new string(' ', depth * 2)}{s.GetBoneName(b)} inHand origin {local.Origin} X {local.Basis.X} Y {local.Basis.Y} Z {local.Basis.Z} restOrigin {rest.Origin}");
            foreach (int c in s.GetBoneChildren(b)) Dump(c, depth + 1);
        }
        Dump(hand, 0);
    }
}
