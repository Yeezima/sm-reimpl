using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SmLauncher;

/// <summary>
/// The launcher's dark look: a warm charcoal base (#302D2C) with a fine, barely visible weave, drawn as a small tile with GDI+ at start-up (no image
/// file is embedded), light text, flat buttons, and the owner-drawn checkbox, radio button, progress bar and message box that Windows Forms cannot
/// theme by itself. <see cref="Apply(Form)"/> themes a whole form; the dark title bar is asked of Windows 10/11 when it is available.
/// </summary>
static class Theme
{
    public static readonly Color Base = Color.FromArgb(0x30, 0x2D, 0x2C);
    public static readonly Color Header = Color.FromArgb(0x23, 0x20, 0x1F);
    public static readonly Color Input = Color.FromArgb(0x25, 0x22, 0x21);
    public static readonly Color Raised = Color.FromArgb(0x42, 0x3E, 0x3D);
    public static readonly Color RaisedHover = Color.FromArgb(0x52, 0x4D, 0x4B);
    public static readonly Color RaisedDown = Color.FromArgb(0x38, 0x35, 0x34);
    public static readonly Color Border = Color.FromArgb(0x5E, 0x58, 0x56);
    public static readonly Color Text = Color.FromArgb(0xEC, 0xE8, 0xE5);
    public static readonly Color Muted = Color.FromArgb(0xAB, 0xA5, 0xA1);
    public static readonly Color Accent = Color.FromArgb(0xE0, 0xA9, 0x4A);        // warm amber: links, focus, progress
    public static readonly Color AccentHover = Color.FromArgb(0xF3, 0xC4, 0x72);
    public static readonly Color Green = Color.FromArgb(0x2C, 0x86, 0x4B);
    public static readonly Color GoodText = Color.FromArgb(0x78, 0xCB, 0x8E);
    public static readonly Color WarnText = Color.FromArgb(0xEB, 0xA8, 0x5C);
    public static readonly Color BadText = Color.FromArgb(0xF0, 0x7E, 0x72);

    static Bitmap? _tile;

    /// <summary>1 normally; the layout check (--ui-scale) sets 1.25 / 1.5 / 2 to simulate a high-DPI display (fonts and sizes are scaled by MainForm).</summary>
    public static double SimScale = 1.0;
    public static double Scale(Control c) => c.DeviceDpi / 96.0 * SimScale;

