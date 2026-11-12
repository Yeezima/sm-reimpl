using System;
using System.Collections.Generic;
using System.Numerics;

namespace SmContent.Extras.Musou;

/// <summary>A body part as a capsule in a bone's frame (metres): the segment <see cref="A"/> to <see cref="B"/> in the bone's local space, and the radius.</summary>
public readonly record struct BodyCapsule(string Part, string Bone, Vector3 A, Vector3 B, float Radius);

/// <summary>The closest the spear comes to the body at one pose: the clearance (metres; negative: the spear is that deep inside the part), the part, and which piece of the spear.</summary>
public readonly record struct SpearClearanceResult(float Clearance, string Part, string SpearPiece)
{
    public static readonly SpearClearanceResult None = new(float.PositiveInfinity, "", "");
}

/// <summary>
/// The clearance measurement of the power spear's poses (owner, 2026-11-10: "Make a pass at all the animations to ensure the spear isn't going through the player models body at any time"; relaxed by the owner on 2026-11-12: "It's ok if the spear or other parts of the character momentarily clip", so the tests report it and fail only a gross error;
/// added content, Musou Mode only; <c>docs/claw-spear-animations.md</c>). The marine's body is a set of capsules on its bones (<see cref="Body"/>) and the spear (W3's model as
/// <see cref="SpearHold"/> measures it) a set of thin segments; <see cref="Measure"/> gives the least clearance between them at one pose. Godot-free: the clearance test
/// (<c>SpearClearanceTests</c>, every spear clip sampled over time on the install's rig) and the preview stage (<c>--claw-spear-test --clearance</c>, the client's own overlay) both call it.
/// The forearms and hands are not in the body: the spear is held in the hands, and its shaft runs through the wrists by design (<see cref="SpearHold.Place"/>).
/// </summary>
public static class SpearClearance
{
    /// <summary>
    /// The body capsules, in the bones' bind-pose frames: each fitted to the install's multiplayer basic armour in its bind pose (the vertices skinned mostly to the bone, or rigid on it;
    /// the head, the torso, the pelvis, the shoulder pads and the jump pack split into clusters), the radius covering 88 % of the part's vertices along its principal axis at the least volume
    /// (measured 2026-11-10 with the clearance test's fit, <c>SpearClearanceTests.Body_capsules_fit_the_armour</c>; **ours**, measured). The Assault wears the jump pack
    /// (<c>sm_jumppack_mp_basic_001</c>) instead of the backpack, so the back is the pack's body and thrusters; its straps are inside the torso's capsules.
    /// </summary>
    public static readonly IReadOnlyList<BodyCapsule> Fitted =
    [
        new("head", "RIG_Head", new(0.071f, 0.135f, -0.075f), new(0.071f, 0.135f, 0.075f), 0.089f),
        new("head", "RIG_Head", new(0.112f, -0.042f, 0.022f), new(0.150f, -0.014f, -0.001f), 0.157f),
        new("torso", "RIG_SpineD", new(0.023f, 0.222f, 0.092f), new(0.205f, 0.280f, -0.046f), 0.111f),
        new("torso", "RIG_SpineD", new(0.465f, -0.110f, 0.090f), new(0.391f, 0.035f, 0.178f), 0.173f),
        new("torso", "RIG_SpineD", new(0.070f, -0.260f, -0.122f), new(0.078f, -0.160f, -0.241f), 0.145f),
        new("torso", "RIG_SpineD", new(0.081f, -0.241f, 0.198f), new(0.044f, 0.024f, 0.279f), 0.159f),
        new("torso", "RIG_SpineD", new(0.422f, -0.037f, -0.169f), new(0.406f, 0.031f, -0.131f), 0.190f),
        new("torso", "RIG_SpineD", new(0.016f, 0.085f, -0.243f), new(0.158f, 0.053f, -0.309f), 0.125f),
        new("waist", "RIG_SpineA", new(0.105f, 0.034f, -0.027f), new(0.105f, 0.035f, 0.023f), 0.267f),
        new("pelvis", "RIG_Pelvis", new(0.115f, -0.207f, 0.110f), new(0.165f, -0.065f, 0.224f), 0.169f),
        new("pelvis", "RIG_Pelvis", new(-0.037f, 0.097f, -0.002f), new(0.087f, 0.172f, 0.012f), 0.177f),
        new("pelvis", "RIG_Pelvis", new(0.095f, -0.217f, -0.155f), new(0.178f, 0.021f, -0.245f), 0.144f),
        new("jump pack", "RIG_SpineD", new(0.190f, -0.671f, -0.272f), new(0.207f, -0.595f, -0.477f), 0.196f),
        new("jump pack", "RIG_SpineD", new(0.554f, -0.291f, 0.459f), new(0.681f, -0.171f, 0.253f), 0.208f),
        new("jump pack", "RIG_SpineD", new(0.181f, -0.657f, 0.291f), new(0.201f, -0.611f, 0.465f), 0.201f),
        new("jump pack", "RIG_SpineD", new(0.573f, -0.277f, -0.459f), new(0.680f, -0.174f, -0.225f), 0.224f),
        new("jump pack", "RIG_SpineD", new(0.529f, -0.532f, -0.080f), new(0.529f, -0.532f, 0.080f), 0.212f),
        new("jump pack", "RIG_SpineD", new(0.075f, -0.439f, 0.008f), new(0.075f, -0.439f, 0.008f), 0.262f),
        new("left pad", "RIG_L_ShoulderPad", new(-0.041f, 0.186f, -0.124f), new(0.037f, 0.182f, -0.229f), 0.191f),
        new("left pad", "RIG_L_ShoulderPad", new(-0.050f, -0.227f, -0.284f), new(0.210f, -0.047f, -0.308f), 0.101f),
        new("left pad", "RIG_L_ShoulderPad", new(-0.014f, -0.083f, -0.015f), new(-0.071f, 0.022f, 0.083f), 0.160f),
        new("right pad", "RIG_R_ShoulderPad", new(-0.082f, -0.096f, 0.078f), new(-0.077f, 0.099f, 0.050f), 0.135f),
        new("right pad", "RIG_R_ShoulderPad", new(-0.023f, 0.168f, -0.190f), new(0.127f, 0.061f, -0.211f), 0.170f),
        new("right pad", "RIG_R_ShoulderPad", new(-0.032f, -0.183f, -0.163f), new(0.053f, -0.172f, -0.243f), 0.181f),
        new("left upper arm", "RIG_L_UpperArm", new(-0.025f, -0.012f, -0.012f), new(0.279f, 0.011f, -0.019f), 0.117f),
        new("right upper arm", "RIG_R_UpperArm", new(-0.016f, -0.003f, 0.010f), new(0.267f, 0.010f, 0.011f), 0.118f),
        new("left thigh", "RIG_L_Thigh", new(0.063f, -0.001f, -0.011f), new(0.407f, -0.007f, 0.034f), 0.185f),
        new("right thigh", "RIG_R_Thigh", new(0.074f, 0.010f, 0.025f), new(0.409f, -0.009f, -0.020f), 0.179f),
        new("left knee", "RIG_L_KneePad", new(0.110f, -0.009f, -0.001f), new(0.110f, -0.009f, -0.001f), 0.161f),
        new("right knee", "RIG_R_KneePad", new(0.111f, 0.007f, -0.003f), new(0.111f, 0.007f, -0.003f), 0.162f),
        new("left shin", "RIG_L_Calf", new(0.117f, 0.008f, 0.012f), new(0.571f, 0.044f, -0.003f), 0.211f),
        new("right shin", "RIG_R_Calf", new(0.095f, 0.018f, 0.010f), new(0.566f, 0.043f, 0.008f), 0.208f),
        new("left foot", "RIG_L_Foot", new(0.081f, -0.050f, -0.006f), new(0.278f, 0.094f, 0.003f), 0.171f),
        new("right foot", "RIG_R_Foot", new(0.073f, -0.065f, 0.010f), new(0.290f, 0.101f, 0.002f), 0.167f),
    ];

