using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;

namespace SmClient.Customizer;

/// <summary>
/// The shared look and widgets of the front end and the customizer (docs/customizer-design.md sections 4 to 7): one <see cref="Theme"/> in the HUD's style (near-black
/// panels, gold borders, white emboldened text with a dark outline), focus that is a 3 px white outline, text for every state (colour is never the only signal),
/// a "Reduce motion" switch, and the small modal dialogs. Everything is real Controls in a 1920 x 1080 virtual space (the window scales it).
/// </summary>
public static partial class UiKit
{
    public static readonly Color Gold = new(1f, 0.8f, 0f);
    public static readonly Color White = new(1f, 1f, 1f);
    public static readonly Color Grey = new(0.62f, 0.62f, 0.64f);
    public static readonly Color LightRed = new(1f, 0.38f, 0.32f);
    public static readonly Color PanelColour = new(0.03f, 0.03f, 0.03f, 0.86f);
    public static readonly Color PanelSolid = new(0.05f, 0.05f, 0.05f, 0.97f);
    public static readonly Color Hover = new(0.16f, 0.14f, 0.06f, 0.95f);
    public static readonly Color BackgroundTop = new(0.07f, 0.075f, 0.09f), BackgroundBottom = new(0.02f, 0.02f, 0.025f);

    public const int VirtualWidth = 1920, VirtualHeight = 1080;
    public const int BodySize = 24, CaptionSize = 20, HeaderSize = 32, TitleSize = 44, MinHit = 48;

    /// <summary>"Reduce motion": no camera moves, no turntable, no fades (set from the front end's settings).</summary>
    public static bool ReduceMotion { get; set; }

