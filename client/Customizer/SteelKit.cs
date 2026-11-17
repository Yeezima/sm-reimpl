using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The steel kit (ui/concept-redesign, the owner's four-panel concept of 2026-11-15): the colours, the vector-drawn metal box styles and the decorative controls (the wordmark, the winged
/// crest, the stone pillar) every redesigned screen is built from. Nothing here is a picture: every plate, bevel, rivet and feather is drawn from code at the size it is asked for, so
/// it is sharp at any window size and carries no art file. Ours (spec "UI concept redesign").
/// </summary>
/// <summary>
/// The painted pieces cut from the owner's concept image (client/assets/ui/*.png, embedded in the assembly; rebuilt by <see cref="UiTextureBuilder"/>; docs/ui-concept-textures.md). A piece that
/// is missing gives null, and the kit falls back to its code-drawn look.
/// </summary>
public static class ConceptArt
{
    static readonly Dictionary<string, Texture2D?> Cache = new();

    public static Texture2D? Get(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            using var stream = typeof(ConceptArt).Assembly.GetManifestResourceStream("Ui/" + name + ".png");
            if (stream is not null)
            {
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                var image = new Image();
                if (image.LoadPngFromBuffer(bytes) == Error.Ok) { image.GenerateMipmaps(); tex = ImageTexture.CreateFromImage(image); }
            }
        }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException) { GD.PrintErr("ui texture " + name + ": " + e.Message); }
        return Cache[name] = tex;
    }

    /// <summary>A nine-patch style of a painted piece, or null when the piece is missing. <paramref name="margin"/> is the stretchable-corner size in the texture's pixels, <paramref name="content"/> the text's margins.</summary>
    public static StyleBoxTexture? Patch(string name, Vector2 margin, Vector2 content, Color? modulate = null, bool center = true)
    {
        if (Get(name) is not { } tex) return null;
        var sb = new StyleBoxTexture
        {
            Texture = tex, TextureMarginLeft = margin.X, TextureMarginRight = margin.X, TextureMarginTop = margin.Y, TextureMarginBottom = margin.Y, ModulateColor = modulate ?? Colors.White,
            DrawCenter = center, AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
        };
        sb.ContentMarginLeft = content.X; sb.ContentMarginRight = content.X; sb.ContentMarginTop = content.Y; sb.ContentMarginBottom = content.Y;
        return sb;
    }

    /// <summary>The frame rings' corner sizes (texture pixels) and their ring thickness (UI pixels): the main menu's (panel 01) and Options' (panel 04), as UiTextureBuilder makes them.</summary>
    public static readonly Vector2 MainCorner = new(110f, 104f), OptionsCorner = new(115f, 92f);
    /// <summary>The wall frame (the owner's crop of panel 01, frame-wall.png, 594 x 414): ring thickness (left, top, right, bottom) and nine-patch margins (left, top, right, bottom), texture pixels; the older ring below is the fallback.</summary>
    public static readonly Vector4 WallRing = new(82f, 47f, 40f, 94f), WallMargins = new(170f, 110f, 150f, 140f);
    public static readonly Vector4 MainRing = new(47f, 42f, 47f, 42f), OptionsRing = new(26f, 24f, 26f, 24f);   // left, top, right, bottom
    public static readonly Color Warm = new(1.32f, 1.16f, 0.9f);
}

public static partial class Steel
{
    // Gold marks focus and selection; inactive rows stay dark (the owner's brief).
    public static readonly Color Gold = new(0.97f, 0.76f, 0.24f), GoldHi = new(1f, 0.91f, 0.58f), GoldDeep = new(0.66f, 0.40f, 0.07f), GoldEdge = new(0.28f, 0.15f, 0.02f);
    public static readonly Color Ink = new(0.07f, 0.045f, 0.015f);          // text on gold
    public static readonly Color Text = new(0.93f, 0.91f, 0.86f), TextDim = new(0.62f, 0.61f, 0.58f), TextOff = new(0.42f, 0.42f, 0.40f);
    public static readonly Color Edge = new(0.025f, 0.027f, 0.03f);         // the dark line round every plate
    public static readonly Color SteelHi = new(0.58f, 0.60f, 0.61f), SteelMid = new(0.30f, 0.31f, 0.33f), SteelLo = new(0.11f, 0.115f, 0.125f);
    public static readonly Color Recess = new(0.045f, 0.042f, 0.04f), Amber = new(0.62f, 0.34f, 0.06f);

    /// <summary>The theme type variations the kit adds (see <see cref="AddTheme"/>).</summary>
    public const string ButtonType = "SmSteelButton", TabType = "SmSteelTab", RowType = "SmSteelRow", LabelType = "SmSteelLabel", SliderType = "SmSteelSlider",
        FrameType = "SmSteelFrame", AmberFrameType = "SmSteelAmberFrame", RecessType = "SmSteelRecess", SmallType = "SmSteelSmall", PrimaryType = "SmSteelPrimary";

    /// <summary>Base text sizes (virtual 1920 x 1080 pixels) of the kit's text types; <see cref="UiKit.FitMenuText"/> raises them when the window is too small to read them.</summary>
    public const int ButtonSize = 27, TabSize = 23, RowSize = 21, SmallSize = 16;

    public static Color Mix(Color a, Color b, float t) => a.Lerp(b, Mathf.Clamp(t, 0f, 1f));

    // ---- primitives (RenderingServer canvas calls, so a StyleBox can draw them) ----

    /// <summary>The octagon of a rectangle with its four corners cut by <paramref name="c"/>.</summary>
    public static Vector2[] Chamfered(Rect2 r, float c)
    {
        c = Mathf.Min(c, Mathf.Min(r.Size.X, r.Size.Y) / 2f - 0.01f);
        if (c < 0f) c = 0f;
        float x = r.Position.X, y = r.Position.Y, w = r.Size.X, h = r.Size.Y;
        return [new(x + c, y), new(x + w - c, y), new(x + w, y + c), new(x + w, y + h - c), new(x + w - c, y + h), new(x + c, y + h), new(x, y + h - c), new(x, y + c)];
    }