    /// <summary>The background tile: 8x8, the base colour with a faint diagonal weave (+3 / -3 per channel), made once and tiled.</summary>
    public static Bitmap Tile()
    {
        if (_tile is not null) return _tile;
        var bmp = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Base);
            using var light = new Pen(Color.FromArgb(0x33, 0x30, 0x2F));
            using var dark = new Pen(Color.FromArgb(0x2D, 0x2A, 0x29));
            for (int i = -8; i < 16; i += 4) g.DrawLine(light, i, 0, i + 8, 8);      // lighter diagonals one way
            for (int i = 2; i < 24; i += 4) g.DrawLine(dark, i, 0, i - 8, 8);        // darker the other way
        }
        return _tile = bmp;
    }

    /// <summary>Themes the form and everything on it (controls added later are themed by <see cref="Apply(Control)"/> too).</summary>
    public static void Apply(Form f)
    {
        f.BackColor = Base;
        f.ForeColor = Text;
        f.BackgroundImage = Tile();
        f.BackgroundImageLayout = ImageLayout.Tile;
        try { typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(f, true); }
        catch (Exception) { }
        Apply((Control)f);
        f.HandleCreated += (_, _) => DarkTitleBar(f.Handle);
        if (f.IsHandleCreated) DarkTitleBar(f.Handle);
    }

    public static void Apply(Control c)
    {
        foreach (Control child in c.Controls) Style(child);
    }

    static void Style(Control c)
    {
        switch (c)
        {
            case DarkCheckBox or DarkRadioButton or DarkProgress:
                break;
            case LinkLabel l:
                l.LinkColor = Accent; l.ActiveLinkColor = AccentHover; l.VisitedLinkColor = Accent; l.DisabledLinkColor = Muted;
                if (l.ForeColor == SystemColors.ControlText) l.ForeColor = Text;
                break;
            case Label l:
                if (l.ForeColor == SystemColors.ControlText) l.ForeColor = Text;
                break;
            case TextBox t:
                t.BackColor = Input; t.ForeColor = Text;
                t.HandleCreated += (_, _) => SetWindowTheme(t.Handle, "DarkMode_Explorer", null);       // dark scroll bar
                if (t.IsHandleCreated) SetWindowTheme(t.Handle, "DarkMode_Explorer", null);
                break;
            case Button b:
                StyleButton(b);
                break;
            case Panel or TableLayoutPanel or FlowLayoutPanel:
                c.ForeColor = Text;
                break;
        }
        Apply(c);
    }

    static void StyleButton(Button b)
    {
        b.UseVisualStyleBackColor = false;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        bool accent = b.BackColor != SystemColors.Control && b.BackColor.ToArgb() != Color.Empty.ToArgb() && b.ForeColor == Color.White;   // Accentuate() already coloured it
        Color normal = accent ? b.BackColor : Raised;
        Color hover = accent ? Lighten(normal, 18) : RaisedHover;
        Color down = accent ? Lighten(normal, -14) : RaisedDown;
        Color disabled = accent ? Color.FromArgb(0x3C, 0x45, 0x3F) : Color.FromArgb(0x38, 0x35, 0x34);
        b.ForeColor = Text;
        b.FlatAppearance.BorderColor = accent ? Lighten(normal, 30) : Border;
        b.FlatAppearance.MouseOverBackColor = hover;
        b.FlatAppearance.MouseDownBackColor = down;
        void Sync()
        {
            b.BackColor = b.Enabled ? normal : disabled;
            b.ForeColor = b.Enabled ? Text : Color.FromArgb(0x85, 0x80, 0x7D);
            b.FlatAppearance.BorderColor = b.Enabled ? (accent ? Lighten(normal, 30) : Border) : Color.FromArgb(0x4A, 0x46, 0x44);
        }
        b.EnabledChanged += (_, _) => Sync();
        Sync();
        b.GotFocus += (_, _) => b.FlatAppearance.BorderColor = Accent;       // keyboard focus: an amber outline
        b.LostFocus += (_, _) => Sync();
    }

    static Color Lighten(Color c, int d) => Color.FromArgb(Math.Clamp(c.R + d, 0, 255), Math.Clamp(c.G + d, 0, 255), Math.Clamp(c.B + d, 0, 255));

    // ---- Windows -----------------------------------------------------------------------------------------------------------

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE (20; 19 on early Windows 10 builds). Silently does nothing where it is not supported.</summary>
    static void DarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch (Exception) { }
    }
}

/// <summary>A dark check box: the box, the tick and the focus ring are drawn here; behaviour is the stock CheckBox's. ButtonBase is an Opaque control (it never
/// gets a background erase of its own), so the paint routine fills the whole rectangle itself, every time (<see cref="DarkGlyph.Paint"/>); before that the box's row
/// showed black behind the text and old pieces of the buttons above it.</summary>
sealed class DarkCheckBox : CheckBox
{
    public DarkCheckBox() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Base; }
    protected override void OnPaintBackground(PaintEventArgs e) => DarkGlyph.PaintBackdrop(this, e.Graphics, e.ClipRectangle);
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); Parent?.Invalidate(Bounds); }
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified) { Parent?.Invalidate(Bounds); base.SetBoundsCore(x, y, width, height, specified); }
    public override Size GetPreferredSize(Size proposedSize) => DarkGlyph.Preferred(this);
    protected override void OnPaint(PaintEventArgs e) => DarkGlyph.Paint(this, e, radio: false, Checked);
}

/// <summary>A dark radio button, drawn like <see cref="DarkCheckBox"/>.</summary>
sealed class DarkRadioButton : RadioButton
{
    public DarkRadioButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Base; }
    protected override void OnPaintBackground(PaintEventArgs e) => DarkGlyph.PaintBackdrop(this, e.Graphics, e.ClipRectangle);
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); Parent?.Invalidate(Bounds); }
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified) { Parent?.Invalidate(Bounds); base.SetBoundsCore(x, y, width, height, specified); }
    public override Size GetPreferredSize(Size proposedSize) => DarkGlyph.Preferred(this);
    protected override void OnPaint(PaintEventArgs e) => DarkGlyph.Paint(this, e, radio: true, Checked);
}

