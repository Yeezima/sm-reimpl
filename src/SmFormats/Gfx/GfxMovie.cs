using System.Text;

namespace SmFormats.Gfx;

/// <summary>A 2x3 affine matrix as a Flash movie stores it: x' = A x + C y + Tx, y' = B x + D y + Ty (twips or pixels, by use).</summary>
public readonly record struct GfxMatrix(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly GfxMatrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>This matrix applied after <paramref name="inner"/> (point -> inner -> this).</summary>
    public GfxMatrix After(GfxMatrix inner) => new(
        A * inner.A + C * inner.B, B * inner.A + D * inner.B,
        A * inner.C + C * inner.D, B * inner.C + D * inner.D,
        A * inner.Tx + C * inner.Ty + Tx, B * inner.Tx + D * inner.Ty + Ty);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + Tx, B * x + D * y + Ty);

    public GfxMatrix? Inverse()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return null;
        double ia = D / det, ib = -B / det, ic = -C / det, id = A / det;
        return new GfxMatrix(ia, ib, ic, id, -(ia * Tx + ic * Ty), -(ib * Tx + id * Ty));
    }
}

/// <summary>A colour transform (multiply in 1/256, add in 0..255 units), as a placement carries it.</summary>
public readonly record struct GfxColourTransform(float MulR, float MulG, float MulB, float MulA, float AddR, float AddG, float AddB, float AddA)
{
    public static readonly GfxColourTransform Identity = new(1, 1, 1, 1, 0, 0, 0, 0);

    /// <summary>This transform applied after <paramref name="inner"/>.</summary>
    public GfxColourTransform After(GfxColourTransform inner) => new(
        MulR * inner.MulR, MulG * inner.MulG, MulB * inner.MulB, MulA * inner.MulA,
        inner.AddR * MulR + AddR, inner.AddG * MulG + AddG, inner.AddB * MulB + AddB, inner.AddA * MulA + AddA);
}

public enum GfxFillKind { Solid, LinearGradient, RadialGradient, Bitmap }

/// <summary>One fill style of a shape. Colours are straight (non-premultiplied) RGBA in 0..1.</summary>
public sealed class GfxFill
{
    public GfxFillKind Kind;
    public (float R, float G, float B, float A) Colour;
    public GfxMatrix Matrix = GfxMatrix.Identity;
    public (float Ratio, float R, float G, float B, float A)[] Stops = [];
    public int BitmapId;
    public bool Repeat, Smooth;
}

/// <summary>A line style: width in twips and a solid colour (a line with a fill style takes that fill's colour).</summary>
public sealed class GfxLine
{
    public double Width;
    public (float R, float G, float B, float A) Colour;
}

/// <summary>A flattened path piece: a straight run of points (twips) with the fill styles on its left and right and its line style (0 none).</summary>
public sealed class GfxEdgeRun
{
    public List<(double X, double Y)> Points = [];
    public int FillLeft, FillRight, Line;
}

/// <summary>A shape: its styles (1-based in the runs) and edge runs, curves kept as quadratics until flattened by the renderer.</summary>
public sealed class GfxShape
{
    public int Id;
    public List<GfxFill> Fills = [];
    public List<GfxLine> Lines = [];
    /// <summary>Segments: either a line (Control null) or a quadratic curve (Control set), with their styles (indices into <see cref="Fills"/> / <see cref="Lines"/>, 1-based, 0 none).</summary>
    public List<GfxSegment> Segments = [];
}

public readonly record struct GfxSegment(double X0, double Y0, double CX, double CY, double X1, double Y1, bool Curved, int FillLeft, int FillRight, int Line);

/// <summary>One placement on a display list. <see cref="Ratio"/>: how far a morph shape is between its start and end (0 to 65535). <see cref="Blend"/>: the Flash blend mode of a PlaceObject3 (0: none given; 1 normal, 8 add ...); <see cref="ClipDepth"/> above 0 makes it a clip layer for the depths up to that one.</summary>
public sealed record GfxPlacement(int Depth, int Character, string Name, GfxMatrix? Matrix, GfxColourTransform? Colour, bool Move, int ClipDepth, int Blend = 0, int Ratio = 0);

