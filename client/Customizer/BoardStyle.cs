using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The steel look of the boards that draw themselves with <c>_Draw</c> (ui/overhaul): the Tab scoreboard of every mode and any other table drawn on a stage (the Warfront XP board, picker and hub panels use it
/// too). One palette, one lettering (<see cref="UiKit.ConceptFont"/>) and the painted pieces of the redesigned menus: the concept's rivetted plate for a big number or a title, a recessed steel panel for a table,
/// a dark steel strip for its header, warm-black rows with a gold wash and bar for the player's own. Every call draws into the control that passes itself; <paramref name="scale"/> is the stage's scale
/// (stage pixels to window pixels). Ours (docs/ui-overhaul.md).
/// </summary>
public sealed class BoardStyle
{
    public static readonly Color Panel = new(0.03f, 0.03f, 0.03f, 0.9f), RowA = new(0.045f, 0.043f, 0.04f, 0.9f), RowB = new(0.075f, 0.072f, 0.066f, 0.9f),
        Rule = new(0.55f, 0.55f, 0.52f, 0.16f), Text = Steel.Text, Dim = Steel.TextDim, Header = Steel.GoldHi, MeFill = new(0.97f, 0.76f, 0.24f, 0.16f), MeBar = Steel.Gold, Gold = Steel.Gold;

    /// <summary>The one lettering of every board.</summary>
    public static FontVariation Font => UiKit.ConceptFont();

    readonly Dictionary<int, StyleBoxTexture> _plates = new();
    readonly Dictionary<(Steel.Kind, int), Steel.SteelStyle> _styles = new();

    /// <summary>The concept's painted plate (rivets and bevel) stretched over <paramref name="r"/> at the buttons' scale; the steel plate when the art is missing.</summary>
    public void Plate(Control c, Rect2 r, float scale)
    {
        int key = (int)MathF.Round(scale * 100f);
        if (!_plates.TryGetValue(key, out var box))
        {
            float k = 0.62f * scale;
            var art = UiKit.ConceptScaled("opt-plate-blank", k);
            if (art is null) { Box(c, Steel.Kind.Plate, r, 14f, scale); return; }
            box = _plates[key] = new StyleBoxTexture { Texture = art, TextureMarginLeft = 70f * k, TextureMarginRight = 70f * k, TextureMarginTop = 50f * k, TextureMarginBottom = 50f * k, DrawCenter = true };
        }
        box.Draw(c.GetCanvasItem(), r);
    }

    readonly Dictionary<int, StyleBoxTexture> _goldPlates = new();

    /// <summary>The concept's gold plate (the focused or selected state of <see cref="Plate"/>), for a selected tab: dark ink goes on it.</summary>
    public void GoldPlate(Control c, Rect2 r, float scale)
    {
        int key = (int)MathF.Round(scale * 100f);
        if (!_goldPlates.TryGetValue(key, out var box))
        {
            float k = 0.62f * scale;
            var art = UiKit.ConceptScaled("opt-plate-gold-blank", k);
            if (art is null) { Box(c, Steel.Kind.Gold, r, 14f, scale); return; }
            box = _goldPlates[key] = new StyleBoxTexture { Texture = art, TextureMarginLeft = 70f * k, TextureMarginRight = 70f * k, TextureMarginTop = 50f * k, TextureMarginBottom = 50f * k, DrawCenter = true };
        }
        box.Draw(c.GetCanvasItem(), r);
    }

    /// <summary>A steel box of the kit (cached per kind and scale).</summary>
    public void Box(Control c, Steel.Kind kind, Rect2 r, float chamfer, float scale)
    {
        var key = (kind, (int)MathF.Round(scale * 100f));
        if (!_styles.TryGetValue(key, out var style)) _styles[key] = style = new Steel.SteelStyle(kind, MathF.Max(2f, chamfer * scale), 0f, 0f);
        style.Draw(c.GetCanvasItem(), r);
    }

    /// <summary>A table's panel: the recessed steel lip round <paramref name="r"/> (grown by 6 stage pixels) and the dark body.</summary>
    public void Table(Control c, Rect2 r, float scale)
    {
        Box(c, Steel.Kind.Recess, r.Grow(6f * scale), 10f, scale);
        c.DrawRect(r, Panel);
    }

    /// <summary>The header strip of a table.</summary>
    public void HeaderStrip(Control c, Rect2 r, float scale) => Box(c, Steel.Kind.Dark, new Rect2(r.Position + new Vector2(2f, 2f) * scale, r.Size - new Vector2(4f, 2f) * scale), 6f, scale);

    /// <summary>A row of a table: alternate warm black, a hairline on top, the own row washed gold with a gold bar at its left.</summary>
    public void Row(Control c, Rect2 r, bool alternate, bool me, float scale)
    {
        var inner = new Rect2(r.Position + new Vector2(2f * scale, 0f), r.Size - new Vector2(4f * scale, 0f));
        c.DrawRect(inner, alternate ? RowB : RowA);
        c.DrawLine(inner.Position, new Vector2(inner.End.X, inner.Position.Y), Rule, 1f);
        if (!me) return;
        c.DrawRect(inner, MeFill);
        c.DrawRect(new Rect2(inner.Position, new Vector2(5f * scale, inner.Size.Y)), MeBar);
    }

    /// <summary>The text colour of a row's cells.</summary>
    public static Color Ink(bool me) => me ? Steel.GoldHi : Text;
}
