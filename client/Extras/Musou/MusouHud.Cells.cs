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
    public string Key;   // not drawn since the second pass (owner 2026-11-12: no unnecessary text in the cells); kept for the callers
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
        _cellXf = baseXf * frame;
        DrawSetTransformMatrix(_cellXf);
        int slot = 0;
        for (int i = 0; i < Data.Cells.Length; i++)
        {
            if (!Data.Cells[i].Shown) continue;
            DrawCell(i, Data.Cells[i], MusouHudLayout.CellRect(m, slot++));
        }
        DrawSetTransformMatrix(baseXf);
    }

    Transform2D _cellXf;

    /// <summary>The icon's height in a cell and the height of the number's band, local units: the icon and the band are spaced evenly (equal gaps above, between and below); CapRatio is the font's capital height over its size.</summary>
    const float IconHeight = 50f, BandHeight = 24f, CapRatio = 0.7f;

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

        // Inside: the icon and the number (or READY) centred on the cell's vertical axis, the three gaps (top, between, bottom) equal. No text but the number, READY or LOCKED (owner 2026-11-12).
        float cx = x + w / 2f, pad = (h - IconHeight - BandHeight) / 3f;
        float iconY = y + pad + IconHeight / 2f, bandY = y + pad * 2f + IconHeight + BandHeight / 2f;
        DrawIcon(c.Kind, new Vector2(cx, iconY), c.Locked ? new Color(0.5f, 0.1f, 0.1f) : cooling ? GlyphCooling : GlyphReady);
        if (c.Locked) Txt("LOCKED", cx, bandY + 17f * CapRatio / 2f, 17, RedText, 0, 2, true);
        else if (cooling)
        {
            int secs = (int)MathF.Ceiling(c.Left);
            if (_cellSecondsFor[index] != secs) { _cellSecondsFor[index] = secs; _cellSeconds[index] = secs.ToString(CultureInfo.InvariantCulture); }
            Txt(_cellSeconds[index], cx, bandY + 36f * CapRatio / 2f, 36, White, 0, 2);   // the weapon box's large thin digits
        }
        else Txt("READY", cx, bandY + 26f * CapRatio / 2f, 26, c.Kind is AbilityKind.Shockwave or AbilityKind.Pull ? Psychic : Gold, 0, 2, true);

        // The cooldown bar beneath the cell, outside it, the cell's width (owner 2026-11-12): the weapon box's white, full when ready, filling as the cooldown runs out.
        float f = c.Locked ? 0f : cooling && c.Total > 0f ? Math.Clamp(1f - c.Left / c.Total, 0f, 1f) : 1f;
        float by = y + h + MusouHudLayout.BarGap;
        DrawRect(new Rect2(x, by, w, MusouHudLayout.BarHeight), new Color(0f, 0f, 0f, 0.7f));
        if (f > 0f) DrawRect(new Rect2(x, by, w * f, MusouHudLayout.BarHeight), new Color(White, ready ? 0.95f : 0.8f));
        DrawRect(new Rect2(x, by, w, MusouHudLayout.BarHeight), ready ? FrameGold : DimGold, false, 1.5f);
    }
}

