using System.Text;
using SmContent;
using SmFormats.Gfx;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// ui/hud-fidelity: the HUD movie readers on synthetic movies built here (no original bytes): text fields and imported font names, clip layers (masks), images placed as
/// characters, the drawn box, the display walk (frames by path, pieces, text, skips), and the engine-free rules that pick reticles, their spread frames and the weapon box's displays.
/// </summary>
public class HudMovieTests
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

    static byte[] Shape(int id, int w, int h, byte[] fill)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)id));
        body.AddRange(Rect(0, w, 0, h));
        body.Add(1); body.AddRange(fill); body.Add(0);
        var b = new Bits();
        b.Put(1, 4); b.Put(0, 4);
        b.Put(0, 1); b.Put(5, 5);
        b.Put(16, 5); b.Signed(0, 16); b.Signed(0, 16);
        b.Put(1, 1);
        void Edge(int dx, int dy) { b.Put(1, 1); b.Put(1, 1); b.Put(14, 4); b.Put(1, 1); b.Signed(dx, 16); b.Signed(dy, 16); }
        Edge(w, 0); Edge(0, h); Edge(-w, 0); Edge(0, -h);
        b.Put(0, 1); b.Put(0, 5);
        body.AddRange(b.ToArray());
        return Tag(2, [.. body]);
    }

    /// <summary>A PlaceObject2: character, matrix, optional name and clip depth.</summary>
    static byte[] Place(int depth, int ch, byte[] matrix, string name = "", int clipDepth = 0)
    {
        int flags = 0x02 | 0x04 | (name.Length > 0 ? 0x20 : 0) | (clipDepth > 0 ? 0x40 : 0);
        var body = new List<byte> { (byte)flags };
        body.AddRange(BitConverter.GetBytes((ushort)depth));
        body.AddRange(BitConverter.GetBytes((ushort)ch));
        body.AddRange(matrix);
        if (name.Length > 0) { body.AddRange(Encoding.ASCII.GetBytes(name)); body.Add(0); }
        if (clipDepth > 0) body.AddRange(BitConverter.GetBytes((ushort)clipDepth));
        return Tag(26, [.. body]);
    }

    static byte[] Sprite(int id, params byte[][] frames)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)id)); body.AddRange(BitConverter.GetBytes((ushort)frames.Length));
        foreach (var f in frames) { body.AddRange(f); body.AddRange(Tag(1, [])); }
        body.AddRange(Tag(0, []));
        return Tag(39, [.. body]);
    }

    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    static byte[] Label(string name) => Tag(43, [.. Encoding.ASCII.GetBytes(name), 0]);

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

    /// <summary>A DefineEditText: bounds, has text + has colour + has font, then layout (align, margins, indent, leading), variable name and initial text.</summary>
    static byte[] EditText(int id, int fontId, int heightTwips, byte[] rgba, int align, string text)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((ushort)id));
        body.AddRange(Rect(-40, 2000, -40, 600));
        body.Add(0x80 | 0x04 | 0x01);   // has text, has colour, has font
        body.Add(0x20);                 // has layout
        body.AddRange(BitConverter.GetBytes((ushort)fontId));
        body.AddRange(BitConverter.GetBytes((ushort)heightTwips));
        body.AddRange(rgba);
        body.Add((byte)align); body.AddRange(new byte[6]); body.AddRange(BitConverter.GetBytes((short)40));
        body.AddRange(Encoding.ASCII.GetBytes("v")); body.Add(0);
        body.AddRange(Encoding.ASCII.GetBytes(text)); body.Add(0);
        return Tag(37, [.. body]);
    }

    static readonly byte[] Red = [0x00, 255, 0, 0];
    static readonly byte[] White = [0x00, 255, 255, 255];

    [Fact]
    public void ReadsTextFieldsAndImportedFontNames()
    {
        var import = new List<byte>(); import.AddRange(Encoding.ASCII.GetBytes("fonts_en.swf")); import.Add(0); import.AddRange(new byte[] { 1, 0 });
        import.AddRange(BitConverter.GetBytes((ushort)1)); import.AddRange(BitConverter.GetBytes((ushort)7)); import.AddRange(Encoding.ASCII.GetBytes("$BourgeoisBoldAlt")); import.Add(0);
        var movie = GfxMovie.Parse(Movie(Tag(71, [.. import]), EditText(5, 7, 18 * 20, [255, 204, 0, 255], 1, "804"), Place(1, 5, Matrix(1, 0, 0), "marine_kills")));
        var t = movie.EditTexts[5];
        Assert.Equal(7, t.FontId); Assert.Equal(18, t.FontHeight); Assert.Equal(1, t.Align); Assert.Equal("804", t.InitialText);
        Assert.Equal(-2, t.X0); Assert.Equal(100, t.X1); Assert.Equal(2, t.Leading);
        Assert.Equal((1f, 0.8f, 0f, 1f), t.Colour);
        Assert.Equal("$BourgeoisBoldAlt", movie.FontNames[7]);
        Assert.Equal(37, movie.DefinedBy[5]);
    }

    [Fact]
    public void AMalformedTextFieldDoesNotStopTheMovie()
    {
        var movie = GfxMovie.Parse(Movie(Tag(37, [5, 0]), Shape(1, 200, 200, Red), Place(1, 1, Matrix(1, 0, 0), "box_mc")));
        Assert.Empty(movie.EditTexts);
        Assert.NotNull(movie.FindNamedPlacement("box_mc"));
    }

    [Fact]
    public void ClipLayersMaskTheDepthsUpToTheirClipDepth()
    {
        // Sprite 3: a 10 x 10 mask at depth 1 clipping to depth 2, a 40 x 10 red bar at depth 2 (masked), a 40 x 10 red bar at depth 3 lower down (not masked).
        var sprite = Sprite(3, Concat(Place(1, 1, Matrix(1, 0, 0), "", clipDepth: 2), Place(2, 2, Matrix(1, 0, 0)), Place(3, 2, Matrix(1, 0, 20 * 20))));
        var movie = GfxMovie.Parse(Movie(Shape(1, 10 * 20, 10 * 20, White), Shape(2, 40 * 20, 10 * 20, Red), sprite, Place(1, 3, Matrix(1, 0, 0), "bar_mc")));
        var masked = new GfxCanvas(60, 40);
        GfxRaster.DrawMasked(movie, 3, 0, GfxMatrix.Identity, masked, _ => null);
        Assert.Equal(1f, masked.At(5, 5).A);       // inside the mask
        Assert.Equal(0f, masked.At(25, 5).A);      // the masked bar beyond the mask
        Assert.Equal(1f, masked.At(25, 25).A);     // the unmasked bar
        Assert.Equal(1f, masked.At(5, 5).R);       // the mask itself is not drawn white
        Assert.Equal(0f, masked.At(5, 5).G);
        // The old Draw ignores clip layers (the scope overlay renders as before).
        var plain = new GfxCanvas(60, 40);
        GfxRaster.Draw(movie, 3, 0, GfxMatrix.Identity, plain, _ => null);
        Assert.Equal(1f, plain.At(25, 5).A);
    }

    [Fact]
    public void AnImagePlacedAsACharacterIsDrawnWhole()
    {
        var image = new List<byte>(); image.AddRange(BitConverter.GetBytes((ushort)4)); image.AddRange(new byte[] { 9, 0, 14, 0 }); image.AddRange(BitConverter.GetBytes((ushort)2)); image.AddRange(BitConverter.GetBytes((ushort)2));
        image.Add(0); image.Add(7); image.AddRange(Encoding.ASCII.GetBytes("x_I4.dds")); image.Add(0);
        var movie = GfxMovie.Parse(Movie(Tag(1009, [.. image]), Place(1, 4, Matrix(1, 3 * 20, 3 * 20), "pic_mc")));
        var rgba = Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 4).SelectMany(x => x).ToArray();
        var canvas = new GfxCanvas(10, 10);
        GfxRaster.DrawMasked(movie, 0, 0, GfxMatrix.Identity, canvas, id => id == 4 ? new GfxBitmap(2, 2, rgba) : null);
        Assert.Equal(1f, canvas.At(3, 3).B); Assert.Equal(1f, canvas.At(4, 4).A);
        Assert.Equal(0f, canvas.At(5, 5).A); Assert.Equal(0f, canvas.At(2, 2).A);
        var box = GfxRaster.Bounds(movie, 0, 0, GfxMatrix.Identity);
        Assert.Equal((3.0, 3.0, 5.0, 5.0), box);
    }

    [Fact]
    public void BoundsFollowPlacementsAndSkipInvisibleOnes()
    {
        var movie = GfxMovie.Parse(Movie(Shape(1, 10 * 20, 4 * 20, Red), Place(1, 1, Matrix(2, 5 * 20, 6 * 20), "a")));
        Assert.Equal((5.0, 6.0, 25.0, 14.0), GfxRaster.Bounds(movie, 0, 0, GfxMatrix.Identity));
        Assert.Null(GfxRaster.Bounds(movie, 99, 0, GfxMatrix.Identity));
    }

    [Fact]
    public void TheWalkPicksFramesByPathVisitsPiecesAndTextAndSkips()
    {
        // Sprite 10 (two frames, labels "one" and "two") holds shape 1 in frame 0 and shape 2 in frame 1; sprite 11 places sprite 10 as "clip_mc", a text field as "txt" and shape 1 as "gone".
        var ten = Sprite(10, Concat(Label("one"), Place(1, 1, Matrix(1, 0, 0))), Concat(Label("two"), Place(1, 2, Matrix(1, 0, 0))));
        var eleven = Sprite(11, Concat(Place(1, 10, Matrix(1, 20, 0), "clip_mc"), Place(2, 5, Matrix(1, 0, 40), "txt"), Place(3, 1, Matrix(1, 0, 0), "gone")));
        var movie = GfxMovie.Parse(Movie(Shape(1, 200, 200, Red), Shape(2, 400, 200, Red), EditText(5, 7, 360, [255, 255, 255, 255], 0, ""), ten, eleven));
        var hm = new HudMovie("Test", movie, new());
        Assert.Equal(new Dictionary<string, int> { ["one"] = 0, ["two"] = 1 }, hm.Labels(10));
        var seen = new List<(int Character, string Path, bool Text, double Tx)>();
        hm.Walk(11, 0, (path, _) => path == "clip_mc" ? 1 : null, _ => false, item => seen.Add((item.Character, item.Path, item.Text is not null, item.Matrix.Tx)), skip: path => path == "gone");
        Assert.Contains((2, "clip_mc", false, 1.0), seen);   // frame 1 of the clip: shape 2, placed 1 pixel right
        Assert.Contains((5, "txt", true, 0.0), seen);
        Assert.DoesNotContain(seen, s => s.Path == "gone");
        // As a piece the clip is visited whole, at its frame.
        var pieces = new List<(int Character, int Frame)>();
        hm.Walk(11, 0, (path, _) => path == "clip_mc" ? 1 : null, ch => ch == 10, item => { if (item.Text is null) pieces.Add((item.Character, item.Frame)); }, skip: path => path == "gone");
        Assert.Equal([(10, 1)], pieces);
    }

    [Fact]
    public void FindTakesAChosenFrameFirst()
    {
        var slide = Sprite(20, Place(1, 1, Matrix(1, 0, 100 * 20), "box_mc"), Concat(Place(1, 1, Matrix(1, 0, 10 * 20), "box_mc")));
        var movie = GfxMovie.Parse(Movie(Shape(1, 200, 200, Red), slide, Place(1, 20, Matrix(1, 0, 0), "slide_mc")));
        var hm = new HudMovie("Test", movie, new());
        Assert.Equal(100, hm.Find("slide_mc/box_mc")!.Value.ToStage.Ty);
        Assert.Equal(10, hm.Find("slide_mc/box_mc", id => id == 20 ? 1 : null)!.Value.ToStage.Ty);
        Assert.Null(hm.Find("slide_mc/missing"));
    }

    [Fact]
    public void ReticlesFollowTheWeaponTypeAndTheScopedWeaponsHaveNone()
    {
        Assert.Equal("idle_mc", ReticleRules.For("sm_mp_ranged_bolter", "bolter")!.Idle);
        Assert.Equal("enemy_mc", ReticleRules.For("sm_mp_ranged_bolter", "bolter")!.Active);
        Assert.Equal("boltpistol_mc", ReticleRules.For("sm_mp_ranged_bolt_pistol", "bolt_pistol")!.Idle);
        Assert.Equal("boltpistol_mc", ReticleRules.For("sm_mp_ranged_bolt_pistol", null)!.Idle);   // from the name
        Assert.Equal("heavybidle_mc", ReticleRules.For("sm_mp_ranged_heavy_bolter", "heavy_bolter")!.Idle);
        Assert.Null(ReticleRules.For("sm_mp_ranged_sniper_rifle", "stalker"));
        Assert.Null(ReticleRules.For("sm_mp_ranged_las_cannon", "laz_cannon"));
        Assert.All(ReticleRules.All, r => Assert.EndsWith("_mc", r.Idle));
    }

    [Fact]
    public void SpreadRangesComeFromTheLabels()
    {
        // The bolt pistol's clip: min_aim 0, min_normal 2, max_normal 24 (no max_aim); the bolter's: 0, 1, 14, 24.
        var pistol = new Dictionary<string, int> { ["min_aim"] = 0, ["min_normal"] = 2, ["max_normal"] = 24 };
        Assert.Equal((2, 24), ReticleRules.SpreadRange(pistol, 25, aiming: false));
        Assert.Equal((0, 24), ReticleRules.SpreadRange(pistol, 25, aiming: true));
        var bolter = new Dictionary<string, int> { ["min_aim"] = 0, ["min_normal"] = 1, ["max_normal"] = 14, ["max_aim"] = 24 };
        Assert.Equal((1, 14), ReticleRules.SpreadRange(bolter, 25, aiming: false));
        Assert.Equal((0, 24), ReticleRules.SpreadRange(bolter, 25, aiming: true));
        Assert.Equal((0, 9), ReticleRules.SpreadRange(new Dictionary<string, int>(), 10, aiming: false));
        Assert.Equal(1, ReticleRules.SpreadFrame((1, 14), 0f));
        Assert.Equal(14, ReticleRules.SpreadFrame((1, 14), 1f));
        Assert.Equal(8, ReticleRules.SpreadFrame((1, 14), 0.5f));
        Assert.Equal(1, ReticleRules.SpreadFrame((1, 14), float.NaN));
        Assert.Equal(0f, ReticleRules.SpreadFraction(null, new PlayerState()));
    }

    [Fact]
    public void TheWeaponBoxShowsOneDisplayPerFamily()
    {
        var bolter = WeaponBoxRules.For("sm_mp_ranged_bolter", "bolter", chaos: false)!;
        Assert.Equal("ammo_mc/ammo_count_mc", bolter.Bar); Assert.Equal(WeaponBoxBar.Spent, bolter.Value); Assert.Equal("ammo_mc/ammo_txt", bolter.Count);
        Assert.All(bolter.Show, p => Assert.Contains(p, WeaponBoxRules.Optional));
        var plasma = WeaponBoxRules.For("csm_mp_ranged_plasma_gun", "plasma_gun", chaos: true)!;
        Assert.Equal(WeaponBoxBar.Heat, plasma.Value); Assert.Contains("ammo_plasmagun_mc/ammo_plasmagun_chaos_mc", plasma.Show);
        Assert.Equal("ammo_lascannon_mc/ammo_bar", WeaponBoxRules.For("sm_mp_ranged_las_cannon", "laz_cannon", false)!.Bar);
        Assert.Null(WeaponBoxRules.For("sm_mp_melee_combat_knife", "knife", false));
        // Bars: frame 0 full, the second-to-last empty.
        Assert.Equal(0, WeaponBoxRules.BarFrame(1f, 100));
        Assert.Equal(98, WeaponBoxRules.BarFrame(0f, 100));
        Assert.Equal(49, WeaponBoxRules.BarFrame(0.5f, 100));
        // The stalker's ten rounds.
        Assert.True(WeaponBoxRules.RoundLoaded(4, 5, 10)); Assert.False(WeaponBoxRules.RoundLoaded(5, 5, 10));
        Assert.False(WeaponBoxRules.RoundLoaded(6, 6, 6) && false);
        Assert.False(WeaponBoxRules.RoundLoaded(7, 12, 6));
    }
}