    static Theme? _theme;
    static FontVariation? _font;
    public static FontVariation Font => _font ??= new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationEmbolden = 0.55f, SpacingGlyph = 1 };

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

    static StyleBoxFlat FocusRing()
    {
        var box = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = White, DrawCenter = false };
        box.SetBorderWidthAll(3);
        box.SetContentMarginAll(0);
        return box;
    }

    static Theme BuildTheme()
    {
        var t = new Theme { DefaultFont = Font, DefaultFontSize = BodySize };
        foreach (var type in new[] { "Label", "Button", "CheckButton", "CheckBox", "LineEdit", "OptionButton", "RichTextLabel" })
        {
            t.SetColor("font_color", type, White);
            t.SetColor("font_outline_color", type, new Color(0, 0, 0, 0.9f));
            t.SetConstant("outline_size", type, 3);
            t.SetFontSize("font_size", type, BodySize);
        }
        t.SetColor("font_color", "Button", White);
        t.SetColor("font_hover_color", "Button", Gold);
        t.SetColor("font_focus_color", "Button", White);
        t.SetColor("font_pressed_color", "Button", Gold);
        t.SetColor("font_disabled_color", "Button", Grey);
        var normal = Box(new Color(0.06f, 0.06f, 0.06f, 0.92f), new Color(0.55f, 0.45f, 0.1f), 2, 12);
        var hover = Box(Hover, Gold, 2, 12);
        var pressed = Box(new Color(0.25f, 0.2f, 0.03f, 0.98f), Gold, 3, 12);
        var disabled = Box(new Color(0.05f, 0.05f, 0.05f, 0.7f), new Color(0.3f, 0.3f, 0.3f), 2, 12);
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            t.SetStylebox("normal", type, normal);
            t.SetStylebox("hover", type, hover);
            t.SetStylebox("pressed", type, pressed);
            t.SetStylebox("disabled", type, disabled);
            t.SetStylebox("focus", type, FocusRing());
        }
        t.SetStylebox("panel", "PanelContainer", Box(PanelColour, Gold, 2, 12));
        t.SetStylebox("panel", "Panel", Box(PanelColour, Gold, 2, 12));
        t.SetStylebox("normal", "LineEdit", Box(new Color(0.01f, 0.01f, 0.01f, 0.95f), Gold, 2, 10));
        t.SetStylebox("focus", "LineEdit", FocusRing());
        t.SetColor("caret_color", "LineEdit", Gold);
        t.SetColor("selection_color", "LineEdit", new Color(Gold, 0.35f));
        // Scroll bars: wide enough to grab, gold grabbers.
        // A StyleBoxFlat has no size of its own, so without content margins the bar measured 0 px wide and a list longer than its panel (the Assault's five melee weapons,
        // the perks) showed no sign that more rows lie below: the margins give the bar its width (fix/customizer-weapons-perks).
        var track = new StyleBoxFlat { BgColor = new Color(0.1f, 0.1f, 0.1f, 0.9f), ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 6, ContentMarginBottom = 6 };
        var grab = new StyleBoxFlat { BgColor = new Color(Gold, 0.8f), ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4 };
        var grabHot = new StyleBoxFlat { BgColor = Gold, ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 4, ContentMarginBottom = 4 };
        t.SetStylebox("scroll", "VScrollBar", track);
        t.SetStylebox("grabber", "VScrollBar", grab);
        t.SetStylebox("grabber_highlight", "VScrollBar", grabHot);
        t.SetStylebox("grabber_pressed", "VScrollBar", grabHot);
        t.SetStylebox("scroll", "HScrollBar", track);
        t.SetStylebox("grabber", "HScrollBar", grab);
        t.SetStylebox("grabber_highlight", "HScrollBar", grabHot);
        t.SetStylebox("grabber_pressed", "HScrollBar", grabHot);
        t.SetConstant("separation", "VBoxContainer", 8);
        t.SetConstant("separation", "HBoxContainer", 8);
        // Sliders.
        t.SetStylebox("slider", "HSlider", new StyleBoxFlat { BgColor = new Color(0.2f, 0.2f, 0.2f), ContentMarginTop = 4, ContentMarginBottom = 4 });
        t.SetStylebox("grabber_area", "HSlider", new StyleBoxFlat { BgColor = Gold, ContentMarginTop = 4, ContentMarginBottom = 4 });
        t.SetStylebox("grabber_area_highlight", "HSlider", new StyleBoxFlat { BgColor = Gold, ContentMarginTop = 4, ContentMarginBottom = 4 });
        t.SetStylebox("focus", "HSlider", FocusRing());
        // Popup menus (the dropdown of an OptionButton).
        t.SetStylebox("panel", "PopupMenu", Box(PanelSolid, Gold, 2, 8));
        t.SetStylebox("hover", "PopupMenu", Box(Hover, Gold, 0, 6));
        t.SetColor("font_color", "PopupMenu", White);
        t.SetColor("font_hover_color", "PopupMenu", Gold);
        t.SetFontSize("font_size", "PopupMenu", BodySize);
        t.SetConstant("v_separation", "PopupMenu", 10);
        return t;
    }

    // ---- widgets ----

    public static Label Text(string text, int size = BodySize, Color? colour = null, HorizontalAlignment align = HorizontalAlignment.Left, bool wrap = false)
    {
        var l = new Label { Text = text, HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        if (colour is { } c) l.AddThemeColorOverride("font_color", c);
        if (wrap) { l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(0, 0); }
        return l;
    }

    public static Button Button(string text, Action onPress, int minWidth = 0, string? tooltip = null)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(minWidth, MinHit), TooltipText = tooltip ?? "" };
        b.Pressed += onPress;
        return b;
    }

    public static Button Toggle(string text, bool on, Action<bool> changed, int minWidth = 0)
    {
        var b = new Button { Text = text, ToggleMode = true, ButtonPressed = on, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(minWidth, MinHit) };
        b.Toggled += v => changed(v);
        return b;
    }

    /// <summary>
    /// The field-of-view row (ui/fov-slider, ours): a slider in HORIZONTAL degrees at the window's aspect ratio, from the data's look to 120, applied live and saved by
    /// <paramref name="save"/>. Focusable, so a controller's left / right changes it (step 1).
    /// </summary>
    public static HBoxContainer FovRow(float saved, Action<float> save, bool compact = false)
    {
        const string tip = "Field of view in degrees, measured across the screen (horizontal) at this window's shape. The lowest value is the original game's view; the highest is 120. Scopes keep their own zoom.";
        var row = HBox(compact ? 8 : 12);
        row.TooltipText = tip;
        row.MouseFilter = Control.MouseFilterEnum.Pass;
        var caption = Text(compact ? "FOV" : "Field of view", CaptionSize, Grey);
        caption.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(caption);
        var size = DisplayServer.WindowGetSize();   // the window's shape when the row is built (the viewport has no tree yet)
        float aspect = FovMath.Aspect(size.X, size.Y);
        var slider = new HSlider { MinValue = FovMath.MinimumHorizontal(aspect), MaxValue = FovMath.Maximum, Step = 1, Value = FovMath.Clamp(saved, aspect),
            CustomMinimumSize = new Vector2(compact ? 130 : 300, 28), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, FocusMode = Control.FocusModeEnum.All, TooltipText = tip, Name = "FovSlider" };
        var value = Text("", CaptionSize, White);
        value.VerticalAlignment = VerticalAlignment.Center;
        value.CustomMinimumSize = new Vector2(compact ? 56 : 120, 0);
        void Show(double v) => value.Text = $"{v:F0}°" + (!compact && v <= slider.MinValue ? " (original)" : "");
        Show(slider.Value);
        float chosen = saved;   // what is saved (0 = the data's look)
        slider.ValueChanged += v => { Show(v); chosen = v <= slider.MinValue ? 0f : (float)v; save(chosen); };
        // The range follows the window's shape: rebuilt when the window is resized (the saved choice is kept, limited to the new range).
        void Refit()
        {
            var vs = row.GetViewport().GetVisibleRect().Size;
            float a = FovMath.Aspect(vs.X, vs.Y);
            slider.MinValue = FovMath.MinimumHorizontal(a);
            slider.SetValueNoSignal(FovMath.Clamp(chosen, a));
            Show(slider.Value);
        }
        row.TreeEntered += () => { Refit(); var vp = row.GetViewport(); if (!vp.IsConnected(Viewport.SignalName.SizeChanged, Callable.From(Refit))) vp.SizeChanged += Refit; };
        row.TreeExiting += () => { try { row.GetViewport().SizeChanged -= Refit; } catch (Exception) { } };
        row.AddChild(slider);
        row.AddChild(value);
        return row;
    }

    public static PanelContainer Panel(Control content, int margin = 14)
    {
        var p = new PanelContainer();
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
        gradient.SetColor(0, BackgroundTop);
        gradient.SetColor(1, BackgroundBottom);
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
            Text = title;
            Alignment = HorizontalAlignment.Left;
            ClipText = true;
            FocusMode = FocusModeEnum.All;
            CustomMinimumSize = new Vector2(0, MinHit + 4);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            IsSelected = selected;
            _bar = new ColorRect { Color = Gold, Visible = selected, MouseFilter = MouseFilterEnum.Ignore };
            _bar.SetAnchorsPreset(LayoutPreset.LeftWide);
            _bar.OffsetRight = 6;
            AddChild(_bar);
            if (glyph is { } g)
            {
                var icon = new Glyph(g, selected ? Gold : enabled ? White : Grey) { Position = new Vector2(16, 14), CustomMinimumSize = new Vector2(24, 24), Size = new Vector2(24, 24) };
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
            var normal = Box(selected ? new Color(0.14f, 0.12f, 0.04f, 0.95f) : new Color(0.06f, 0.06f, 0.06f, 0.92f), selected ? Gold : new Color(0.4f, 0.33f, 0.08f), 2, 8);
            normal.ContentMarginLeft = 52;
            normal.ContentMarginRight = rightMargin + (swatches is { Length: > 0 } ? swatches.Length * 34 : 0);
            var hover = Box(Hover, Gold, 2, 8);
            hover.ContentMarginLeft = 52;
            hover.ContentMarginRight = rightMargin + (swatches is { Length: > 0 } ? swatches.Length * 34 : 0);
            var disabledBox = Box(new Color(0.05f, 0.05f, 0.05f, 0.7f), new Color(0.3f, 0.3f, 0.3f), 2, 8);
            disabledBox.ContentMarginLeft = 52;
            disabledBox.ContentMarginRight = rightMargin + (swatches is { Length: > 0 } ? swatches.Length * 34 : 0);
            AddThemeStyleboxOverride("normal", normal);
            AddThemeStyleboxOverride("hover", hover);
            AddThemeStyleboxOverride("pressed", hover);
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
            column.AddChild(Text(title, HeaderSize, titleColour ?? Gold));
            if (body.Length > 0) { var b = Text(body, BodySize, White, HorizontalAlignment.Left, true); b.CustomMinimumSize = new Vector2(740, 0); column.AddChild(b); }
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
            panel.AddThemeStyleboxOverride("panel", Box(PanelSolid, titleColour ?? Gold, 3, 24));
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
            if (e.IsActionPressed("ui_cancel")) { GetViewport().SetInputAsHandled(); Choose(_cancelIndex); }
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