    /// <summary>
    /// The torso's core: the fitted capsules hug the armour's surface patches and leave a few centimetres of the chest's middle uncovered (bind-pose points along the spine were up to 0.03 m
    /// outside them), so a capsule along the spine from the waist to the upper chest fills it, its radius well inside the armour there (the chest is at least 0.21 m deep in front of the spine
    /// and 0.25 m to each side; ours).
    /// </summary>
    public static readonly IReadOnlyList<BodyCapsule> Core =
    [
        new("torso", "RIG_SpineD", new(-0.15f, 0f, 0f), new(0.40f, -0.02f, 0f), 0.17f),
    ];

    /// <summary>The whole body: the fitted capsules and the core.</summary>
    public static readonly IReadOnlyList<BodyCapsule> Body = [.. Fitted, .. Core];

    /// <summary>How deep the spear may pass into a capsule before a pose counts as clipping (m; the capsules cover 88 % of each part's surface, so a graze of the outer armour passes).</summary>
    public const float Tolerance = 0.02f;

    /// <summary>The spear as segments from the butt (metres along the shaft, metres along the edge's direction) to another such point, with a radius: the shaft, the collar, the blade's spine and edge, the crossguard.</summary>
    static readonly (string Piece, float Along0, float Across0, float Along1, float Across1, float Radius)[] Pieces =
    [
        ("shaft", 0f, 0f, SpearHold.CollarFromButt - 0.11f, 0f, SpearHold.ShaftRadius),
        ("collar", SpearHold.CollarFromButt - 0.11f, 0f, SpearHold.BladeBaseFromButt, 0f, SpearHold.CollarRadius),
        ("guard", SpearHold.BladeBaseFromButt, -SpearHold.GuardReach, SpearHold.BladeBaseFromButt, SpearHold.GuardReach, 0.012f),
        ("blade spine", SpearHold.BladeBaseFromButt, -SpearHold.SpineReach, SpearHold.TipFromButt, -0.025f, 0.012f),
        ("blade edge", SpearHold.BladeBaseFromButt, SpearHold.EdgeReach, SpearHold.TipFromButt - 0.115f, SpearHold.EdgeReach, 0.012f),
    ];

