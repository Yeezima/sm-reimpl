using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The owner's menu layout (ui/menu-rebuild): the look of a Half-Life 2 style main menu, measured from his 1920 x 1080 reference: a wide-tracked title with its capitals from y 448 to 494 at
/// x 118, then one column of small bold capitals at x 119 with rows 22.5 px apart (the first row's centre at y 551), transparent buttons (only the text shows) and a brightness change on hover
/// and focus. The positions are in the 1920 x 1080 virtual space; the height follows the window (anchored as fractions of it), so a taller window keeps the proportions. The rows' text never
/// renders below <see cref="MinMenuPixels"/> real pixels, so it stays readable at 1280 x 720.
/// </summary>
public static partial class UiKit
{
    public const string MenuTitle = "SM - REIMPL";
    public const int MenuTextSize = 12, MenuTitleSize = 63, MenuTitleTracking = 36;
    /// <summary>The smallest size the menu rows' text is drawn at, in real pixels (the reference's 12 px at 1080p would be 8 px at 720p).</summary>
    public const float MinMenuPixels = 10.5f;
    public const float MenuLeft = 118f, MenuTitleCapTop = 448f, MenuFirstRowCentre = 551f, MenuRowHeight = 22.5f, MenuValueColumn = 320f, MenuWidth = 860f;
    /// <summary>The capital height of DejaVu Sans Bold as a part of the font size.</summary>
    const float CapHeight = 0.729f;

    /// <summary>The idle text colour of the rows: a little under white, so hover and focus can brighten it.</summary>
    public static readonly Color MenuIdle = new(0.9f, 0.9f, 0.9f);
    public const string MenuButtonType = "SmMenuButton", MenuLabelType = "SmMenuLabel";

    /// <summary>Where the background image is read from (the file is optional: without it the menus are plain dark). Not imported by Godot: read as a file.</summary>
    public const string MenuBackgroundPath = "res://assets/menu/background.jpg";

    static Texture2D? _menuBackground;
    static bool _menuBackgroundRead;

    /// <summary>The image, once, or null when the file is missing or unreadable: beside the project in a development run, under <c>assets/menu</c> beside the game in an exported one.</summary>
    public static Texture2D? MenuBackgroundImage()
    {
        if (_menuBackgroundRead) return _menuBackground;
        _menuBackgroundRead = true;
        try
        {
            string exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
            foreach (string path in new[] { ProjectSettings.GlobalizePath(MenuBackgroundPath), System.IO.Path.Combine(exeDir, "assets", "menu", "background.jpg") })
            {
                if (!System.IO.File.Exists(path)) continue;
                var image = Image.LoadFromFile(path);
                if (image is null || image.IsEmpty()) continue;
                _menuBackground = ImageTexture.CreateFromImage(image);
                break;
            }
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or NotSupportedException) { GD.PrintErr("menu background: " + e.Message); }
        return _menuBackground;
    }

