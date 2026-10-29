using System.Text;
using SmFormats.Gfx;

namespace SmFormats.Tests;

/// <summary>
/// The Scaleform movie reader and its small renderer (feel/scope-lascannon), on a synthetic movie built here (no original bytes): a sprite placed at a position with a colour transform
/// holds a red rectangle shape; another shape is filled with a bitmap; an exporter sub-image record names an external image.
/// </summary>
public class GfxTests
{
    sealed class Bits
    {
        readonly List<byte> _b = [];
        int _n;
        public void Put(int value, int bits) { for (int i = bits - 1; i >= 0; i--) PutBit((value >> i) & 1); }
        void PutBit(int bit) { if (_n % 8 == 0) _b.Add(0); if (bit != 0) _b[^1] |= (byte)(0x80 >> (_n % 8)); _n++; }
        public void Signed(int value, int bits) => Put(value & ((1 << bits) - 1), bits);
        public byte[] ToArray() => [.. _b];
    }

    static byte[] Rect(int xmin, int xmax, int ymin, int ymax)
    {
        var b = new Bits(); b.Put(16, 5);
        foreach (int v in new[] { xmin, xmax, ymin, ymax }) b.Signed(v, 16);
        return b.ToArray();
    }

    static byte[] Tag(int code, byte[] body)
    {
        var o = new List<byte>();
        o.AddRange(BitConverter.GetBytes((ushort)((code << 6) | 0x3f)));
        o.AddRange(BitConverter.GetBytes(body.Length));
        o.AddRange(body);
        return [.. o];
    }

    static byte[] Matrix(double scale, int tx, int ty)
    {
        var b = new Bits();
        if (scale != 1) { b.Put(1, 1); b.Put(26, 5); b.Signed((int)(scale * 65536), 26); b.Signed((int)(scale * 65536), 26); } else b.Put(0, 1);
        b.Put(0, 1);
        b.Put(16, 5); b.Signed(tx, 16); b.Signed(ty, 16);
        return b.ToArray();
    }

