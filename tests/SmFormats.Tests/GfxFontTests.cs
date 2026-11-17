using System.Text;
using SmFormats.Gfx;

namespace SmFormats.Tests;

/// <summary>
/// The font reader of classic-score-hud (the HUD movies' faces live in the install's font movie as vector outlines), on a synthetic font movie built here (no original bytes): one blank glyph and
/// one square glyph in a DefineFont3, read, measured and filled at a size.
/// </summary>
public class GfxFontTests
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

    static byte[] Tag(int code, byte[] body)
    {
        var o = new List<byte>();
        o.AddRange(BitConverter.GetBytes((ushort)((code << 6) | 0x3f)));
        o.AddRange(BitConverter.GetBytes(body.Length));
        o.AddRange(body);
        return [.. o];
    }

    /// <summary>A square from (2048, -10240) to (12288, 0) in a 20480-unit em, drawn clockwise, right-filled.</summary>
    static byte[] SquareGlyph(bool leftFillOnly = false)
    {
        var b = new Bits();
        b.Put(1, 4); b.Put(0, 4);                       // fill bits 1, line bits 0
        b.Put(0, 1); b.Put(leftFillOnly ? 0b00011 : 0b00101, 5);   // style change: move + fill1 (or fill0)
        b.Put(16, 5); b.Signed(2048, 16); b.Signed(0, 16);
        b.Put(1, 1);                                    // the fill style
        void Edge(int dx, int dy)
        {
            b.Put(1, 1); b.Put(1, 1); b.Put(14, 4);     // straight edge, 16 bits
            if (dx != 0 && dy != 0) { b.Put(1, 1); b.Signed(dx, 16); b.Signed(dy, 16); }
            else if (dy != 0) { b.Put(0, 1); b.Put(1, 1); b.Signed(dy, 16); }
            else { b.Put(0, 1); b.Put(0, 1); b.Signed(dx, 16); }
        }
        Edge(0, -10240); Edge(10240, 0); Edge(0, 10240); Edge(-10240, 0);
        b.Put(0, 1); b.Put(0, 5);                       // end
        return b.ToArray();
    }

    static byte[] FontMovie(string name, byte[] glyph1)
    {
        var f = new List<byte>();
        f.AddRange(BitConverter.GetBytes((ushort)7));    // font id
        f.Add(0x84); f.Add(1);                           // layout + wide codes, language
        f.Add((byte)name.Length); f.AddRange(Encoding.UTF8.GetBytes(name));
        f.AddRange(BitConverter.GetBytes((ushort)2));    // glyphs
        byte[] blank = [0x00, 0x00];
        int first = 2 * 2 + 2;
        f.AddRange(BitConverter.GetBytes((ushort)first));
        f.AddRange(BitConverter.GetBytes((ushort)(first + blank.Length)));
        f.AddRange(BitConverter.GetBytes((ushort)(first + blank.Length + glyph1.Length)));   // code table offset
        f.AddRange(blank); f.AddRange(glyph1);
        f.AddRange(BitConverter.GetBytes((ushort)' ')); f.AddRange(BitConverter.GetBytes((ushort)'I'));
        f.AddRange(BitConverter.GetBytes((short)16000)); f.AddRange(BitConverter.GetBytes((short)4000)); f.AddRange(BitConverter.GetBytes((short)500));   // ascent, descent, leading
        f.AddRange(BitConverter.GetBytes((short)5000)); f.AddRange(BitConverter.GetBytes((short)12000));                                              // advances
        var o = new List<byte> { (byte)'G', (byte)'F', (byte)'X', 8 };
        var rect = new Bits(); rect.Put(1, 5); for (int i = 0; i < 4; i++) rect.Signed(0, 1);
        var head = new List<byte>(rect.ToArray());
        head.AddRange([24, 0, 1, 0]);                    // frame rate, frame count
        var tags = new List<byte>();
        tags.AddRange(Tag(75, [.. f]));
        tags.AddRange(BitConverter.GetBytes((ushort)0));  // End
        o.AddRange(BitConverter.GetBytes(8 + head.Count + tags.Count));
        o.AddRange(head); o.AddRange(tags);
        return [.. o];
    }

    [Fact]
    public void ReadsTheFacesNameMetricsAndCodes()
    {
        var faces = GfxFontReader.Read(FontMovie("XTest OT Bold Alternate", SquareGlyph()));
        var face = Assert.Single(faces);
        Assert.Equal(7, face.Id);
        Assert.Equal("XTest OT Bold Alternate", face.Name);
        Assert.True(face.IsBold);
        Assert.Equal(20480.0, face.EmSquare);
        Assert.Equal(16000, face.Ascent); Assert.Equal(4000, face.Descent);
        Assert.True(face.Has('I')); Assert.True(face.Has(' ')); Assert.False(face.Has('x'));
        Assert.Equal(12000 / 20480.0, face.AdvanceEm('I'), 6);
        Assert.Equal((5000 + 12000) / 20480.0, face.WidthEm(" I"), 6);
        Assert.Equal(0.0, face.AdvanceEm('x'));
        Assert.Empty(face.Contours[0]);
        Assert.Single(face.Contours[1]);
    }

    [Fact]
    public void AMediumNameIsNotBold() =>
        Assert.False(Assert.Single(GfxFontReader.Read(FontMovie("XTest OT Medium", SquareGlyph()))).IsBold);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // a contour that only has a left fill is turned round: it fills just the same
    public void FillsASquareAtTheSizeItWasAskedFor(bool leftFillOnly)
    {
        var face = Assert.Single(GfxFontReader.Read(FontMovie("XTest OT Medium", SquareGlyph(leftFillOnly))));
        var map = face.Rasterize('I', 20.0);
        Assert.NotNull(map);
        // 10240 units of 20480 at 20 px to the em: 10 x 10 px, from x 2 and 10 px above the baseline; one pixel of padding on every side.
        Assert.Equal(1, map!.Left); Assert.Equal(-11, map.Top);
        Assert.Equal(12, map.Width); Assert.Equal(12, map.Height);
        double sum = map.Alpha.Sum(a => a / 255.0);
        Assert.InRange(sum, 99.0, 101.0);
        Assert.Equal(255, map.Alpha[(map.Height / 2) * map.Width + map.Width / 2]);
        Assert.Equal(0, map.Alpha[0]);
        // A bigger size covers four times the area.
        var big = face.Rasterize('I', 40.0)!;
        Assert.InRange(big.Alpha.Sum(a => a / 255.0), 399.0, 401.0);
    }

    [Fact]
    public void ABlankGlyphAnUnknownCharacterAndANonPositiveSizeHaveNoPicture()
    {
        var face = Assert.Single(GfxFontReader.Read(FontMovie("XTest OT Medium", SquareGlyph())));
        Assert.Null(face.Rasterize(' ', 20.0));
        Assert.Null(face.Rasterize('x', 20.0));
        Assert.Null(face.Rasterize('I', 0.0));
    }

    [Fact]
    public void RefusesAFileThatIsNotAMovieAndSurvivesATruncatedOne()
    {
        Assert.Throws<InvalidDataException>(() => GfxFontReader.Read([1, 2, 3]));
        var good = FontMovie("XTest OT Medium", SquareGlyph());
        // Cut inside the font tag: no face, no exception outside InvalidDataException.
        var cut = good[..(good.Length - 20)];
        try { Assert.True(GfxFontReader.Read(cut).Count <= 1); }
        catch (InvalidDataException) { }
    }
}