/// <summary>One frame of a sprite: what is on the display list after its control tags ran (by depth).</summary>
public sealed class GfxFrame
{
    public SortedDictionary<int, GfxPlacement> Display = new();
    public string? Label;
}

public sealed class GfxSprite
{
    public int Id;
    public List<GfxFrame> Frames = [];
}

/// <summary>
/// A text field (DefineEditText) as the movie defines it: its rectangle (pixels, in the field's own space), the font's character id and height (pixels),
/// the default colour (straight RGBA 0..1), the alignment (0 left, 1 right, 2 centre, 3 justify), the leading (pixels) and the text the field starts with.
/// The text itself is drawn by the client (the glyphs live in the font movies); ui/hud-fidelity.
/// </summary>
public sealed record GfxEditText(int Id, double X0, double Y0, double X1, double Y1, int FontId, double FontHeight, (float R, float G, float B, float A) Colour,
    int Align, double Leading, string InitialText, bool Html, bool Multiline);

/// <summary>A picture inside an external image (the Scaleform exporter's sub-image record: rectangle in the image's pixels).</summary>
public readonly record struct GfxSubImage(int Image, int X0, int Y0, int X1, int Y1);

/// <summary>
/// A Scaleform GFX movie (uncompressed, SWF-like tag stream) read for its display structure: sprites with their frames, shapes with their styles and edges,
/// instance names, and the exporter's references to external image files (the bitmaps live next to the movie as DDS). Only structure and resource names are
/// read; no action (ActionScript, DoAction, DoABC) is read at all: those tags are skipped by their length. Used for the scope overlay (stalker bolter and lascannon aim mode).
/// </summary>
public sealed class GfxMovie
{
    public double StageWidth, StageHeight;
    public Dictionary<int, GfxShape> Shapes { get; } = new();
    /// <summary>Morph shapes (DefineMorphShape 46 and DefineMorphShape2 84) by character id (ui/hud-2).</summary>
    public Dictionary<int, GfxMorph> Morphs { get; } = new();
    public Dictionary<int, GfxSprite> Sprites { get; } = new();
    public Dictionary<int, GfxSubImage> SubImages { get; } = new();
    /// <summary>External image id -> file name as the exporter wrote it (e.g. <c>hud_I5D.dds</c>), with the size the movie gives.</summary>
    public Dictionary<int, (string File, int Width, int Height)> Images { get; } = new();
    /// <summary>The tag code that defined each character id (shapes 2/22/32/83, sprites 39, text 37, bitmaps 6/20/21/35/36/90, the exporter's images 1008/1009 ...).</summary>
    public Dictionary<int, int> DefinedBy { get; } = new();
    /// <summary>Text fields by character id (DefineEditText, tag 37).</summary>
    public Dictionary<int, GfxEditText> EditTexts { get; } = new();
    /// <summary>Font names by character id: a font the movie defines (DefineFont2/3) or imports (ImportAssets, e.g. <c>$BourgeoisBoldAlt</c>).</summary>
    public Dictionary<int, string> FontNames { get; } = new();
    /// <summary>The root timeline's first frame.</summary>
    public GfxSprite Root { get; } = new() { Id = 0 };

    byte[] _d = [];
    int _bit;

    /// <summary>Deepest sprite nesting read (a movie that nests deeper is refused: no stack overflow from a crafted file).</summary>
    public const int MaxSpriteDepth = 24;
    /// <summary>Most frames one timeline may hold.</summary>
    public const int MaxFrames = 4096;

