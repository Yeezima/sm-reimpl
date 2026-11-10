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
    /// <summary>W3's model's own origin (the thunder hammer's two-handed grip point, 0 on its shaft axis), metres from the butt; W3's <c>WeaponRig.SpearButt</c> is its negative.</summary>
    public const float AxisFromButt = 1.05f;
    /// <summary>W3's collar (the top of the shaft, where the blade's fittings begin), metres from the butt: the highest a hand can hold.</summary>
    public const float CollarFromButt = 1.89f;
    /// <summary>
    /// W3's own one-handed mount (used when no clip drives the spear): the fist 1.34 m from the butt while running and at rest (measured over the run clip with <c>ModelViewer --spear-probe</c>,
    /// the butt 4 cm off the ground at its lowest) and 0.45 m for melee; <c>WeaponRig.SpearGripHigh</c> and <c>SpearGripLow</c> are these on its axis (<see cref="AxisAt"/>).
    /// </summary>
    public const float MountHighFromButt = 1.34f, MountLowFromButt = 0.45f;
    /// <summary>
    /// The one-handed carry's tilt: at the low grip the shaft stands upright from the hand; at the carry grip (<see cref="WeaponPoseClip.CarryGrip"/>) it leans forward to this elevation (degrees
    /// above the horizontal), the butt just behind the hand and the blade forward-up over the shoulder (ours, from the owner's rule "right at the point where the butt of the spear is just
    /// above the ground"; measured in a match the hand hangs about 1.28 m up standing and 1.08 m running, so the butt rides about 0.34 m and 0.14 m off the ground).
    /// </summary>
    public const float CarryTilt = 80f;

    /// <summary>A point given in metres from the butt (a clip's <see cref="WeaponPoseClip.GripPointAt"/>) on W3's shaft axis, the model's own X: 0.95 m is -0.10, W3's run grip 1.34 m is 0.29.</summary>
    public static float AxisAt(float fromButt) => fromButt - AxisFromButt;

    /// <summary>W3's <c>WeaponRig.GripSlide</c> for a one-handed fist at <paramref name="fromButt"/> metres from the butt: 0 its run grip, 1 its low grip, clamped between.</summary>
    public static float SlideAt(float fromButt) => Math.Clamp((MountHighFromButt - fromButt) / (MountHighFromButt - MountLowFromButt), 0f, 1f);

    /// <summary>
    /// The spear on the hands (skeleton space): the shaft runs from the right (rear) hand toward the left (forward) hand and the blade, the right hand <paramref name="rightFromButt"/> metres from
    /// the butt. With the left hand off the shaft (<paramref name="leftGrip"/> 0, the one-handed carry) the shaft stands upright from the right hand at the low grip and leans toward
    /// <paramref name="front"/> to <see cref="CarryTilt"/> at the carry grip; between, the left hand's hold brings it onto the line through both hands. The blade's edge faces
    /// <paramref name="front"/> (owner, 2026-11-10: "the blade needs to face forward at rest"), turning up only when the shaft itself points near the front.
    /// </summary>
    /// <returns>The butt's place, the shaft's direction from the butt toward the blade (unit) and the edge's direction (unit, square to the shaft).</returns>
    public static (Vector3 Butt, Vector3 Shaft, Vector3 Edge) Place(Vector3 rightHand, Vector3 leftHand, float leftGrip, float rightFromButt, Vector3 front, Vector3 up)
    {
        var d = leftHand - rightHand;
        d = d.LengthSquared() < 1e-4f ? front : Vector3.Normalize(d);
        float grip = Math.Clamp(leftGrip, 0f, 1f);
        if (grip < 1f)
        {
            float high = Math.Clamp((rightFromButt - WeaponPoseClip.LowGrip) / (WeaponPoseClip.CarryGrip - WeaponPoseClip.LowGrip), 0f, 1f);
            float el = (90f + (CarryTilt - 90f) * high) * MathF.PI / 180f;
            var carry = Vector3.Normalize(front * MathF.Cos(el) + up * MathF.Sin(el));
            d = Vector3.Normalize(Vector3.Lerp(carry, d, grip));
        }
        var butt = rightHand - d * rightFromButt;
        var edge = front - d * Vector3.Dot(front, d);
        var hint = edge.LengthSquared() > 0.09f ? Vector3.Normalize(edge) : Vector3.Normalize(up - d * Vector3.Dot(d, up));
        return (butt, d, hint);
    }
}