    /// <summary>A filled rectangle shape of w x h twips at the origin: a solid fill (type 0) or a bitmap fill (type 0x41, bitmap id, 1:1).</summary>
    static byte[] Shape(int id, int w, int h, byte[] fill)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)id));
        body.AddRange(Rect(0, w, 0, h));
        body.Add(1);          // one fill style
        body.AddRange(fill);
        body.Add(0);          // no line styles
        var b = new Bits();
        b.Put(1, 4); b.Put(0, 4);                       // fill bits 1, line bits 0
        b.Put(0, 1); b.Put(5, 5);                       // style change: MoveTo + FillStyle1
        b.Put(16, 5); b.Signed(0, 16); b.Signed(0, 16); // move to 0,0
        b.Put(1, 1);                                    // fill style 1
        void Edge(int dx, int dy) { b.Put(1, 1); b.Put(1, 1); b.Put(14, 4); b.Put(1, 1); b.Signed(dx, 16); b.Signed(dy, 16); }   // straight, general line, 16-bit deltas
        Edge(w, 0); Edge(0, h); Edge(-w, 0); Edge(0, -h);
        b.Put(0, 1); b.Put(0, 5);                       // end
        body.AddRange(b.ToArray());
        return Tag(2, [.. body]);
    }

    static byte[] Place(int depth, int ch, byte[] matrix, string name = "", byte[]? colour = null)
    {
        int flags = 0x02 | 0x04 | (colour is not null ? 0x08 : 0) | (name.Length > 0 ? 0x20 : 0);
        var body = new List<byte> { (byte)flags };
        body.AddRange(BitConverter.GetBytes((ushort)depth));
        body.AddRange(BitConverter.GetBytes((ushort)ch));
        body.AddRange(matrix);
        if (colour is not null) body.AddRange(colour);
        if (name.Length > 0) { body.AddRange(Encoding.ASCII.GetBytes(name)); body.Add(0); }
        return Tag(26, [.. body]);
    }

    /// <summary>A colour transform with alpha: multiply alpha by <paramref name="alpha"/>/256, nothing else.</summary>
    static byte[] AlphaTransform(int alpha)
    {
        var b = new Bits();
        b.Put(0, 1); b.Put(1, 1); b.Put(10, 4);          // no add terms, multiply terms, 10 bits
        foreach (int v in new[] { 256, 256, 256, alpha }) b.Signed(v, 10);
        return b.ToArray();
    }

    static byte[] Sprite(int id, params byte[][] tags)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)id)); body.AddRange(BitConverter.GetBytes((ushort)1));
        foreach (var t in tags) body.AddRange(t);
        body.AddRange(Tag(1, [])); body.AddRange(Tag(0, []));
        return Tag(39, [.. body]);
    }

    static byte[] Movie(params byte[][] tags)
    {
        var body = new List<byte>();
        body.AddRange(Rect(0, 100 * 20, 0, 60 * 20));
        body.AddRange(BitConverter.GetBytes((ushort)(30 << 8))); body.AddRange(BitConverter.GetBytes((ushort)1));
        foreach (var t in tags) body.AddRange(t);
        body.AddRange(Tag(1, [])); body.AddRange(Tag(0, []));
        var o = new List<byte>(Encoding.ASCII.GetBytes("GFX")) { 8 };
        o.AddRange(BitConverter.GetBytes(8 + body.Count));
        o.AddRange(body);
        return [.. o];
    }

    static readonly byte[] Red = [0x00, 255, 0, 0];

    [Fact]
    public void ReadsSpritesShapesNamesAndPlacements()
    {
        var movie = GfxMovie.Parse(Movie(Shape(1, 20 * 20, 10 * 20, Red), Sprite(2, Place(1, 1, Matrix(1, 5 * 20, 7 * 20))), Place(1, 2, Matrix(1, 30 * 20, 20 * 20), "box_mc")));
        Assert.Equal(100, movie.StageWidth); Assert.Equal(60, movie.StageHeight);
        Assert.Single(movie.Shapes); Assert.Single(movie.Sprites);
        Assert.Equal(4, movie.Shapes[1].Segments.Count);
        var p = movie.FindNamedPlacement("box_mc");
        Assert.NotNull(p); Assert.Equal(2, p!.Character);
        Assert.Equal(30 * 20, p.Matrix!.Value.Tx);
        Assert.Null(movie.FindNamedPlacement("missing"));
    }

    [Fact]
    public void DrawsTheRectangleWhereItIsPlacedAndHonoursTheColourTransformAndScale()
    {
        var movie = GfxMovie.Parse(Movie(Shape(1, 20 * 20, 10 * 20, Red), Sprite(2, Place(1, 1, Matrix(1, 5 * 20, 7 * 20), "", AlphaTransform(128))), Place(1, 2, Matrix(1, 30 * 20, 20 * 20), "box_mc")));
        var canvas = new GfxCanvas(200, 120);
        var m = new GfxMatrix(2, 0, 0, 2, 0, 0);   // the stage drawn at twice the size
        GfxRaster.Draw(movie, 0, 0, m, canvas, _ => null);
        // The rectangle: x 30+5 .. 55, y 20+7 .. 37 on the stage -> 70..110, 54..74 on the canvas.
        float[] At(int x, int y) { var (r, g, b, a) = canvas.At(x, y); return [r, g, b, a]; }
        var inside = At(90, 64);
        Assert.InRange(inside[3], 0.49f, 0.51f);          // the transform halves the alpha (premultiplied: red 0.5)
        Assert.InRange(inside[0], 0.49f, 0.51f); Assert.Equal(0f, inside[1]);
        Assert.Equal(0f, At(60, 64)[3]); Assert.Equal(0f, At(90, 40)[3]); Assert.Equal(0f, At(120, 64)[3]);
        // The edge is anti-aliased: a pixel straddling x = 70 is partly covered... (the rectangle starts exactly on a pixel border here, so the pixels either side are 0 and full).
        Assert.InRange(At(70, 64)[3], 0.49f, 0.51f);
    }

    [Fact]
    public void FillsAShapeFromABitmapThroughItsMatrixAndReadsTheExporterRecords()
    {
        // A bitmap fill (type 0x41) of bitmap 9, mapped 1:1 (a scale of 20 twips a pixel).
        var fill = new List<byte> { 0x41 };
        fill.AddRange(BitConverter.GetBytes((ushort)9)); fill.AddRange(Matrix(20, 0, 0));
        // External image 3 named hud_x.dds and sub-image 9 = the 2 x 2 corner of it (tag 1009 and 1008 as the exporter writes them).
        var image = new List<byte>(); image.AddRange(BitConverter.GetBytes((ushort)3)); image.AddRange(new byte[] { 9, 0, 14, 0 }); image.AddRange(BitConverter.GetBytes((ushort)64)); image.AddRange(BitConverter.GetBytes((ushort)64));
        image.Add(0); image.Add(9); image.AddRange(Encoding.ASCII.GetBytes("hud_X.dds")); image.Add(0);
        var sub = new List<byte>(); sub.AddRange(BitConverter.GetBytes((ushort)9)); sub.AddRange(BitConverter.GetBytes((ushort)3));
        foreach (int v in new[] { 0, 0, 2, 2 }) sub.AddRange(BitConverter.GetBytes((ushort)v));
        var movie = GfxMovie.Parse(Movie(Tag(1009, [.. image]), Tag(1008, [.. sub]), Shape(1, 4 * 20, 4 * 20, [.. fill]), Place(1, 1, Matrix(1, 10 * 20, 10 * 20), "pic_mc")));
        Assert.Equal("hud_X.dds", movie.Images[3].File);
        Assert.Equal(new GfxSubImage(3, 0, 0, 2, 2), movie.SubImages[9]);
        // Pixels (x, y): (0,0) red, (1,0) green, (0,1) blue, (1,1) white; clamped beyond.
        var rgba = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 };
        var canvas = new GfxCanvas(40, 40);
        GfxRaster.Draw(movie, 0, 0, GfxMatrix.Identity, canvas, id => id == 9 ? new GfxBitmap(2, 2, rgba) : null);
        float[] At(int x, int y) { var (r, g, b, a) = canvas.At(x, y); return [r, g, b, a]; }
        Assert.True(At(10, 10)[0] > 0.9f && At(10, 10)[1] < 0.1f);       // red corner
        Assert.True(At(11, 10)[1] > 0.5f && At(11, 10)[0] < 0.5f);       // greenish: the sample of pixel 1 (bilinear with the clamped edge)
        Assert.Equal(1f, At(13, 13)[3]);                                  // the whole 4 x 4 shape is covered
        Assert.Equal(0f, At(14, 14)[3]);
        // Without the bitmap nothing is drawn for the fill.
        var empty = new GfxCanvas(40, 40);
        GfxRaster.Draw(movie, 0, 0, GfxMatrix.Identity, empty, _ => null);
        Assert.All(empty.Pixels, v => Assert.Equal((ushort)0, v));
    }

    /// <summary>A placement with a blend mode needs PlaceObject3 (tag 70): flags, flags2 (blend mode present), depth, character, matrix, then the blend byte.</summary>
    static byte[] PlaceBlend(int depth, int ch, byte[] matrix, int blend)
    {
        var body = new List<byte> { 0x06, 0x02 };   // has character + has matrix; has blend mode
        body.AddRange(BitConverter.GetBytes((ushort)depth)); body.AddRange(BitConverter.GetBytes((ushort)ch));
        body.AddRange(matrix); body.Add((byte)blend);
        return Tag(70, [.. body]);
    }

    [Fact]
    public void AddBlendedPlacementsGoToTheAdditiveCanvasAndSumThere()
    {
        var movie = GfxMovie.Parse(Movie(Shape(1, 20 * 20, 10 * 20, Red), Sprite(2, Place(1, 1, Matrix(1, 0, 0)), PlaceBlend(2, 1, Matrix(1, 0, 0), 8)), Place(1, 2, Matrix(1, 0, 0), "glow_mc")));
        Assert.Equal(8, movie.Sprites[2].Frames[0].Display[2].Blend);
        var normal = new GfxCanvas(100, 60); var additive = new GfxCanvas(100, 60, additive: true);
        GfxRaster.Draw(movie, 0, 0, GfxMatrix.Identity, normal, _ => null, null, additive);
        Assert.Equal(1f, normal.At(10, 5).A);                      // the normal red rectangle
        Assert.Equal(1f, additive.At(10, 5).R);                    // the added one (saturated at 1)
        Assert.Equal(0f, additive.At(10, 5).A);                    // an additive canvas keeps no alpha
        Assert.Equal(0f, additive.At(40, 5).R);
        // Without an additive canvas the same placement draws as normal.
        var only = new GfxCanvas(100, 60);
        GfxRaster.Draw(movie, 0, 0, GfxMatrix.Identity, only, _ => null);
        Assert.Equal(1f, only.At(10, 5).A);
        // The additive layer's bytes are the colour with alpha 255.
        var bytes = additive.ToRgba8();
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, bytes[((5 * 100 + 10) * 4)..((5 * 100 + 10) * 4 + 4)]);
    }

    [Fact]
    public void ARenderCanBeCancelled()
    {
        var movie = GfxMovie.Parse(Movie(Shape(1, 20 * 20, 10 * 20, Red), Place(1, 1, Matrix(1, 0, 0))));
        var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => GfxRaster.Draw(movie, 0, 0, GfxMatrix.Identity, new GfxCanvas(100, 60), _ => null, null, null, cts.Token));
    }

    [Fact]
    public void ActionTagsAreSkippedByTheirLengthAndNeverRead()
    {
        // DoAction (12), DoABC (82) and DoInitAction (59) full of junk (not valid actions, not valid ABC), then real content.
        var junk = Enumerable.Range(0, 200).Select(i => (byte)(i * 37 + 11)).ToArray();
        var movie = GfxMovie.Parse(Movie(Tag(12, junk), Tag(82, junk), Tag(59, junk), Shape(1, 20 * 20, 10 * 20, Red), Sprite(2, Tag(12, junk), Place(1, 1, Matrix(1, 0, 0))), Place(1, 2, Matrix(1, 0, 0), "box_mc")));
        Assert.NotNull(movie.FindNamedPlacement("box_mc"));
        Assert.Single(movie.Shapes);
    }

    [Fact]
    public void AMoveWithANewCharacterKeepsTheOldMatrixAndColourTransform()
    {
        // Frame 1 places character 1 with a matrix and a colour transform; the next frame moves depth 1 to character 3 giving only the character.
        byte[] MoveChar(int depth, int ch) { var b = new List<byte> { 0x03 }; b.AddRange(BitConverter.GetBytes((ushort)depth)); b.AddRange(BitConverter.GetBytes((ushort)ch)); return Tag(26, [.. b]); }
        var sprite = new List<byte>();
        sprite.AddRange(BitConverter.GetBytes((ushort)2)); sprite.AddRange(BitConverter.GetBytes((ushort)2));
        sprite.AddRange(Place(1, 1, Matrix(1, 7 * 20, 9 * 20), "", AlphaTransform(128))); sprite.AddRange(Tag(1, []));
        sprite.AddRange(MoveChar(1, 3)); sprite.AddRange(Tag(1, [])); sprite.AddRange(Tag(0, []));
        var movie = GfxMovie.Parse(Movie(Shape(1, 20, 20, Red), Shape(3, 20, 20, Red), Tag(39, [.. sprite])));
        var second = movie.Sprites[2].Frames[1].Display[1];
        Assert.Equal(3, second.Character);
        Assert.Equal(7 * 20, second.Matrix!.Value.Tx);
        Assert.Equal(0.5f, second.Colour!.Value.MulA);
    }

    static byte[] WithoutEnd(params byte[][] tags)
    {
        var body = new List<byte>(Rect(0, 100 * 20, 0, 60 * 20));
        body.AddRange(BitConverter.GetBytes((ushort)(30 << 8))); body.AddRange(BitConverter.GetBytes((ushort)1));
        foreach (var t in tags) body.AddRange(t);
        var o = new List<byte>(Encoding.ASCII.GetBytes("GFX")) { 8 };
        o.AddRange(BitConverter.GetBytes(8 + body.Count)); o.AddRange(body);
        return [.. o];
    }

    static byte[] LongTagHeader(int code, int length)
    {
        var o = new List<byte>(BitConverter.GetBytes((ushort)((code << 6) | 0x3f))); o.AddRange(BitConverter.GetBytes(length)); o.AddRange(new byte[4]);
        return [.. o];
    }

    [Fact]
    public void MalformedMoviesFailCleanly()
    {
        var good = Movie(Shape(1, 20 * 20, 10 * 20, Red), Sprite(2, Place(1, 1, Matrix(1, 0, 0))), Place(1, 2, Matrix(1, 0, 0), "box_mc"));
        // Every truncation either parses or throws InvalidDataException; nothing else escapes.
        for (int cut = 12; cut < good.Length; cut += 3)
            try { GfxMovie.Parse(good[..cut]); } catch (InvalidDataException) { }
        // A long tag whose length runs past the end, and a negative one.
        Assert.Throws<InvalidDataException>(() => GfxMovie.Parse(WithoutEnd(LongTagHeader(12, int.MaxValue - 8))));
        Assert.Throws<InvalidDataException>(() => GfxMovie.Parse(WithoutEnd(LongTagHeader(12, -1))));
    }

    [Fact]
    public void SpritesNestedTooDeeplyAreRefusedWithoutOverflowingTheStack()
    {
        byte[] inner = [];
        for (int i = 0; i < 40; i++) inner = Sprite(100 + i, inner);
        Assert.Throws<InvalidDataException>(() => GfxMovie.Parse(Movie(inner)));
    }

    [Fact]
    public void RejectsAMovieThatIsNotAnUncompressedGfx()
    {
        Assert.Throws<InvalidDataException>(() => GfxMovie.Parse(Encoding.ASCII.GetBytes("CFX\u0008............")));
        Assert.Throws<InvalidDataException>(() => GfxMovie.Parse([1, 2, 3]));
    }

    // ---- morph shapes (ui/hud-2) ----

    /// <summary>
    /// A DefineMorphShape2 (tag 84) of a rectangle with a solid fill that is <paramref name="w0"/> x 10 pixels at the start and <paramref name="w1"/> x 10 at the end (colours red then blue), as the
    /// exporter lays it out: four bounds, flags, the offset, the morph fill and line styles, the start edges and the end edges (move only).
    /// </summary>
    static byte[] MorphRect(int id, int w0, int w1, bool withRatioInPlace = false)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)id));
        for (int i = 0; i < 4; i++) body.AddRange(Rect(0, Math.Max(w0, w1) * 20, 0, 10 * 20));
        body.Add(0);                                   // reserved + stroke flags
        body.AddRange(BitConverter.GetBytes(0u));      // offset to the end edges (not needed by the reader)
        body.Add(1);                                   // one fill style
        body.Add(0); body.AddRange([255, 0, 0, 255]); body.AddRange([0, 0, 255, 255]);   // solid, red -> blue
        body.Add(0);                                   // no line styles
        byte[] Edges(int w, bool first)
        {
            var b = new Bits();
            b.Put(1, 4); b.Put(0, 4);                  // fill bits 1, line bits 0
            b.Put(0, 1); b.Put(first ? 5 : 1, 5);      // style change: MoveTo (+ FillStyle1 in the start edges)
            b.Put(16, 5); b.Signed(0, 16); b.Signed(0, 16);
            if (first) b.Put(1, 1);
            void Edge(int dx, int dy) { b.Put(1, 1); b.Put(1, 1); b.Put(14, 4); b.Put(1, 1); b.Signed(dx, 16); b.Signed(dy, 16); }
            Edge(w * 20, 0); Edge(0, 10 * 20); Edge(-w * 20, 0); Edge(0, -10 * 20);
            b.Put(0, 1); b.Put(0, 5);
            return b.ToArray();
        }
        body.AddRange(Edges(w0, true));
        body.AddRange(Edges(w1, false));
        return Tag(84, [.. body]);
    }

    /// <summary>A placement with a ratio (PlaceObject2: character, matrix, ratio), or a move that sets only the ratio.</summary>
    static byte[] PlaceRatio(int depth, int ch, int ratio, bool move = false)
    {
        var body = new List<byte>();
        if (move) { body.Add(0x01 | 0x10); body.AddRange(BitConverter.GetBytes((ushort)depth)); }
        else { body.Add(0x02 | 0x04 | 0x10); body.AddRange(BitConverter.GetBytes((ushort)depth)); body.AddRange(BitConverter.GetBytes((ushort)ch)); body.AddRange(Matrix(1, 0, 0)); }
        body.AddRange(BitConverter.GetBytes((ushort)ratio));
        return Tag(26, [.. body]);
    }

    static float CoveredWidth(GfxCanvas c, int y)
    {
        float sum = 0f;
        for (int x = 0; x < 100; x++) sum += c.At(x, y).A;
        return sum;
    }

    [Fact]
    public void AMorphShapeIsDrawnBetweenItsStartAndEndByThePlacementsRatio()
    {
        var movie = GfxMovie.Parse(Movie(MorphRect(1, 10, 30), Sprite(2, PlaceRatio(1, 1, 0)), Place(1, 2, Matrix(1, 0, 0), "m_mc")));
        Assert.True(movie.Morphs.ContainsKey(1));
        Assert.True(movie.Morphs[1].Consistent);
        Assert.Equal(4, movie.Morphs[1].Start.Segments.Count);
        Assert.Equal(1, movie.Sprites[2].Frames[0].Display[1].Character);
        Assert.Equal(0, movie.Sprites[2].Frames[0].Display[1].Ratio);
        float Width(int ratio)
        {
            var m = GfxMovie.Parse(Movie(MorphRect(1, 10, 30), Sprite(2, PlaceRatio(1, 1, ratio)), Place(1, 2, Matrix(1, 0, 0), "m_mc")));
            var c = new GfxCanvas(100, 60);
            GfxRaster.Draw(m, 0, 0, GfxMatrix.Identity, c, _ => null);
            return CoveredWidth(c, 5);
        }
        Assert.InRange(Width(0), 9.9f, 10.1f);        // the start: 10 pixels
        Assert.InRange(Width(32768), 19.5f, 20.6f);   // halfway: 20 (the quantised ratio is within a hundredth of the range)
        Assert.InRange(Width(65535), 29.9f, 30.1f);   // the end: 30
    }

    [Fact]
    public void AMorphShapesColourAndBoundsFollowTheRatioToo()
    {
        var movie = GfxMovie.Parse(Movie(MorphRect(1, 10, 30), Sprite(2, PlaceRatio(1, 1, 32768)), Place(1, 2, Matrix(1, 0, 0), "m_mc")));
        var c = new GfxCanvas(100, 60);
        GfxRaster.Draw(movie, 0, 0, GfxMatrix.Identity, c, _ => null);
        var (r, g, b, a) = c.At(5, 5);
        Assert.InRange(r, 0.4f, 0.6f); Assert.InRange(b, 0.4f, 0.6f); Assert.Equal(0f, g);   // halfway between red and blue
        var box = GfxRaster.Bounds(movie, 2, 0, GfxMatrix.Identity);
        Assert.NotNull(box);
        Assert.InRange(box!.Value.X1, 19.5, 20.6);   // the shape's extent at the ratio, not the start's
    }

    [Fact]
    public void AMoveKeepsTheRatioItDoesNotSet()
    {
        // Frame 0 places the morph at ratio 0, frame 1 moves it to 65535; a later move without a ratio keeps that.
        var sprite = new List<byte>();
        sprite.AddRange(BitConverter.GetBytes((ushort)2)); sprite.AddRange(BitConverter.GetBytes((ushort)1));
        sprite.AddRange(PlaceRatio(1, 1, 0)); sprite.AddRange(Tag(1, []));
        sprite.AddRange(PlaceRatio(1, 1, 65535, move: true)); sprite.AddRange(Tag(1, []));
        sprite.AddRange(Tag(26, [0x01, 1, 0])); sprite.AddRange(Tag(1, [])); sprite.AddRange(Tag(0, []));   // a bare move
        var movie = GfxMovie.Parse(Movie(MorphRect(1, 10, 30), Tag(39, [.. sprite]), Place(1, 2, Matrix(1, 0, 0), "m_mc")));
        var frames = movie.Sprites[2].Frames;
        Assert.Equal(3, frames.Count);
        Assert.Equal(0, frames[0].Display[1].Ratio);
        Assert.Equal(65535, frames[1].Display[1].Ratio);
        Assert.Equal(65535, frames[2].Display[1].Ratio);
        Assert.Equal(1, frames[2].Display[1].Character);
    }

    [Fact]
    public void AMorphWhoseHalvesDoNotMatchDrawsItsStart()
    {
        var movie = GfxMovie.Parse(Movie(MorphRect(1, 10, 30)));
        var morph = movie.Morphs[1];
        var broken = new GfxMorph(1, morph.Start, new GfxShape { Id = 1 });
        Assert.False(broken.Consistent);
        Assert.Same(morph.Start, broken.At(30000));
    }
}
