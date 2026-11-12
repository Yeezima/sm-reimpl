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
    public static Godot.Vector3 VitalsCorner => Corners ? new(VitalsBoxLeft, VitalsBoxBottom, VitalsScale) : new(Left - 6f, 40f, 0.65f);

    /// <summary>
    /// <c>--musou-old-hud-layout</c> (restore switch, owner 2026-11-12): the own bar at 0.65 of its size with its margins of 2026-11-10, the weapon and equipment panel where the match HUD puts it (115 right,
    /// 66 up) and the Librarian's cells as they were; no ability cells beside the panel. Off by default.
    /// </summary>
    public static readonly bool OldCorners = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-old-hud-layout") >= 0;

    /// <summary>The corner layout of 2026-11-12 is on (neither restore switch is given).</summary>
    public static bool Corners => !Old && !OldCorners;

    /// <summary>
    /// The one margin of both bottom corners (owner 2026-11-12: "They should match in distance from the edges of the screen in their respective corners"): the distance from the screen's edge to the
    /// outermost visible pixel of the own armour and health bar (left edge, bottom edge; bottom left) and of the weapon and equipment panel's gold frame (right edge, bottom edge; bottom right), in the
    /// 1080-high virtual units (one pixel at 1080 high, scaling with the height; the corners stay anchored at any aspect ratio). Measured on windowed shots at 1920 x 1080 (milestones, 2026-11-12). Ours.
    /// </summary>
    public const float CornerMargin = 44f;

    /// <summary>The panel's margins as <see cref="Hud.Frame.WeaponBoxCorner"/> (null with the restore switches: the match HUD's own place).</summary>
    public static Godot.Vector2? WeaponBoxCorner => Corners ? new Godot.Vector2(CornerMargin, CornerMargin) : null;

    /// <summary>The own bar's size, 10 % over the 0.65 it had (owner 2026-11-12: "Scale up the health and armor HUD in musou mode only by 10%").</summary>
    public const float VitalsScale = 0.65f * 1.1f;

    /// <summary>
    /// The bar's box (<see cref="Hud.Frame.VitalsCorner"/>: its left and bottom edge, the box is the bar plus a small margin whose width and height scale with the bar) such that the bar's outermost visible
    /// pixels stand <see cref="CornerMargin"/> from the edges: the visible bar starts 10 and ends 3 units inside the box at 0.65, which grow with the scale. The anchor stays the box's corner, so the
    /// margins hold at any scale.
    /// </summary>
    const float VitalsBoxLeft = CornerMargin - 10f * VitalsScale / 0.65f, VitalsBoxBottom = CornerMargin - 3f * VitalsScale / 0.65f;

    /// <summary>The own bar's box height at its scale (the stage box's 40 pixels at 1.5 virtual units each).</summary>
    public const float VitalsBoxHeight = 40f * 1.5f * VitalsScale;

    /// <summary>The own bar's box width at its scale (the stage box's 395 pixels).</summary>
    public const float VitalsBoxWidth = 395f * 1.5f * VitalsScale;

    // ---------------------------------------------------------------- the ability cells (owner 2026-11-12)

    /// <summary>The cells' size and the gap, in the units of the panel's own frame (the equipment column's width and height and the gap that column has to the weapon box).</summary>
    public readonly record struct CellMetrics(float Width, float Height, float Gap);

    /// <summary>The weapon box's tilt (Hud.Fidelity.cs BoxTilt; the cells share it so they stand flush on its line).</summary>
    public const float Tilt = 2.5f;

    /// <summary>
    /// The equipment column's outer bottom-left corner of the match HUD's movie box in its original place, from the screen's bottom right (virtual units), measured on a 1920 x 1080 shot: the cells' row
    /// starts there. The column is 85 wide and 147 tall (along the box's tilted axes) and 10 from the weapon box.
    /// </summary>
    static readonly Godot.Vector2 MovieColumnCorner = new(-603.6f, -87.2f);

    public static readonly CellMetrics MovieCells = new(85f, 147f, 10f), LibrarianCells = new(80f, 122f, 6f);

    /// <summary>
    /// The frame of the cells' row (local units to virtual units): its origin the bottom-left corner of the equipment column (the match HUD's grenade box for the Librarian, who carries no gun and
    /// has his own box: <see cref="LibrarianHud"/>), its x axis along the box's tilted bottom edge, y down. Cell k stands at x from -(k + 1) * (width + gap) + ... to the left of the column, y from -height to 0.
    /// </summary>
    public static Godot.Transform2D CellFrame(Godot.Vector2 vs, bool librarian)
    {
        var tilt = Godot.Mathf.DegToRad(Tilt);
        if (!librarian)
        {
            var shift = Hud.MovieBoxMargin - new Godot.Vector2(CornerMargin, CornerMargin);
            return new Godot.Transform2D(tilt, new Godot.Vector2(vs.X, vs.Y) + MovieColumnCorner + shift);
        }
        // The Librarian's box: the match HUD's M44 box frame (origin 514 left of the right edge, 248 above the bottom), the column 86 left of its origin (80 wide, 122 tall).
        var origin = new Godot.Vector2(vs.X - 514f, vs.Y - 248f) + LibrarianShift;
        return new Godot.Transform2D(tilt, origin) * new Godot.Transform2D(0f, new Godot.Vector2(-86f, 122f));
    }

    /// <summary>The Librarian's box (and the match HUD's grenade box beside it) moved so the box's gold frame stands <see cref="CornerMargin"/> from the corner (the M44 frame's outermost corners).</summary>
    public static Godot.Vector2 LibrarianShift => Corners ? Hud.ClassicBoxMargin - new Godot.Vector2(CornerMargin, CornerMargin) : Godot.Vector2.Zero;

    /// <summary>The local rectangle of cell <paramref name="k"/> (0 is the one next to the equipment column) in <see cref="CellFrame"/>.</summary>
    public static Godot.Rect2 CellRect(CellMetrics m, int k) => new(-(k + 1) * (m.Width + m.Gap), -m.Height, m.Width, m.Height);

    /// <summary>The left edge of the leftmost ability cell (or of the equipment column when there is none), virtual units; set each frame by <see cref="MusouHud"/>. Infinity before the first frame.</summary>
    public static float CellsLeftEdge = float.PositiveInfinity;

    /// <summary>
    /// How far the bottom strip's prompts (the war's prompt, the pickup hold, the weapon-drop prompt; centred on the screen) move left so that, on a narrow screen (4:3), the widest of them
    /// (<paramref name="halfWidth"/> either side of the centre) stays 16 units clear of the cells; 0 on wide screens and with the restore switches.
    /// </summary>
    public static float PromptShift(float screenWidth, float halfWidth = 190f) => Corners && !float.IsPositiveInfinity(CellsLeftEdge) ? MathF.Min(0f, CellsLeftEdge - 16f - (screenWidth / 2f + halfWidth)) : 0f;
}
