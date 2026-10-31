using System.Numerics;

namespace SmNet;

/// <summary>
/// camera/hop-and-walls (the owner, 2026-11-01: "i don't like its behavior up against walls or other objects"), client presentation only: the place of a follow camera on its
/// collision cast, as a fraction of the cast's length. Pulls in fast and eases back out slowly, after a hold and only for a real gain, so a free distance flickering at an edge
/// (a pillar's corner, a door frame, a railing) moves the camera once instead of pumping it. All numbers are ours: the original's camera collision is not decoded (the rig has no
/// collision data). Tested in <c>CameraBoomTests</c>; <c>--old-camera-collision</c> restores the M40-T6 rule.
/// </summary>
public sealed class CameraBoom
{
    /// <summary>Ours: 1/s, the exponential rate of the pull-in (M40-T6's value).</summary>
    public const float PullRate = 20f;
    /// <summary>Ours: m/s, the pull-in's speed cap (M40-T6's value).</summary>
    public const float PullMaxSpeed = 28f;
    /// <summary>Ours: seconds the cast must stay free by <see cref="ReleaseMargin"/> before the camera starts back out.</summary>
    public const float ReleaseHold = 0.2f;
    /// <summary>Ours: metres of freed distance that count as a gain; less leaves the camera where it is (hysteresis).</summary>
    public const float ReleaseMargin = 0.15f;
    /// <summary>Ours: 1/s, the exponential rate of growing back out (M40-T6's value).</summary>
    public const float ReleaseRate = 3f;
    /// <summary>Ours: seconds over which the release rate ramps up from nothing, so the way out has no sudden start.</summary>
    public const float ReleaseRamp = 0.25f;
    /// <summary>Ours: m/s, the release's speed cap.</summary>
    public const float ReleaseMaxSpeed = 5f;

    /// <summary>Ours: metres; the crosshair's world point is never taken closer than this to the rig's camera.</summary>
    public const float FocusMin = 4f;
    /// <summary>Ours: metres; and never farther (a shot's reach in the open; the correction at this distance is under a tenth of a degree).</summary>
    public const float FocusMax = 100f;
    /// <summary>Ours: 1/s, how fast the focus distance follows the ray's hit (in log distance, so a hit jumping from far to near turns the camera over a few frames).</summary>
    public const float FocusRate = 12f;
    /// <summary>Ours: radians, the most the view is turned toward the focus (5 degrees).</summary>
    public const float AimMaxTurn = 5f * MathF.PI / 180f;

    /// <summary>The camera's place on its cast: 0 at the start, 1 at the wanted place.</summary>
    public float Fraction { get; set; } = 1f;
    float _free;   // seconds the cast has been free by the margin
    float _out;    // seconds the release has been running

    /// <summary>Back to the wanted place (a respawn).</summary>
    public void Reset() { Fraction = 1f; _free = 0f; _out = 0f; }

    /// <summary>
    /// One frame. <paramref name="soft"/> and <paramref name="hard"/> are the free fractions of the soft and hard casts, <paramref name="length"/> the cast's length in metres.
    /// Unplaced (a first frame, a cut): directly at <paramref name="soft"/>. The hard cast always clamps, at once. Returns <see cref="Fraction"/>.
    /// </summary>
    public float Step(float soft, float hard, float length, float delta, bool placed)
    {
        soft = Sane(soft); hard = Sane(hard);
        delta = MathF.Max(delta, 0f);
        length = MathF.Max(length, 1e-3f);
        float f = Fraction;
        if (!placed) { f = soft; _free = 0f; _out = 0f; }
        else if (soft < f)
        {
            float next = f + (soft - f) * (1f - MathF.Exp(-PullRate * delta));
            f = MathF.Max(MathF.Max(next, f - PullMaxSpeed * delta / length), soft);
            _free = 0f; _out = 0f;
        }
        else if ((soft - f) * length < ReleaseMargin && soft < 1f)
        {
            _free = 0f; _out = 0f;   // too little to be worth a move (the wanted place itself is always worth it, the last centimetres included)
        }
        else
        {
            _free += delta;
            if (_free >= ReleaseHold)
            {
                _out += delta;
                float ramp = MathF.Min(_out / ReleaseRamp, 1f);
                float step = (soft - f) * (1f - MathF.Exp(-ReleaseRate * ramp * delta));
                f += MathF.Min(step, ReleaseMaxSpeed * delta / length);
            }
        }
        if (hard < f) { f = hard; _free = 0f; _out = 0f; }
        Fraction = f;
        return f;
    }

    static float Sane(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

    /// <summary>The focus distance after one frame toward <paramref name="target"/>, both in metres from the rig's camera (see <see cref="FocusRate"/>); kept within <see cref="FocusMin"/>..<see cref="FocusMax"/>.</summary>
    public static float StepFocus(float focus, float target, float delta, bool placed)
    {
        target = float.IsFinite(target) ? Math.Clamp(target, FocusMin, FocusMax) : FocusMax;
        if (!placed || !float.IsFinite(focus) || focus <= 0f) return target;
        float k = 1f - MathF.Exp(-FocusRate * MathF.Max(delta, 0f));
        return Math.Clamp(MathF.Exp(MathF.Log(focus) + (MathF.Log(target) - MathF.Log(focus)) * k), FocusMin, FocusMax);
    }

    /// <summary>
    /// The direction from the camera at <paramref name="actual"/> that keeps the crosshair on the point the camera at <paramref name="rig"/> looks at <paramref name="focus"/> metres
    /// along <paramref name="view"/> (unit). Equal places: the view itself. A turn beyond <see cref="AimMaxTurn"/> stops there; a focus behind the camera leaves the view.
    /// </summary>
    public static Vector3 StableLook(Vector3 rig, Vector3 actual, Vector3 view, float focus)
    {
        var to = rig + view * focus - actual;
        float length = to.Length();
        if (length < 0.5f || !float.IsFinite(length)) return view;
        to /= length;
        float cos = Math.Clamp(Vector3.Dot(view, to), -1f, 1f);
        if (cos <= 0f) return view;
        float angle = MathF.Acos(cos);
        if (angle <= AimMaxTurn) return to;
        var side = to - view * cos;
        float sideLength = side.Length();
        if (sideLength < 1e-6f) return view;
        return Vector3.Normalize(view * MathF.Cos(AimMaxTurn) + side / sideLength * MathF.Sin(AimMaxTurn));
    }
}
