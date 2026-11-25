using System;
using Godot;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X6 on the HUD (forwards in <c>Hud.ExecutionsHooks.cs</c>, only with <see cref="SmSim.ContentExtras.Executions"/>): the armour tint (<see cref="ExecutionsArmourTint"/>). While the own
/// armour is above its bar (Warfront's overshield) the armour bar is modulated toward <see cref="OvershieldTint"/> in proportion to the overshield's fraction of its cap times the overshield look's
/// strength (<see cref="OvershieldShell.Strength"/>, the one tunable shared with the shell), so it fades back as the overshield goes; White (the movie's own colours) otherwise, and always in Classic PvP.
/// No Execute text is drawn (owner 2026-11-25: the world-space red dot, <see cref="ExecutionMarker"/>, replaced the text prompt), so the X5a prompt seam (<c>ExecutionsHud</c>) stays empty.
/// </summary>
public sealed partial class Hud
{
    /// <summary>The armour bar's modulate at a full overshield and strength 1 (RGB; ours, the owner's "deeper blue armour HUD"; spec/behavior.md "camera, input, HUD and shield (X6)").</summary>
    public static readonly Color OvershieldTint = new(0.36f, 0.56f, 1f);

    /// <summary>The armour tint applied this frame (White: none; the self-test reads it).</summary>
    public Color ExecutionArmourTintLast { get; private set; } = White;
    /// <summary>The X6 test aids' overshield (a fraction of the cap; null: the own record's), for the tint only.</summary>
    public static float? ExecutionsTestOvershield { get; set; }
    /// <summary>The armour bar's tint hook has run (the movie's bar is drawn in this run).</summary>
    public bool ExecutionsTintCalled { get; private set; }

    /// <summary>The armour bar's modulate for <paramref name="shield"/> of <paramref name="shieldMax"/>: White at or under the bar, else toward <see cref="OvershieldTint"/> by the overshield's fraction.</summary>
    public static Color ArmourTintFor(float shield, float shieldMax)
    {
        float f = ExecutionCameraMath.OvershieldFraction(shield, shieldMax);
        return f > 0f ? White.Lerp(OvershieldTint, Math.Clamp(f * OvershieldShell.Strength, 0f, 1f)) : White;
    }

    partial void ExecutionsArmourTint(ref Color tint)
    {
        var d = Data;
        float shield = ExecutionsTestOvershield is { } f ? d.ShieldMax * (1f + Math.Clamp(f, 0f, 1f) * ExecutionRules.OvershieldBars) : d.Shield;
        tint = d.Alive ? ArmourTintFor(shield, d.ShieldMax) : White;
        ExecutionArmourTintLast = tint;
        ExecutionsTintCalled = true;
    }
}
