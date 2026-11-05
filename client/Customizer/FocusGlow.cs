using System;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The focus glow (ui/focus-glow, the owner's request of 2026-11-06: "a soft white glow to indicate what button is currently highlighted to be activated by A button press on controller
/// or left click by mouse over"; ours). One node for the app on the root, so every menu (front end, options, customizer, lobby and host, the match's Esc menu and pickers, Musou Mode's
/// panels) gets it without code of its own: a soft white halo, drawn additively over the control that has the keyboard and controller focus (any enabled button, choice row, tab, list
/// row or slider), sized to its text when a row is wider than its words. The focus is the one source of truth: moving the mouse onto such a control gives it the focus, so there is never one
/// glow for the mouse and another for the pad. Nothing is laid out (a separate canvas layer), so no row moves. Under the brightness pass (<see cref="FrontEnd.OptionsHost.BrightnessLayer"/>).
/// </summary>
public sealed partial class FocusGlow : CanvasLayer
{
    public const int GlowLayer = 110;
    /// <summary>The halo's reach beyond the words, its strength (additive white) and its fade-in time (ours, by eye).</summary>
    public const float Feather = 14f, Strength = 0.24f, FadeSeconds = 0.08f;

    static FocusGlow? _current;
    static Texture2D? _texture;

    readonly Halo _halo = new() { Name = "Halo" };
    bool _mouseMoved;
    Control? _lastHovered;

    /// <summary>The control the glow is on (null: none). Test read.</summary>
    public static Control? Target => _current is { } c && IsInstanceValid(c) ? c._halo.Target : null;

    /// <summary>The glow's rectangle in the viewport (test read).</summary>
    public static Rect2 GlowRect => _current is { } c && IsInstanceValid(c) ? c._halo.Area : default;

    FocusGlow()
    {
        Name = "FocusGlow";
        Layer = GlowLayer;
        ProcessMode = ProcessModeEnum.Always;
        _halo.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _halo.MouseFilter = Control.MouseFilterEnum.Ignore;
        _halo.Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
        AddChild(_halo);
    }

    /// <summary>Makes the node on the root once (deferred; safe before the tree is ready).</summary>
    public static void Ensure(Node any)
    {
        if (_current is not null && IsInstanceValid(_current)) return;
        if (any.GetTree()?.Root is not { } root) return;
        _current = new FocusGlow();
        root.CallDeferred(Node.MethodName.AddChild, _current);
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseMotion) _mouseMoved = true;
    }

    // Screenshot aids (private-desktop captures have no mouse or pad): --glow-focus <ROW TEXT> gives that row the focus as the pad would, --glow-hover <ROW TEXT> moves the
    // pointer onto it; each once, as soon as such a row is shown and --glow-at seconds (default 1.5) have passed.
    static readonly string[] Args = OS.GetCmdlineUserArgs();
    static string? Arg(string name) { int i = Array.IndexOf(Args, name); return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null; }
    readonly string? _demoFocus = Arg("--glow-focus"), _demoHover = Arg("--glow-hover");
    readonly double _demoAt = double.TryParse(Arg("--glow-at"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : 1.5;
    double _elapsed;
    bool _demoDone;

    void Demo(Viewport viewport)
    {
        string? text = _demoHover ?? _demoFocus;
        if (_demoDone || text is null || _elapsed < _demoAt) return;
        Button? row = null;
        foreach (var node in GetTree().Root.FindChildren("*", "Button", true, false))
            if (node is Button b && Glows(b) && string.Equals(b.Text, text, StringComparison.OrdinalIgnoreCase)) { row = b; break; }
        if (row is null) return;
        _demoDone = true;
        if (_demoHover is null) { row.GrabFocus(); GD.Print($"glow demo: focus on {row.Text}"); return; }
        var centre = row.GetGlobalTransformWithCanvas() * new Vector2(Mathf.Min(row.Size.X / 2f, 60f), row.Size.Y / 2f);
        viewport.PushInput(new InputEventMouseMotion { Position = centre, GlobalPosition = centre }, true);
        GD.Print($"glow demo: pointer on {row.Text} at {centre}");
    }

    public override void _Process(double delta)
    {
        var viewport = GetViewport();
        if (viewport is null) return;
        _elapsed += delta;
        Demo(viewport);
        if (_mouseMoved)
        {
            _mouseMoved = false;
            SyncHover(viewport);
        }
        var focus = viewport.GuiGetFocusOwner();
        _halo.Follow(Glows(focus) ? focus : null, (float)delta);
    }

    /// <summary>The mouse moved onto a control the glow can mark: it takes the focus (one highlight for the mouse and the pad). A text field being typed in keeps it.</summary>
    void SyncHover(Viewport viewport)
    {
        if (Input.MouseMode == Input.MouseModeEnum.Captured) { _lastHovered = null; return; }
        var hovered = viewport.GuiGetHoveredControl();
        if (hovered == _lastHovered) return;
        _lastHovered = hovered;
        if (!Glows(hovered) || hovered!.HasFocus()) return;
        if (viewport.GuiGetFocusOwner() is LineEdit or TextEdit) return;
        hovered.GrabFocus();
    }

    /// <summary>Whether <paramref name="c"/> is a control A or a left click would activate: an enabled button (choice rows, tabs, list rows included) or a slider, shown and focusable (scroll bars are not).</summary>
    public static bool Glows(Control? c) =>
        c is not null && IsInstanceValid(c) && c.FocusMode != Control.FocusModeEnum.None && c.IsVisibleInTree()
        && c is BaseButton { Disabled: false } or Slider { Editable: true };

    /// <summary>The area to glow around, in the control's own space: its words when it is a left-aligned text row much wider than them (with a choice row's value), else the whole control.</summary>
    public static Rect2 AreaOf(Control c)
    {
        var whole = new Rect2(Vector2.Zero, c.Size);
        if (c is not Button { Icon: null } b || string.IsNullOrEmpty(b.Text) || b.Alignment != HorizontalAlignment.Left) return whole;
        var font = b.GetThemeFont("font");
        int size = b.GetThemeFontSize("font_size");
        if (font is null) return whole;
        float left = b.GetThemeStylebox("normal")?.GetMargin(Side.Left) ?? 0f;
        var words = font.GetStringSize(b.Text, HorizontalAlignment.Left, -1, size);
        float right = left + words.X;
        foreach (var child in b.GetChildren())
            if (child is Label { Visible: true } l && !string.IsNullOrEmpty(l.Text))
            {
                var lf = l.GetThemeFont("font");
                if (lf is null) continue;
                float lw = lf.GetStringSize(l.Text, HorizontalAlignment.Left, -1, l.GetThemeFontSize("font_size")).X;
                right = Mathf.Max(right, l.Position.X + lw);
            }
        if (right + 40f >= c.Size.X) return whole;
        float h = Mathf.Min(c.Size.Y, font.GetHeight(size) + 2f);
        return new Rect2(left, (c.Size.Y - h) / 2f, right - left, h);
    }

    /// <summary>The soft halo texture: white, full inside, fading to nothing over the border (a gaussian-like falloff), drawn as a nine-patch so the core stretches over the words.</summary>
    static Texture2D HaloTexture()
    {
        if (_texture is not null) return _texture;
        const int n = 48, m = 16;
        var image = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = Mathf.Max(0f, Mathf.Max(m - (x + 0.5f), x + 0.5f - (n - m))) / m;
                float dy = Mathf.Max(0f, Mathf.Max(m - (y + 0.5f), y + 0.5f - (n - m))) / m;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = d >= 1f ? 0f : Mathf.Exp(-3.2f * d * d) * (1f - d);
                image.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        return _texture = ImageTexture.CreateFromImage(image);
    }

    sealed partial class Halo : Control
    {
        public Control? Target { get; private set; }
        public Rect2 Area { get; private set; }
        float _alpha;
        readonly StyleBoxTexture _box = new() { Texture = HaloTexture(), TextureMarginLeft = 16, TextureMarginRight = 16, TextureMarginTop = 16, TextureMarginBottom = 16 };

        public void Follow(Control? target, float delta)
        {
            if (target != Target) { Target = target; _alpha = UiKit.ReduceMotion ? 1f : 0f; }
            var area = default(Rect2);
            if (target is not null)
            {
                var local = AreaOf(target);
                var xf = target.GetGlobalTransformWithCanvas();
                var a = xf * local.Position;
                var b = xf * local.End;
                float grow = Feather * Mathf.Max(0.5f, xf.Scale.Y);
                area = new Rect2(a, b - a).Abs().Grow(grow);
                _alpha = Mathf.Min(1f, _alpha + delta / FadeSeconds);
            }
            if (area != Area || target is not null) { Area = area; QueueRedraw(); }
        }

        public override void _Draw()
        {
            if (Target is null || !IsInstanceValid(Target) || Area.Size.X <= 0f) return;
            float scale = Mathf.Max(0.5f, Target.GetGlobalTransformWithCanvas().Scale.Y);
            DrawSetTransform(Area.Position, 0f, Vector2.One * (Feather / 16f) * scale);
            var size = Area.Size / ((Feather / 16f) * scale);
            _box.ModulateColor = new Color(1f, 1f, 1f, Strength * _alpha);
            _box.Draw(GetCanvasItem(), new Rect2(Vector2.Zero, size));
        }
    }
}
