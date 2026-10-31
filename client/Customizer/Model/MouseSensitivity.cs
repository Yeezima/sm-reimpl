using System;

namespace SmClient.Customizer.Model;

/// <summary>
/// The mouse sensitivity option (ui/mouse-sensitivity, a client convenience: ours): a factor on the mouse look scale of the camera tuning (<c>speed_mouse</c>), the same in hip fire,
/// aim and scope; 1.0 is the original feel (the default). The gamepad's look is not scaled. Engine-free.
/// </summary>
public static class MouseSensitivity
{
    public const float Minimum = 0.1f, Maximum = 3f, Step = 0.05f, Default = 1f;

    /// <summary>The saved choice, set by <c>CustomizerServices</c> and read by the camera on every mouse movement.</summary>
    public static float Selected { get; set; } = Default;

    /// <summary>The camera tuning's mouse look scale times the player's sensitivity.</summary>
    public static float Look(float cameraScale) => cameraScale * Selected;

    /// <summary>The value limited to the range and snapped to the step; a missing or non-finite value is the default.</summary>
    public static float Clamp(float value) => float.IsFinite(value) ? MathF.Round(Math.Clamp(value, Minimum, Maximum) / Step) * Step : Default;
}