    /// <summary>The full-screen background of every menu but the customizer and the in-match menu: the image, covering the window without stretching, or the plain dark background.</summary>
    public static Control MenuBackground()
    {
        if (MenuBackgroundImage() is not { } image) return Background();
        var root = new TextureRect { Name = "MenuBackground", Texture = image, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
        return root;
    }

    /// <summary>Keeps the rows' text readable: their size follows the window's scale on the virtual space, never under <see cref="MinMenuPixels"/> real pixels.</summary>
    public static void FitMenuText(Vector2 windowPixels)
    {
        float scale = Mathf.Max(0.01f, Mathf.Min(windowPixels.X / VirtualWidth, windowPixels.Y / VirtualHeight));
        int size = Mathf.Max(MenuTextSize, Mathf.CeilToInt(MinMenuPixels / scale));
        if (Theme.GetFontSize("font_size", MenuButtonType) == size) return;
        foreach (var type in new[] { MenuButtonType, MenuLabelType }) Theme.SetFontSize("font_size", type, size);
    }

    /// <summary>The menus' look on the theme (from <see cref="BuildTheme"/>).</summary>
    static void AddMenuTheme(Theme t)
    {
        t.SetTypeVariation(MenuButtonType, "Button");
        t.SetTypeVariation(MenuLabelType, "Label");
        foreach (var state in new[] { "normal", "hover", "focus", "disabled" }) t.SetStylebox(state, MenuButtonType, new StyleBoxEmpty());
        foreach (var state in new[] { "pressed", "hover_pressed" }) t.SetStylebox(state, MenuButtonType, new StyleBoxEmpty());
        t.SetFontSize("font_size", MenuButtonType, MenuTextSize);
        t.SetFontSize("font_size", MenuLabelType, MenuTextSize);
        t.SetColor("font_color", MenuButtonType, MenuIdle);
        t.SetColor("font_focus_color", MenuButtonType, White);
        t.SetColor("font_hover_color", MenuButtonType, White);
        t.SetColor("font_hover_pressed_color", MenuButtonType, White);
        t.SetColor("font_pressed_color", MenuButtonType, White);
        t.SetColor("font_disabled_color", MenuButtonType, new Color(0.56f, 0.56f, 0.56f));
        t.SetColor("font_color", MenuLabelType, MenuIdle);
    }

    /// <summary>A menu row: a transparent text button in capitals.</summary>
    public static Button MenuButton(string text, Action? onPress, string? tooltip = null)
    {
        var b = new Button { Text = text.ToUpperInvariant(), ThemeTypeVariation = MenuButtonType, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.All, TooltipText = tooltip ?? "", Name = "Menu" + text.Replace(" ", "").Replace("'", "") };
        if (onPress is not null) b.Pressed += onPress;
        return b;
    }

    /// <summary>A line of text in the rows' size.</summary>
    public static Label MenuText(string text, Color? colour = null, bool clip = true)
    {
        var l = new Label { Text = text, ThemeTypeVariation = MenuLabelType, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, ClipText = clip };
        if (colour is { } c) l.AddThemeColorOverride("font_color", c);
        return l;
    }

    // ---- the page ----

    /// <summary>The rows of a page: each row is <see cref="MenuRowHeight"/> high, so the spacing is the reference's exactly (a box container rounds it to whole pixels).</summary>
    public partial class MenuColumn : Container
    {
        public MenuColumn() { MouseFilter = MouseFilterEnum.Ignore; }

        /// <summary>Rows that take a second height (a wrapped note): the child's own minimum when it is taller than one row.</summary>
        float HeightOf(Control c) => Mathf.Max(MenuRowHeight, c.GetCombinedMinimumSize().Y);

        public override Vector2 _GetMinimumSize()
        {
            float h = 0f, w = 0f;
            foreach (var child in GetChildren())
                if (child is Control { Visible: true, TopLevel: false } c) { h += HeightOf(c); w = Mathf.Max(w, c.GetCombinedMinimumSize().X); }
            return new Vector2(w, h);
        }

        public override void _Notification(int what)
        {
            if (what != NotificationSortChildren) return;
            float y = 0f;
            foreach (var child in GetChildren())
                if (child is Control { Visible: true, TopLevel: false } c)
                {
                    float h = HeightOf(c);
                    FitChildInRect(c, new Rect2(0f, y, Size.X, h));
                    y += h;
                }
        }
    }

    /// <summary>An empty row (a gap in a page's rows).</summary>
    public static Control MenuGap(float rows = 1f) => new Control { CustomMinimumSize = new Vector2(0, MenuRowHeight * rows - 0.01f), MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>
    /// A page of the menus: the title and the rows at the reference's positions, on nothing (the screen behind shows through). <see cref="Heading"/> is the small line above the rows that
    /// says where a nested page is; the main menu has none, as in the reference. The rows may scroll: on a page taller than the room left, the column sits in a scroll list.
    /// </summary>
    public partial class MenuPage : Control
    {
        readonly Label _title, _heading;
        readonly ScrollContainer _scroll;
        public MenuColumn Rows { get; } = new() { Name = "Rows" };
        public Label TitleLabel => _title;
        public Control List => _scroll;

        public string Heading { get => _heading.Text; set { _heading.Text = value.ToUpperInvariant(); _heading.Visible = value.Length > 0; } }

        public MenuPage(string name, string heading = "")
        {
            Name = name;
            SetAnchorsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Ignore;
            _title = new Label { Name = "MenuTitle", Text = MenuTitle, MouseFilter = MouseFilterEnum.Ignore };
            _title.AddThemeFontOverride("font", TitleFont);
            _title.AddThemeFontSizeOverride("font_size", MenuTitleSize);
            _title.AddThemeColorOverride("font_color", White);
            AddChild(_title);
            _heading = MenuText("", White, clip: false);
            _heading.Name = "MenuHeading";
            AddChild(_heading);
            Heading = heading;
            _scroll = new ScrollContainer { Name = "MenuList", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, MouseFilter = MouseFilterEnum.Pass };
            Rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _scroll.AddChild(Rows);
            AddChild(_scroll);
            Resized += Place;
            VisibilityChanged += Place;
        }

        public override void _Ready()
        {
            Place();
            var viewport = GetViewport();
            viewport.SizeChanged += OnWindowChanged;
            TreeExiting += () => viewport.SizeChanged -= OnWindowChanged;
            OnWindowChanged();
        }

        void OnWindowChanged() { if (GetWindow() is { } w) FitMenuText(w.Size); }

        /// <summary>Puts the title and the rows at their height: the reference's fractions of the window's height, measured from the top.</summary>
        void Place()
        {
            float f = Size.Y / VirtualHeight;
            var font = TitleFont;
            float ascent = font.GetAscent(MenuTitleSize), capTop = ascent - CapHeight * MenuTitleSize;
            _title.Position = new Vector2(MenuLeft - 3.5f, MenuTitleCapTop * f - capTop);   // the S has a bearing of 4 px: the ink starts at the reference's x
            float rowsTop = MenuFirstRowCentre * f - MenuRowHeight / 2f;
            _heading.Position = new Vector2(MenuLeft + 1f, rowsTop - MenuRowHeight * 1.2f);
            _heading.Size = new Vector2(MenuWidth, MenuRowHeight);
            _scroll.Position = new Vector2(MenuLeft + 1f, rowsTop);
            _scroll.Size = new Vector2(Mathf.Clamp(Size.X - MenuLeft * 2f, 200f, MenuWidth), Mathf.Max(MenuRowHeight * 4f, Size.Y - rowsTop - 56f));
        }
    }

    // ---- rows with a value ----

    /// <summary>
    /// A row with a value: the label, then "&lt; VALUE &gt;". A click or Enter / A steps to the next value; left and right (keys, D-pad, stick) step either way. <see cref="Changed"/> gets the index.
    /// </summary>
    public sealed partial class MenuChoice : Button
    {
        readonly string[] _values;
        readonly Label _value;
        int _index;
        public Action<int>? Changed;
        public int Index => _index;
        public string Value => _values[_index];
        public IReadOnlyList<string> Values => _values;

        public MenuChoice(string label, string[] values, int index, Action<int>? changed = null, string? tooltip = null)
        {
            _values = values; Changed = changed;
            _index = Math.Clamp(index, 0, values.Length - 1);
            Text = label.ToUpperInvariant();
            ThemeTypeVariation = MenuButtonType;
            Alignment = HorizontalAlignment.Left;
            FocusMode = FocusModeEnum.All;
            TooltipText = tooltip ?? "";
            Name = "Choice" + label.Replace(" ", "").Replace("'", "");
            _value = MenuText("", null, clip: false);
            _value.Name = "Value";
            _value.Position = new Vector2(MenuValueColumn, 0);
            _value.SetAnchorsPreset(LayoutPreset.LeftWide);
            _value.OffsetLeft = MenuValueColumn; _value.OffsetRight = MenuValueColumn + 520f;
            AddChild(_value);
            Show();
            Pressed += () => Step(1);
            FocusEntered += Paint; FocusExited += Paint; MouseEntered += () => Paint(true); MouseExited += () => Paint(false);
            Paint(false);
        }

        void Show() => _value.Text = $"<  {_values[_index]}  >";

        void Paint() => Paint(false);

        void Paint(bool hover) => _value.AddThemeColorOverride("font_color", Disabled ? new Color(0.56f, 0.56f, 0.56f) : hover || HasFocus() ? White : MenuIdle);

        /// <summary>Sets the value without telling <see cref="Changed"/>.</summary>
        public void SetIndex(int index) { _index = Math.Clamp(index, 0, _values.Length - 1); Show(); }

        public void SetDisabled(bool disabled) { if (Disabled != disabled) { Disabled = disabled; Paint(false); } }

        /// <summary>Moves to the next or previous value, wrapping, as the player does.</summary>
        public void Step(int direction)
        {
            if (Disabled || _values.Length < 2) return;
            _index = (_index + direction + _values.Length) % _values.Length;
            Show();
            Changed?.Invoke(_index);
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e.IsActionPressed("ui_left")) { Step(-1); AcceptEvent(); }
            else if (e.IsActionPressed("ui_right")) { Step(1); AcceptEvent(); }
        }
    }

    /// <summary>A row with a slider: the label, the slider and the value in words; left and right move it when it has the focus.</summary>
    public sealed partial class MenuSlider : Control
    {
        public HSlider Slider { get; }
        readonly Label _label, _value;
        public string ValueText => _value.Text;

        public MenuSlider(string label, double min, double max, double step, double value, Func<double, string> format, Action<double> changed, string? name = null, string? tooltip = null)
        {
            Name = name is null ? "Slider" + label.Replace(" ", "") : name + "Row";
            MouseFilter = MouseFilterEnum.Pass;
            TooltipText = tooltip ?? "";
            _label = MenuText(label.ToUpperInvariant());
            _label.SetAnchorsPreset(LayoutPreset.LeftWide);
            _label.OffsetRight = MenuValueColumn - 8f;
            AddChild(_label);
            Slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, FocusMode = FocusModeEnum.All, TooltipText = tooltip ?? "", Name = name ?? "Slider" };
            Slider.SetAnchorsPreset(LayoutPreset.LeftWide);
            Slider.OffsetLeft = MenuValueColumn; Slider.OffsetRight = MenuValueColumn + 240f; Slider.OffsetTop = 3f; Slider.OffsetBottom = -3f;
            AddChild(Slider);
            _value = MenuText("", null, clip: false);
            _value.Name = name is null ? "Value" : name.Replace("Slider", "") + "Value";
            _value.SetAnchorsPreset(LayoutPreset.LeftWide);
            _value.OffsetLeft = MenuValueColumn + 254f; _value.OffsetRight = MenuValueColumn + 520f;
            AddChild(_value);
            _value.Text = format(Slider.Value);
            Slider.ValueChanged += v => { _value.Text = format(v); changed(v); };
            Slider.FocusEntered += () => Paint(true); Slider.FocusExited += () => Paint(false);
            Slider.MouseEntered += () => Paint(true); Slider.MouseExited += () => Paint(Slider.HasFocus());
        }

        void Paint(bool hot)
        {
            foreach (var l in new[] { _label, _value }) l.AddThemeColorOverride("font_color", hot ? White : MenuIdle);
        }

        /// <summary>Sets the words beside the slider (a range that moved with the window).</summary>
        public void ShowValue(string text) => _value.Text = text;
    }

    /// <summary>A row with a text field: the label, then the field.</summary>
    public static Control MenuField(string label, LineEdit field)
    {
        var row = new Control { MouseFilter = Control.MouseFilterEnum.Pass, Name = "Field" + label.Replace(" ", "") };
        var l = MenuText(label.ToUpperInvariant());
        l.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        l.OffsetRight = MenuValueColumn - 8f;
        row.AddChild(l);
        field.ThemeTypeVariation = "SmMenuField";
        field.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        field.OffsetLeft = MenuValueColumn; field.OffsetRight = MenuValueColumn + 420f;
        row.AddChild(field);
        return row;
    }
}