static class DarkGlyph
{
    static int BoxSize(Control c) => (int)Math.Round(16 * Theme.Scale(c));
    static int Gap(Control c) => (int)Math.Round(8 * Theme.Scale(c));

    public static Size Preferred(ButtonBase c)
    {
        var t = TextRenderer.MeasureText(c.Text.Length == 0 ? " " : c.Text, c.Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(BoxSize(c) + Gap(c) + t.Width + 2, Math.Max(BoxSize(c), t.Height) + 6);
    }

    /// <summary>Paints what a control sits on, itself, with no reliance on transparency: a solid colour when an ancestor is a <see cref="IBackdrop"/> with one
    /// (the header), else the form's tile at the right offset. Every label, link, check box and panel of the launcher paints its own background this way, so a
    /// text change can never leave old glyphs behind.</summary>
    public static void PaintBackdrop(Control c, Graphics g, Rectangle clip)
    {
        for (Control? p = c.Parent; p is not null; p = p.Parent)
            if (p is IBackdrop { Solid: { } col }) { using var sb = new SolidBrush(col); g.FillRectangle(sb, clip); return; }
        PaintTile(c, g, clip);
    }

    public static void PaintTile(Control c, Graphics g, Rectangle clip)
    {
        var top = c.TopLevelControl;
        if (top is null) { g.Clear(Theme.Base); return; }
        var origin = top.PointToClient(c.PointToScreen(Point.Empty));      // the form's tile starts at its client origin
        using var brush = new TextureBrush(Theme.Tile(), WrapMode.Tile);
        brush.TranslateTransform(-origin.X, -origin.Y);
        g.FillRectangle(brush, clip);
    }

    public static void Paint(ButtonBase c, PaintEventArgs e, bool radio, bool isChecked)
    {
        var g = e.Graphics;
        PaintBackdrop(c, g, c.ClientRectangle);        // the whole rectangle, every time: ButtonBase is Opaque and never gets an erase of its own
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int s = BoxSize(c);
        var box = new Rectangle(0, (c.Height - s) / 2, s - 1, s - 1);
        var enabled = c.Enabled;
        var border = !enabled ? Color.FromArgb(0x4A, 0x46, 0x44) : c.Focused ? Theme.Accent : Theme.Border;
        using (var fill = new SolidBrush(Theme.Input)) { if (radio) g.FillEllipse(fill, box); else g.FillRectangle(fill, box); }
        using (var pen = new Pen(border, c.Focused ? 2f : 1f)) { if (radio) g.DrawEllipse(pen, box); else g.DrawRectangle(pen, box); }
        if (isChecked)
        {
            var mark = enabled ? Theme.Accent : Color.FromArgb(0x85, 0x80, 0x7D);
            if (radio)
            {
                var dot = Rectangle.Inflate(box, -(s / 4), -(s / 4));
                using var b = new SolidBrush(mark);
                g.FillEllipse(b, dot);
            }
            else
            {
                using var pen = new Pen(mark, Math.Max(2f, s / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, [new PointF(box.X + s * 0.2f, box.Y + s * 0.5f), new PointF(box.X + s * 0.42f, box.Y + s * 0.72f), new PointF(box.X + s * 0.8f, box.Y + s * 0.26f)]);
            }
        }
        var textRect = new Rectangle(s + Gap(c), 0, Math.Max(1, c.Width - s - Gap(c)), c.Height);
        TextRenderer.DrawText(g, c.Text, c.Font, textRect, enabled ? Theme.Text : Color.FromArgb(0x85, 0x80, 0x7D),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>A dark progress bar with the members MainForm uses (Value, Maximum, Style continuous / marquee).</summary>
sealed class DarkProgress : Control
{
    int _value, _max = 100;
    ProgressBarStyle _style = ProgressBarStyle.Continuous;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    float _phase;

    public DarkProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        _timer.Tick += (_, _) => { _phase = (_phase + 0.012f) % 1f; Invalidate(); };
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Maximum { get => _max; set { _max = Math.Max(1, value); Invalidate(); } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Value { get => _value; set { _value = Math.Clamp(value, 0, _max); Invalidate(); } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ProgressBarStyle Style
    {
        get => _style;
        set { if (_style == value) return; _style = value; _timer.Enabled = value == ProgressBarStyle.Marquee && Visible; Invalidate(); }
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); _timer.Enabled = _style == ProgressBarStyle.Marquee && Visible; }
    protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
    protected override Size DefaultSize => new(200, 22);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var bg = new SolidBrush(Theme.Input)) g.FillRectangle(bg, r);
        var inner = Rectangle.Inflate(r, -2, -2);
        if (inner.Width > 0 && inner.Height > 0)
        {
            Rectangle bar;
            if (_style == ProgressBarStyle.Marquee)
            {
                int w = Math.Max(8, inner.Width / 4);
                int x = inner.X + (int)((inner.Width + w) * _phase) - w;
                int x0 = Math.Max(inner.X, x), x1 = Math.Min(inner.Right, x + w);
                bar = new Rectangle(x0, inner.Y, Math.Max(0, x1 - x0), inner.Height);
            }
            else bar = new Rectangle(inner.X, inner.Y, (int)(inner.Width * (_value / (double)_max)), inner.Height);
            if (bar.Width > 0)
            {
                using var fill = new LinearGradientBrush(bar, Theme.AccentHover, Theme.Accent, LinearGradientMode.Vertical);
                g.FillRectangle(fill, bar);
            }
        }
        using var pen = new Pen(Theme.Border);
        g.DrawRectangle(pen, r);
    }
}

/// <summary>A dark replacement for MessageBox.Show with the same arguments (the system dialog cannot be themed).</summary>
static class DarkBox
{
    public static DialogResult Show(string? text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        => Show(null, text, caption, buttons, icon);

    public static DialogResult Show(IWin32Window? owner, string? text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        using var f = new Form
        {
            Text = caption, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = owner is null,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            AutoScaleDimensions = new SizeF(96f, 96f), AutoScaleMode = AutoScaleMode.Dpi, Font = new Font("Segoe UI", 9.5f), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
        };
        var glyph = new DarkLabel { AutoSize = false, Size = new Size(36, 36), Text = GlyphFor(icon), TextAlign = ContentAlignment.MiddleCenter, ForeColor = GlyphColor(icon), Font = new Font("Segoe UI", 16f, FontStyle.Bold), Margin = new Padding(0, 0, 12, 0) };
        var message = new DarkLabel { AutoSize = true, MaximumSize = new Size(520, 0), Text = text ?? "", ForeColor = Theme.Text };
        var top = new TileFlow { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        if (icon != MessageBoxIcon.None) top.Controls.Add(glyph);
        top.Controls.Add(message);
        var row = new TileFlow { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 18, 0, 0) };
        (string, DialogResult)[] defs = buttons switch
        {
            MessageBoxButtons.YesNo => [("No", DialogResult.No), ("Yes", DialogResult.Yes)],
            MessageBoxButtons.OKCancel => [("Cancel", DialogResult.Cancel), ("OK", DialogResult.OK)],
            _ => [("OK", DialogResult.OK)],
        };
        Button? first = null;
        foreach (var (label, result) in defs)       // right to left: the last in the list sits left, the first on the right edge
        {
            var b = new Button { Text = label, DialogResult = result, AutoSize = true, MinimumSize = new Size(84, 30), Margin = new Padding(8, 0, 0, 0) };
            row.Controls.Add(b);
            first ??= b;
        }
        f.AcceptButton = row.Controls.OfType<Button>().FirstOrDefault(b => b.DialogResult is DialogResult.Yes or DialogResult.OK);
        f.CancelButton = row.Controls.OfType<Button>().FirstOrDefault(b => b.DialogResult is DialogResult.No or DialogResult.Cancel or DialogResult.OK);
        var layout = new TileFlow { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        layout.Controls.Add(top);
        layout.Controls.Add(row);
        f.Controls.Add(layout);
        Theme.Apply(f);
        f.Shown += (_, _) => (f.AcceptButton as Button)?.Focus();
        var result2 = owner is null ? f.ShowDialog() : f.ShowDialog(owner);
        return result2 == DialogResult.None ? defs[^1].Item2 : result2;
    }

    static string GlyphFor(MessageBoxIcon i) => i switch
    {
        MessageBoxIcon.Question => "?",
        MessageBoxIcon.Warning => "!",
        MessageBoxIcon.Error => "x",
        MessageBoxIcon.Information => "i",
        _ => "",
    };

    static Color GlyphColor(MessageBoxIcon i) => i switch
    {
        MessageBoxIcon.Warning => Theme.WarnText,
        MessageBoxIcon.Error => Theme.BadText,
        _ => Theme.Accent,
    };
}

/// <summary>A link label whose disabled state is plain muted text (the stock one draws an embossed shadow that looks wrong on dark).</summary>
sealed class DarkLink : LinkLabel
{
    public DarkLink() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Base; }
    protected override void OnPaintBackground(PaintEventArgs e) => DarkGlyph.PaintBackdrop(this, e.Graphics, e.ClipRectangle);
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified) { Parent?.Invalidate(Bounds); base.SetBoundsCore(x, y, width, height, specified); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(0x8A, 0x84, 0x80), TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
    }
}

/// <summary>Something that is a solid colour under its children (the header) instead of the form's tile.</summary>
interface IBackdrop { Color? Solid { get; } }

/// <summary>A label that paints its own background (the tile or its solid parent) and repaints the area it leaves when its text or size changes. With
/// <see cref="Ellipsize"/> it is one line that ends in "..." instead of growing or being cut (paths).</summary>
sealed class DarkLabel : Label
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Ellipsize { get; set; }
    public DarkLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Theme.Base; }
    protected override void OnPaintBackground(PaintEventArgs e) => DarkGlyph.PaintBackdrop(this, e.Graphics, e.ClipRectangle);
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        Parent?.Invalidate(Bounds);
        if (Ellipsize && !AutoSize) height = GetPreferredSize(Size.Empty).Height;       // one line: as tall as the text and its padding, whatever the font or scale
        base.SetBoundsCore(x, y, width, height, specified);
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (Ellipsize && !AutoSize) Height = GetPreferredSize(Size.Empty).Height; }
    protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); if (Ellipsize && !AutoSize) Height = GetPreferredSize(Size.Empty).Height; }
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (!Ellipsize) return base.GetPreferredSize(proposedSize);
        var t = TextRenderer.MeasureText("Xg", Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(Padding.Horizontal + 20, t.Height + Padding.Vertical);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Ellipsize) { base.OnPaint(e); return; }
        var r = new Rectangle(Padding.Left, Padding.Top, Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical));
        TextRenderer.DrawText(e.Graphics, Text, Font, r, ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Panels that paint the tile (or a solid colour) themselves, so nothing on the window depends on transparent controls.</summary>
sealed class TilePanel : Panel, IBackdrop
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? Solid { get; set; }
    public TilePanel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaintBackground(PaintEventArgs e) => TilePaint.Paint(this, Solid, e);
}

sealed class TileTable : TableLayoutPanel, IBackdrop
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? Solid { get; set; }
    public TileTable() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaintBackground(PaintEventArgs e) => TilePaint.Paint(this, Solid, e);
}

sealed class TileFlow : FlowLayoutPanel, IBackdrop
{
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? Solid { get; set; }
    public TileFlow() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaintBackground(PaintEventArgs e) => TilePaint.Paint(this, Solid, e);
}

static class TilePaint
{
    public static void Paint(Control c, Color? solid, PaintEventArgs e)
    {
        if (solid is { } col) { using var b = new SolidBrush(col); e.Graphics.FillRectangle(b, e.ClipRectangle); }
        else DarkGlyph.PaintTile(c, e.Graphics, e.ClipRectangle);
    }
}