    /// <summary>Reads a movie; any malformed or truncated data is an <see cref="InvalidDataException"/>.</summary>
    public static GfxMovie Parse(byte[] data)
    {
        if (data.Length < 12 || data[0] != 'G' || data[1] != 'F' || data[2] != 'X') throw new InvalidDataException("not an uncompressed GFX movie");
        try
        {
            var m = new GfxMovie { _d = data };
            int pos = 8;
            m._bit = pos * 8;
            int nbits = (int)m.UBits(5);
            double xmin = m.SBits(nbits), xmax = m.SBits(nbits), ymin = m.SBits(nbits), ymax = m.SBits(nbits);
            m.StageWidth = (xmax - xmin) / 20.0; m.StageHeight = (ymax - ymin) / 20.0;
            pos = (m._bit + 7) / 8 + 4;
            m.ReadTags(pos, data.Length, m.Root, 0);
            return m;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
        {
            throw new InvalidDataException("truncated or malformed GFX movie: " + e.Message, e);
        }
    }

    // ---- bits ----

    uint UBits(int n) { uint v = 0; for (int i = 0; i < n; i++) { v = (v << 1) | (uint)((_d[_bit >> 3] >> (7 - (_bit & 7))) & 1); _bit++; } return v; }
    int SBits(int n) { if (n == 0) return 0; int v = (int)UBits(n); if ((v & (1 << (n - 1))) != 0) v -= 1 << n; return v; }
    void Align() => _bit = (_bit + 7) & ~7;
    int Byte() { int v = _d[_bit >> 3]; _bit += 8; return v; }
    int U16() { int v = BitConverter.ToUInt16(_d, _bit >> 3); _bit += 16; return v; }
    string CString() { int p = _bit >> 3; int e = Array.IndexOf(_d, (byte)0, p); _bit = (e + 1) * 8; return Encoding.UTF8.GetString(_d, p, e - p); }

    // ---- tags ----

    void ReadTags(int pos, int end, GfxSprite owner, int depth)
    {
        if (depth > MaxSpriteDepth) throw new InvalidDataException("sprites nested too deeply");
        var frame = new GfxFrame();
        owner.Frames.Add(frame);
        while (pos + 2 <= end)
        {
            int hdr = BitConverter.ToUInt16(_d, pos); pos += 2;
            int code = hdr >> 6, len = hdr & 0x3f;
            if (len == 0x3f)
            {
                if (pos + 4 > end) throw new InvalidDataException("truncated tag header");
                len = BitConverter.ToInt32(_d, pos); pos += 4;
            }
            if (len < 0 || len > end - pos) throw new InvalidDataException("a tag's length runs past its parent");
            int body = pos; pos += len;
            _bit = body * 8;
            // Which tag defined each character (diagnostics: a character that is neither a shape nor a sprite, e.g. a bitmap, is not drawn).
            if (len >= 2 && code is 2 or 6 or 7 or 10 or 11 or 14 or 20 or 21 or 22 or 32 or 33 or 34 or 35 or 36 or 37 or 39 or 46 or 48 or 60 or 75 or 83 or 84 or 90 or 1008 or 1009)
                DefinedBy[BitConverter.ToUInt16(_d, body)] = code;
            switch (code)
            {
                case 0: if (owner.Frames.Count > 1) owner.Frames.RemoveAt(owner.Frames.Count - 1); return;   // the frame opened after the last ShowFrame has no tags
                case 1: // ShowFrame: the next frame starts from this one's display list
                    if (owner.Frames.Count >= MaxFrames) throw new InvalidDataException("too many frames");
                    var next = new GfxFrame();
                    foreach (var kv in frame.Display) next.Display[kv.Key] = kv.Value;
                    owner.Frames.Add(next); frame = next; break;
                case 43: frame.Label = CString(); break;
                case 26 or 70: Place(code, frame, body + len); break;
                case 28: { int d = U16(); frame.Display.Remove(d); break; }
                case 5: { U16(); int d = U16(); frame.Display.Remove(d); break; }
                case 2 or 22 or 32 or 83: Shape(code, body + len); break;
                case 46 or 84:
                    {
                        // A morph we cannot read is only absent (the tag is skipped by its length, as before morphs were read): it never stops the movie loading.
                        int morphId = len >= 2 ? BitConverter.ToUInt16(_d, body) : -1;
                        try { MorphShape(code, body + len); }
                        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException or InvalidDataException) { Morphs.Remove(morphId); }
                        break;
                    }
                case 39:
                    {
                        if (len < 4) throw new InvalidDataException("short sprite tag");
                        int id = U16(); U16();
                        var sprite = new GfxSprite { Id = id };
                        ReadTags(body + 4, body + len, sprite, depth + 1);
                        Sprites[id] = sprite; break;
                    }
                case 1008: { if (len < 12) break; int id = U16(); int img = U16(); SubImages[id] = new GfxSubImage(img, U16(), U16(), U16(), U16()); break; }
                case 1009:
                    {
                        // DefineExternalImage2 as the exporter wrote it: id(2), format(2), flags(2), width(2), height(2), export name (length byte, here 0), file name (length byte, text), 0.
                        if (len < 13) break;
                        int id = U16();
                        int w = BitConverter.ToUInt16(_d, body + 6), h = BitConverter.ToUInt16(_d, body + 8);
                        int exportLength = _d[body + 10];
                        int nameAt = body + 11 + exportLength;
                        if (nameAt + 1 + _d[nameAt] > body + len) break;
                        Images[id] = (Encoding.ASCII.GetString(_d, nameAt + 1, _d[nameAt]), w, h);
                        break;
                    }
                case 37: try { EditText(body + len); } catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { } break;   // a field we cannot read only loses its text info
                case 48 or 75:
                    try
                    {
                        // DefineFont2/3: id, flags, language, name (length byte, text); the glyphs are not read.
                        if (len >= 5)
                        {
                            int id = U16(); Byte(); Byte(); int n = Byte();
                            if ((_bit >> 3) + n <= body + len) FontNames[id] = Encoding.UTF8.GetString(_d, _bit >> 3, n).TrimEnd('\0');
                        }
                    }
                    catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { }
                    break;
                case 57 or 71:
                    try
                    {
                        // ImportAssets(2): source URL, (two reserved bytes in 71), count, then id + name pairs (the fonts a HUD movie takes from the font movie).
                        CString();
                        if (code == 71) { Byte(); Byte(); }
                        int count = U16();
                        for (int i = 0; i < count && (_bit >> 3) < body + len; i++) { int id = U16(); FontNames[id] = CString(); }
                    }
                    catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { }
                    break;
                // Everything else (actions: DoAction 12, DoInitAction 59, DoABC 82; glyphs, static text, sounds, ...) is skipped by its length and never read.
            }
        }
    }

