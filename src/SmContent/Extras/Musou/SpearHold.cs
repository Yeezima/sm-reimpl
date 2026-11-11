using System;
using System.Numerics;

namespace SmContent.Extras.Musou;

/// <summary>
/// How the power spear sits in the hands (added content, Musou Mode only; <c>docs/claw-spear-animations.md</c> section 1): the shaft the clips' grip points are measured on, and the one
/// placement that both the preview's stand-in and W3's model in a match use, so a match shows the approved previews. Godot-free (System.Numerics; the client converts); skeleton space
/// (Z up), metres. **Ours** (the owner approved the look on the preview stage, 2026-11-10).
/// </summary>
public static class SpearHold
{
    /// <summary>
    /// The hands' grip sockets on the marine rig: the children of <c>RIG_R_Hand</c> and <c>RIG_L_Hand</c> that every held weapon's handle sits on (the install's wield mounts: the power sword's
    /// handle at <c>RIG_Prop_RHand</c>'s origin, the pistols' at <c>RIG_Prop_LHand</c>'s), in the palm. The shaft runs through them (owner, 2026-11-11: "the spear is always attached to the
    /// inside palm of the hand"); before, through the hand bones' origins at the wrists.
    /// </summary>
    public const string RightGripBone = "RIG_Prop_RHand", LeftGripBone = "RIG_Prop_LHand";
    /// <summary>
    /// How far the shaft's axis may pass from a holding hand's grip socket (m; ours, from the hand model: the gauntlet's fist is about 0.16 m across its palm side and 0.2 m thick, the shaft's
    /// radius is 0.021 m, so within this the shaft stays inside the closed fist). <c>SpearClearanceTests</c> holds every spear clip to it on every frame.
    /// </summary>
    public const float PalmTolerance = 0.03f;

    /// <summary>W3's model's own origin (the thunder hammer's two-handed grip point, 0 on its shaft axis), metres from the butt; W3's <c>WeaponRig.SpearButt</c> is its negative.</summary>
    public const float AxisFromButt = 1.05f;
    /// <summary>W3's collar (the top of the shaft, where the blade's fittings begin), metres from the butt: the highest a hand can hold.</summary>
    public const float CollarFromButt = 1.89f;
    /// <summary>W3's blade: its base (the crossguard) and its tip, metres from the butt (the model's <c>SpearBladeBase</c> 0.93 and <c>SpearTip</c> 1.80 on its own axis); the whole spear is 2.85 m.</summary>
    public const float BladeBaseFromButt = 1.98f, TipFromButt = 2.85f;
    /// <summary>W3's blade across the shaft (m): the edge side reaches this far along the edge's direction, the spine side the other way; the crossguard's prongs stand out either side.</summary>
    public const float EdgeReach = 0.08f, SpineReach = 0.045f, GuardReach = 0.115f;
    /// <summary>W3's shaft's radius and its collar's (m), for the clearance check.</summary>
    public const float ShaftRadius = 0.021f, CollarRadius = 0.042f;
    /// <summary>
    /// W3's own one-handed mount (used when no clip drives the spear): the fist 1.34 m from the butt while running and at rest (measured over the run clip with <c>ModelViewer --spear-probe</c>,
    /// the butt 4 cm off the ground at its lowest) and 0.45 m for melee; <c>WeaponRig.SpearGripHigh</c> and <c>SpearGripLow</c> are these on its axis (<see cref="AxisAt"/>).
    /// </summary>
    public const float MountHighFromButt = 1.34f, MountLowFromButt = 0.45f;
    /// <summary>
    /// The one-handed carry at the carry grip (<see cref="WeaponPoseClip.CarryGrip"/>), in the stance's frame (the hips' front, the body's right, up): the shaft leans from the hand to this
    /// elevation above the horizontal (degrees) and this azimuth from the front toward the body's right (owner, 2026-11-10: "the spear should be held out away from the body at idle and when
    /// running"). The hand is held out wide of the hip (<c>ClawSpearClips.SpearRest</c>), so the shaft stands forward of the shoulder pad and the butt hangs behind and outside the leg; the
    /// slight lean in at the top keeps the butt out of the trailing leg's kick when he strafes and sprints. Ours, chosen with the clearance check (<c>SpearClearanceTests</c>: every hammer stance
    /// clip, the least clearance 0.10 m). At the low grip with the left hand off the shaft stands upright from the hand. Before the owner's review of 2026-11-10 the carry leaned 80 degrees
    /// toward the way the player faces, so the shaft rose beside the head and through the pad.
    /// <para>Second follow-up pass (2026-11-11: the carry closer in to the body, the blade out of the crosshair's column): read in the facing frame (the carry clip's
    /// <see cref="WeaponPoseClip.FacingFrame"/>: <c>front</c> is the way the player faces), 65 degrees above the horizontal at -50 (forward and in at the top), from the fist held close at the
    /// right of the chest (<c>ClawSpearClips.SpearRest</c>): the blade above and forward of the right shoulder, left of the crosshair's column by 5.9 degrees at idle, the butt behind and outside
    /// the right leg (clearance over every stance clip 0.086 m at the least, the butt at least 0.65 m up; since pass 3 the walk, run and sprint hold the fist against the shoulders by a solve, <c>WeaponPoseClip.FistTarget</c>). Before: 55 at -15 in the stance's frame from a fist held out wide (pass 2).</para>
    /// </summary>
    public static float CarryElevation => ClawSpearClips.OldPass2 ? 65f : Pass2CarryElevation;
    public static float CarryAzimuth => ClawSpearClips.OldPass2 ? -50f : Pass2CarryAzimuth;
    /// <summary>The carried shaft's lean while he moves (the walk, run and sprint): the carry of before pass 2 (the fist held at the chest keeps the shaft clear of the legs and the crosshair's column there).</summary>
    public const float MovingCarryElevation = 65f, MovingCarryAzimuth = -50f;
    /// <summary>
    /// Spear pass 2 (owner, 2026-11-12, with his carry reference image: "the blade of the spear point forwards"): the shaft leans forward and up from the hand at the hip, the blade ahead of the body, only a little
    /// across it (62 degrees above the horizontal, 22 degrees left of the front while he stands; ours, chosen with the clearance and crosshair column checks, <c>SpearClearanceTests</c>). <c>--old-spear-pass2</c> restores 65 at -50.
    /// </summary>
    public const float Pass2CarryElevation = 62f, Pass2CarryAzimuth = -22f;
    /// <summary>
    /// The way the carried blade's edge faces, as an azimuth from the front toward the body's right (degrees; flat, then squared to the shaft): forward and out, away from the body (owner,
    /// 2026-11-10: "the blade is facing away from the character model at idle and when running"; ours). Once both hands swing it the edge faces the front, as the strikes were approved.
    /// </summary>
    public const float EdgeAzimuth = 60f;

