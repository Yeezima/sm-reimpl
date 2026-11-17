using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmFormats.Gfx;

namespace SmClient;

// classic-score-hud (owner 2026-11-17: the bottom-left element 1:1 with the original, slashed zeros included): the score panel's text drawn in the original's own face. The HUD movies import
// "$Bourgeois" / "$BourgeoisBoldAlt" from the install's font movie (fonts_en.gfx: XBourgeoix OT Medium / Bold Alternate), whose glyphs are vector outlines; they are read at runtime
// (SmFormats.Gfx.GfxFont), filled into coverage maps at the size on screen and drawn as quads, tilted with the panel. Nothing of the install is kept. Without the font movie the Bahnschrift text stays.
public sealed partial class Hud
{
    /// <summary><c>--old-score-font</c> (before / after aid): the score panel's text in Bahnschrift as it was.</summary>
    static readonly bool OldScoreFont = Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-score-font") >= 0;

    /// <summary>True while the score panel's fields are drawn: their text takes the original's face (the rest of the HUD keeps its own).</summary>
    bool _originalFontText;

    /// <summary>The next field's vertical shift in movie pixels (set by the caller's text callback just before the field is drawn).</summary>
    float _fieldNudge;

    readonly Dictionary<(bool Bold, char C, int Px), (ImageTexture? Texture, GfxGlyphMap? Map)> _glyphs = new();

    /// <summary>The original's face for a bold or regular field, or null while the font movie is not read (or missing).</summary>
    GfxFontFace? OriginalFace(bool bold)
    {
        if (OldScoreFont || _movieArt?.Movies?.Fonts is not { Count: > 0 } faces) return null;
        GfxFontFace? medium = null, boldFace = null;
        foreach (var f in faces) { if (f.IsBold) boldFace ??= f; else medium ??= f; }
        return bold ? boldFace ?? medium : medium ?? boldFace;
    }

    /// <summary>
    /// A text field's text in the original's face: <paramref name="xf"/> takes the field's pixels to virtual units (it may carry the panel's tilt), the first baseline lies the face's ascent below
    /// the field's top gutter, left, centre or right by the field's alignment. False without the face (the caller draws the Bahnschrift text).
    /// </summary>
    bool OriginalFieldText(HudMovie movie, GfxEditText t, string s, Transform2D xf, Color colour, float fontScale)
    {
        bool bold = movie.Movie.FontNames.GetValueOrDefault(t.FontId, "").Contains("Bold", StringComparison.OrdinalIgnoreCase);
        if (OriginalFace(bold) is not { } face) return false;
        double size = t.FontHeight * fontScale;   // the em, in field pixels
        double widthEm = face.WidthEm(s);
        double pen = t.Align == 1 ? t.X1 - 2.0 - widthEm * size : t.Align == 2 ? (t.X0 + t.X1) / 2.0 - widthEm * size / 2.0 : t.X0 + 2.0;
        double baseline = t.Y0 + 2.0 + face.Ascent / face.EmSquare * size + _fieldNudge;
        // Glyph pixels per field pixel: the raster is made at the size the text has on screen (virtual units to screen pixels by the HUD's scale).
        double toScreen = xf.Y.Length() * _scale;
        int px = Math.Max(4, (int)Math.Round(size * toScreen));
        double fieldPerGlyphPx = size / px;
        foreach (char c in s)
        {
            if (GlyphAt(face, bold, c, px) is { } g && g.Texture is { } tex && g.Map is { } map)
            {
                var m = xf * new Transform2D(0f, new Vector2((float)fieldPerGlyphPx, (float)fieldPerGlyphPx), 0f, new Vector2((float)pen, (float)baseline));
                DrawSetTransformMatrix(_base * _localXf * m);
                DrawTextureRect(tex, new Rect2(map.Left, map.Top, map.Width, map.Height), false, colour);
                DrawSetTransformMatrix(_base * _localXf);
            }
            pen += face.AdvanceEm(c) * size;
        }
        return true;
    }

    (ImageTexture? Texture, GfxGlyphMap? Map)? GlyphAt(GfxFontFace face, bool bold, char c, int px)
    {
        if (_glyphs.TryGetValue((bold, c, px), out var kept)) return kept;
        var map = face.Rasterize(c, px);
        ImageTexture? tex = null;
        if (map is not null)
        {
            var rgba = new byte[map.Width * map.Height * 4];
            for (int i = 0; i < map.Alpha.Length; i++) { rgba[i * 4] = 255; rgba[i * 4 + 1] = 255; rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = map.Alpha[i]; }
            tex = ImageTexture.CreateFromImage(Image.CreateFromData(map.Width, map.Height, false, Image.Format.Rgba8, rgba));
        }
        return _glyphs[(bold, c, px)] = (tex, map);
    }

    /// <summary>The text's width in virtual units in the original's face at <paramref name="sizeVirtual"/> (the em), or null without the face.</summary>
    float? OriginalWidth(string s, float sizeVirtual, bool bold) => OriginalFace(bold) is { } f ? (float)(f.WidthEm(s) * sizeVirtual) : null;
}
