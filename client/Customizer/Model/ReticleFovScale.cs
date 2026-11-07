using System;

namespace SmClient.Customizer.Model;

/// <summary>
/// ui/crosshair-fov-scale (ours, owner 2026-11-07, playtester feedback): the HUD crosshair covers the same ANGLE on screen at every field of view. At the original look (the FOV slider's
/// minimum, <see cref="FovMath.Scale"/> = 1) it keeps its size exactly; a wider view makes it smaller and a narrower one larger, by <c>tan(reference / 2) / tan(current / 2)</c>. The
/// reference is the same camera state (hip, aim, weapon, blend) drawn at the default setting, so aiming down the sights alone never resizes the crosshair. Engine-free.
/// </summary>
public static class ReticleFovScale
{
    /// <summary>The smallest and largest factor (a guard against a degenerate FOV).</summary>
    public const float Min = 0.2f, Max = 4f;

    /// <summary><c>tan(reference/2) / tan(current/2)</c> for two vertical angles in degrees; 1 when equal or when either is not a usable angle.</summary>
    public static float Between(float referenceDegrees, float currentDegrees)
    {
        if (!Usable(referenceDegrees) || !Usable(currentDegrees)) return 1f;
        float k = MathF.Tan(Rad(referenceDegrees) / 2f) / MathF.Tan(Rad(currentDegrees) / 2f);
        return float.IsFinite(k) ? Math.Clamp(k, Min, Max) : 1f;
    }

    /// <summary>The factor for the FOV slider alone at <paramref name="aspect"/>: the data's vertical angle against the one the slider gives (1 at the default).</summary>
    public static float ForSetting(float selectedHorizontal, float aspect) =>
        Between(FovMath.DataVertical, FovMath.Apply(FovMath.DataVertical, FovMath.Scale(selectedHorizontal, aspect)));

    static bool Usable(float d) => float.IsFinite(d) && d > 1f && d < 179f;
    static float Rad(float d) => d * MathF.PI / 180f;
}
