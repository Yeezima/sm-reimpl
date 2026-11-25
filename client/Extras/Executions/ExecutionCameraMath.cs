using System;
using System.Numerics;

namespace SmClient;

/// <summary>Executions X6: the camera's engine-free maths (tested in <c>ExecutionPromptRuleTests</c>).</summary>
public static class ExecutionCameraMath
{
    /// <summary>
    /// The view (sim yaw and pitch, radians) that looks along <paramref name="forward"/> (game space, Z up): the game's forward at yaw y is (-sin y, cos y), a positive pitch looks up. A forward with no
    /// horizontal part (straight up or down) keeps <paramref name="yawBefore"/>. The orbit's exit hands the view its heading with this.
    /// </summary>
    public static (float Yaw, float Pitch) ViewOf(Vector3 forward, float yawBefore)
    {
        if (!float.IsFinite(forward.X) || !float.IsFinite(forward.Y) || !float.IsFinite(forward.Z)) return (yawBefore, 0f);
        float flat = MathF.Sqrt(forward.X * forward.X + forward.Y * forward.Y);
        float yaw = flat > 1e-4f ? MathF.Atan2(-forward.X, forward.Y) : yawBefore;
        float pitch = MathF.Atan2(forward.Z, MathF.Max(flat, 1e-6f));
        return (yaw, pitch);
    }

    /// <summary>The share of the overshield's cap <paramref name="shield"/> holds above <paramref name="shieldMax"/> (0..1), the shell's and the tint's strength.</summary>
    public static float OvershieldFraction(float shield, float shieldMax)
    {
        float cap = SmSim.Extras.Executions.ExecutionRules.OvershieldCap(shieldMax);
        return cap > 0f && float.IsFinite(shield) ? Math.Clamp(SmSim.Extras.Executions.ExecutionRules.Overshield(shield, shieldMax) / cap, 0f, 1f) : 0f;
    }
}