    void Place(int code, GfxFrame frame, int end)
    {
        int flags = Byte(); int flags2 = code == 70 ? Byte() : 0;
        int depth = U16();
        int ch = -1;
        if (code == 70 && ((flags2 & 8) != 0 || ((flags2 & 0x10) != 0 && (flags & 2) != 0))) CString();   // class name
        if ((flags & 2) != 0) ch = U16();
        GfxMatrix? matrix = null; GfxColourTransform? cx = null; string name = ""; int clip = 0, blend = 0;
        if ((flags & 4) != 0) matrix = ReadMatrix();
        if ((flags & 8) != 0) cx = ReadColourTransform(true);
        int ratio = 0;
        if ((flags & 0x10) != 0) ratio = U16();
        if ((flags & 0x20) != 0) name = CString();
        if ((flags & 0x40) != 0) clip = U16();
        // PlaceObject3: surface filters (not read: their size is variable, so a placement with them gets no blend mode), then the blend mode, then bitmap caching.
        if (code == 70 && (flags2 & 1) == 0 && (flags2 & 2) != 0 && (_bit >> 3) < end) blend = Byte();
        bool move = (flags & 1) != 0;
        // Flash: a move keeps what the placement does not give (the character, the matrix, the colour transform, the name); a move with a new character replaces the character only.
        if (move && frame.Display.TryGetValue(depth, out var old))
            frame.Display[depth] = new GfxPlacement(depth, ch >= 0 ? ch : old.Character, name.Length > 0 ? name : old.Name, matrix ?? old.Matrix, cx ?? old.Colour, true, clip > 0 ? clip : old.ClipDepth, blend > 0 ? blend : old.Blend,
                (flags & 0x10) != 0 ? ratio : old.Ratio);
        else frame.Display[depth] = new GfxPlacement(depth, ch, name, matrix, cx, move, clip, blend, ratio);
    }