    /// <summary>A chamfered plate filled with a top-to-bottom gradient.</summary>
    public static void Plate(Rid canvas, Rect2 r, float chamfer, Color top, Color bottom)
    {
        if (r.Size.X <= 0f || r.Size.Y <= 0f) return;
        var pts = Chamfered(r, chamfer);
        var colours = new Color[pts.Length];
        for (int i = 0; i < pts.Length; i++) colours[i] = top.Lerp(bottom, r.Size.Y <= 0f ? 0f : (pts[i].Y - r.Position.Y) / r.Size.Y);
        RenderingServer.CanvasItemAddPolygon(canvas, pts, colours);
    }

    public static void Plate(Rid canvas, Rect2 r, float chamfer, Color flat) => Plate(canvas, r, chamfer, flat, flat);

    public static void Line(Rid canvas, Vector2 a, Vector2 b, Color colour, float width = 1f) => RenderingServer.CanvasItemAddLine(canvas, a, b, colour, width);

    public static void Fill(Rid canvas, Rect2 r, Color colour) { if (r.Size.X > 0f && r.Size.Y > 0f) RenderingServer.CanvasItemAddRect(canvas, r, colour); }

    public static void Dot(Rid canvas, Vector2 at, float radius, Color colour) => RenderingServer.CanvasItemAddCircle(canvas, at, radius, colour);

    /// <summary>A rivet: a dark ring, a lit dome and a pin-point of light.</summary>
    public static void Rivet(Rid canvas, Vector2 at, float radius = 3.2f)
    {
        Dot(canvas, at, radius + 1.2f, Edge);
        Dot(canvas, at, radius, new Color(0.38f, 0.39f, 0.40f));
        Dot(canvas, at + new Vector2(-radius * 0.25f, -radius * 0.3f), radius * 0.55f, new Color(0.78f, 0.79f, 0.78f));
    }

    /// <summary>A bevelled steel ring of the given thickness: dark edge, a lit top and left, a shaded bottom and right.</summary>
    static void Bevel(Rid canvas, Rect2 r, float chamfer, float width, Color lit, Color shade)
    {
        var p = Chamfered(r, chamfer);
        // Top and left edges lit, bottom and right shaded (the octagon's eight corner points, in the order Chamfered returns them).
        Line(canvas, p[7], p[0], lit, width); Line(canvas, p[0], p[1], lit, width);
        Line(canvas, p[1], p[2], Mix(lit, shade, 0.5f), width); Line(canvas, p[6], p[7], Mix(lit, shade, 0.5f), width);
        Line(canvas, p[2], p[3], shade, width); Line(canvas, p[3], p[4], shade, width); Line(canvas, p[4], p[5], shade, width); Line(canvas, p[5], p[6], shade, width);
    }

    // ---- the one StyleBox ----

    /// <summary>What a <see cref="SteelStyle"/> draws.</summary>
    public enum Kind
    {
        /// <summary>A dark-metal horizontal button (an inactive row).</summary>
        Dark,
        /// <summary>A dark button the pointer is on: a gold rim.</summary>
        Hot,
        /// <summary>A gold button: the focused or selected row.</summary>
        Gold,
        /// <summary>A dark button that cannot be used.</summary>
        Off,
        /// <summary>The heavy stepped steel frame round a recessed dark panel.</summary>
        Frame,
        /// <summary>The same frame with the recess lit amber from its edges (the main menu).</summary>
        AmberFrame,
        /// <summary>A recessed dark panel with a thin steel lip (the options' content area, a card).</summary>
        Recess,
        /// <summary>An engraved plate (a title, a nameplate): a darker, flatter button.</summary>
        Plate,
        /// <summary>A card: a recess with a gold rim (a selected item).</summary>
        CardGold,
        /// <summary>Only a gold outline, no fill: the focus ring over a selected (gold) tab or primary button.</summary>
        Rim,
    }

    /// <summary>
    /// A metal box drawn from code (a <see cref="StyleBox"/> whose <c>_Draw</c> adds canvas polygons and lines), so one theme entry gives every button, tab, frame and card the same steel at any
    /// size. The content margins are the frame's thickness; a gold box is drawn beneath the text of the focused or pressed control.
    /// </summary>
    public sealed partial class SteelStyle : StyleBox
    {
        public Kind Kind { get; init; }
        public float Chamfer { get; init; } = 7f;

        public SteelStyle() { }

        public SteelStyle(Kind kind, float chamfer = 7f, float marginX = 14f, float marginY = 8f)
        {
            Kind = kind; Chamfer = chamfer;
            SetContentMargin(Side.Left, marginX); SetContentMargin(Side.Right, marginX);
            SetContentMargin(Side.Top, marginY); SetContentMargin(Side.Bottom, marginY);
        }

        public override void _Draw(Rid toCanvasItem, Rect2 rect)
        {
            switch (Kind)
            {
                case Kind.Dark: DrawButton(toCanvasItem, rect, false, false); break;
                case Kind.Hot: DrawButton(toCanvasItem, rect, true, false); break;
                case Kind.Off: DrawButton(toCanvasItem, rect, false, true); break;
                case Kind.Gold: DrawGold(toCanvasItem, rect); break;
                case Kind.Frame: DrawFrame(toCanvasItem, rect, false); break;
                case Kind.AmberFrame: DrawFrame(toCanvasItem, rect, true); break;
                case Kind.Recess: DrawRecess(toCanvasItem, rect, false); break;
                case Kind.CardGold: DrawRecess(toCanvasItem, rect, true); break;
                case Kind.Plate: DrawPlate(toCanvasItem, rect); break;
                case Kind.Rim: Bevel(toCanvasItem, rect.Grow(-1f), Chamfer, 2.4f, GoldHi, Gold); Bevel(toCanvasItem, rect.Grow(-3.6f), Chamfer - 2f, 1.2f, new Color(0.25f, 0.13f, 0.02f, 0.9f), new Color(0.25f, 0.13f, 0.02f, 0.9f)); break;
            }
        }

