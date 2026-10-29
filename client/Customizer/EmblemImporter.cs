using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SmClient.Customizer.Model;
using SmFormats.Dds;
using SmSim;

namespace SmClient.Customizer;

/// <summary>
/// The custom shoulder emblem import (docs/customizer-design.md F7, architecture decision 5): a native file dialog (PNG, JPG, WebP, BMP, TGA; at most 8 MB and
/// 4096 x 4096), a square crop with pan and zoom and live previews, a Lanczos resample to 128 x 128 and BC3 (<see cref="EmblemPipeline"/>), stored by hash in the
/// profile's emblem folder (<see cref="EmblemPipeline.Add"/>), then applied to the left pad, the right pad or both. The source path is never stored. A modal
/// over the appearance screen; Escape cancels. The decoding of the file is Godot's (<see cref="Image.Load"/>); everything after is engine-free and tested.
/// </summary>
public sealed partial class EmblemImporter : Control
{
    readonly CustomizerServices _services;
    readonly Control _background;
    readonly Action<EmblemHash, string> _applied;
    readonly Action _closed;
    readonly bool _leftOk, _rightOk;
    readonly Label _error = UiKit.Text("", UiKit.CaptionSize, UiKit.LightRed, HorizontalAlignment.Left, true);
    readonly Label _zoomText = UiKit.Text("", UiKit.CaptionSize, UiKit.White);
    readonly Label _fileText = UiKit.Text("", UiKit.CaptionSize, UiKit.Grey);
    readonly EmblemSquare _preview128 = new();
    readonly PadPreview _pad = new();
    CropArea? _crop;
    HSlider? _slider;
    Button? _left, _right, _both;
    byte[] _full = [];
    int _width, _height;
    byte[] _reduced = [];
    int _reducedWidth, _reducedHeight, _factor = 1;
    double _previewAt = -1;
    double _time;
    bool _closedOnce;
    Control? _previousFocus;

    public EmblemImporter(CustomizerServices services, Control background, Action<EmblemHash, string> applied, Action closed, bool leftOk, bool rightOk)
    {
        _services = services; _background = background; _applied = applied; _closed = closed; _leftOk = leftOk; _rightOk = rightOk;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Name = "EmblemImporter";
    }

    /// <summary>Opens the native file dialog. In a test window (no dialog can be driven) or with <paramref name="file"/> the file is read at once.</summary>
    public void Begin(Control host, string? file = null)
    {
        _previousFocus = _background.GetViewport()?.GuiGetFocusOwner();
        _background.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Disabled;
        host.AddChild(this);
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.7f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
        if (file is not null) LoadFile(file);
        else OpenFileDialog();
    }

