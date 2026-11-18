using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;

namespace SmClient.Customizer;

/// <summary>
/// The shared look and widgets of the front end and the customizer: one <see cref="Theme"/> (ui/menu-rebuild, the owner's Half-Life 2 style menus: DejaVu Sans Bold, white text, transparent
/// text buttons that brighten on hover and focus, white text on a chosen toggle, no boxes or borders), text for every state (colour is never the only signal),
/// a "Reduce motion" switch, and the small modal dialogs. Everything is real Controls in a 1920 x 1080 virtual space (the window scales it). The page layout is in MenuKit.cs.
/// </summary>
public static partial class UiKit
{
    public static readonly Color White = new(1f, 1f, 1f);
    /// <summary>The highlight colour. ui/menu-rebuild: every menu has one colour, so this is white too (the name stays for the many places that say "the highlighted text").</summary>
    public static readonly Color Gold = White;
    public static readonly Color Grey = new(0.72f, 0.72f, 0.72f);
    public static readonly Color LightRed = new(1f, 0.38f, 0.32f);
    public static readonly Color PanelColour = new(0.03f, 0.03f, 0.03f, 0.86f);
    public static readonly Color PanelSolid = new(0.05f, 0.05f, 0.05f, 0.97f);
    public static readonly Color Hover = new(0.16f, 0.16f, 0.16f, 0.95f);
    public static readonly Color BackgroundTop = new(0.07f, 0.075f, 0.09f), BackgroundBottom = new(0.02f, 0.02f, 0.025f);

    public const int VirtualWidth = 1920, VirtualHeight = 1080;
    public const int BodySize = 22, CaptionSize = 18, HeaderSize = 30, TitleSize = 38, MinHit = 44;   // ui/overhaul: Oswald is narrower and lower than the DejaVu these were set for, so one step up

    /// <summary>"Reduce motion": no camera moves, no turntable, no fades (set from the front end's settings).</summary>
    public static bool ReduceMotion { get; set; }

    static Theme? _theme;
    static FontVariation? _font;
    static FontVariation? _titleFont;
    /// <summary>The one menu font (bundled: client/assets/fonts). The file is embedded in the client assembly, so it needs no Godot import; the engine's font is the fallback if it cannot be read.</summary>
    public static FontVariation Font => _font ??= new FontVariation { BaseFont = BundledFont() };
    /// <summary>The same font with the title's tracking.</summary>
    public static FontVariation TitleFont => _titleFont ??= new FontVariation { BaseFont = BundledFont(), SpacingGlyph = MenuTitleTracking };

    static Font? _bundled;
    static Font BundledFont()
    {
        if (_bundled is not null) return _bundled;
        using var stream = typeof(UiKit).Assembly.GetManifestResourceStream("Fonts/DejaVuSans-Bold.ttf");
        if (stream is null) { GD.PrintErr("menus: the bundled font is missing; using the engine's"); return _bundled = ThemeDB.FallbackFont; }
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return _bundled = new FontFile { Data = bytes, Antialiasing = TextServer.FontAntialiasing.Gray, Hinting = TextServer.Hinting.Light, SubpixelPositioning = TextServer.SubpixelPositioning.Disabled };
    }

    // ---- the theme ----

    public static Theme Theme => _theme ??= BuildTheme();