        void DrawButton(Rid c, Rect2 r, bool hot, bool off)
        {
            float ch = Chamfer;
            Plate(c, r, ch, off ? new Color(0.05f, 0.05f, 0.052f) : Edge);
            var body = r.Grow(-1.6f);
            Plate(c, body, ch - 1f, off ? new Color(0.20f, 0.205f, 0.21f) : hot ? new Color(0.43f, 0.43f, 0.42f) : new Color(0.34f, 0.35f, 0.36f), off ? new Color(0.10f, 0.10f, 0.105f) : hot ? new Color(0.17f, 0.165f, 0.15f) : new Color(0.12f, 0.125f, 0.135f));
            var inset = r.Grow(-4.5f);
            Plate(c, inset, ch - 3f, off ? new Color(0.115f, 0.115f, 0.12f) : hot ? new Color(0.24f, 0.225f, 0.19f) : new Color(0.205f, 0.21f, 0.22f), off ? new Color(0.075f, 0.075f, 0.08f) : hot ? new Color(0.095f, 0.085f, 0.065f) : new Color(0.075f, 0.078f, 0.085f));
            Bevel(c, body, ch - 1f, 1f, new Color(1f, 1f, 1f, off ? 0.12f : 0.34f), new Color(0f, 0f, 0f, 0.55f));
            if (hot) Bevel(c, r.Grow(-0.8f), ch, 1.6f, GoldDeep, GoldEdge);
        }

        void DrawGold(Rid c, Rect2 r)
        {
            float ch = Chamfer;
            Plate(c, r, ch, GoldEdge);
            var body = r.Grow(-1.6f);
            Plate(c, body, ch - 1f, new Color(1f, 0.86f, 0.40f), new Color(0.74f, 0.45f, 0.07f));
            var inset = r.Grow(-4.5f);
            Plate(c, inset, ch - 3f, new Color(0.99f, 0.80f, 0.30f), new Color(0.84f, 0.55f, 0.11f));
            Bevel(c, body, ch - 1f, 1f, new Color(1f, 0.97f, 0.78f, 0.9f), new Color(0.35f, 0.18f, 0.02f, 0.8f));
        }

        void DrawPlate(Rid c, Rect2 r)
        {
            float ch = Chamfer;
            Plate(c, r, ch, Edge);
            Plate(c, r.Grow(-1.6f), ch - 1f, new Color(0.26f, 0.27f, 0.28f), new Color(0.09f, 0.095f, 0.10f));
            Plate(c, r.Grow(-4.5f), ch - 3f, new Color(0.115f, 0.12f, 0.125f), new Color(0.06f, 0.062f, 0.066f));
            Bevel(c, r.Grow(-1.6f), ch - 1f, 1f, new Color(1f, 1f, 1f, 0.3f), new Color(0f, 0f, 0f, 0.55f));
        }

        void DrawRecess(Rid c, Rect2 r, bool gold)
        {
            float ch = Chamfer;
            Plate(c, r, ch, Edge);
            Plate(c, r.Grow(-1.5f), ch - 1f, new Color(0.30f, 0.31f, 0.32f), new Color(0.12f, 0.125f, 0.13f));
            var inner = r.Grow(-4f);
            Plate(c, inner, ch - 2f, Edge);
            Plate(c, inner.Grow(-1f), ch - 3f, new Color(0.075f, 0.072f, 0.07f), new Color(0.04f, 0.038f, 0.036f));
            Bevel(c, r.Grow(-1.5f), ch - 1f, 1f, new Color(1f, 1f, 1f, 0.28f), new Color(0f, 0f, 0f, 0.5f));
            if (gold) Bevel(c, inner, ch - 2f, 1.6f, Gold, GoldDeep);
        }

        StyleBoxTexture? _ring;

        /// <summary>The painted frame (the concept's ring, a nine-patch with a transparent hole) over the recess: amber-lit for the main menu, dark for Options.</summary>
        bool DrawPaintedFrame(Rid c, Rect2 r, bool amber, Texture2D art)
        {
            bool wall = amber && ConceptArt.Get("frame-wall") is not null;
            if (wall) art = ConceptArt.Get("frame-wall")!;
            var ring = wall ? ConceptArt.WallRing : amber ? ConceptArt.MainRing : ConceptArt.OptionsRing;
            var corner = amber ? ConceptArt.MainCorner : ConceptArt.OptionsCorner;
            var rc = new Rect2(r.Position.X + ring.X - 2f, r.Position.Y + ring.Y - 2f, r.Size.X - ring.X - ring.Z + 4f, r.Size.Y - ring.Y - ring.W + 4f);
            if (rc.Size.X > 8f && rc.Size.Y > 8f)
            {
                var floor = new Color(0.035f, 0.032f, 0.03f);
                if (amber)
                {
                    // The amber glow of the recess (the reference's soft one): dark at the steel, warming inward to a mid amber, with the concept's fine horizontal scan lines.
                    const int steps = 26;
                    var rim = new Color(0.16f, 0.09f, 0.02f); var mid = new Color(0.50f, 0.29f, 0.05f);
                    for (int i = 0; i < steps; i++) Plate(c, rc.Grow(-i * 1.6f), 3f, Mix(rim, mid, Mathf.SmoothStep(0f, 1f, i / 14f)));
                    for (float y = rc.Position.Y + 3f; y < rc.End.Y - 3f; y += 4f) Fill(c, new Rect2(rc.Position.X + 3f, y, rc.Size.X - 6f, 1f), new Color(0f, 0f, 0f, 0.28f));
                }
                else Plate(c, rc, 3f, floor);
            }
            _ring ??= new StyleBoxTexture
            {
                Texture = art, DrawCenter = false,
                TextureMarginLeft = wall ? ConceptArt.WallMargins.X : corner.X, TextureMarginRight = wall ? ConceptArt.WallMargins.Z : corner.X,
                TextureMarginTop = wall ? ConceptArt.WallMargins.Y : corner.Y, TextureMarginBottom = wall ? ConceptArt.WallMargins.W : corner.Y,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
            };
            _ring.Draw(c, r);
            return true;
        }