    void OpenFileDialog()
    {
        if (ModelViewer.TestWindow) { ShowError("No file dialog in a test window (use --emblem-file)"); return; }
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
            Title = $"Choose an image for the shoulder pad (at most {EmblemPipeline.MaxSide} x {EmblemPipeline.MaxSide} pixels, {EmblemPipeline.MaxFileBytes / (1024 * 1024)} MB)", Filters = ["*.png, *.jpg, *.jpeg, *.webp, *.bmp, *.tga ; Images"],
        };
        AddChild(dialog);
        dialog.FileSelected += path => { dialog.QueueFree(); LoadFile(path); };
        dialog.Canceled += () => { dialog.QueueFree(); Close(); };
        dialog.PopupCentered(new Vector2I(1000, 650));
    }

    // ---- loading ----

    void LoadFile(string path)
    {
        long length = 0;
        try { length = new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        string? problem = EmblemPipeline.CheckFile(path, length);
        if (problem is null)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                // The header is read first (kind, size, animation): nothing over 4096 pixels a side, nothing empty and nothing unrecognised reaches a decoder.
                var inspection = EmblemPipeline.Inspect(bytes, path);
                problem = inspection.Error;
                if (problem is null)
                {
                    var image = new Image();
                    var err = inspection.Kind switch
                    {
                        ImageKind.Png => image.LoadPngFromBuffer(bytes),
                        ImageKind.Jpeg => image.LoadJpgFromBuffer(bytes),
                        ImageKind.Webp => image.LoadWebpFromBuffer(bytes),
                        ImageKind.Bmp => image.LoadBmpFromBuffer(bytes),
                        ImageKind.Tga => image.LoadTgaFromBuffer(bytes),
                        _ => Error.FileUnrecognized,
                    };
                    if (err != Error.Ok || image.IsEmpty()) problem = EmblemPipeline.ErrorUnreadable;
                    else
                    {
                        problem = EmblemPipeline.CheckImage(image.GetWidth(), image.GetHeight());   // the decoded size must agree with the limits too
                        if (problem is null)
                        {
                            if (image.IsCompressed()) image.Decompress();
                            image.Convert(Image.Format.Rgba8);
                            _full = image.GetData();
                            _width = image.GetWidth(); _height = image.GetHeight();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = EmblemPipeline.ErrorUnreadable; }
        }
        if (problem is not null) { ShowError(problem); return; }
        (_reduced, _reducedWidth, _reducedHeight, _factor) = EmblemPipeline.Downscale(_full, _width, _height, 1024);
        _fileText.Text = $"{Path.GetFileName(path)}  ({_width} x {_height})";   // only the file's name is shown, never stored
        BuildCropUi();
    }

    void ShowError(string message)
    {
        // The dialog is a sibling of this overlay (its background is the overlay), so it can be reached while the screen behind cannot.
        UiKit.Dialog.Open(this, GetParent() as Control ?? this, "Can't use this image", message + ".", ["Choose another", "Cancel"], i =>
        {
            if (i == 0 && !ModelViewer.TestWindow) OpenFileDialog(); else Close();
        }, cancelIndex: 1, titleColour: UiKit.LightRed);
    }

    // ---- the crop screen ----

    void BuildCropUi()
    {
        var row = UiKit.HBox(24);
        _crop = new CropArea(_reduced, _reducedWidth, _reducedHeight, _factor, _width, _height) { CustomMinimumSize = new Vector2(540, 540) };
        _crop.Changed += OnCropChanged;
        var left = UiKit.VBox(8);
        left.AddChild(_crop);
        left.AddChild(UiKit.Text("Drag to move. Mouse wheel or the slider to zoom. Arrow keys and + / - work too.", UiKit.CaptionSize, UiKit.Grey, HorizontalAlignment.Left, true).With(l => l.CustomMinimumSize = new Vector2(540, 0)));
        row.AddChild(left);

        var right = UiKit.VBox(10);
        right.CustomMinimumSize = new Vector2(400, 0);
        right.AddChild(UiKit.Text("SHOULDER EMBLEM", UiKit.HeaderSize, UiKit.Gold));
        right.AddChild(_fileText);
        right.AddChild(UiKit.Text("Actual size (128 x 128); keep important parts inside the dotted line", UiKit.CaptionSize, UiKit.Grey, HorizontalAlignment.Left, true).With(l => l.CustomMinimumSize = new Vector2(380, 0)));
        var previews = UiKit.HBox(16);
        previews.AddChild(_preview128);
        previews.AddChild(_pad);
        right.AddChild(previews);
        var zoomRow = UiKit.HBox(8);
        zoomRow.AddChild(UiKit.Text("Zoom", UiKit.BodySize, UiKit.White));
        _slider = new HSlider { MinValue = _crop.MinZoom, MaxValue = EmblemPipeline.MaxZoom, Step = 0.01, Value = _crop.Zoom, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, UiKit.MinHit), FocusMode = FocusModeEnum.All };
        _slider.ValueChanged += v => _crop.SetZoom((float)v);
        zoomRow.AddChild(_slider);
        right.AddChild(zoomRow);
        right.AddChild(_zoomText);
        var fit = UiKit.HBox(8);
        fit.AddChild(UiKit.Button("Fit", () => { _crop.SetZoom(_crop.InsetZoom); }, 120, "The whole picture with a margin, so the pad's curve does not clip it"));
        fit.AddChild(UiKit.Button("Fill", () => { _crop.SetZoom(1f); _crop.Recentre(); }, 120, "The shorter side fills the square"));
        right.AddChild(fit);
        right.AddChild(_error);
        right.AddChild(UiKit.Spacer(false));
        right.AddChild(UiKit.Text("APPLY TO", UiKit.CaptionSize, UiKit.Grey));
        var apply = UiKit.HBox(8);
        _left = UiKit.Button("LEFT PAD", () => Apply("left"), 0);
        _right = UiKit.Button("RIGHT PAD", () => Apply("right"), 0);
        _both = UiKit.Button("BOTH PADS", () => Apply("both"), 0);
        foreach (var b in new[] { _left, _right, _both }) { b.SizeFlagsHorizontal = SizeFlags.ExpandFill; apply.AddChild(b); }
        _left.Disabled = !_leftOk; _right.Disabled = !_rightOk; _both.Disabled = !(_leftOk && _rightOk);
        right.AddChild(apply);
        right.AddChild(UiKit.Button("CANCEL (Esc)", Close, 0));
        row.AddChild(right);

        var panel = UiKit.Panel(row, 24);
        panel.AddThemeStyleboxOverride("panel", UiKit.Box(UiKit.PanelSolid, UiKit.Gold, 3, 24));
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);
        OnCropChanged();
        _previewAt = 0;
        (_left.Disabled ? (_right.Disabled ? (Control)_crop : _right) : _left).FocusLater();
        var tail = new List<Control> { _left, _right, _both };
        UiKit.ChainHorizontal(tail);
    }

    void OnCropChanged()
    {
        if (_crop is null || _slider is null) return;
        _slider.SetValueNoSignal(_crop.Zoom);
        _zoomText.Text = $"Zoom {_crop.Zoom:F2}x   (window {_crop.CropWindow.Side:F0} px of the image)";
        _previewAt = _time + 0.12;
    }

    public override void _Process(double delta)
    {
        _time += delta;
        if (_previewAt >= 0 && _time >= _previewAt && _crop is not null) { _previewAt = -1; UpdatePreview(); }
    }

    void UpdatePreview()
    {
        var crop = _crop!.ReducedWindow;
        var rgba = EmblemPipeline.Resample(_reduced, _reducedWidth, _reducedHeight, crop);
        byte[] bc3 = EmblemPipeline.ToBc3(rgba);
        byte[] shown = Bc3.Decode(bc3, EmblemHash.Side, EmblemHash.Side);   // what will be stored, as it will look
        var image = Image.CreateFromData(EmblemHash.Side, EmblemHash.Side, false, Image.Format.Rgba8, shown);
        var texture = ImageTexture.CreateFromImage(image);
        _preview128.Emblem = texture;
        _pad.Emblem = texture;
    }

    void Apply(string target)
    {
        if (_crop is null) return;
        _error.Text = "";
        byte[] bc3 = EmblemPipeline.Build(_full, _width, _height, _crop.CropWindow);
        var (hash, error) = EmblemPipeline.Add(_services.Store, bc3);
        if (error is not null) { _error.Text = error; return; }
        var callback = _applied;
        Close();
        callback(hash, target);
    }

    void Close()
    {
        if (_closedOnce) return;
        _closedOnce = true;
        _background.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Inherited;
        if (GodotObject.IsInstanceValid(_previousFocus) && _previousFocus!.IsVisibleInTree()) _previousFocus.GrabFocus();
        QueueFree();
        _closed();
    }

    public override void _Input(InputEvent e)
    {
        if (_closedOnce) return;
        if (e.IsActionPressed("ui_cancel")) { GetViewport().SetInputAsHandled(); Close(); }
    }

    // ---- the cropping surface ----

    /// <summary>The picture with a square window over it: pan by dragging, zoom with the wheel; the whole control is the window.</summary>
    sealed partial class CropArea : Control
    {
        readonly ImageTexture _texture;
        readonly int _reducedWidth, _reducedHeight, _factor, _width, _height;
        public float Zoom { get; private set; } = 1f;
        float _cx, _cy;
        public event Action? Changed;

        public CropArea(byte[] rgba, int reducedWidth, int reducedHeight, int factor, int width, int height)
        {
            _reducedWidth = reducedWidth; _reducedHeight = reducedHeight; _factor = factor; _width = width; _height = height;
            _texture = ImageTexture.CreateFromImage(Image.CreateFromData(reducedWidth, reducedHeight, false, Image.Format.Rgba8, rgba));
            FocusMode = FocusModeEnum.All;
            MouseFilter = MouseFilterEnum.Stop;
            ClipContents = true;
            _cx = width / 2f; _cy = height / 2f;
            Zoom = EmblemPipeline.InsetZoom(width, height);   // the default leaves a margin round the picture
        }

        public float InsetZoom => EmblemPipeline.InsetZoom(_width, _height);

        public float MinZoom => EmblemPipeline.MinZoom(_width, _height);
        /// <summary>The window over the full-size image.</summary>
        public EmblemPipeline.Crop CropWindow => EmblemPipeline.CropFor(_width, _height, Zoom, _cx, _cy);
        /// <summary>The same window over the reduced copy.</summary>
        public EmblemPipeline.Crop ReducedWindow { get { var c = CropWindow; return new(c.X / _factor, c.Y / _factor, c.Side / _factor); } }

        public void SetZoom(float zoom)
        {
            Zoom = Mathf.Clamp(zoom, MinZoom, EmblemPipeline.MaxZoom);
            Normalise();
            Changed?.Invoke();
            QueueRedraw();
        }

        public void Recentre() { _cx = _width / 2f; _cy = _height / 2f; Normalise(); Changed?.Invoke(); QueueRedraw(); }

        void Normalise()
        {
            var c = CropWindow;   // clamps to the image
            _cx = c.X + c.Side / 2f; _cy = c.Y + c.Side / 2f;
        }

        public override void _Draw()
        {
            var size = Size;
            // A checker behind transparent and padded areas.
            float cell = 20f;
            for (int y = 0; y * cell < size.Y; y++)
                for (int x = 0; x * cell < size.X; x++)
                    DrawRect(new Rect2(x * cell, y * cell, cell, cell), (x + y) % 2 == 0 ? new Color(0.13f, 0.13f, 0.14f) : new Color(0.09f, 0.09f, 0.1f));
            var crop = CropWindow;
            float scale = size.X / crop.Side;
            // The part of the image inside the window, in source pixels, and where it lands.
            float sx0 = Mathf.Max(crop.X, 0), sy0 = Mathf.Max(crop.Y, 0), sx1 = Mathf.Min(crop.Right, _width), sy1 = Mathf.Min(crop.Bottom, _height);
            if (sx1 > sx0 && sy1 > sy0)
            {
                var dest = new Rect2((sx0 - crop.X) * scale, (sy0 - crop.Y) * scale, (sx1 - sx0) * scale, (sy1 - sy0) * scale);
                var src = new Rect2(sx0 / _factor, sy0 / _factor, (sx1 - sx0) / _factor, (sy1 - sy0) / _factor);
                DrawTextureRectRegion(_texture, dest, src);
            }
            // The square the emblem is cut from is the whole control; frame it, with the corners marked.
            DrawRect(new Rect2(Vector2.Zero, size), UiKit.Gold, false, 3f);
            if (HasFocus()) DrawRect(new Rect2(Vector2.One * 3, size - Vector2.One * 6), UiKit.White, false, 3f);
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseMotion { ButtonMask: var mask } motion && (mask & MouseButtonMask.Left) != 0)
            {
                float scale = Size.X / CropWindow.Side;
                _cx -= motion.Relative.X / scale; _cy -= motion.Relative.Y / scale;
                Normalise(); Changed?.Invoke(); QueueRedraw(); AcceptEvent();
            }
            else if (e is InputEventMouseButton { Pressed: true } button)
            {
                if (button.ButtonIndex == MouseButton.WheelUp) { SetZoom(Zoom * 1.1f); AcceptEvent(); }
                else if (button.ButtonIndex == MouseButton.WheelDown) { SetZoom(Zoom / 1.1f); AcceptEvent(); }
                else if (button.ButtonIndex == MouseButton.Left) GrabFocus();
            }
            else if (e is InputEventKey { Pressed: true } key && HasFocus())
            {
                float step = CropWindow.Side * 0.05f;
                switch (key.PhysicalKeycode)
                {
                    case Key.Left: _cx -= step; break;
                    case Key.Right: _cx += step; break;
                    case Key.Up: _cy -= step; break;
                    case Key.Down: _cy += step; break;
                    case Key.Equal or Key.KpAdd: SetZoom(Zoom * 1.1f); AcceptEvent(); return;
                    case Key.Minus or Key.KpSubtract: SetZoom(Zoom / 1.1f); AcceptEvent(); return;
                    default: return;
                }
                Normalise(); Changed?.Invoke(); QueueRedraw(); AcceptEvent();
            }
        }
    }

    /// <summary>The 128 x 128 emblem at actual size, with the safe area (<see cref="EmblemPipeline.InsetFraction"/>) marked by a dotted line.</summary>
    sealed partial class EmblemSquare : Control
    {
        public Texture2D? Emblem { get => _emblem; set { _emblem = value; QueueRedraw(); } }
        Texture2D? _emblem;

        public EmblemSquare() { CustomMinimumSize = new Vector2(128, 128); MouseFilter = MouseFilterEnum.Ignore; }

        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.12f, 0.12f, 0.13f));
            if (_emblem is not null) DrawTextureRect(_emblem, new Rect2(Vector2.Zero, Size), false);
            DrawSafeArea(this, new Rect2(Vector2.Zero, Size));
        }
    }

    /// <summary>A dotted rectangle at the safe share of <paramref name="area"/> (the pad's curvature clips what lies outside it).</summary>
    static void DrawSafeArea(CanvasItem item, Rect2 area)
    {
        float inset = (1f - EmblemPipeline.InsetFraction) / 2f;
        var r = new Rect2(area.Position + area.Size * inset, area.Size * EmblemPipeline.InsetFraction);
        var colour = new Color(UiKit.Gold, 0.9f);
        const float dash = 5f;
        for (float x = r.Position.X; x < r.End.X; x += dash * 2f)
        {
            float x2 = Mathf.Min(x + dash, r.End.X);
            item.DrawLine(new Vector2(x, r.Position.Y), new Vector2(x2, r.Position.Y), colour, 1.5f);
            item.DrawLine(new Vector2(x, r.End.Y), new Vector2(x2, r.End.Y), colour, 1.5f);
        }
        for (float y = r.Position.Y; y < r.End.Y; y += dash * 2f)
        {
            float y2 = Mathf.Min(y + dash, r.End.Y);
            item.DrawLine(new Vector2(r.Position.X, y), new Vector2(r.Position.X, y2), colour, 1.5f);
            item.DrawLine(new Vector2(r.End.X, y), new Vector2(r.End.X, y2), colour, 1.5f);
        }
    }

    /// <summary>A mock shoulder pad with the emblem on it: the same picture as it will sit on the armour, larger than the 128 px preview.</summary>
    sealed partial class PadPreview : Control
    {
        public Texture2D? Emblem { get => _emblem; set { _emblem = value; QueueRedraw(); } }
        Texture2D? _emblem;

        public PadPreview() { CustomMinimumSize = new Vector2(220, 150); MouseFilter = MouseFilterEnum.Ignore; }

        public override void _Draw()
        {
            var s = Size;
            // A chamfered plate in a gunmetal colour, like the pad's face.
            Vector2[] plate = [new(10, 30), new(s.X * 0.5f, 6), new(s.X - 10, 30), new(s.X - 24, s.Y - 10), new(24, s.Y - 10)];
            DrawColoredPolygon(plate, new Color(0.22f, 0.24f, 0.3f));
            DrawPolyline([.. plate, plate[0]], UiKit.Gold, 3f);
            if (_emblem is not null)
            {
                float side = Mathf.Min(s.X, s.Y) * 0.62f;
                var square = new Rect2((s.X - side) / 2f, (s.Y - side) / 2f + 8f, side, side);
                DrawTextureRect(_emblem, square, false);
                DrawSafeArea(this, square);
            }
        }
    }
}
