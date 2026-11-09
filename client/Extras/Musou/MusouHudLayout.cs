using System;
using Godot;

namespace SmClient;

/// <summary>
/// Musou's HUD layout (owner, 2026-11-10: "move all musou mode specific UI elements to the edges of the screen, prioritizing the corners nd minimizing impact on free screen
/// space to view the game world"): the capture tiles and the on-point bar top left, the commander's and the Boss Orks' bars bottom left (the team score panel's corner, empty in
/// Musou), the KO chain bottom right above the weapon box, the call-outs in a strip along the top edge above the armour bar, the prompts in a strip along the bottom edge.
/// <c>--musou-old-hud</c> (test aid and rollback) puts every one back where it stood before. Musou only: the match HUD (<see cref="Hud"/>) is not touched. Positions ours (kind: ours).
/// </summary>
public static class MusouHudLayout
{
    /// <summary><c>--musou-old-hud</c>: the layout before 2026-11-10 (boss bar top centre, chain right of centre, call-outs, prompts and the on-point bar in the middle).</summary>
    public static readonly bool Old = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-old-hud") >= 0;

    /// <summary>The left margin of the corner panels and the bottom edge's margin under the bottom-left bars (1080-high virtual units).</summary>
    public const float Left = 40f, BottomMargin = 48f;

    /// <summary>
    /// The own armour and health bar (the match HUD's, <see cref="Hud.Frame.VitalsCorner"/>; owner 2026-11-10 "Bottom left, approve", then "put your player armor and health in the bottom left, then
    /// the boss health bars above that"): its box's left edge at <see cref="Left"/>, its bottom 40 above the screen's bottom, at 0.65 of its size (about 385 wide); the boss column stands over it.
    /// </summary>
    public static readonly Godot.Vector3 VitalsCorner = new(Left - 6f, 40f, 0.65f);
}