        /// <summary>
        /// The heavy stepped frame, its steps in proportion to the frame's side margin <c>m</c>: a dark edge, a lit outer step, a dark groove, a second lit step, then the recess (amber-lit from its edge
        /// when asked) and four notched corner brackets with a rivet each, rivets along the edges.
        /// </summary>
        void DrawFrame(Rid c, Rect2 r, bool amber)
        {
            if (ConceptArt.Get(amber ? "frame-main" : "frame-options") is { } art && DrawPaintedFrame(c, r, amber, art)) return;
            float m = Mathf.Max(20f, Mathf.Min(GetMargin(Side.Left), GetMargin(Side.Top)));
            float ch = Mathf.Min(16f, m * 0.4f);
            Plate(c, r, ch, Edge);
            Plate(c, r.Grow(-2f), ch - 1f, new Color(0.62f, 0.64f, 0.65f), new Color(0.24f, 0.25f, 0.27f));                    // outer step, lit
            Bevel(c, r.Grow(-2f), ch - 1f, 1.4f, new Color(1f, 1f, 1f, 0.55f), new Color(0f, 0f, 0f, 0.6f));
            Plate(c, r.Grow(-m * 0.30f), ch - 3f, new Color(0.035f, 0.037f, 0.04f), new Color(0.065f, 0.068f, 0.073f));         // groove
            Plate(c, r.Grow(-m * 0.40f), ch - 4f, new Color(0.47f, 0.48f, 0.49f), new Color(0.16f, 0.165f, 0.18f));             // inner step
            Bevel(c, r.Grow(-m * 0.40f), ch - 4f, 1.3f, new Color(1f, 1f, 1f, 0.45f), new Color(0f, 0f, 0f, 0.6f));
            // The recess is the content rectangle: the frame's content margins from the edge.
            var rc = new Rect2(r.Position.X + GetMargin(Side.Left) - 3f, r.Position.Y + GetMargin(Side.Top) - 3f,
                r.Size.X - GetMargin(Side.Left) - GetMargin(Side.Right) + 6f, r.Size.Y - GetMargin(Side.Top) - GetMargin(Side.Bottom) + 6f);
            if (rc.Size.X > 8f && rc.Size.Y > 8f)
            {
                Plate(c, rc.Grow(2f), 6f, Edge);
                var floor = new Color(0.035f, 0.032f, 0.03f);
                if (amber)
                {
                    // The amber light of the recess: rings from the rim inward, from amber to the dark floor.
                    const int steps = 18;
                    for (int i = 0; i < steps; i++)
                    {
                        float t = i / (float)steps;
                        Plate(c, rc.Grow(-i * 3.4f), Mathf.Max(2f, 6f - i * 0.4f), Mix(Amber, floor, Mathf.Pow(t, 0.5f)));
                    }
                    Bevel(c, rc, 6f, 1.4f, new Color(1f, 0.74f, 0.28f, 0.9f), new Color(0.55f, 0.28f, 0.04f, 0.9f));
                }
                else
                {
                    Plate(c, rc, 6f, floor);
                    Bevel(c, rc, 6f, 1f, new Color(1f, 1f, 1f, 0.18f), new Color(0f, 0f, 0f, 0.7f));
                }
                // Notched corner brackets over the recess's corners: a small steel step each, with a rivet.
                float k = m * 1.25f;
                foreach (var (cx, cy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                {
                    float x = cx == 0 ? r.Position.X : r.End.X - k, y = cy == 0 ? r.Position.Y : r.End.Y - k;
                    var block = new Rect2(x, y, k, k);
                    Plate(c, block, 8f, Edge);
                    Plate(c, block.Grow(-2f), 7f, new Color(0.60f, 0.62f, 0.63f), new Color(0.24f, 0.25f, 0.27f));
                    Plate(c, block.Grow(-m * 0.30f), 5f, new Color(0.40f, 0.41f, 0.42f), new Color(0.15f, 0.155f, 0.17f));
                    Bevel(c, block.Grow(-2f), 7f, 1.3f, new Color(1f, 1f, 1f, 0.5f), new Color(0f, 0f, 0f, 0.6f));
                    Rivet(c, block.GetCenter(), Mathf.Max(2.6f, m * 0.11f));
                }
            }
            for (float t = 0.2f; t < 0.85f; t += 0.3f)
            {
                Rivet(c, r.Position + new Vector2(r.Size.X * t, m * 0.2f + 1.5f), 2f);
                Rivet(c, r.Position + new Vector2(r.Size.X * t, r.Size.Y - m * 0.2f - 1.5f), 2f);
            }
        }
    }

    // ---- the theme ----

    /// <summary>
    /// Adds the kit's type variations to a theme: <see cref="ButtonType"/> (a dark-metal horizontal button; gold when focused, pressed or selected), <see cref="TabType"/> (the same, smaller),
    /// <see cref="RowType"/> (a transparent row whose text is the label of an option), <see cref="LabelType"/>, <see cref="SliderType"/> (amber fill, steel knob), <see cref="FrameType"/> and
    /// <see cref="AmberFrameType"/> (panel containers) and <see cref="RecessType"/>.
    /// </summary>
    public static void AddTheme(Theme t)
    {
        var face = CondensedFont();
        foreach (var type in new[] { ButtonType, TabType, RowType, LabelType, PrimaryType, SmallType }) t.SetFont("font", type, face);
        foreach (var (type, size, mx, my) in new[] { (ButtonType, ButtonSize, 22f, 10f), (TabType, TabSize, 18f, 7f) })
        {
            t.SetTypeVariation(type, "Button");
            bool tab = type == TabType;
            var cornerMargin = tab ? new Vector2(30f, 12f) : new Vector2(40f, 14f);
            var contentMargin = new Vector2(mx, my);
            string dark = tab ? "tab-dark" : "button-dark", gold = tab ? "tab-gold" : "button-gold";
            t.SetStylebox("normal", type, (StyleBox?)ConceptArt.Patch(dark, cornerMargin, contentMargin) ?? new SteelStyle(Kind.Dark, 7f, mx, my));
            t.SetStylebox("hover", type, (StyleBox?)ConceptArt.Patch(dark, cornerMargin, contentMargin, ConceptArt.Warm) ?? new SteelStyle(Kind.Hot, 7f, mx, my));
            t.SetStylebox("pressed", type, (StyleBox?)ConceptArt.Patch(gold, cornerMargin, contentMargin) ?? new SteelStyle(Kind.Gold, 7f, mx, my));
            t.SetStylebox("hover_pressed", type, (StyleBox?)ConceptArt.Patch(gold, cornerMargin, contentMargin) ?? new SteelStyle(Kind.Gold, 7f, mx, my));
            t.SetStylebox("focus", type, tab ? new SteelStyle(Kind.Rim, 7f, mx, my) : (StyleBox?)ConceptArt.Patch(gold, cornerMargin, contentMargin) ?? new SteelStyle(Kind.Gold, 7f, mx, my));
            t.SetStylebox("disabled", type, (StyleBox?)ConceptArt.Patch(dark, cornerMargin, contentMargin, new Color(0.5f, 0.5f, 0.5f)) ?? new SteelStyle(Kind.Off, 7f, mx, my));
            t.SetFontSize("font_size", type, size);
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, White);
            t.SetColor("font_focus_color", type, tab ? Text : White);
            t.SetColor("font_pressed_color", type, tab ? Ink : White);   // the concept: dark lettering on the gold tab, white on the bronze plates
            t.SetColor("font_hover_pressed_color", type, tab ? Ink : White);
            t.SetColor("font_disabled_color", type, TextOff);
        }
        // The primary action (APPLY, CONFIRM LOADOUT): gold at rest, the focus a ring over it.
        t.SetTypeVariation(PrimaryType, "Button");
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed" }) t.SetStylebox(state, PrimaryType, (StyleBox?)ConceptArt.Patch("button-gold", new Vector2(40f, 14f), new Vector2(22f, 10f)) ?? new SteelStyle(Kind.Gold, 7f, 22f, 10f));
        t.SetStylebox("focus", PrimaryType, new SteelStyle(Kind.Rim, 7f, 22f, 10f));
        t.SetStylebox("disabled", PrimaryType, new SteelStyle(Kind.Off, 7f, 22f, 10f));
        t.SetFontSize("font_size", PrimaryType, ButtonSize);
        foreach (var colour in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color" }) t.SetColor(colour, PrimaryType, White);
        t.SetColor("font_disabled_color", PrimaryType, TextOff);
        // An option's label: transparent, a gold text while it has the focus.
        t.SetTypeVariation(RowType, "Button");
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" }) t.SetStylebox(state, RowType, new StyleBoxEmpty());
        t.SetFontSize("font_size", RowType, RowSize);
        t.SetColor("font_color", RowType, Text);
        t.SetColor("font_hover_color", RowType, White);
        t.SetColor("font_focus_color", RowType, GoldHi);
        t.SetColor("font_pressed_color", RowType, GoldHi);
        t.SetColor("font_hover_pressed_color", RowType, GoldHi);
        t.SetColor("font_disabled_color", RowType, TextOff);
        t.SetTypeVariation(LabelType, "Label");
        t.SetFontSize("font_size", LabelType, RowSize);
        t.SetColor("font_color", LabelType, Text);
        t.SetTypeVariation(SmallType, "Label");
        t.SetFontSize("font_size", SmallType, SmallSize);
        t.SetColor("font_color", SmallType, TextDim);
        // The slider: a dark channel, an amber fill, a steel knob that goes gold on focus.
        t.SetTypeVariation(SliderType, "HSlider");
        var channel = new StyleBoxFlat { BgColor = new Color(0.05f, 0.048f, 0.045f), BorderColor = new Color(0.36f, 0.37f, 0.38f), AntiAliasing = true };
        channel.SetBorderWidthAll(1); channel.SetCornerRadiusAll(5); channel.ContentMarginTop = 6; channel.ContentMarginBottom = 6;
        var fill = new StyleBoxFlat { BgColor = new Color(0.82f, 0.55f, 0.12f), BorderColor = new Color(0.30f, 0.17f, 0.03f), AntiAliasing = true };
        fill.SetBorderWidthAll(1); fill.SetCornerRadiusAll(5); fill.ContentMarginTop = 6; fill.ContentMarginBottom = 6;
        var fillHot = new StyleBoxFlat { BgColor = Gold, BorderColor = new Color(0.30f, 0.17f, 0.03f), AntiAliasing = true };
        fillHot.SetBorderWidthAll(1); fillHot.SetCornerRadiusAll(5); fillHot.ContentMarginTop = 6; fillHot.ContentMarginBottom = 6;
        t.SetStylebox("slider", SliderType, channel);
        t.SetStylebox("grabber_area", SliderType, fill);
        t.SetStylebox("grabber_area_highlight", SliderType, fillHot);
        t.SetStylebox("focus", SliderType, new StyleBoxEmpty());
        t.SetIcon("grabber", SliderType, Knob(false)); t.SetIcon("grabber_highlight", SliderType, Knob(true)); t.SetIcon("grabber_disabled", SliderType, Knob(false));
        t.SetConstant("center_grabber", SliderType, 0);
        // The containers.
        t.SetTypeVariation(FrameType, "PanelContainer");
        t.SetStylebox("panel", FrameType, new SteelStyle(Kind.Frame, 16f, 44f, 44f));
        t.SetTypeVariation(AmberFrameType, "PanelContainer");
        t.SetStylebox("panel", AmberFrameType, new SteelStyle(Kind.AmberFrame, 16f, 46f, 46f));
        t.SetTypeVariation(RecessType, "PanelContainer");
        t.SetStylebox("panel", RecessType, new SteelStyle(Kind.Recess, 7f, 18f, 14f));
    }

    static readonly Color White = new(1f, 1f, 1f);
    static FontVariation? _condensed;

    /// <summary>The kit's text face: the bundled DejaVu Sans Bold squeezed to 84 % in width (a condensed bold close to the concept's lettering; the concept's own face has no licence to bundle here).</summary>
    public static FontVariation CondensedFont() => _condensed ??= BuildFace();

    /// <summary>Oswald (OFL, the concept's condensed lettering's nearest free face) at weight 600; the squeezed DejaVu Sans Bold if the file cannot be read.</summary>
    static FontVariation BuildFace()
    {
        try
        {
            using var stream = typeof(Steel).Assembly.GetManifestResourceStream("Fonts/Oswald-wght.ttf");
            if (stream is not null)
            {
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                var file = new FontFile { Data = bytes, Antialiasing = TextServer.FontAntialiasing.Gray, Hinting = TextServer.Hinting.Light, SubpixelPositioning = TextServer.SubpixelPositioning.Disabled };
                var variation = new FontVariation { BaseFont = file, SpacingGlyph = 1 };
                long tag = TextServerManager.GetPrimaryInterface().NameToTag("weight");
                variation.VariationOpentype = new Godot.Collections.Dictionary { { tag, 600 } };
                return variation;
            }
        }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException) { GD.PrintErr("menus: Oswald: " + e.Message); }
        return new FontVariation { BaseFont = UiKit.Font.BaseFont, VariationTransform = new Transform2D(new Vector2(0.84f, 0f), new Vector2(0f, 1f), Vector2.Zero), VariationEmbolden = 0.12f, SpacingGlyph = -2 };
    }
    static ImageTexture? _knob, _knobHot;

