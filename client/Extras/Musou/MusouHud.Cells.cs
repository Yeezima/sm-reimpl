using System;
using System.Globalization;
using Godot;

namespace SmClient;

/// <summary>What an ability cell shows (its glyph and name).</summary>
public enum AbilityKind { Artillery, DropPod, Shockwave, Pull }

/// <summary>One ability's cell on the Musou HUD (owner 2026-11-12): the call or cast, its key, the seconds of its cooldown left and in all, whether it is locked.</summary>
public struct AbilityCell
{
    public bool Shown, Locked;
    public AbilityKind Kind;
    public string Key;
    public float Left, Total;
}

/// <summary>
/// The ability cooldown cells (owner 2026-11-12: "rework the ability cooldown icons to fit perfectly uniformly to the left side of the grenade/equipment HUD. They should match in visual aesthetic and style as well"):
/// the support call (B: the artillery strike or the drop pod) and the Librarian's Shockwave (and the restore switch's Pull), each a cell the size of the equipment column (or the Librarian's own column), in its dark
/// chamfered panel with the thin gold line, in a row flush left of the column in the weapon box's tilted frame (<see cref="MusouHudLayout.CellFrame"/>), the same gap between them as the column has to the weapon box,
/// bottom edges on the box's line. In a cell: the key at its top left, the glyph, the seconds left in the weapon box's large thin white digits (READY when it is back), the name, and a bar along the bottom that
/// fills as the cooldown runs out (the weapon box's bar is the same white). Layout, glyphs and sizes ours (kind: ours; the original has no such cells). <c>--musou-old-hud-layout</c> restores the Librarian's cell
/// and draws no others.
/// </summary>
public sealed partial class MusouHud
{
    static readonly Color DimGold = new(0.45f, 0.4f, 0.2f, 0.9f);
    static readonly Color Psychic = new(0.62f, 0.8f, 1f);
    static readonly Color GlyphReady = new(0.93f, 0.85f, 0.55f), GlyphCooling = new(0.62f, 0.58f, 0.42f, 0.85f);
    static readonly Color[] CellFill = [PanelFill, PanelFill, new(0f, 0f, 0f, 0.86f), new(0f, 0f, 0f, 0.9f), new(0f, 0f, 0f, 0.9f), new(0f, 0f, 0f, 0.9f), PanelFill, PanelFill];
    readonly Vector2[] _cell = new Vector2[8], _cellClosed = new Vector2[9];
    readonly string[] _cellSeconds = new string[3];
    readonly int[] _cellSecondsFor = [-1, -1, -1];

    void DrawAbilityCells(Vector2 vs)
    {
        if (!MusouHudLayout.Corners) return;
        int count = 0;
        foreach (var c in Data.Cells) if (c.Shown && Data.Alive) count++;
        float scale = Size.Y / RefHeight;
        var m = Data.LibrarianBox ? MusouHudLayout.LibrarianCells : MusouHudLayout.MovieCells;
        var frame = MusouHudLayout.CellFrame(vs, Data.LibrarianBox);
        MusouHudLayout.CellsLeftEdge = frame.Origin.X + (count == 0 ? 0f : MusouHudLayout.CellRect(m, count - 1).Position.X) - 4f;   // the row's left end (the turn moves its lower end 4 left at most)
        if (count == 0) return;
        var baseXf = new Transform2D(0f, new Vector2(scale, scale), 0f, Vector2.Zero);
        DrawSetTransformMatrix(baseXf * frame);
        int slot = 0;
        for (int i = 0; i < Data.Cells.Length; i++)
        {
            if (!Data.Cells[i].Shown) continue;
            DrawCell(i, Data.Cells[i], MusouHudLayout.CellRect(m, slot++));
        }
        DrawSetTransformMatrix(baseXf);
    }