    void EditText(int end)
    {
        int id = U16();
        int nb = (int)UBits(5);
        double x0 = SBits(nb) / 20.0, x1 = SBits(nb) / 20.0, y0 = SBits(nb) / 20.0, y1 = SBits(nb) / 20.0;
        Align();
        int f1 = Byte(), f2 = Byte();
        bool hasText = (f1 & 0x80) != 0, multiline = (f1 & 0x20) != 0, hasColour = (f1 & 0x04) != 0, hasMax = (f1 & 0x02) != 0, hasFont = (f1 & 0x01) != 0;
        bool hasFontClass = (f2 & 0x80) != 0, hasLayout = (f2 & 0x20) != 0, html = (f2 & 0x02) != 0;
        int font = 0; double height = 0, leading = 0; int align = 0;
        (float, float, float, float) colour = (1f, 1f, 1f, 1f);
        if (hasFont) font = U16();
        if (hasFontClass) CString();
        if (hasFont || hasFontClass) height = U16() / 20.0;
        if (hasColour) colour = Colour(true);
        if (hasMax) U16();
        if (hasLayout) { align = Byte(); U16(); U16(); U16(); leading = (short)U16() / 20.0; }
        if ((_bit >> 3) < end) CString();   // variable name
        string text = hasText && (_bit >> 3) < end ? CString() : "";
        if ((_bit + 7) / 8 > end) return;   // ran past the tag: not a field we can read
        EditTexts[id] = new GfxEditText(id, x0, y0, x1, y1, font, height, colour, align, leading, text, html, multiline);
    }

    GfxMatrix ReadMatrix()
    {
        double a = 1, d = 1, b = 0, c = 0;
        if (UBits(1) == 1) { int n = (int)UBits(5); a = SBits(n) / 65536.0; d = SBits(n) / 65536.0; }
        if (UBits(1) == 1) { int n = (int)UBits(5); b = SBits(n) / 65536.0; c = SBits(n) / 65536.0; }
        int nt = (int)UBits(5); double tx = SBits(nt), ty = SBits(nt);
        Align();
        return new GfxMatrix(a, b, c, d, tx, ty);
    }

    GfxColourTransform ReadColourTransform(bool alpha)
    {
        bool hasAdd = UBits(1) == 1, hasMul = UBits(1) == 1; int n = (int)UBits(4);
        float mr = 256, mg = 256, mb = 256, ma = 256, ar = 0, ag = 0, ab = 0, aa = 0;
        if (hasMul) { mr = SBits(n); mg = SBits(n); mb = SBits(n); if (alpha) ma = SBits(n); }
        if (hasAdd) { ar = SBits(n); ag = SBits(n); ab = SBits(n); if (alpha) aa = SBits(n); }
        Align();
        return new GfxColourTransform(mr / 256f, mg / 256f, mb / 256f, ma / 256f, ar / 255f, ag / 255f, ab / 255f, aa / 255f);
    }

    // ---- shapes ----

    (float, float, float, float) Colour(bool rgba)
    {
        float r = Byte() / 255f, g = Byte() / 255f, b = Byte() / 255f, a = rgba ? Byte() / 255f : 1f;
        return (r, g, b, a);
    }

    GfxFill ReadFill(bool rgba)
    {
        int type = Byte();
        var f = new GfxFill();
        if (type == 0) { f.Kind = GfxFillKind.Solid; f.Colour = Colour(rgba); }
        else if (type is 0x10 or 0x12 or 0x13)
        {
            f.Kind = type == 0x10 ? GfxFillKind.LinearGradient : GfxFillKind.RadialGradient;
            f.Matrix = ReadMatrix();
            int flags = Byte(); int n = flags & 15;
            f.Stops = new (float, float, float, float, float)[n];
            for (int i = 0; i < n; i++) { float ratio = Byte() / 255f; var c = Colour(rgba); f.Stops[i] = (ratio, c.Item1, c.Item2, c.Item3, c.Item4); }
            if (type == 0x13) U16();
        }
        else if (type is >= 0x40 and <= 0x43)
        {
            f.Kind = GfxFillKind.Bitmap; f.BitmapId = U16(); f.Matrix = ReadMatrix();
            f.Repeat = type is 0x40 or 0x42; f.Smooth = type is 0x40 or 0x41;
        }
        else throw new InvalidDataException("unknown fill style 0x" + type.ToString("X"));
        return f;
    }