    /// <summary>The slider's knob, drawn once: a rounded steel bar with a lit top, gold when it is the focused one.</summary>
    static ImageTexture Knob(bool hot)
    {
        if ((hot ? _knobHot : _knob) is { } cached) return cached;
        const int w = 14, h = 24;
        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        image.Fill(new Color(0f, 0f, 0f, 0f));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float dx = Mathf.Min(x, w - 1 - x), dy = Mathf.Min(y, h - 1 - y);
                if (dx < 1.5f && dy < 1.5f && dx + dy < 1.5f) continue;   // the corners
                bool edge = dx < 1f || dy < 1f;
                float t = y / (float)(h - 1);
                Color body = hot ? Mix(new Color(1f, 0.9f, 0.52f), new Color(0.72f, 0.44f, 0.08f), t) : Mix(new Color(0.78f, 0.79f, 0.80f), new Color(0.26f, 0.27f, 0.29f), t);
                if (!edge && Math.Abs(x - w / 2f + 0.5f) < 1.1f) body = Mix(body, new Color(0.05f, 0.05f, 0.05f), 0.55f);   // the groove
                image.SetPixel(x, y, edge ? Edge : body);
            }
        var texture = ImageTexture.CreateFromImage(image);
        if (hot) _knobHot = texture; else _knob = texture;
        return texture;
    }
}