    void DrawCell(int index, in AbilityCell c, Rect2 r)
    {
        bool cooling = c.Left > 0f, ready = !cooling && !c.Locked;
        // The panel: the equipment column's dark fill and thin gold line, its four corners cut; the line dims while the call is not ready (the column's dim line at no grenades).
        const float cut = 8f;
        float x = r.Position.X, y = r.Position.Y, w = r.Size.X, h = r.Size.Y;
        _cell[0] = new(x + cut, y); _cell[1] = new(x + w - cut, y); _cell[2] = new(x + w, y + cut); _cell[3] = new(x + w, y + h - cut);
        _cell[4] = new(x + w - cut, y + h); _cell[5] = new(x + cut, y + h); _cell[6] = new(x, y + h - cut); _cell[7] = new(x, y + cut);
        DrawPolygon(_cell, CellFill);
        for (int i = 0; i < 8; i++) _cellClosed[i] = _cell[i];
        _cellClosed[8] = _cell[0];
        DrawPolyline(_cellClosed, ready ? FrameGold : DimGold, 2f, true);

        float cx = x + w / 2f;
        Txt(c.Key ?? "", x + 11f, y + 22f, 15, ready ? Gold : Grey, -1, 2, true);
        DrawGlyph(c.Kind, new Vector2(cx, y + h * 0.36f), c.Locked ? new Color(0.5f, 0.1f, 0.1f) : cooling ? GlyphCooling : GlyphReady);
        float valueY = y + h * 0.72f;
        if (c.Locked) Txt("LOCKED", cx, valueY - 4f, 15, RedText, 0, 2, true);
        else if (cooling)
        {
            int secs = (int)MathF.Ceiling(c.Left);
            if (_cellSecondsFor[index] != secs) { _cellSecondsFor[index] = secs; _cellSeconds[index] = secs.ToString(CultureInfo.InvariantCulture); }
            Txt(_cellSeconds[index], cx, valueY, 36, White, 0, 2);   // the weapon box's large thin digits
        }
        else Txt("READY", cx, valueY - 4f, 17, c.Kind is AbilityKind.Shockwave or AbilityKind.Pull ? Psychic : Gold, 0, 2, true);
        Txt(NameOf(c.Kind), cx, y + h - 24f, 12, Grey, 0, 2, true);
        // The bar along the bottom: the weapon box's white, full when the call is ready, filling as the cooldown runs out.
        float f = c.Locked ? 0f : cooling && c.Total > 0f ? Math.Clamp(1f - c.Left / c.Total, 0f, 1f) : 1f;
        DrawRect(new Rect2(x + 9f, y + h - 15f, w - 18f, 6f), new Color(0f, 0f, 0f, 0.65f));
        if (f > 0f) DrawRect(new Rect2(x + 9f, y + h - 15f, (w - 18f) * f, 6f), new Color(White, ready ? 0.95f : 0.8f));
    }

    static string NameOf(AbilityKind k) => k switch { AbilityKind.Artillery => "STRIKE", AbilityKind.DropPod => "DROP POD", AbilityKind.Shockwave => "SHOCKWAVE", _ => "PULL" };

    readonly Vector2[] _glyph = new Vector2[5];
    static readonly Vector2[] Cross = [new(0f, -1f), new(1f, 0f), new(0f, 1f), new(-1f, 0f)];
    static readonly float[] Sides = [-1f, 1f];

    /// <summary>The glyphs: a target ring with a cross for the artillery strike, a pod falling in flame for the drop pod, rings spreading for the Shockwave, arrows drawn in for the Pull.</summary>
    void DrawGlyph(AbilityKind kind, Vector2 c, Color col)
    {
        switch (kind)
        {
            case AbilityKind.Artillery:
                DrawArc(c, 12f, 0f, MathF.Tau, 28, col, 2f, true);
                foreach (var d in Cross) DrawLine(c + d * 7f, c + d * 17f, col, 2f, true);
                DrawCircle(c, 2.5f, col);
                break;
            case AbilityKind.DropPod:
                _glyph[0] = c + new Vector2(-6f, -9f); _glyph[1] = c + new Vector2(6f, -9f); _glyph[2] = c + new Vector2(8f, 4f); _glyph[3] = c + new Vector2(0f, 14f); _glyph[4] = c + new Vector2(-8f, 4f);
                DrawColoredPolygon(_glyph, col);
                DrawLine(c + new Vector2(-4f, -13f), c + new Vector2(-4f, -20f), col, 2f, true);
                DrawLine(c + new Vector2(4f, -13f), c + new Vector2(4f, -20f), col, 2f, true);
                break;
            case AbilityKind.Shockwave:
                DrawCircle(c, 4f, col);
                DrawArc(c, 10f, 0f, MathF.Tau, 24, col, 2f, true);
                DrawArc(c, 16f, 0f, MathF.Tau, 32, new Color(col, col.A * 0.6f), 2f, true);
                break;
            default:
                foreach (float s in Sides)
                {
                    DrawLine(c + new Vector2(s * 16f, 0f), c + new Vector2(s * 5f, 0f), col, 2f, true);
                    DrawLine(c + new Vector2(s * 10f, -5f), c + new Vector2(s * 5f, 0f), col, 2f, true);
                    DrawLine(c + new Vector2(s * 10f, 5f), c + new Vector2(s * 5f, 0f), col, 2f, true);
                }
                break;
        }
    }
}
