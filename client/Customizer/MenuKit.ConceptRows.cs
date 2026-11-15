using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The row controls drawn as the concept's panel 04 draws them (phase 4b, docs/ui-concept-textures.md "Phase 4b"): the arrow pill, the OFF / ON switch and the amber slider are the concept's own pixels
/// (opt-pill, opt-switch-off, opt-switch-on, opt-slider-track / -fill / -knob), the label is live Oswald at the concept's cap height. All sizes are the concept's own pixels times <c>s</c> (the window's scale);
/// behaviour, focus, keys and handlers are the steel rows' own. Ours.
/// </summary>
public static partial class UiKit
{
    /// <summary>The concept's own sizes in its 780 x 440 screen: row pitch, label and value fonts, control heights and widths, in concept pixels.</summary>
    public const float ConceptRowPitch = 31.2f, ConceptLabelFont = 14.3f, ConceptValueFont = 12.6f, ConceptPillW = 116.2f, ConceptPillH = 21f, ConceptSwitchW = 74.7f, ConceptSwitchH = 22f, ConceptSliderH = 17.7f;

    static readonly Dictionary<(string, int), ImageTexture> ScaledArt = new();

    /// <summary>A painted piece resampled by <paramref name="k"/> (the 4x art at the window's scale: <c>s / 4</c>), cached; a nine-patch does not scale its corners, so the corners are made the right size here.</summary>
    public static ImageTexture? ConceptScaled(string name, float k)
    {
        int key = Mathf.RoundToInt(k * 200f);
        if (ScaledArt.TryGetValue((name, key), out var cached)) return cached;
        if (ConceptArt.Get(name) is not { } tex) return null;
        var img = (Image)tex.GetImage().Duplicate(); img.Decompress();
        if (img.HasMipmaps()) img.ClearMipmaps();
        img.Resize(Mathf.Max(2, Mathf.RoundToInt(img.GetWidth() * key / 200f)), Mathf.Max(2, Mathf.RoundToInt(img.GetHeight() * key / 200f)), Image.Interpolation.Lanczos);
        return ScaledArt[(name, key)] = ImageTexture.CreateFromImage(img);
    }

    static FontVariation? _conceptFont;
    /// <summary>The concept's row lettering: Oswald squeezed to 84 % in width (the concept's face is narrower than Oswald at the same cap height).</summary>
    public static FontVariation ConceptFont() => _conceptFont ??= new FontVariation { BaseFont = Steel.CondensedFont(), VariationTransform = new Transform2D(new Vector2(0.84f, 0f), new Vector2(0f, 1f), Vector2.Zero), VariationEmbolden = 0.32f, SpacingGlyph = -1 };

    public sealed partial class MenuChoice
    {
        bool _concept;
        float _cs = 1f, _cCtlW;

        /// <summary>The width the control needs at scale <paramref name="s"/> (a pill grows past the concept's 117 for a long value).</summary>
        public float ConceptControlWidth(float s)
        {
            if (_isSwitch) return ConceptSwitchW * s;
            var font = ConceptFont(); float w = 0f; int size = Mathf.RoundToInt(ConceptValueFont * s);
            foreach (var v in _values) w = Mathf.Max(w, font.GetStringSize(v, HorizontalAlignment.Left, -1, size).X);
            return Mathf.Max(ConceptPillW * s, w + 36f * s);
        }

        public float ConceptLabelWidth(float s) => ConceptFont().GetStringSize(Text, HorizontalAlignment.Left, -1, Mathf.RoundToInt(ConceptLabelFont * s)).X;