public static partial class UiKit
{
    /// <summary>The wordmark: SM-REIMPL as a large bevelled steel-blue lettering with a dark extrusion and outline (the main menu's title).</summary>
    public sealed partial class Wordmark : Control
    {
        const string FillShader = """
            shader_type canvas_item;
            uniform float top = 0.0;
            uniform float bottom = 100.0;
            varying float ly;
            void vertex() { ly = VERTEX.y; }
            void fragment() {
                float t = clamp((ly - top) / max(bottom - top, 1.0), 0.0, 1.0);
                vec3 hi = vec3(0.80, 0.93, 1.0);
                vec3 mid = vec3(0.30, 0.60, 0.97);
                vec3 low = vec3(0.10, 0.27, 0.68);
                vec3 deep = vec3(0.04, 0.13, 0.42);
                vec3 c = t < 0.5 ? mix(hi, mid, t * 2.0) : mix(low, deep, (t - 0.5) * 2.0);
                COLOR = vec4(c, COLOR.a);
            }
            """;

        readonly string _text;
        readonly FontVariation _font;
        readonly Control _fill;
        readonly Texture2D? _art;
        int _size = 100;

        public Wordmark(string text, float width)
        {
            _text = text;
            Name = "Wordmark";
            MouseFilter = MouseFilterEnum.Ignore;
            _font = new FontVariation { BaseFont = BundledFont(), VariationEmbolden = 0.55f, SpacingGlyph = 2 };
            if (ConceptArt.Get("wordmark") is { } art)
            {
                // The painted lettering of the concept (fixed text SM-REIMPL).
                _art = art;
                CustomMinimumSize = new Vector2(width, width * art.GetHeight() / art.GetWidth());
                _fill = new Control { Visible = false };
                AddChild(_fill);
                return;
            }
            // The font size that makes the text as wide as asked.
            float unit = _font.GetStringSize(text, HorizontalAlignment.Left, -1, 100).X;
            _size = Mathf.Max(10, Mathf.FloorToInt(100f * (width - 16f) / Mathf.Max(unit, 1f)));
            float height = _font.GetHeight(_size) + 14f;
            CustomMinimumSize = new Vector2(width, height);
            _fill = new WordmarkFill(this) { Name = "WordmarkFill", MouseFilter = MouseFilterEnum.Ignore };
            _fill.SetAnchorsPreset(LayoutPreset.FullRect);
            _fill.Material = new ShaderMaterial { Shader = new Shader { Code = FillShader } };
            AddChild(_fill);
        }