    void ReadStyles(GfxShape s, int code, int fillBase, int lineBase)
    {
        bool rgba = code >= 32;
        int nf = Byte(); if (nf == 0xff && code != 2) nf = U16();
        for (int i = 0; i < nf; i++) s.Fills.Add(ReadFill(rgba));
        int nl = Byte(); if (nl == 0xff && code != 2) nl = U16();
        for (int i = 0; i < nl; i++)
        {
            var l = new GfxLine { Width = U16() };
            if (code == 83)
            {
                int f1 = Byte(), f2 = Byte();
                int join = (f1 >> 4) & 3; bool hasFill = (f1 & 8) != 0;
                if (join == 2) U16();
                if (hasFill) { var fill = ReadFill(true); l.Colour = fill.Kind == GfxFillKind.Solid ? fill.Colour : (fill.Stops.Length > 0 ? (fill.Stops[0].R, fill.Stops[0].G, fill.Stops[0].B, fill.Stops[0].A) : (1f, 1f, 1f, 1f)); }
                else l.Colour = Colour(true);
            }
            else l.Colour = Colour(rgba);
            s.Lines.Add(l);
        }
    }

    void Shape(int code, int end)
    {
        var s = new GfxShape { Id = U16() };
        // bounds
        int nb = (int)UBits(5); SBits(nb); SBits(nb); SBits(nb); SBits(nb); Align();
        if (code == 83) { nb = (int)UBits(5); SBits(nb); SBits(nb); SBits(nb); SBits(nb); Align(); Byte(); }
        ReadStyles(s, code, 0, 0);
        int fillBits = (int)UBits(4), lineBits = (int)UBits(4);
        int fillBase = 0, lineBase = 0, fill0 = 0, fill1 = 0, line = 0;
        double x = 0, y = 0;
        while (true)
        {
            if (UBits(1) == 0)
            {
                uint flags = UBits(5);
                if (flags == 0) break;
                if ((flags & 1) != 0) { int n = (int)UBits(5); x = SBits(n); y = SBits(n); }
                if ((flags & 2) != 0) { int v = (int)UBits(fillBits); fill0 = v == 0 ? 0 : v + fillBase; }
                if ((flags & 4) != 0) { int v = (int)UBits(fillBits); fill1 = v == 0 ? 0 : v + fillBase; }
                if ((flags & 8) != 0) { int v = (int)UBits(lineBits); line = v == 0 ? 0 : v + lineBase; }
                if ((flags & 16) != 0)
                {
                    Align(); fillBase = s.Fills.Count; lineBase = s.Lines.Count;
                    ReadStyles(s, code, fillBase, lineBase);
                    fillBits = (int)UBits(4); lineBits = (int)UBits(4);
                }
            }
            else if (UBits(1) == 1)
            {
                int n = (int)UBits(4) + 2; double dx = 0, dy = 0;
                if (UBits(1) == 1) { dx = SBits(n); dy = SBits(n); }
                else if (UBits(1) == 0) dx = SBits(n); else dy = SBits(n);
                s.Segments.Add(new GfxSegment(x, y, 0, 0, x + dx, y + dy, false, fill0, fill1, line));
                x += dx; y += dy;
            }
            else
            {
                int n = (int)UBits(4) + 2;
                double cx = x + SBits(n), cy = y + SBits(n), ax = cx + SBits(n), ay = cy + SBits(n);
                s.Segments.Add(new GfxSegment(x, y, cx, cy, ax, ay, true, fill0, fill1, line));
                x = ax; y = ay;
            }
        }
        Shapes[s.Id] = s;
    }