    /// <summary>
    /// The least clearance between the spear placed at <paramref name="butt"/> along <paramref name="shaft"/> with its edge toward <paramref name="edge"/> (skeleton space, <see cref="SpearHold.Place"/>)
    /// and the body posed by <paramref name="bone"/> (a bone's global origin and rotation by name, or null when the rig lacks it).
    /// </summary>
    public static SpearClearanceResult Measure(Func<string, (Vector3 Origin, Quaternion Rotation)?> bone, Vector3 butt, Vector3 shaft, Vector3 edge)
    {
        var best = SpearClearanceResult.None;
        foreach (var c in Body)
        {
            if (bone(c.Bone) is not { } pose) continue;
            var a = pose.Origin + Vector3.Transform(c.A, pose.Rotation);
            var b = pose.Origin + Vector3.Transform(c.B, pose.Rotation);
            foreach (var p in Pieces)
            {
                var p0 = butt + shaft * p.Along0 + edge * p.Across0;
                var p1 = butt + shaft * p.Along1 + edge * p.Across1;
                float clearance = SegmentDistance(p0, p1, a, b) - c.Radius - p.Radius;
                if (clearance < best.Clearance) best = new SpearClearanceResult(clearance, c.Part, p.Piece);
            }
        }
        return best;
    }

    /// <summary>
    /// The crosshair's column in the own third-person view (owner via the lead, 2026-11-11: the carried blade crossed it at idle): how far either side of the screen's centre column the blade
    /// must stay, in degrees of the horizontal view (ours: the reticle at rest is about 2 degrees across its half at the data's 40-degree view in the 1600 x 900 window, so about a degree of gap
    /// is left beside it). The third-person view stays parallel to the facing and is never rolled (<c>NetworkView.RigCamera</c>), so the screen's centre column is the vertical plane through the
    /// camera along the facing, whatever the pitch.
    /// </summary>
    public const float CrosshairColumnDegrees = 3f;

    /// <summary>
    /// The least horizontal angle (degrees) between the crosshair's column and the spear's head (its collar, crossguard and blade, thickness included) for the spear placed at
    /// <paramref name="butt"/> along <paramref name="shaft"/> with its edge toward <paramref name="edge"/>, seen from a camera at <paramref name="camera"/> looking along the facing; skeleton space
    /// (+Y the way the player faces, +X his right, Z up; the client's character frame). Positive: the whole head stands that far to the column's right; negative: to its left; 0: it crosses it
    /// (or a point is not in front of the camera). A straight piece's image is a straight line, so its ends bound it.
    /// </summary>
    /// <param name="pitch">The view's pitch (radians, up positive): the depth is taken along the view, so the angle is the one on the screen.</param>
    public static float HeadColumnAngle(Vector3 butt, Vector3 shaft, Vector3 edge, Vector3 camera, float pitch = 0f)
    {
        float left = float.PositiveInfinity, right = float.NegativeInfinity;   // the least angle of the head's left side and the greatest of its right side
        float cos = MathF.Cos(pitch), sin = MathF.Sin(pitch);
        foreach (var p in Pieces)
        {
            if (p.Piece == "shaft") continue;
            for (int end = 0; end < 2; end++)
            {
                var at = butt + shaft * (end == 0 ? p.Along0 : p.Along1) + edge * (end == 0 ? p.Across0 : p.Across1) - camera;
                float depth = at.Y * cos + at.Z * sin;
                if (depth <= 0.05f) return 0f;
                const float deg = 180f / MathF.PI;
                left = MathF.Min(left, MathF.Atan2(at.X - p.Radius, depth) * deg);
                right = MathF.Max(right, MathF.Atan2(at.X + p.Radius, depth) * deg);
            }
        }
        return left > 0f ? left : right < 0f ? right : 0f;
    }

    /// <summary>The butt's height above the ground (skeleton space: the ground is Z 0, where the stances plant the feet), the butt cap included (m).</summary>
    public static float ButtHeight(Vector3 butt, Vector3 shaft) => butt.Z - MathF.Max(0f, shaft.Z) * 0.025f - 0.027f * MathF.Sqrt(MathF.Max(0f, 1f - shaft.Z * shaft.Z));

    /// <summary>The distance between segments <paramref name="p0"/>-<paramref name="p1"/> and <paramref name="q0"/>-<paramref name="q1"/> (either may be a point).</summary>
    public static float SegmentDistance(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        var d1 = p1 - p0; var d2 = q1 - q0; var r = p0 - q0;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-12f && e <= 1e-12f) return r.Length();
        if (a <= 1e-12f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-12f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                s = denom > 1e-12f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        return Vector3.Distance(p0 + d1 * s, q0 + d2 * t);
    }
}