    /// <summary>A point given in metres from the butt (a clip's <see cref="WeaponPoseClip.GripPointAt"/>) on W3's shaft axis, the model's own X: 0.95 m is -0.10, W3's run grip 1.34 m is 0.29.</summary>
    public static float AxisAt(float fromButt) => fromButt - AxisFromButt;

    /// <summary>W3's <c>WeaponRig.GripSlide</c> for a one-handed fist at <paramref name="fromButt"/> metres from the butt: 0 its run grip, 1 its low grip, clamped between.</summary>
    public static float SlideAt(float fromButt) => Math.Clamp((MountHighFromButt - fromButt) / (MountHighFromButt - MountLowFromButt), 0f, 1f);

    /// <summary>
    /// The spear on the hands (skeleton space): the shaft runs from the right (rear) hand toward the left (forward) hand and the blade, the right hand <paramref name="rightFromButt"/> metres from
    /// the butt. With the left hand off the shaft (<paramref name="leftGrip"/> 0, the one-handed carry) the shaft stands upright from the right hand at the low grip and leans to
    /// <see cref="CarryElevation"/> and <see cref="CarryAzimuth"/> in the frame of <paramref name="front"/> at the carry grip; between, the left hand's hold brings it onto the line through
    /// both hands. The blade's edge faces away from the body while the spear is carried (<see cref="EdgeAzimuth"/>, square to the shaft; owner, 2026-11-10: "the blade is facing away from
    /// the character model at idle and when running") and <paramref name="front"/> once both hands swing it (the strikes as approved), turning up only when the shaft itself points near it.
    /// </summary>
    /// <param name="front">The body's flat front (the stance's, read from the hips).</param>
    /// <returns>The butt's place, the shaft's direction from the butt toward the blade (unit) and the edge's direction (unit, square to the shaft).</returns>
    public static (Vector3 Butt, Vector3 Shaft, Vector3 Edge) Place(Vector3 rightHand, Vector3 leftHand, float leftGrip, float rightFromButt, Vector3 front, Vector3 up, float moving = 0f)
    {
        var d = leftHand - rightHand;
        d = d.LengthSquared() < 1e-4f ? front : Vector3.Normalize(d);
        float grip = Math.Clamp(leftGrip, 0f, 1f);
        var right = Vector3.Cross(front, up);
        right = right.LengthSquared() < 1e-6f ? Vector3.Zero : Vector3.Normalize(right);
        const float deg = MathF.PI / 180f;
        if (grip < 1f)
        {
            float high = Math.Clamp((rightFromButt - WeaponPoseClip.LowGrip) / (WeaponPoseClip.CarryGrip - WeaponPoseClip.LowGrip), 0f, 1f);
            float carryEl = CarryElevation, carryAz = CarryAzimuth;
            if (!ClawSpearClips.OldPass2 && moving > 0f) { float m = Math.Clamp(moving, 0f, 1f); carryEl += (MovingCarryElevation - carryEl) * m; carryAz += (MovingCarryAzimuth - carryAz) * m; }   // pass 2: the idle's hip carry eases into the moving carry as the fist solve comes in
            float el = (90f + (carryEl - 90f) * high) * deg;
            var lean = front * MathF.Cos(carryAz * deg) + right * MathF.Sin(carryAz * deg);   // the way the carried shaft leans, flat
            var carry = Vector3.Normalize(lean * MathF.Cos(el) + up * MathF.Sin(el));
            d = Vector3.Normalize(Vector3.Lerp(carry, d, grip));
        }
        var butt = rightHand - d * rightFromButt;
        var away = front * MathF.Cos(EdgeAzimuth * deg) + right * MathF.Sin(EdgeAzimuth * deg);   // the carried edge's facing, flat
        var facing = Vector3.Lerp(away, front, grip);
        var edge = facing - d * Vector3.Dot(facing, d);
        var hint = edge.LengthSquared() > 0.09f ? Vector3.Normalize(edge) : Vector3.Normalize(up - d * Vector3.Dot(d, up));
        return (butt, d, hint);
    }
}