    /// <summary>The shape a placement of character <paramref name="id"/> draws at <paramref name="ratio"/>: a plain shape as it is, a morph shape between its start and end (ui/hud-2); null for neither.</summary>
    public GfxShape? ShapeAt(int id, int ratio) => Shapes.TryGetValue(id, out var shape) ? shape : Morphs.TryGetValue(id, out var morph) ? morph.At(ratio) : null;

    // ---- morph shapes (ui/hud-2) ----

    /// <summary>One morph fill style: the start and the end fill (colours, gradient stops and matrices, or a bitmap's matrix).</summary>
    (GfxFill Start, GfxFill End) ReadMorphFill()
    {
        int type = Byte();
        GfxFill a = new(), b = new();
        if (type == 0) { a.Kind = b.Kind = GfxFillKind.Solid; a.Colour = Colour(true); b.Colour = Colour(true); }
        else if (type is 0x10 or 0x12 or 0x13)
        {
            a.Kind = b.Kind = type == 0x10 ? GfxFillKind.LinearGradient : GfxFillKind.RadialGradient;
            a.Matrix = ReadMatrix(); b.Matrix = ReadMatrix();
            int n = Byte() & 15;
            a.Stops = new (float, float, float, float, float)[n]; b.Stops = new (float, float, float, float, float)[n];
            for (int i = 0; i < n; i++)
            {
                float ra = Byte() / 255f; var ca = Colour(true); float rb = Byte() / 255f; var cb = Colour(true);
                a.Stops[i] = (ra, ca.Item1, ca.Item2, ca.Item3, ca.Item4); b.Stops[i] = (rb, cb.Item1, cb.Item2, cb.Item3, cb.Item4);
            }
        }
        else if (type is >= 0x40 and <= 0x43)
        {
            a.Kind = b.Kind = GfxFillKind.Bitmap; a.BitmapId = b.BitmapId = U16(); a.Matrix = ReadMatrix(); b.Matrix = ReadMatrix();
            a.Repeat = b.Repeat = type is 0x40 or 0x42; a.Smooth = b.Smooth = type is 0x40 or 0x41;
        }
        else throw new InvalidDataException("unknown morph fill style 0x" + type.ToString("X"));
        return (a, b);
    }

    /// <summary>
    /// DefineMorphShape (46) and DefineMorphShape2 (84): the bounds, the morph fill and line styles, then the start edges and the end edges as two shapes (the end shape's segments take the
    /// start segment's styles: the end edges only move points). The two halves are paired by <see cref="GfxMorph"/>.
    /// </summary>
    void MorphShape(int code, int tagEnd)
    {
        int id = U16();
        for (int r = 0; r < (code == 84 ? 4 : 2); r++) { int nb = (int)UBits(5); SBits(nb); SBits(nb); SBits(nb); SBits(nb); Align(); }
        if (code == 84) Byte();   // reserved, uses non-scaling / scaling strokes
        _bit += 32;   // the offset to the end edges
        GfxShape start = new() { Id = id }, end = new() { Id = id };
        int nf = Byte(); if (nf == 0xff) nf = U16();
        for (int i = 0; i < nf; i++) { var (a, b) = ReadMorphFill(); start.Fills.Add(a); end.Fills.Add(b); }
        int nl = Byte(); if (nl == 0xff) nl = U16();
        for (int i = 0; i < nl; i++)
        {
            double wa = U16(), wb = U16();
            GfxLine la = new() { Width = wa }, lb = new() { Width = wb };
            if (code == 84)
            {
                int f1 = Byte(); Byte();
                int join = (f1 >> 4) & 3; bool hasFill = (f1 & 8) != 0;
                if (join == 2) U16();
                if (hasFill)
                {
                    var (fa, fb) = ReadMorphFill();
                    (float, float, float, float) C(GfxFill f) => f.Kind == GfxFillKind.Solid ? f.Colour : (f.Stops.Length > 0 ? (f.Stops[0].R, f.Stops[0].G, f.Stops[0].B, f.Stops[0].A) : (1f, 1f, 1f, 1f));
                    la.Colour = C(fa); lb.Colour = C(fb);
                }
                else { la.Colour = Colour(true); lb.Colour = Colour(true); }
            }
            else { la.Colour = Colour(true); lb.Colour = Colour(true); }
            start.Lines.Add(la); end.Lines.Add(lb);
        }
        ReadEdgeRecords(start, null);
        Align();
        ReadEdgeRecords(end, start);
        if ((_bit + 7) / 8 > tagEnd) throw new InvalidDataException("a morph shape runs past its tag");
        Morphs[id] = new GfxMorph(id, start, end);
    }

