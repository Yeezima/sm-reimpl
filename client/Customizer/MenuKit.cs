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

    /// <summary>Where the still background image is read from (the file is optional: without it the menus are plain dark; behind the menus only with <c>--menu-still</c> or without the
    /// video since 2026-11-10, see <see cref="MenuVideoPath"/>). Not imported by Godot: read as a file.</summary>
    public const string MenuBackgroundPath = "res://assets/menu/background.jpg";

    /// <summary>
    /// The loading screen's picture (the owner, 2026-11-10: "i want the old main menu screen to be the new loading screen background"): the main menu's picture of before 2026-11-09
    /// (3840 x 2160), read like <see cref="MenuBackgroundPath"/>. Optional: without it the loading screen is black.
    /// </summary>
    public const string LoadingBackgroundPath = "res://assets/menu/loading-background.jpg";

    static Texture2D? _menuBackground;
    static bool _menuBackgroundRead;

    /// <summary>The image, once, or null when the file is missing or unreadable: beside the project in a development run, under <c>assets/menu</c> beside the game in an exported one.</summary>
    public static Texture2D? MenuBackgroundImage()
    {
        if (_menuBackgroundRead) return _menuBackground;
        _menuBackgroundRead = true;
        if (ReadMenuPicture(MenuBackgroundPath, "menu background") is { } image) _menuBackground = ImageTexture.CreateFromImage(image);
        return _menuBackground;
    }

    /// <summary>
    /// The loading screen's picture, read for each loading screen and not kept (at full size it is a 33 MB texture the match does not need), halved (a box filter) while it is at
    /// least twice the window's height, so a 1080p window draws a 1920 x 1080 copy; null when the file is missing or unreadable.
    /// </summary>
    public static Texture2D? LoadingBackgroundImage(float windowHeight)
    {
        if (ReadMenuPicture(LoadingBackgroundPath, "loading background") is not { } image) return null;
        while (image.GetHeight() >= 2f * Mathf.Max(windowHeight, 360f)) image.ShrinkX2();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>One of the pictures in <c>assets/menu</c> (a folder Godot does not import): beside the project in a development run, beside the game in an exported one.</summary>
    static Image? ReadMenuPicture(string resPath, string what)
    {
        try
        {
            string exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
            foreach (string path in new[] { ProjectSettings.GlobalizePath(resPath), System.IO.Path.Combine(exeDir, "assets", "menu", System.IO.Path.GetFileName(resPath)) })
            {
                if (!System.IO.File.Exists(path)) continue;
                var image = Image.LoadFromFile(path);
                if (image is null || image.IsEmpty()) continue;
                return image;
            }
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or NotSupportedException) { GD.PrintErr(what + ": " + e.Message); }
        return null;
    }

    // The full-screen background of every menu but the customizer and the in-match menu (MenuBackground, the looping video since 2026-11-10, or the still picture): MenuVideo.cs.

    /// <summary>Keeps the rows' text readable: their size follows the window's scale on the virtual space, never under <see cref="MinMenuPixels"/> real pixels.</summary>
    public static void FitMenuText(Vector2 windowPixels)
    {
        float scale = Mathf.Max(0.01f, Mathf.Min(windowPixels.X / VirtualWidth, windowPixels.Y / VirtualHeight));
        int size = Mathf.Max(MenuTextSize, Mathf.CeilToInt(MinMenuPixels / scale));
        if (Theme.GetFontSize("font_size", MenuButtonType) == size && Theme.GetFontSize("font_size", Steel.RowType) == Mathf.Max(Steel.RowSize, size)) return;
        foreach (var type in new[] { MenuButtonType, MenuLabelType }) Theme.SetFontSize("font_size", type, size);
        // The steel kit's text (ui/concept-redesign) keeps its own larger sizes and is raised by the same rule, so it never draws under the least readable size.
        foreach (var (type, baseSize) in new[] { (Steel.ButtonType, Steel.ButtonSize), (Steel.TabType, Steel.TabSize), (Steel.RowType, Steel.RowSize), (Steel.LabelType, Steel.RowSize), (Steel.SmallType, Steel.SmallSize), (Steel.PrimaryType, Steel.ButtonSize) })
            Theme.SetFontSize("font_size", type, Mathf.Max(baseSize, Mathf.CeilToInt(MinMenuPixels / scale)));
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

        /// <summary>One row's height and the gap under it: the reference's 22.5 and 0 by default; the steel main menu's button rows set their own (ui/concept-redesign).</summary>
        float _rowHeight = MenuRowHeight, _rowGap;
        public float RowHeight { get => _rowHeight; set { _rowHeight = value; UpdateMinimumSize(); QueueSort(); } }
        public float RowGap { get => _rowGap; set { _rowGap = value; UpdateMinimumSize(); QueueSort(); } }

        /// <summary>Rows that take a second height (a wrapped note): the child's own minimum when it is taller than one row. A gap (<see cref="MenuGap"/>) keeps its own height.</summary>
        float HeightOf(Control c)
        {
            float own = c is MenuColumn nested && nested.CompactGaps ? nested.ContentHeight() : c.GetCombinedMinimumSize().Y;   // a nested steel column measures itself (its cached minimum may be a layout behind)
            return CompactGaps && c.HasMeta(GapMeta) ? own : Mathf.Max(_rowHeight, own);
        }

        /// <summary>The height of the rows as laid out now (one column, or the taller of two).</summary>
        public float ContentHeight() => _GetMinimumSize().Y;

        /// <summary>Set on the gap rows made by <see cref="MenuGap"/>.</summary>
        public const string GapMeta = "menu_gap";
        /// <summary>Set on a row that must start the second column when the rows flow into two (a group that belongs together).</summary>
        public const string BreakMeta = "menu_break";
        /// <summary>Gap rows keep their own (scaled) height instead of a whole row (the steel pages).</summary>
        public bool CompactGaps { get; set; }

        float _wrapHeight, _columnGap;
        /// <summary>The steel pages (ui/overhaul): when the rows are taller than this (UI pixels; 0 = never) they flow into a second column, split at the row boundary nearest the middle.</summary>
        public float WrapHeight { get => _wrapHeight; set { _wrapHeight = value; UpdateMinimumSize(); QueueSort(); } }
        public float ColumnGap { get => _columnGap; set { _columnGap = value; UpdateMinimumSize(); QueueSort(); } }

        /// <summary>The visible rows with their heights, the total, and the index of the row that starts the second column (-1: one column).</summary>
        (System.Collections.Generic.List<(Control C, float H)> Rows, float Total, int Break) Measure()
        {
            var list = new System.Collections.Generic.List<(Control, float)>();
            float total = 0f;
            foreach (var child in GetChildren())
                if (child is Control { Visible: true, TopLevel: false } c) { float h = HeightOf(c); list.Add((c, h)); total += h + _rowGap; }
            total = Mathf.Max(0f, total - _rowGap);
            int brk = -1;
            if (_wrapHeight > 0f && total > _wrapHeight && list.Count > 1)
            {
                float cum = 0f;
                for (int i = 0; i < list.Count; i++)
                {
                    cum += list[i].Item2 + _rowGap;
                    if (cum >= total / 2f + _rowGap) { brk = Mathf.Min(i + 1, list.Count - 1); break; }
                }
                for (int i = 1; i < list.Count; i++) if (list[i].Item1.HasMeta(BreakMeta)) { brk = i; break; }
            }
            return (list, total, brk);
        }

        public override Vector2 _GetMinimumSize()
        {
            var (rows, total, brk) = Measure();
            float w = 0f;
            foreach (var (c, _) in rows) w = Mathf.Max(w, c.GetCombinedMinimumSize().X);
            if (brk < 0) return new Vector2(w, total);
            float h1 = 0f, h2 = 0f;
            for (int i = 0; i < rows.Count; i++) { if (i < brk) h1 += rows[i].H + _rowGap; else h2 += rows[i].H + _rowGap; }
            return new Vector2(w * 2f + _columnGap, Mathf.Max(h1, h2) - _rowGap);
        }

        public override void _Notification(int what)
        {
            if (what != NotificationSortChildren) return;
            var (rows, _, brk) = Measure();
            float colW = brk < 0 ? Size.X : (Size.X - _columnGap) / 2f, y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i == brk) y = 0f;
                float x = brk >= 0 && i >= brk ? colW + _columnGap : 0f;
                FitChildInRect(rows[i].C, new Rect2(x, y, colW, rows[i].H));
                y += rows[i].H + _rowGap;
            }
        }
    }

    /// <summary>An empty row (a gap in a page's rows).</summary>
    public static Control MenuGap(float rows = 1f) { var g = new Control { CustomMinimumSize = new Vector2(0, MenuRowHeight * rows - 0.01f), MouseFilter = Control.MouseFilterEnum.Ignore }; g.SetMeta(MenuColumn.GapMeta, true); return g; }

    /// <summary>
    /// A page of the menus: the title and the rows at the reference's positions, on nothing (the screen behind shows through). <see cref="Heading"/> is the small line above the rows that
    /// says where a nested page is; the main menu has none, as in the reference. The rows may scroll: on a page taller than the room left, the column sits in a scroll list.
    /// </summary>
    public partial class MenuPage : Control
    {
        protected readonly Label _title, _heading;
        protected readonly ScrollContainer _scroll;
        public MenuColumn Rows { get; } = new() { Name = "Rows" };
        public Label TitleLabel => _title;
        public virtual Control List => _scroll;

        public string Heading { get => _heading.Text; set { _heading.Text = value.ToUpperInvariant(); _heading.Visible = value.Length > 0; } }

        protected Control? _foot;

        /// <summary>
        /// A note under the rows that is not one of them (the main menu's OPTIONS: where the game was found, the builds' address). It sits one row under the last row shown, outside the
        /// scroll list, so it never makes the rows scroll, and stays <see cref="FootMargin"/> inside the window, the list stopping above it when the rows are too many. ui-selftest fix
        /// (2026-11-10): as the column's last rows it pushed OPTIONS, GRAPHICS past the window once that tab had 17 rows (2026-11-09), so the page scrolled and the note was hidden.
        /// </summary>
        public Control? Foot
        {
            get => _foot;
            set
            {
                if (_foot is not null) { _foot.MinimumSizeChanged -= Place; RemoveChild(_foot); }
                _foot = value;
                if (value is not null) { AddChild(value); value.MinimumSizeChanged += Place; }
                Place();
            }
        }

        /// <summary>The least room under a <see cref="Foot"/>, in the 1080-high UI space.</summary>
        public const float FootMargin = 12f;

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
            Rows.MinimumSizeChanged += Place;   // a tab's rows changed: the foot follows them
        }

        public override void _Ready()
        {
            FocusGlow.Ensure(this);   // ui/focus-glow: the one glow for every menu
            Place();
            var viewport = GetViewport();
            viewport.SizeChanged += OnWindowChanged;
            TreeExiting += () => viewport.SizeChanged -= OnWindowChanged;
            OnWindowChanged();
        }

        void OnWindowChanged() { if (GetWindow() is { } w) FitMenuText(w.Size); }

        /// <summary>Puts the title and the rows at their height: the reference's fractions of the window's height, measured from the top.</summary>
        protected virtual void Place()
        {
            float f = Size.Y / VirtualHeight;
            var font = TitleFont;
            float ascent = font.GetAscent(MenuTitleSize), capTop = ascent - CapHeight * MenuTitleSize;
            _title.Position = new Vector2(MenuLeft - 3.5f, MenuTitleCapTop * f - capTop);   // the S has a bearing of 4 px: the ink starts at the reference's x
            float rowsTop = MenuFirstRowCentre * f - MenuRowHeight / 2f;
            _heading.Position = new Vector2(MenuLeft + 1f, rowsTop - MenuRowHeight * 1.2f);
            _heading.Size = new Vector2(MenuWidth, MenuRowHeight);
            _scroll.Position = new Vector2(MenuLeft + 1f, rowsTop);
            float width = Mathf.Clamp(Size.X - MenuLeft * 2f, 200f, MenuWidth);
            float listHeight = Mathf.Max(MenuRowHeight * 4f, Size.Y - rowsTop - 56f);
            if (_foot is { } foot)
            {
                // One row under the last row shown (where it stood as the column's last rows), never past the window's bottom; the list ends half a row above it.
                float footHeight = foot.GetCombinedMinimumSize().Y;
                float shown = Mathf.Min(Rows.GetCombinedMinimumSize().Y, listHeight);
                float footTop = Mathf.Min(rowsTop + shown + MenuRowHeight, Size.Y - FootMargin - footHeight);
                listHeight = Mathf.Clamp(footTop - MenuRowHeight * 0.5f - rowsTop, MenuRowHeight * 4f, listHeight);
                foot.Position = new Vector2(MenuLeft + 1f, footTop);
                foot.Size = new Vector2(width, footHeight);
            }
            _scroll.Size = new Vector2(width, listHeight);
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
            Pressed += () => { int dir = _backClick ? -1 : 1; _backClick = false; Step(dir); };
            FocusEntered += Paint; FocusExited += Paint; MouseEntered += () => Paint(true); MouseExited += () => Paint(false);
            Paint(false);
        }

        void Show() { _value.Text = _steel ? _values[_index] : $"<  {_values[_index]}  >"; QueueRedraw(); }

        void Paint() => Paint(false);

        void Paint(bool hover)
        {
            _hover = hover;
            _value.AddThemeColorOverride("font_color", _steel ? (Disabled ? Steel.TextOff : _isSwitch ? Colors.Transparent : hover || HasFocus() ? Steel.GoldHi : Steel.Text)
                : Disabled ? new Color(0.56f, 0.56f, 0.56f) : hover || HasFocus() ? White : MenuIdle);
            QueueRedraw();
        }

        // ---- the steel skin (ui/concept-redesign): the label in a column on the left, the value in a dark pill with arrows (or an OFF / ON switch) on the right ----

        bool _steel, _isSwitch, _hover, _backClick;
        /// <summary>The row wears the steel skin (<see cref="UseSteel"/>).</summary>
        public bool IsSteel => _steel;
        /// <summary>The value pill's rectangle in the row's own space (the focus glow hugs it).</summary>
        public Rect2 PillRect => Pill();
        float _labelWidth;
        const float PillHeight = 36f, ArrowWidth = 30f;

        /// <summary>
        /// Draws the row as the concept's option row: the label (the button's own text) in a column of <paramref name="labelWidth"/>, then a dark recessed pill with an arrow at each end and the
        /// value between them; two values OFF and ON are drawn as a switch with the chosen half lit gold. The row's behaviour (a click or Enter steps on, left and right step either way,
        /// <see cref="Changed"/>) is the same. Returns the row.
        /// </summary>
        public MenuChoice UseSteel(float labelWidth)
        {
            _steel = true; _labelWidth = labelWidth;
            _isSwitch = _values.Length == 2 && _values[0] == "OFF" && _values[1] == "ON";
            ThemeTypeVariation = Steel.RowType;
            ClipText = true;
            CustomMinimumSize = new Vector2(0f, 46f);
            _value.ThemeTypeVariation = Steel.LabelType;
            _value.HorizontalAlignment = HorizontalAlignment.Center;
            _value.SetAnchorsPreset(LayoutPreset.FullRect);
            _value.OffsetLeft = labelWidth + ArrowWidth; _value.OffsetRight = -ArrowWidth; _value.OffsetTop = 0f; _value.OffsetBottom = 0f;
            Show();
            Paint(false);
            return this;
        }

        static StyleBoxTexture? _pillArt;
        static StyleBoxTexture? PillArt() => _pillArt ??= ConceptArt.Patch("pill", new Vector2(40f, 14f), Vector2.Zero);

        Rect2 Pill() => _concept ? ConceptRect() : new(_labelWidth, (Size.Y - PillHeight) / 2f, Mathf.Max(40f, Size.X - _labelWidth), PillHeight);

        public override void _Draw()
        {
            if (!_steel) return;
            if (_concept) { DrawConcept(); return; }
            var pill = Pill();
            bool focus = HasFocus(), lit = (focus || _hover) && !Disabled;
            var c = GetCanvasItem();
            if (!_isSwitch && PillArt() is { } art)
            {
                // The concept's painted value pill (its arrows are in the picture); warm when it has the focus.
                art.ModulateColor = Disabled ? new Color(0.5f, 0.5f, 0.5f) : lit ? ConceptArt.Warm : Colors.White;
                art.Draw(c, pill);
                return;
            }
            Steel.Plate(c, pill, 5f, Disabled ? new Color(0.03f, 0.03f, 0.03f) : lit ? Steel.GoldDeep : Steel.Edge);
            Steel.Plate(c, pill.Grow(-1.4f), 4f, new Color(0.055f, 0.052f, 0.05f), new Color(0.10f, 0.095f, 0.09f));
            var arrowColour = Disabled ? Steel.TextOff : lit ? Steel.Gold : new Color(0.78f, 0.62f, 0.26f);
            if (_isSwitch)
            {
                // OFF | ON: the chosen half is a lit gold plate; the other stays dark with its word dim.
                var half = new Rect2(pill.Position.X + pill.Size.X / 2f * _index + 3f, pill.Position.Y + 3f, pill.Size.X / 2f - 6f, pill.Size.Y - 6f);
                Steel.Plate(c, half, 3f, Disabled ? new Color(0.22f, 0.22f, 0.22f) : _index == 1 ? new Color(1f, 0.86f, 0.40f) : new Color(0.38f, 0.38f, 0.38f), Disabled ? new Color(0.14f, 0.14f, 0.14f) : _index == 1 ? new Color(0.78f, 0.48f, 0.08f) : new Color(0.20f, 0.20f, 0.20f));
                var font = GetThemeFont("font");
                int size = GetThemeFontSize("font_size");
                for (int i = 0; i < 2; i++)
                {
                    string word = _values[i];
                    var colour = i == _index ? (Disabled ? Steel.TextOff : Steel.Ink) : Steel.TextDim;
                    float w = font.GetStringSize(word, HorizontalAlignment.Left, -1, size).X;
                    DrawString(font, new Vector2(pill.Position.X + pill.Size.X / 2f * i + (pill.Size.X / 2f - w) / 2f, pill.Position.Y + (pill.Size.Y + font.GetAscent(size) - font.GetDescent(size)) / 2f), word, HorizontalAlignment.Left, -1, size, colour);
                }
                return;
            }
            float cy = pill.Position.Y + pill.Size.Y / 2f;
            var left = new Vector2[] { new(pill.Position.X + 20f, cy - 6f), new(pill.Position.X + 20f, cy + 6f), new(pill.Position.X + 11f, cy) };
            var right = new Vector2[] { new(pill.End.X - 20f, cy - 6f), new(pill.End.X - 20f, cy + 6f), new(pill.End.X - 11f, cy) };
            DrawColoredPolygon(left, arrowColour);
            DrawColoredPolygon(right, arrowColour);
        }

        public override void _Notification(int what) { if (what == NotificationResized && _steel) QueueRedraw(); }

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
            // The steel skin's left arrow (the left quarter of the pill) steps back; a click anywhere else steps on, as before.
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
                _backClick = _steel && !_isSwitch && click.Position.X >= Pill().Position.X && click.Position.X < Pill().Position.X + ArrowWidth + 6f;
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

        bool _steel;

        void Paint(bool hot)
        {
            foreach (var l in new[] { _label, _value }) l.AddThemeColorOverride("font_color", _steel ? (hot ? Steel.GoldHi : Steel.Text) : hot ? White : MenuIdle);
        }

        /// <summary>
        /// The concept's slider row (ui/concept-redesign): the label in a column of <paramref name="labelWidth"/>, an amber-filled steel slider, the value on the right (right-aligned in
        /// <paramref name="valueWidth"/>). The slider, its range, its keys and its handler are the same.
        /// </summary>
        public MenuSlider UseSteel(float labelWidth, float valueWidth = 70f)
        {
            _steel = true;
            CustomMinimumSize = new Vector2(0f, 46f);
            _label.ThemeTypeVariation = Steel.LabelType;
            _label.SetAnchorsPreset(LayoutPreset.LeftWide);
            _label.OffsetLeft = 0f; _label.OffsetRight = labelWidth - 8f;
            Slider.ThemeTypeVariation = Steel.SliderType;
            Slider.SetAnchorsPreset(LayoutPreset.FullRect);
            Slider.OffsetLeft = labelWidth; Slider.OffsetRight = -valueWidth - 10f; Slider.OffsetTop = 8f; Slider.OffsetBottom = -8f;
            _value.ThemeTypeVariation = Steel.LabelType;
            _value.HorizontalAlignment = HorizontalAlignment.Right;
            _value.SetAnchorsPreset(LayoutPreset.FullRect);
            _value.OffsetLeft = -valueWidth; _value.OffsetRight = 0f; _value.OffsetTop = 0f; _value.OffsetBottom = 0f;
            Paint(Slider.HasFocus());
            return this;
        }

        /// <summary>Sets the words beside the slider (a range that moved with the window).</summary>
        public void ShowValue(string text) => _value.Text = text;

        /// <summary>A slider that rests (musou/perf-pass: the render scale while FSR 2 sets the scale): not editable, drawn faded.</summary>
        public void SetDisabled(bool disabled)
        {
            Slider.Editable = !disabled;
            Slider.FocusMode = disabled ? FocusModeEnum.None : FocusModeEnum.All;
            Modulate = disabled ? new Color(1f, 1f, 1f, 0.45f) : Colors.White;
        }
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