        public string Text => _text;
        Vector2 Origin() => new((Size.X - _font.GetStringSize(_text, HorizontalAlignment.Left, -1, _size).X) / 2f, 5f + _font.GetAscent(_size));

        public override void _Draw()
        {
            if (_art is not null) { DrawTextureRect(_art, new Rect2(Vector2.Zero, Size), false); return; }
            var o = Origin();
            // The extrusion: the same lettering stepped down and to the right in a dark navy, under an outline.
            for (int i = 9; i >= 1; i--)
                DrawString(_font, o + new Vector2(i * 0.55f, i * 0.9f), _text, HorizontalAlignment.Left, -1, _size, Steel.Mix(new Color(0.02f, 0.05f, 0.14f), new Color(0.05f, 0.12f, 0.30f), 1f - i / 9f));
            DrawStringOutline(_font, o, _text, HorizontalAlignment.Left, -1, _size, 7, new Color(0.01f, 0.02f, 0.06f));
            DrawString(_font, o + new Vector2(-1.4f, -1.6f), _text, HorizontalAlignment.Left, -1, _size, new Color(0.88f, 0.96f, 1f));   // the lit rim of the bevel
            ((ShaderMaterial)_fill.Material).SetShaderParameter("top", o.Y - _font.GetAscent(_size) * 0.8f);
            ((ShaderMaterial)_fill.Material).SetShaderParameter("bottom", o.Y);
        }

        public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }

