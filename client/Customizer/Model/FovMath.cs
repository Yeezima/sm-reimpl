using System;

namespace SmClient.Customizer.Model;

/// <summary>
/// The field-of-view option (ui/fov-slider, an added option: ours): the slider shows a HORIZONTAL angle at the window's aspect ratio (the usual
/// convention), from the data's look (the exploration camera sets' <c>field_of_view</c>, 40 degrees, read as vertical) up to 120. Godot's Camera3D takes a
/// vertical angle, so the camera multiplies <c>tan(v / 2)</c> of the normal third-person and the aim cameras by <see cref="Scale"/>; scope zoom levels are
/// not scaled. Engine-free.
/// </summary>
public static class FovMath
{
    /// <summary>The exploration camera sets' <c>field_of_view</c> (vertical: interpretation), the slider's minimum.</summary>
    public const float DataVertical = 40f;
    public const float Maximum = 120f;

    /// <summary>The saved choice (horizontal degrees; 0 = the data's look), set by <c>CustomizerServices</c> and read by the camera every frame.</summary>
    public static float Selected { get; set; }

    public static float Aspect(float width, float height) => height > 1f && width > 1f ? Math.Clamp(width / height, 0.5f, 6f) : 16f / 9f;

    public static float ToHorizontal(float verticalDegrees, float aspect) => Deg(2f * MathF.Atan(MathF.Tan(Rad(verticalDegrees) / 2f) * aspect));
    public static float ToVertical(float horizontalDegrees, float aspect) => Deg(2f * MathF.Atan(MathF.Tan(Rad(horizontalDegrees) / 2f) / aspect));

    /// <summary>The slider's minimum at this aspect: the data's vertical angle as a horizontal one, rounded up to a whole degree.</summary>
    public static float MinimumHorizontal(float aspect) => MathF.Min(MathF.Ceiling(ToHorizontal(DataVertical, aspect)), Maximum - 1f);

    /// <summary>The saved value limited to the slider's range at this aspect; 0 (unset) is the minimum.</summary>
    public static float Clamp(float horizontalDegrees, float aspect) => float.IsFinite(horizontalDegrees) && horizontalDegrees > 0f
        ? Math.Clamp(horizontalDegrees, MinimumHorizontal(aspect), Maximum) : MinimumHorizontal(aspect);

    /// <summary>The factor on <c>tan(vertical / 2)</c> of the data's camera angles; 1 at the minimum (and for an unset choice).</summary>
    public static float Scale(float selectedHorizontal, float aspect)
    {
        if (!float.IsFinite(selectedHorizontal) || selectedHorizontal <= 0f) return 1f;
        float h = Clamp(selectedHorizontal, aspect);
        float scale = MathF.Tan(Rad(ToVertical(h, aspect)) / 2f) / MathF.Tan(Rad(DataVertical) / 2f);
        return h <= MinimumHorizontal(aspect) ? 1f : MathF.Max(1f, scale);
    }

    /// <summary>
    /// Ours (estimate, camera/right-shoulder-fov, owner 2026-11-02): metres the camera moves right per unit of <see cref="Scale"/> over 1. Holding the character where the default view draws
    /// it would take the rig's whole 0.64 m per unit; this modest part keeps the crosshair's surroundings clearer without leaving the shoulder (0.34 m at 120 degrees at 16:9).
    /// </summary>
    public const float ShiftPerScale = 0.2f;
    /// <summary>Ours (estimate): the most the camera moves right; a narrower window has a larger <see cref="Scale"/> at the same slider value.</summary>
    public const float MaxShift = 0.35f;
    /// <summary>Ours: metres kept inside the server's shot origin limit by <see cref="LimitShift"/> (the drawn player and the predicted one differ a little).</summary>
    public const float OriginMargin = 0.1f;

    /// <summary>Metres to the right of the rig's place for this <see cref="Scale"/>: 0 at the default view, growing with the slider up to <see cref="MaxShift"/>. The view stays parallel, so the crosshair and the shot move with it.</summary>
    public static float CameraShift(float scale) => float.IsFinite(scale) ? MathF.Min(MaxShift, ShiftPerScale * MathF.Max(0f, scale - 1f)) : 0f;

    /// <summary>
    /// <paramref name="shift"/> cut so the crosshair's ray stays within <paramref name="limit"/> metres of the eye: the server shortens a farther shot origin, which would put the shot beside the
    /// crosshair. <paramref name="side"/> and <paramref name="across"/> are the unshifted ray's offsets from the eye to the right and across the view in the vertical plane. Never below 0.
    /// </summary>
    public static float LimitShift(float shift, float side, float across, float limit) =>
        MathF.Max(0f, MathF.Min(shift, MathF.Sqrt(MathF.Max(0f, limit * limit - across * across)) - side));

    /// <summary>A data vertical angle widened by <paramref name="scale"/>.</summary>
    public static float Apply(float verticalDegrees, float scale) => scale == 1f ? verticalDegrees : Deg(2f * MathF.Atan(MathF.Tan(Rad(verticalDegrees) / 2f) * scale));

    static float Rad(float d) => d * MathF.PI / 180f;
    static float Deg(float r) => r * 180f / MathF.PI;
}