        /// <summary>Draws the row in the concept's style: label column <paramref name="labelW"/> then the control (px); <paramref name="s"/> is the window's scale.</summary>
        public MenuChoice UseConcept(float s, float labelW, float ctlW)
        {
            UseSteel(labelW);
            _concept = true; _cs = s; _cCtlW = ctlW;
            CustomMinimumSize = new Vector2(labelW + ctlW, ConceptRowPitch * s);
            AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(ConceptLabelFont * s)); AddThemeFontOverride("font", ConceptFont());
            ClipText = false;
            _value.AddThemeFontOverride("font", ConceptFont());
            _value.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(ConceptValueFont * s));
            _value.SetAnchorsPreset(LayoutPreset.TopLeft);
            _value.VerticalAlignment = VerticalAlignment.Center;
            PlaceValue();
            Paint(false);
            return this;
        }

        void PlaceValue()
        {
            var r = ConceptRect();
            _value.Position = r.Position; _value.Size = r.Size;
        }

        /// <summary>The control's rectangle in the row's own pixels (the comparison tool reads it).</summary>
        public Rect2 ConceptControlRect => ConceptRect();

        /// <summary>The label column's width in the row's own pixels (the fit test reads it).</summary>
        public float LabelColumn => _concept ? _labelWidth : 0f;

        float ConceptControlW() => _isSwitch ? ConceptSwitchW * _cs : Mathf.Min(_cCtlW, ConceptControlWidth(_cs));

        Rect2 ConceptRect()
        {
            float h = (_isSwitch ? ConceptSwitchH : ConceptPillH) * _cs, w = ConceptControlW();
            float x = _labelWidth + Mathf.Min((_isSwitch ? 24f : 8f) * _cs, Mathf.Max(0f, _cCtlW - w));
            return new Rect2(x, (ConceptRowPitch * _cs - h) / 2f, w, h);
        }

        void DrawConcept()
        {
            var r = ConceptRect(); bool lit = (HasFocus() || _hover) && !Disabled;
            var c = GetCanvasItem();
            var mod = Disabled ? new Color(0.5f, 0.5f, 0.5f) : lit ? ConceptArt.Warm : Colors.White;
            if (_isSwitch)
            {
                if (ConceptArt.Get(_index == 1 ? "opt-switch-on" : "opt-switch-off") is { } tex) { var art = tex.GetSize() / 4f * UiTextureBuilder.GridPerV2 * _cs; DrawTextureRect(tex, new Rect2(r.GetCenter() - art / 2f, art), false, mod); }   // each art at its own size (v2: ON 76 x 21, OFF 75 x 22 in the page's pixels), centred on the control
                var font = ConceptFont(); int size = Mathf.RoundToInt(12.2f * _cs);
                string word = _values[_index];
                float artW = (ConceptArt.Get(_index == 1 ? "opt-switch-on" : "opt-switch-off")?.GetWidth() ?? 320) / 4f * UiTextureBuilder.GridPerV2 * _cs;
                float cx = r.GetCenter().X - artW / 2f + artW * (_index == 1 ? 0.307f : 0.631f);   // the word's centre on the concept's art (ON: 308 of 283..364.5, OFF: 650.5 of 600..680 in v2)
                float w = font.GetStringSize(word, HorizontalAlignment.Left, -1, size).X;
                var ink = Disabled ? Steel.TextOff : _index == 1 ? new Color(0.20f, 0.13f, 0.02f) : new Color(0.84f, 0.83f, 0.80f);
                DrawString(font, new Vector2(cx - w / 2f, r.Position.Y + (r.Size.Y + font.GetAscent(size) - font.GetDescent(size)) / 2f), word, HorizontalAlignment.Left, -1, size, ink);
            }
            else if (ConceptScaled("opt-pill", _cs * UiTextureBuilder.GridPerV2 / 4f) is { } pill)
            {
                float k = _cs * UiTextureBuilder.GridPerV2 / 4f;
                var patch = new StyleBoxTexture { Texture = pill, TextureMarginLeft = 72 * k, TextureMarginRight = 72 * k, TextureMarginTop = 8 * k, TextureMarginBottom = 8 * k, ModulateColor = mod, AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch };
                patch.Draw(c, r);
            }
            if (HasFocus() && !Disabled) PaintedButtons.Ring(_cs, 0.8f).Draw(c, r);
        }
    }

    public sealed partial class MenuSlider
    {
        public string LabelText => _label.Text;
        /// <summary>The track's rectangle in the row's own pixels, set by <see cref="UseConcept"/> (the comparison tool reads it).</summary>
        public Rect2 ConceptTrackRect { get; private set; }
        static readonly Dictionary<int, ImageTexture> KnobCache = new();

        /// <summary>The knob scaled to the window (cached by scale), optionally warmed for the focus.</summary>
        static ImageTexture ConceptKnob(float s, bool warm)
        {
            int key = Mathf.RoundToInt(s * 20f) * 2 + (warm ? 1 : 0);
            if (KnobCache.TryGetValue(key, out var cached)) return cached;
            var img = (Image)ConceptArt.Get("opt-slider-knob")!.GetImage().Duplicate();
            img.Decompress();
            img.Resize(Mathf.Max(4, Mathf.RoundToInt(16f * s)), Mathf.Max(4, Mathf.RoundToInt(17f * s)), Image.Interpolation.Lanczos);
            if (warm) for (int y = 0; y < img.GetHeight(); y++) for (int x = 0; x < img.GetWidth(); x++) { var p = img.GetPixel(x, y); img.SetPixel(x, y, new Color(Mathf.Min(1f, p.R * 1.18f), Mathf.Min(1f, p.G * 1.08f), p.B * 0.9f, p.A)); }
            return KnobCache[key] = ImageTexture.CreateFromImage(img);
        }

        public float ConceptLabelWidth(float s) => ConceptFont().GetStringSize(_label.Text, HorizontalAlignment.Left, -1, Mathf.RoundToInt(ConceptLabelFont * s)).X;

        /// <summary>The concept's slider row: label column <paramref name="labelW"/>, the track <paramref name="trackW"/> wide (concept art), then the value (px; <paramref name="s"/> is the window's scale).</summary>
        public MenuSlider UseConcept(float s, float labelW, float trackW, float valueW)
        {
            UseSteel(labelW, valueW);
            float k = s * UiTextureBuilder.GridPerV2 / 4f;   // the art is the v2 reference at x4 (v2 pixels are 14/15 of the page's grid pixels)
            if (ConceptScaled("opt-slider-track", k) is not { } track || ConceptScaled("opt-slider-fill", k) is not { } fill) return this;
            float h = ConceptSliderH * s;
            ConceptTrackRect = new Rect2(labelW, (ConceptRowPitch * s - h) / 2f + 3f * s, trackW, h);
            CustomMinimumSize = new Vector2(labelW + trackW + valueW + 6f * s, ConceptRowPitch * s);
            _label.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(ConceptLabelFont * s)); _label.AddThemeFontOverride("font", ConceptFont()); _value.AddThemeFontOverride("font", ConceptFont());
            _label.VerticalAlignment = VerticalAlignment.Center;
            _label.SetAnchorsPreset(LayoutPreset.TopLeft); _label.Position = Vector2.Zero; _label.Size = new Vector2(labelW, ConceptRowPitch * s);
            StyleBoxTexture Box(Texture2D tex, float ml, float mr, Color mod, float el, float er) => new()
            {
                Texture = tex, TextureMarginLeft = ml * k, TextureMarginRight = mr * k, TextureMarginTop = 6 * k, TextureMarginBottom = 6 * k, ModulateColor = mod,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch, AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
                ContentMarginTop = h / 2f, ContentMarginBottom = h / 2f, ExpandMarginLeft = el, ExpandMarginRight = er,
            };
            Slider.AddThemeStyleboxOverride("slider", Box(track, 56, 64, Colors.White, 7f * s, 7.5f * s));
            Slider.AddThemeStyleboxOverride("grabber_area", Box(fill, 22, 6, Colors.White, 0f, 0f));
            Slider.AddThemeStyleboxOverride("grabber_area_highlight", Box(fill, 22, 6, ConceptArt.Warm, 0f, 0f));
            Slider.AddThemeIconOverride("grabber", ConceptKnob(s, false)); Slider.AddThemeIconOverride("grabber_highlight", ConceptKnob(s, true)); Slider.AddThemeIconOverride("grabber_disabled", ConceptKnob(s, false));
            // The slider's own rectangle is the knob's travel (the track's caps stick out of it): from 7 px in to 7.5 px before the track's end (v2: the knob sits flush with the channel's end).
            Slider.SetAnchorsPreset(LayoutPreset.TopLeft);
            Slider.Position = new Vector2(labelW + 7f * s, (ConceptRowPitch * s - h) / 2f + 3f * s); Slider.Size = new Vector2(trackW - 14.5f * s, h);   // the concept's sliders hang 3 px below the label's centre
            _value.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(ConceptValueFont * s));
            _value.HorizontalAlignment = HorizontalAlignment.Left; _value.VerticalAlignment = VerticalAlignment.Center;
            _value.SetAnchorsPreset(LayoutPreset.TopLeft);
            _value.Position = new Vector2(labelW + trackW + 6f * s, 0f); _value.Size = new Vector2(valueW, ConceptRowPitch * s);
            return this;
        }
    }
}
