using System;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The frame of the redesigned pages as a node of its own (ui/overhaul, Opus review 1): a veil, the panel frame (<see cref="PanelFrame"/>), the live title on its plate and the BACK plate at the screen's bottom right, for a
/// screen that lays its own content out (the keybinds). <see cref="Layout"/> tells where the strip under the title (the tabs' row) and the rows area are, in the parent's pixels; <see cref="Changed"/> fires when they move.
/// </summary>
public sealed partial class PanelChrome : Control
{
    const float OverlayW = PanelFrame.OverlayW, OverlayH = PanelFrame.OverlayH, RowsX = 79.4f, RowsW = 580f, StripY = 84f, RowsY = 124f, RowsBottom = 262f;
    static readonly Rect2 TitleRect = new(268f, 41f, 198f, 33f);
    readonly TextureRect _overlay = new() { Name = "ChromeOverlay", MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.Linear };
    readonly ColorRect _dim = new() { Name = "ChromeDim", Color = new Color(0f, 0f, 0f, 0.38f), MouseFilter = MouseFilterEnum.Ignore };
    readonly Label _title = new() { Name = "ChromeTitle", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
    readonly int _slices = 2;

    /// <summary>The strip under the title and the rows area, in this node's pixels, and the lettering's scale (px per grid pixel).</summary>
    public Rect2 Strip { get; private set; }
    public Rect2 RowsArea { get; private set; }
    /// <summary>The bottom tray where the page's main action stands (the same rectangle the other pages use), in this node's pixels.</summary>
    public Rect2 Tray { get; private set; }
    public float Scale { get; private set; } = 1f;
    public event Action? Changed;
    public bool Painted => _overlay.Texture is not null;

    public PanelChrome(string title)
    {
        Name = "PanelChrome";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _overlay.Texture = PanelFrame.Texture("overlay-panel", _slices);
        _title.Text = title.ToUpperInvariant();
        _title.AddThemeFontOverride("font", UiKit.ConceptFont());
        SteelBackdrop.EmbossTitle(_title);
        AddChild(_dim); AddChild(_overlay); AddChild(_title);
        Resized += Layout;
    }

    public override void _Ready() => Layout();

    public void Layout()
    {
        float extra = PanelFrame.ExtraV2(_slices), totalH = OverlayH + extra;
        float sv = Mathf.Max(0.2f, Mathf.Min(Size.Y * 0.95f / totalH, Size.X * 0.96f / OverlayW));
        var o = new Vector2((Size.X - OverlayW * sv) / 2f, (Size.Y - totalH * sv) / 2f);
        Rect2 R(Rect2 r) => new(o + r.Position * sv, r.Size * sv);
        _dim.Position = Vector2.Zero; _dim.Size = Size;
        _overlay.Position = o; _overlay.Size = new Vector2(OverlayW, totalH) * sv;
        var t = R(TitleRect); _title.Position = t.Position; _title.Size = t.Size;
        Scale = sv * 15f / 14f;
        var font = UiKit.ConceptFont(); int size = Mathf.RoundToInt(21f * Scale);
        while (size > 8 && font.GetStringSize(_title.Text, HorizontalAlignment.Left, -1, size).X > t.Size.X * 0.92f) size--;
        _title.AddThemeFontSizeOverride("font_size", size);
        Strip = R(new Rect2(RowsX, 86f, RowsW, 26f));
        RowsArea = R(new Rect2(RowsX, RowsY, RowsW, RowsBottom - RowsY + extra));
        Tray = R(new Rect2(295f, 274.5f + extra, 146.5f, 28f));
        Changed?.Invoke();
    }
}