        sealed partial class WordmarkFill(Wordmark owner) : Control
        {
            public override void _Draw() => DrawString(owner._font, owner.Origin(), owner._text, HorizontalAlignment.Left, -1, owner._size, Colors.White);
        }
    }

    /// <summary>The winged skull crest (an aquila): steel feathers fanned out to both sides of a bone-white skull on a dark disc. Drawn from polygons at whatever size it has.</summary>
    public sealed partial class WingedCrest : Control
    {
        /// <summary>False draws the skull alone (the small one on a frame's lower edge).</summary>
        public bool Wings { get; init; } = true;

        public WingedCrest(float width = 360f, float height = 84f, bool wings = true)
        {
            Wings = wings;
            Name = "Crest";
            MouseFilter = MouseFilterEnum.Ignore;
            CustomMinimumSize = new Vector2(width, height);
            if (wings && ConceptArt.Get("crest") is { } art) CustomMinimumSize = new Vector2(width, width * art.GetHeight() / art.GetWidth());
        }

        public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }

        public override void _Draw()
        {
            if (Wings && ConceptArt.Get("crest") is { } art) { DrawTextureRect(art, new Rect2(Vector2.Zero, Size), false); return; }
            if (!Wings && ConceptArt.Get("crest") is not null) return;   // the painted crest has no separate skull; the frame's lower edge goes without
            float cx = Size.X / 2f, cy = Size.Y * 0.52f, s = Wings ? Mathf.Min(Size.Y / 84f, Size.X / 360f) : Mathf.Min(Size.Y / 50f, Size.X / 50f);
            if (Wings)
                for (int side = -1; side <= 1; side += 2)
                    for (int i = 0; i < 8; i++)
                    {
                        // Feathers, the longest and highest first, each a swept, curved blade: its upper edge lit, its lower edge in shade, its tip tinted gold.
                        float len = (190f - i * 19f) * s, rootY = cy - (20f - i * 6.2f) * s, tipY = cy - (40f - i * 5.2f) * s, thick = (13f - i * 0.9f) * s;
                        float rootX = cx + side * 19f * s;
                        const int n = 6;
                        var upper = new Vector2[n]; var lower = new Vector2[n];
                        for (int k = 0; k < n; k++)
                        {
                            float t = k / (float)(n - 1);
                            var p = new Vector2(rootX + side * len * t, rootY + (tipY - rootY) * t - 9f * s * Mathf.Sin(Mathf.Pi * t));
                            upper[k] = p;
                            lower[k] = p + new Vector2(0f, thick * (1f - Mathf.Pow(t, 0.8f)) + 0.6f * s);
                        }
                        var pts = new Vector2[2 * n - 1];
                        var cols = new Color[2 * n - 1];
                        var tip = new Color(0.92f, 0.80f, 0.48f);
                        for (int k = 0; k < n; k++) { pts[k] = upper[k]; cols[k] = Steel.Mix(new Color(0.74f, 0.75f, 0.74f), tip, Mathf.Pow(k / (float)(n - 1), 2f)); }
                        for (int k = 1; k < n; k++) { pts[n - 1 + k] = lower[n - 1 - k]; cols[n - 1 + k] = Steel.Mix(new Color(0.16f, 0.165f, 0.18f), new Color(0.35f, 0.30f, 0.20f), (n - 1 - k) / (float)(n - 1) * 0f + k / (float)(n - 1) * 0.3f); }
                        var shadow = new Vector2[pts.Length];
                        for (int k = 0; k < pts.Length; k++) shadow[k] = pts[k] + new Vector2(0.8f * s * side, 1.4f * s);
                        RenderingServer.CanvasItemAddPolygon(GetCanvasItem(), shadow, [new Color(0f, 0f, 0f, 0.8f)]);
                        RenderingServer.CanvasItemAddPolygon(GetCanvasItem(), pts, cols);
                        for (int k = 0; k + 1 < n; k++) DrawLine(upper[k], upper[k + 1], new Color(1f, 0.96f, 0.80f, 0.7f), 1.1f);
                        DrawLine(lower[0], lower[n - 2], new Color(0f, 0f, 0f, 0.5f), 1f);
                    }
            // The disc behind the skull, then the skull.
            DrawCircle(new Vector2(cx, cy), 22f * s, Steel.Edge);
            DrawCircle(new Vector2(cx, cy), 20f * s, new Color(0.24f, 0.25f, 0.27f));
            DrawCircle(new Vector2(cx, cy + 0.8f * s), 17.5f * s, new Color(0.09f, 0.09f, 0.10f));
            var bone = new Color(0.90f, 0.88f, 0.80f);
            DrawCircle(new Vector2(cx, cy - 3f * s), 12.6f * s, bone);
            DrawColoredPolygon([new(cx - 8.4f * s, cy + 4f * s), new(cx + 8.4f * s, cy + 4f * s), new(cx + 6f * s, cy + 13f * s), new(cx - 6f * s, cy + 13f * s)], new Color(0.82f, 0.80f, 0.72f));
            var socket = new Color(0.05f, 0.04f, 0.04f);
            DrawCircle(new Vector2(cx - 5f * s, cy - 2.5f * s), 3.7f * s, socket);
            DrawCircle(new Vector2(cx + 5f * s, cy - 2.5f * s), 3.7f * s, socket);
            DrawColoredPolygon([new(cx, cy + 1f * s), new(cx - 1.8f * s, cy + 5f * s), new(cx + 1.8f * s, cy + 5f * s)], socket);
            for (int k = -2; k <= 2; k++) DrawLine(new Vector2(cx + k * 2.6f * s, cy + 8f * s), new Vector2(cx + k * 2.6f * s, cy + 12.4f * s), socket, Mathf.Max(1f, 0.9f * s));
            DrawLine(new Vector2(cx - 6.6f * s, cy + 8f * s), new Vector2(cx + 6.6f * s, cy + 8f * s), socket, Mathf.Max(1f, 0.9f * s));
        }

        static Vector2[] Offset(Vector2[] pts, float dx, float dy)
        {
            var o = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++) o[i] = pts[i] + new Vector2(dx, dy);
            return o;
        }
    }

    /// <summary>A column of weathered stone blocks (the stone side pieces of the main menu and the options frame): drawn from seeded rectangles, the same every time.</summary>
    public sealed partial class StonePillar : Control
    {
        readonly int _seed;

        readonly string? _art;
        readonly bool _flip;

        /// <param name="art">A painted piece to tile down the pillar (ConceptArt); null: the drawn blocks.</param>
        public StonePillar(float width, int seed = 7, string? art = null, bool flip = false)
        {
            _seed = seed; _art = art; _flip = flip;
            Name = "StonePillar";
            MouseFilter = MouseFilterEnum.Ignore;
            CustomMinimumSize = new Vector2(width, 0f);
            ClipContents = true;
        }

        public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }

        public override void _Draw()
        {
            if (_art is not null && ConceptArt.Get(_art) is { } tile)
            {
                // The painted stone, tiled down the pillar at its own pixel size, mirrored for a left-hand pillar.
                float k = Mathf.Max(1f, Size.X / tile.GetWidth()), tw = tile.GetWidth() * k, th = tile.GetHeight() * k;
                int n = 0;
                for (float ty = 0f; ty < Size.Y; ty += th, n++)
                {
                    // Every second tile upside down, so the courses of stone do not repeat in step.
                    float y0 = n % 2 == 1 ? ty + th : ty, hh = n % 2 == 1 ? -th : th;
                    DrawTextureRect(tile, _flip ? new Rect2(tw, y0, -tw, hh) : new Rect2(0f, y0, tw, hh), false);
                }
                return;
            }
            var rng = new Random(_seed);
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.07f, 0.065f, 0.06f));
            float y = -rng.Next(0, 30);
            int row = 0;
            while (y < Size.Y)
            {
                float h = 44f + rng.Next(0, 34);
                float x = -rng.Next(0, 40);
                while (x < Size.X)
                {
                    float w = 70f + rng.Next(0, 70);
                    float tone = 0.27f + (float)rng.NextDouble() * 0.12f;
                    var block = new Rect2(x + 1.5f, y + 1.5f, w - 3f, h - 3f);
                    var top = new Color(tone + 0.05f, tone + 0.045f, tone + 0.035f);
                    var low = new Color(tone - 0.07f, tone - 0.07f, tone - 0.06f);
                    RenderingServer.CanvasItemAddPolygon(GetCanvasItem(), [block.Position, new(block.End.X, block.Position.Y), block.End, new(block.Position.X, block.End.Y)], [top, top, low, low]);
                    DrawLine(block.Position, new Vector2(block.End.X, block.Position.Y), new Color(1f, 1f, 1f, 0.12f), 1f);
                    int chips = rng.Next(1, 5);
                    for (int k = 0; k < chips; k++)
                        DrawRect(new Rect2(block.Position.X + 4 + (float)rng.NextDouble() * (block.Size.X - 18), block.Position.Y + 4 + (float)rng.NextDouble() * (block.Size.Y - 14), 3 + rng.Next(0, 9), 2 + rng.Next(0, 4)), new Color(0f, 0f, 0f, 0.22f));
                    x += w;
                }
                y += h; row++;
            }
            // Shade towards the screen's edge and light from the right, so it reads as a wall the panel is set into.
            RenderingServer.CanvasItemAddPolygon(GetCanvasItem(), [Vector2.Zero, new(Size.X, 0f), Size, new(0f, Size.Y)], [new Color(0f, 0f, 0f, 0.55f), new Color(0f, 0f, 0f, 0.05f), new Color(0f, 0f, 0f, 0.05f), new Color(0f, 0f, 0f, 0.55f)]);
        }
    }
}