    /// <summary>
    /// A SHAPE (fill and line bit counts, then records) of a morph half into <paramref name="s"/>; for the end half <paramref name="styled"/> is the start shape whose segments' styles the end
    /// segments take (paired by order).
    /// </summary>
    void ReadEdgeRecords(GfxShape s, GfxShape? styled)
    {
        int fillBits = (int)UBits(4), lineBits = (int)UBits(4);
        int fill0 = 0, fill1 = 0, line = 0;
        double x = 0, y = 0;
        while (true)
        {
            if (UBits(1) == 0)
            {
                uint flags = UBits(5);
                if (flags == 0) break;
                if ((flags & 1) != 0) { int n = (int)UBits(5); x = SBits(n); y = SBits(n); }
                if ((flags & 2) != 0) fill0 = (int)UBits(fillBits);
                if ((flags & 4) != 0) fill1 = (int)UBits(fillBits);
                if ((flags & 8) != 0) line = (int)UBits(lineBits);
                if ((flags & 16) != 0) throw new InvalidDataException("a morph shape's edges change styles");
            }
            else
            {
                GfxSegment seg;
                if (UBits(1) == 1)
                {
                    int n = (int)UBits(4) + 2; double dx = 0, dy = 0;
                    if (UBits(1) == 1) { dx = SBits(n); dy = SBits(n); }
                    else if (UBits(1) == 0) dx = SBits(n); else dy = SBits(n);
                    seg = new GfxSegment(x, y, 0, 0, x + dx, y + dy, false, fill0, fill1, line);
                    x += dx; y += dy;
                }
                else
                {
                    int n = (int)UBits(4) + 2;
                    double cx = x + SBits(n), cy = y + SBits(n), ax = cx + SBits(n), ay = cy + SBits(n);
                    seg = new GfxSegment(x, y, cx, cy, ax, ay, true, fill0, fill1, line);
                    x = ax; y = ay;
                }
                if (styled is not null && s.Segments.Count < styled.Segments.Count) { var m = styled.Segments[s.Segments.Count]; seg = seg with { FillLeft = m.FillLeft, FillRight = m.FillRight, Line = m.Line }; }
                s.Segments.Add(seg);
            }
        }
    }

    /// <summary>The ids of the bitmaps that the shapes under a character (a sprite or a shape; every frame, nested sprites included) fill with.</summary>
    public IReadOnlyCollection<int> BitmapIds(int character)
    {
        var ids = new HashSet<int>(); var seen = new HashSet<int>();
        void Walk(int id)
        {
            if (!seen.Add(id)) return;
            if (Shapes.TryGetValue(id, out var shape)) { foreach (var f in shape.Fills) if (f.Kind == GfxFillKind.Bitmap) ids.Add(f.BitmapId); return; }
            if (!Sprites.TryGetValue(id, out var sprite)) return;
            foreach (var frame in sprite.Frames) foreach (var p in frame.Display.Values) if (p.Character >= 0) Walk(p.Character);
        }
        Walk(character);
        return ids;
    }

    /// <summary>The first placement named <paramref name="name"/> (an instance name) in any sprite or the root, with a character; null when none.</summary>
    public GfxPlacement? FindNamedPlacement(string name)
    {
        foreach (var sprite in Sprites.Values.Append(Root))
            foreach (var frame in sprite.Frames)
                foreach (var p in frame.Display.Values)
                    if (p.Name == name && p.Character >= 0) return p;
        return null;
    }
}