    public static StyleBoxFlat Box(Color fill, Color border, int borderWidth = 2, int margin = 12, int radius = 0)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = border, AntiAliasing = false };
        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(radius);
        box.SetContentMarginAll(margin);
        return box;
    }

    static StyleBoxEmpty Clear(int margin)
    {
        var box = new StyleBoxEmpty();
        box.SetContentMarginAll(margin);
        return box;
    }

    static ImageTexture Grabber(int width, int height)
    {
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        image.Fill(White);
        return ImageTexture.CreateFromImage(image);
    }

    static Theme BuildTheme()
    {
        var t = new Theme { DefaultFont = ConceptFont(), DefaultFontSize = BodySize };   // ui/overhaul: one lettering (Oswald) for every control without its own
        foreach (var type in new[] { "Label", "Button", "CheckButton", "CheckBox", "LineEdit", "OptionButton", "RichTextLabel" })
        {
            t.SetColor("font_color", type, White);
            t.SetFontSize("font_size", type, BodySize);
        }
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            t.SetColor("font_color", type, MenuIdle);
            t.SetColor("font_hover_color", type, White);
            t.SetColor("font_focus_color", type, White);
            t.SetColor("font_pressed_color", type, White);
            t.SetColor("font_hover_pressed_color", type, White);
            t.SetColor("font_disabled_color", type, new Color(0.5f, 0.5f, 0.5f));
            t.SetStylebox("normal", type, Clear(8));
            t.SetStylebox("hover", type, Clear(8));
            t.SetStylebox("disabled", type, Clear(8));
            t.SetStylebox("focus", type, new StyleBoxEmpty());
            t.SetStylebox("pressed", type, Clear(8));
            t.SetStylebox("hover_pressed", type, Clear(8));
        }
        t.SetStylebox("panel", "PanelContainer", Clear(12));
        t.SetStylebox("panel", "Panel", Clear(12));
        t.SetStylebox("normal", "LineEdit", new Steel.SteelStyle(Steel.Kind.Recess, 5f, 12f, 6f));   // ui/overhaul: a text field is the kit's steel recess (the menu rows' own field type stays clear)
        t.SetStylebox("focus", "LineEdit", new Steel.SteelStyle(Steel.Kind.CardGold, 5f, 12f, 6f));
        t.SetColor("caret_color", "LineEdit", White);
        t.SetColor("selection_color", "LineEdit", new Color(White, 0.3f));
        t.SetTypeVariation("SmMenuField", "LineEdit");
        t.SetFontSize("font_size", "SmMenuField", MenuTextSize);
        t.SetStylebox("normal", "SmMenuField", Clear(1));
        t.SetStylebox("focus", "SmMenuField", Clear(1));
        // Scroll bars: wide enough to grab (a StyleBoxFlat has no size of its own, so without content margins the bar measured 0 px: fix/customizer-weapons-perks).
        // ui/overhaul: a dark recessed track with a steel lip, a steel grabber that turns gold under the pointer (one scroll bar for every menu).
        StyleBoxFlat Bar(Color bg, Color border, int margin, int radius) => new()
        {
            BgColor = bg, BorderColor = border, BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginLeft = margin, ContentMarginRight = margin, ContentMarginTop = margin, ContentMarginBottom = margin,
        };
        var track = Bar(new Color(0.02f, 0.02f, 0.02f, 0.85f), new Color(0.30f, 0.31f, 0.32f, 0.9f), 6, 2);
        var grab = Bar(new Color(0.46f, 0.47f, 0.48f), new Color(0.07f, 0.07f, 0.075f), 4, 2);
        var grabHot = Bar(Steel.Gold, Steel.GoldEdge, 4, 2);
        foreach (var type in new[] { "VScrollBar", "HScrollBar" })
        {
            t.SetStylebox("scroll", type, track);
            t.SetStylebox("grabber", type, grab);
            t.SetStylebox("grabber_highlight", type, grabHot);
            t.SetStylebox("grabber_pressed", type, grabHot);
        }
        t.SetConstant("separation", "VBoxContainer", 8);
        t.SetConstant("separation", "HBoxContainer", 8);
        // Sliders: a thin line and a small white grabber.
        t.SetStylebox("slider", "HSlider", new StyleBoxFlat { BgColor = new Color(1f, 1f, 1f, 0.28f), ContentMarginTop = 1, ContentMarginBottom = 1 });
        t.SetStylebox("grabber_area", "HSlider", new StyleBoxFlat { BgColor = new Color(1f, 1f, 1f, 0.7f), ContentMarginTop = 1, ContentMarginBottom = 1 });
        t.SetStylebox("grabber_area_highlight", "HSlider", new StyleBoxFlat { BgColor = White, ContentMarginTop = 1, ContentMarginBottom = 1 });
        t.SetStylebox("focus", "HSlider", new StyleBoxEmpty());
        var grabber = Grabber(6, 14);
        foreach (var icon in new[] { "grabber", "grabber_highlight", "grabber_disabled" }) t.SetIcon(icon, "HSlider", grabber);
        // Popup menus (the dropdown of an OptionButton) and tooltips need something to stand on.
        t.SetStylebox("panel", "PopupMenu", Box(PanelSolid, PanelSolid, 0, 8));
        t.SetStylebox("hover", "PopupMenu", Box(Hover, Hover, 0, 6));
        t.SetColor("font_color", "PopupMenu", MenuIdle);
        t.SetColor("font_hover_color", "PopupMenu", White);
        t.SetFontSize("font_size", "PopupMenu", BodySize);
        t.SetConstant("v_separation", "PopupMenu", 10);
        t.SetStylebox("panel", "TooltipPanel", Box(PanelSolid, PanelSolid, 0, 6));
        t.SetColor("font_color", "TooltipLabel", White);
        t.SetFontSize("font_size", "TooltipLabel", CaptionSize);
        AddMenuTheme(t);
        Steel.AddTheme(t);   // ui/concept-redesign: the steel kit's types (SteelKit.cs)
        // ui/overhaul: a drop-down is a steel plate too.
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" }) t.SetStylebox(state, "OptionButton", t.GetStylebox(state, Steel.ButtonType));
        foreach (var colour in new[] { "font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color", "font_disabled_color" }) t.SetColor(colour, "OptionButton", t.GetColor(colour, Steel.ButtonType));
        return t;
    }

    // ---- widgets ----

    /// <summary>A page title on the concept's rivetted plate with the painted title's emboss (the failure and set-up pages).</summary>
    public static Label PlateTitle(string text, Color? ink = null)
    {
        var l = new Label { Text = text.ToUpperInvariant(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(420f, 84f) };
        l.AddThemeFontOverride("font", ConceptFont()); l.AddThemeFontSizeOverride("font_size", 40);
        SteelBackdrop.EmbossTitle(l);
        if (ink is { } c) l.AddThemeColorOverride("font_color", c);
        if (ConceptArt.Patch("opt-plate-blank", new Vector2(60f, 30f), new Vector2(40f, 10f)) is { } plate) l.AddThemeStyleboxOverride("normal", plate);
        return l;
    }

    public static Label Text(string text, int size = BodySize, Color? colour = null, HorizontalAlignment align = HorizontalAlignment.Left, bool wrap = false)
    {
        var l = new Label { Text = text, HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontOverride("font", ConceptFont());   // ui/overhaul: one lettering in every menu
        l.AddThemeFontSizeOverride("font_size", size);
        if (colour is { } c) l.AddThemeColorOverride("font_color", c);
        if (wrap) { l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(0, 0); }
        return l;
    }

    public static Button Button(string text, Action onPress, int minWidth = 0, string? tooltip = null)
    {
        // ui/overhaul: every menu button is a steel plate (the kit's dark plate, gold under the focus), in the one lettering.
        var b = new Button { Text = text.ToUpperInvariant(), FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(minWidth, MinHit), TooltipText = tooltip ?? "", ThemeTypeVariation = Steel.ButtonType, Alignment = HorizontalAlignment.Center };
        b.Pressed += onPress;
        b.SetMeta(FocusGlow.NoGlowMeta, true);   // the plate's own gold is the focus cue: no halo behind it (Opus review 3)
        // The plate's rivets sit 40 px in from each end: the plate is never narrower than its words plus both (Opus review 1: labels over rivets).
        float need = ConceptFont().GetStringSize(b.Text, HorizontalAlignment.Left, -1, Steel.ButtonSize).X + 2f * 52f;
        b.CustomMinimumSize = new Vector2(Mathf.Max(minWidth, need), MinHit);
        return b;
    }

    public static Button Toggle(string text, bool on, Action<bool> changed, int minWidth = 0)
    {
        var b = new Button { Text = text.ToUpperInvariant(), ToggleMode = true, ButtonPressed = on, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(minWidth, MinHit), ThemeTypeVariation = Steel.ButtonType, Alignment = HorizontalAlignment.Center };
        b.Toggled += v => changed(v);
        return b;
    }

    /// <summary>
    /// The field-of-view row (ui/fov-slider, ours): a slider in HORIZONTAL degrees at the window's aspect ratio, from the data's look to 120, applied live and saved by
    /// <paramref name="save"/>. Focusable, so a controller's left / right changes it (step 1).
    /// </summary>
    public static MenuSlider FovRow(float saved, Action<float> save)
    {
        const string tip = "Field of view in degrees, measured across the screen (horizontal) at this window's shape. The lowest value is the original game's view; the highest is 120. Scopes keep their own zoom.";
        var size = DisplayServer.WindowGetSize();   // the window's shape when the row is built (the viewport has no tree yet)
        float aspect = FovMath.Aspect(size.X, size.Y);
        float chosen = saved;   // what is saved (0 = the data's look)
        MenuSlider row = null!;
        string Words(double v) => $"{v:F0}°" + (v <= row.Slider.MinValue ? " (ORIGINAL)" : "");
        row = new MenuSlider("Field of view", FovMath.MinimumHorizontal(aspect), FovMath.Maximum, 1, FovMath.Clamp(saved, aspect), v => row is null ? "" : Words(v), v => { chosen = v <= row.Slider.MinValue ? 0f : (float)v; save(chosen); }, "FovSlider", tip);
        row.ShowValue(Words(row.Slider.Value));
        // The range follows the window's shape: rebuilt when the window is resized (the saved choice is kept, limited to the new range).
        void Refit()
        {
            var vs = row.GetViewport().GetVisibleRect().Size;
            float a = FovMath.Aspect(vs.X, vs.Y);
            row.Slider.MinValue = FovMath.MinimumHorizontal(a);
            row.Slider.SetValueNoSignal(FovMath.Clamp(chosen, a));
            row.ShowValue(Words(row.Slider.Value));
        }
        row.TreeEntered += () => { Refit(); row.GetViewport().SizeChanged += Refit; };
        row.TreeExiting += () => { try { row.GetViewport().SizeChanged -= Refit; } catch (Exception) { } };
        return row;
    }

    /// <summary>
    /// The mouse sensitivity row (ui/mouse-sensitivity, a client convenience: ours): a slider from 0.1x to 3.0x (step 0.05; 1.0x is the original feel) on the mouse look, applied live and saved by
    /// <paramref name="save"/>. Focusable, so a controller's left / right changes it.
    /// </summary>
    public static MenuSlider MouseSensitivityRow(float saved, Action<float> save)
    {
        const string tip = "Mouse sensitivity: a factor on the mouse look in the hip, aimed and scoped views. 1.00x is the original game's feel. The controller is not affected.";
        return new MenuSlider("Mouse sensitivity", MouseSensitivity.Minimum, MouseSensitivity.Maximum, MouseSensitivity.Step, MouseSensitivity.Clamp(saved), v => $"{v:F2}x", v => save((float)v), "MouseSensitivitySlider", tip);
    }

    public static PanelContainer Panel(Control content, int margin = 14)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", new Steel.SteelStyle(Steel.Kind.PaintedRecess, 8f, 22f, 20f) { Scale = 0.45f });   // ui/overhaul: the painted steel ring round a dark floor (callers that pass their own box override it)
        var m = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) m.AddThemeConstantOverride("margin_" + side, margin);
        p.AddChild(m);
        m.AddChild(content);
        return p;
    }

    public static VBoxContainer VBox(int separation = 8) { var v = new VBoxContainer(); v.AddThemeConstantOverride("separation", separation); return v; }
    public static HBoxContainer HBox(int separation = 8) { var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", separation); return h; }

    public static Control Spacer(bool horizontal = true) => new Control { SizeFlagsHorizontal = horizontal ? Control.SizeFlags.ExpandFill : 0, SizeFlagsVertical = horizontal ? 0 : Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    public static ScrollContainer Scroll(Control content)
    {
        var s = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        s.AddChild(content);
        return s;
    }

    /// <summary>A flat background gradient for a screen (the preview and menus are drawn over it).</summary>
    public static TextureRect Background()
    {
        var gradient = new Gradient();
        // ui/overhaul: with the redesigned art the screen is a dark veil over the menu scene (the concept's Armoury stands in front of its battlefield); without it the old opaque gradient.
        bool veil = ConceptArt.Get("overlay-armoury") is not null;
        gradient.SetColor(0, veil ? new Color(BackgroundTop, 0.74f) : BackgroundTop);
        gradient.SetColor(1, veil ? new Color(BackgroundBottom, 0.90f) : BackgroundBottom);
        var tex = new GradientTexture2D { Gradient = gradient, FillFrom = new Vector2(0.5f, 0f), FillTo = new Vector2(0.5f, 1f), Width = 8, Height = 256 };
        return new TextureRect { Texture = tex, StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
    }

    /// <summary>The window's UI scaling for the menu: a 1920 x 1080 virtual space (restored with <see cref="LeaveVirtualSpace"/> when a match starts).</summary>
    public static void EnterVirtualSpace(Window window)
    {
        window.ContentScaleSize = new Vector2I(VirtualWidth, VirtualHeight);
        window.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        window.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
    }

    public static void LeaveVirtualSpace(Window window)
    {
        window.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;
        window.ContentScaleSize = Vector2I.Zero;
    }

    // ---- a selectable row ----

    /// <summary>
    /// A list row: a button with the title on the left, state words on the right and a gold bar on the left when selected. Focus (keyboard or controller) and mouse
    /// hover both call <see cref="Focused"/>, which the screens use to fill the item card.
    /// </summary>
    public sealed partial class Row : Button
    {
        readonly ColorRect _bar;
        readonly Label _tags;
        public Action? Focused;
        /// <summary>Focus or the mouse left the row (a focus preview ends unless another row takes over at once).</summary>
        public Action? Unfocused;
        public bool IsSelected { get; private set; }

        /// <param name="glyph">The icon at the left (a check for the selected row, a padlock for a locked one); every state also has its word in <paramref name="tags"/>.</param>
        public Row(string title, IEnumerable<string> tags, bool selected, bool enabled, Glyph.Kind? glyph = null, Color? tagColour = null, Color[]? swatches = null)
        {
            Text = title.ToUpperInvariant();   // one case per level
            Alignment = HorizontalAlignment.Left;
            ClipText = true;
            FocusMode = FocusModeEnum.All;
            CustomMinimumSize = new Vector2(0, MinHit);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            IsSelected = selected;
            _bar = new ColorRect { Color = White, Visible = selected, MouseFilter = MouseFilterEnum.Ignore };
            _bar.SetAnchorsPreset(LayoutPreset.LeftWide);
            _bar.OffsetRight = 3;
            AddChild(_bar);
            if (glyph is { } g)
            {
                var icon = new Glyph(g, selected ? Gold : enabled ? White : Grey) { Position = new Vector2(16, 12), CustomMinimumSize = new Vector2(20, 20), Size = new Vector2(20, 20) };
                AddChild(icon);
            }
            var tagList = tags.ToList();
            string tagText = string.Join("  ", tagList);
            float tagWidth = tagList.Count > 0 ? Font.GetStringSize(tagText, HorizontalAlignment.Left, -1, CaptionSize).X + 4f : 0f;
            int rightMargin = tagList.Count > 0 ? (int)tagWidth + 38 : 16;
            _tags = UiKit.Text(tagText, CaptionSize, tagColour ?? (selected ? Gold : Grey));
            _tags.SetAnchorsPreset(LayoutPreset.RightWide);
            _tags.GrowHorizontal = GrowDirection.Begin;
            _tags.HorizontalAlignment = HorizontalAlignment.Right;
            int reserved = 14;
            if (swatches is { Length: > 0 })
            {
                // Colour chips at the right edge (the colours' names are printed in the card).
                var chips = HBox(4);
                chips.MouseFilter = MouseFilterEnum.Ignore;
                foreach (var c in swatches) chips.AddChild(Swatch(c, 30));
                chips.SetAnchorsPreset(LayoutPreset.CenterRight);
                chips.GrowHorizontal = GrowDirection.Begin;
                chips.OffsetRight = -12;
                AddChild(chips);
                reserved = 12 + swatches.Length * 34 + 8;
            }
            _tags.OffsetRight = -reserved;
            _tags.OffsetLeft = -reserved - tagWidth - 8f;
            _tags.VerticalAlignment = VerticalAlignment.Center;
            AddChild(_tags);
            // ui/overhaul: a list row is a warm-black strip with a steel hairline; the pointer or focus turns it into the kit's dark plate with a gold rim, the selected row keeps a gold bar.
            int mr = rightMargin + (swatches is { Length: > 0 } ? swatches.Length * 34 : 0);
            var normal = new StyleBoxFlat { BgColor = new Color(0.045f, 0.043f, 0.04f, 0.72f), BorderColor = new Color(0.55f, 0.55f, 0.52f, 0.16f), BorderWidthBottom = 1, ContentMarginLeft = 52, ContentMarginRight = mr, ContentMarginTop = 6, ContentMarginBottom = 6 };
            var hover = new Steel.SteelStyle(Steel.Kind.Hot, 6f, 52f, 6f); hover.SetContentMargin(Side.Right, mr);
            var disabledBox = (StyleBoxFlat)normal.Duplicate(); disabledBox.BgColor = new Color(0.03f, 0.03f, 0.03f, 0.6f);
            _bar.Color = Steel.Gold;
            SetMeta(FocusGlow.NoGlowMeta, true);
            AddThemeStyleboxOverride("normal", normal);
            AddThemeStyleboxOverride("hover", hover);
            AddThemeStyleboxOverride("pressed", hover); AddThemeStyleboxOverride("focus", hover); AddThemeStyleboxOverride("hover_pressed", hover);
            AddThemeStyleboxOverride("disabled", disabledBox);
            if (!enabled) AddThemeColorOverride("font_color", Grey);
            FocusEntered += () => Focused?.Invoke();
            MouseEntered += () => Focused?.Invoke();
            FocusExited += () => Unfocused?.Invoke();
            MouseExited += () => Unfocused?.Invoke();
        }
    }

    /// <summary>A small drawn icon (never the only signal: a word always sits next to it).</summary>
    public sealed partial class Glyph : Control
    {
        public enum Kind { Check, Star, Lock, Warning, Fixed }
        readonly Kind _kind;
        readonly Color _colour;

        public Glyph(Kind kind, Color colour)
        {
            _kind = kind; _colour = colour;
            MouseFilter = MouseFilterEnum.Ignore;
            CustomMinimumSize = new Vector2(24, 24);
        }

        public override void _Draw()
        {
            var s = Size;
            float w = Mathf.Min(s.X, s.Y);
            var o = new Vector2((s.X - w) / 2f, (s.Y - w) / 2f);
            Vector2 P(float x, float y) => o + new Vector2(x, y) * w;
            switch (_kind)
            {
                case Kind.Check:
                    DrawPolyline([P(0.12f, 0.55f), P(0.38f, 0.82f), P(0.88f, 0.2f)], _colour, Mathf.Max(3f, w * 0.14f), true);
                    break;
                case Kind.Star:
                {
                    var pts = new Vector2[10];
                    for (int i = 0; i < 10; i++)
                    {
                        float r = i % 2 == 0 ? 0.5f : 0.22f, a = -Mathf.Pi / 2f + i * Mathf.Pi / 5f;
                        pts[i] = P(0.5f + r * Mathf.Cos(a), 0.52f + r * Mathf.Sin(a));
                    }
                    DrawColoredPolygon(pts, _colour);
                    break;
                }
                case Kind.Lock:
                    DrawRect(new Rect2(P(0.18f, 0.45f), new Vector2(0.64f, 0.45f) * w), _colour);
                    DrawArc(P(0.5f, 0.45f), 0.24f * w, Mathf.Pi, Mathf.Tau, 16, _colour, Mathf.Max(3f, w * 0.12f), true);
                    break;
                case Kind.Warning:
                    DrawColoredPolygon([P(0.5f, 0.08f), P(0.95f, 0.88f), P(0.05f, 0.88f)], _colour);
                    DrawRect(new Rect2(P(0.46f, 0.36f), new Vector2(0.08f, 0.28f) * w), new Color(0, 0, 0, 0.9f));
                    DrawRect(new Rect2(P(0.46f, 0.7f), new Vector2(0.08f, 0.08f) * w), new Color(0, 0, 0, 0.9f));
                    break;
                case Kind.Fixed:
                    DrawRect(new Rect2(P(0.2f, 0.2f), new Vector2(0.6f, 0.6f) * w), _colour, false, Mathf.Max(2f, w * 0.1f));
                    DrawRect(new Rect2(P(0.4f, 0.4f), new Vector2(0.2f, 0.2f) * w), _colour);
                    break;
            }
        }
    }

    /// <summary>A palette colour you can pick: a coloured square, a white frame when focused, a gold one when hovered, a check when it is the current colour.</summary>
    public sealed partial class ColourButton : Button
    {
        public Action? Focused;
        public Action? Unfocused;

        public ColourButton(Color colour, bool current, string name)
        {
            FocusMode = FocusModeEnum.All;
            CustomMinimumSize = new Vector2(56, 56);
            TooltipText = name;
            var normal = Box(colour, current ? White : new Color(0f, 0f, 0f, 0.8f), current ? 4 : 2, 0);
            AddThemeStyleboxOverride("normal", normal);
            AddThemeStyleboxOverride("pressed", Box(colour, Gold, 4, 0));
            AddThemeStyleboxOverride("hover", Box(colour, Gold, 3, 0));
            AddThemeStyleboxOverride("disabled", normal);
            if (current)
            {
                // A check mark in a contrasting colour (luminance decides), never colour alone: the name is printed under the grid too.
                float lum = 0.2126f * colour.R + 0.7152f * colour.G + 0.0722f * colour.B;
                AddChild(new Glyph(Glyph.Kind.Check, lum > 0.5f ? Colors.Black : Colors.White) { Position = new Vector2(14, 14), Size = new Vector2(28, 28) });
            }
            FocusEntered += () => Focused?.Invoke();
            MouseEntered += () => Focused?.Invoke();
            FocusExited += () => Unfocused?.Invoke();
            MouseExited += () => Unfocused?.Invoke();
        }
    }

    // ---- modal dialogs ----

    /// <summary>
    /// A modal question over a screen: dims it, takes the focus (the screen behind cannot be reached by key, controller or mouse) and returns it afterwards.
    /// Escape / the cancel button picks the last button. <see cref="Open"/> returns the dialog; <paramref name="onChoice"/> gets the button index.
    /// </summary>
    public sealed partial class Dialog : Control
    {
        readonly Control _background;
        readonly Control? _previousFocus;
        readonly Action<int, string> _onChoice;
        readonly LineEdit? _input;
        readonly int _cancelIndex;
        bool _closed;

        Dialog(Control background, string title, string body, string[] buttons, Action<int, string> onChoice, string? input, int cancelIndex, Color? titleColour)
        {
            _background = background; _onChoice = onChoice; _cancelIndex = cancelIndex;
            _previousFocus = background.GetViewport()?.GuiGetFocusOwner();
            SetAnchorsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Stop;
            AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.65f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
            var column = VBox(14);
            column.CustomMinimumSize = new Vector2(760, 0);
            var plateTitle = PlateTitle(title, titleColour); plateTitle.SizeFlagsHorizontal = SizeFlags.ShrinkBegin; column.AddChild(plateTitle);
            if (body.Length > 0) { var b = Text(body.ToUpperInvariant(), BodySize, White, HorizontalAlignment.Left, true); b.AddThemeFontOverride("font", NoteFont()); b.CustomMinimumSize = new Vector2(740, 0); column.AddChild(b); }
            if (input is not null)
            {
                _input = new LineEdit { Text = input, MaxLength = Model.LoadoutEdits.MaxNameLength, CustomMinimumSize = new Vector2(0, MinHit), SelectAllOnFocus = true };
                _input.TextSubmitted += _ => Choose(0);
                column.AddChild(_input);
            }
            var row = HBox(12);
            row.Alignment = BoxContainer.AlignmentMode.End;
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                row.AddChild(Button(buttons[i], () => Choose(index), 140));
            }
            column.AddChild(row);
            var panel = Panel(column, 24);
            panel.AddThemeStyleboxOverride("panel", new Steel.SteelStyle(Steel.Kind.PaintedRecess, 10f, 30f, 28f) { Scale = 0.6f });
            panel.SetAnchorsPreset(LayoutPreset.Center);
            panel.GrowHorizontal = GrowDirection.Both;
            panel.GrowVertical = GrowDirection.Both;
            AddChild(panel);
        }

        public static Dialog Open(Control background, Control host, string title, string body, string[] buttons, Action<int> onChoice, int cancelIndex = -1, Color? titleColour = null) =>
            Show(background, host, title, body, buttons, (i, _) => onChoice(i), null, cancelIndex < 0 ? buttons.Length - 1 : cancelIndex, titleColour);

        /// <summary>A question with a text field; the first button submits.</summary>
        public static Dialog Prompt(Control background, Control host, string title, string body, string current, string[] buttons, Action<int, string> onChoice) =>
            Show(background, host, title, body, buttons, onChoice, current, buttons.Length - 1, null);

        static Dialog Show(Control background, Control host, string title, string body, string[] buttons, Action<int, string> onChoice, string? input, int cancel, Color? titleColour)
        {
            var d = new Dialog(background, title, body, buttons, onChoice, input, cancel, titleColour);
            background.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Disabled;
            host.AddChild(d);
            d.CallDeferred(nameof(FocusFirst));
            return d;
        }

        void FocusFirst()
        {
            if (!IsInsideTree() || _closed) return;   // answered before the deferred call ran
            if (_input is not null) { _input.GrabFocus(); return; }
            foreach (var n in FindChildren("*", "Button", true, false)) if (n is Button b) { b.GrabFocus(); return; }
        }

        public override void _UnhandledKeyInput(InputEvent e)
        {
            if (_closed) return;
            if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape }) { GetViewport().SetInputAsHandled(); Choose(_cancelIndex); }
        }

        public override void _Input(InputEvent e)
        {
            if (_closed) return;
            if (PadHost.IsBack(e)) { GetViewport().SetInputAsHandled(); Choose(_cancelIndex); }
        }

        void Choose(int index)
        {
            if (_closed) return;
            _closed = true;
            string text = _input?.Text ?? "";
            _background.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Inherited;
            if (GodotObject.IsInstanceValid(_previousFocus) && _previousFocus!.IsVisibleInTree()) _previousFocus.GrabFocus();
            QueueFree();
            _onChoice(index, text);
        }
    }

    // ---- small helpers ----

    /// <summary>Keyboard-only players tab through everything; this chains the focus neighbours of a row of controls left to right.</summary>
    public static void ChainHorizontal(IReadOnlyList<Control> controls)
    {
        for (int i = 0; i < controls.Count; i++)
        {
            if (i > 0) controls[i].FocusNeighborLeft = controls[i].GetPathTo(controls[i - 1]);
            if (i + 1 < controls.Count) controls[i].FocusNeighborRight = controls[i].GetPathTo(controls[i + 1]);
        }
    }

    public static string Upper(string text) => text.ToUpperInvariant();

    /// <summary>The width a button's text needs at its current font and size, its style's side margins included.</summary>
    public static float TextNeed(Button b)
    {
        var font = b.GetThemeFont("font");
        int size = b.GetThemeFontSize("font_size");
        var style = b.GetThemeStylebox("normal");
        float margins = style is null ? 0f : style.GetMargin(Side.Left) + style.GetMargin(Side.Right);
        return (font?.GetStringSize(b.Text, HorizontalAlignment.Left, -1, size).X ?? 0f) + margins;
    }

    /// <summary>
    /// Keeps a button's whole label readable (ui/hud-fidelity review: the creator's "Tactical Marine / Chaos Space Marine" tab was cut off): whenever it is resized, its font shrinks
    /// from the theme's size until the text fits the width, down to <paramref name="smallest"/>.
    /// </summary>
    public static void ShrinkToFit(Button b, int smallest = 13)
    {
        bool busy = false;
        float fitted = -1f;
        string fittedText = "";
        void Fit()
        {
            // A size change re-lays the button out and resizes it again: fit once per width and text, never from inside a fit.
            if (busy || (MathF.Abs(b.Size.X - fitted) < 0.5f && fittedText == b.Text)) return;
            busy = true;
            fitted = b.Size.X; fittedText = b.Text;
            b.RemoveThemeFontSizeOverride("font_size");
            int size = b.GetThemeFontSize("font_size");
            while (size > smallest && TextNeed(b) > b.Size.X - 2f)
            {
                size--;
                b.AddThemeFontSizeOverride("font_size", size);
            }
            busy = false;
        }
        b.Resized += Fit;
        b.Ready += Fit;
    }

    /// <summary>A coloured square swatch with a thin border (palette colours; the name is always printed elsewhere).</summary>
    public static Control Swatch(Color colour, int size = 40)
    {
        var p = new Panel { CustomMinimumSize = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", Box(colour, new Color(1, 1, 1, 0.55f), 2, 0));
        return p;
    }
}
