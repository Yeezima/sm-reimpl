using System.Numerics;
using SmContent;
using SmContent.Customization;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Dds;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// armour-pieces-1125 repair attempt 1 (the 92 % gate's code findings): built masks per shared texture set, the decal rule, the fallback to the baked look,
/// the build bound and the built-mask cache. Synthetic models and textures only (no original bytes).
/// </summary>
public sealed class BakedArmourPaintTests : IDisposable
{
    const int W = 32, H = 32;
    readonly string _cache = Path.Combine(Path.GetTempPath(), "sm-bakedpaint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_cache)) Directory.Delete(_cache, true); } catch (IOException) { }
    }

    /// <summary>A red plate texture (with a little noise) and its specular map, as RGBA8 DDS bytes.</summary>
    static (byte[] Diffuse, byte[] Specular) RedPlate(byte red = 160)
    {
        var dif = new byte[W * H * 4]; var spc = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            dif[i * 4] = (byte)(red + i % 7); dif[i * 4 + 1] = 24; dif[i * 4 + 2] = 20; dif[i * 4 + 3] = 255;
            spc[i * 4] = spc[i * 4 + 1] = spc[i * 4 + 2] = 70; spc[i * 4 + 3] = 255;
        }
        return (DdsImage.EncodeRgba8WithMips(W, H, dif), DdsImage.EncodeRgba8WithMips(W, H, spc));
    }

    static BmatMaterial Baked(string name, string diffuse = "plate_dif", string specular = "plate_spc") =>
        new(name, "character_wargear", new Dictionary<string, string> { ["DiffuseMap"] = diffuse, ["SpecularMap"] = specular, ["NormalMap"] = "plate_nrm" });

    /// <summary>
    /// One block, two triangles: the first covers the left half of the texture (UV x 0..0.45), the second the right half (0.55..1); each is its own surface with
    /// its own material. The second UV set spans the unit square (a pad's decal layout).
    /// </summary>
    static StaticModel Model(BmatMaterial left, BmatMaterial right, IReadOnlyDictionary<string, byte[]> textures)
    {
        Vector2[] uv0 = [new(0.02f, 0.02f), new(0.45f, 0.02f), new(0.02f, 0.98f), new(0.55f, 0.02f), new(0.98f, 0.02f), new(0.98f, 0.98f)];
        Vector2[] uv1 = [new(0, 0), new(1, 0), new(0, 1), new(0, 0), new(1, 0), new(1, 1)];
        var positions = uv0.Select(u => new Vector3(u.X, u.Y, 0)).ToArray();
        var ranges = new List<DcmMaterialRange> { new(left.Name!, 0, 0, 3, 0, 3, 0, 0), new(right.Name!, 1, 3, 3, 3, 3, 0, 0) };
        var block = new DcmBlock(0, Vector3.Zero, Vector3.One, ranges, [0, 1, 2, 3, 4, 5], positions, positions.Select(_ => Vector3.UnitZ).ToArray(),
            positions.Select(_ => Vector4.UnitX).ToArray(), uv0, uv1, new uint[6], null);
        var surfaces = new List<StaticModelSurface> { new(0, ranges[0], left, left.Textures["DiffuseMap"], null), new(0, ranges[1], right, right.Textures["DiffuseMap"], null) };
        return new StaticModel("x.oppc", "x", DcmMesh.FromBlocks([block]), []).WithSurfaces(surfaces, textures);
    }

    static Dictionary<string, byte[]> Textures(byte red = 160)
    {
        var (d, s) = RedPlate(red);
        return new(StringComparer.OrdinalIgnoreCase) { ["plate_dif"] = d, ["plate_spc"] = s };
    }

    static DdsImage MaskOf(StaticModel m, int surface) => DdsImage.TryDecode(m.Textures[m.Surfaces[surface].Material!.Textures["PaintMask"]])!;

    [Fact]
    public void Materials_sharing_a_diffuse_share_one_mask_that_paints_all_their_texels()
    {
        // Finding 2: built per material name but stored by diffuse name, the second material's mask (its texels only) replaced the first's.
        var built = BakedArmourPaint.Apply(Model(Baked("pad_a"), Baked("pad_b"), Textures()));
        var a = built.Surfaces[0].Material!; var b = built.Surfaces[1].Material!;
        Assert.True(BakedArmourPaint.IsBuilt(a) && BakedArmourPaint.IsBuilt(b));
        Assert.Equal(a.Textures["PaintMask"], b.Textures["PaintMask"]);
        Assert.Equal(("pad_a", "pad_b"), (a.Name, b.Name));   // each surface keeps its own material
        var mask = MaskOf(built, 0);
        int Primary(int x, int y) => mask.Rgba[(y * W + x) * 4];
        Assert.True(Primary(5, 10) > 200, "the first material's texels are painted");
        Assert.True(Primary(28, 10) > 200, "the second material's texels are painted");
    }

    [Fact]
    public void A_pad_with_a_built_mask_never_takes_the_chapter_decal()
    {
        // Finding 1: the decal layout check alone let every built-mask pad take a stretched chapter decal.
        Vector2[] square = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        var gameMasked = new BmatMaterial("pad", "character_wargear", new Dictionary<string, string> { ["DiffuseMap"] = "d", ["PaintMask"] = "pad_pnt" });
        Assert.True(BakedArmourPaint.SurfaceTakesDecal(gameMasked, square));
        var built = BakedArmourPaint.Apply(Model(Baked("pad_a"), Baked("pad_b"), Textures()));
        Assert.False(BakedArmourPaint.SurfaceTakesDecal(built.Surfaces[0].Material, built.Mesh.Blocks[0].Uv1));
        Assert.False(BakedArmourPaint.PieceTakesDecal(new CharacterPiece("pad", built, "bone")));
        Assert.False(BakedArmourPaint.SurfaceTakesDecal(Baked("baked"), square));   // nor a baked one (the full predefined set): as in the original
    }

    [Fact]
    public void A_material_whose_build_fails_stays_baked_and_the_rest_of_the_package_is_built()
    {
        // Finding 5: a texture with no covered texel threw in the builder and the whole package was "unreadable; not drawn".
        var black = new byte[W * H * 4];
        var noPlate = PaintMaskBuilder.Build(new DdsImage(W, H, black), new DdsImage(W, H, black), null);
        Assert.Equal(0f, noPlate.Primary + noPlate.Secondary + noPlate.Tertiary);
        var tex = Textures();
        tex["black_dif"] = DdsImage.EncodeRgba8WithMips(W, H, black);
        tex["black_spc"] = DdsImage.EncodeRgba8WithMips(W, H, black);
        tex["broken_dif"] = [1, 2, 3];
        var notes = new List<string>();
        var built = BakedArmourPaint.Apply(Model(Baked("dark", "black_dif", "black_spc"), Baked("plate"), tex), notes);
        Assert.False(BakedArmourPaint.IsBuilt(built.Surfaces[0].Material));   // drawn baked
        Assert.True(BakedArmourPaint.IsBuilt(built.Surfaces[1].Material));
        Assert.Contains(notes, n => n.Contains("dark", StringComparison.Ordinal) && n.Contains("baked", StringComparison.Ordinal));
        var broken = BakedArmourPaint.Apply(Model(Baked("broken", "broken_dif"), Baked("plate"), tex), notes);
        Assert.False(BakedArmourPaint.IsBuilt(broken.Surfaces[0].Material));
        Assert.True(BakedArmourPaint.IsBuilt(broken.Surfaces[1].Material));
    }

    [Fact]
    public void At_most_two_masks_are_built_at_once_however_many_packages_load_in_parallel()
    {
        // Finding 3: one read task per package built every mask at the same time (a nine-piece player at join).
        BakedArmourPaint.ResetStatistics();
        BakedArmourPaint.BuildHook = () => Thread.Sleep(40);
        try
        {
            var models = Enumerable.Range(0, 8).Select(i => Model(Baked("a" + i), Baked("b" + i), Textures((byte)(120 + i * 5)))).ToList();
            Parallel.ForEach(models, new ParallelOptions { MaxDegreeOfParallelism = 8 }, m => BakedArmourPaint.Apply(m));
        }
        finally { BakedArmourPaint.BuildHook = null; }
        Assert.InRange(BakedArmourPaint.PeakConcurrentBuilds, 1, BakedArmourPaint.MaxConcurrentBuilds);
    }

    [Fact]
    public void The_cache_returns_the_same_mask_and_a_changed_texture_builds_again()
    {
        var before = BakedArmourPaint.CacheDirectory;
        try
        {
            BakedArmourPaint.CacheDirectory = _cache;
            var first = BakedArmourPaint.Apply(Model(Baked("a"), Baked("b"), Textures()));
            var files = Directory.GetFiles(_cache, "*.bm");
            Assert.Single(files);
            var second = BakedArmourPaint.Apply(Model(Baked("a"), Baked("b"), Textures()));
            string mask = first.Surfaces[0].Material!.Textures["PaintMask"], grey = first.Surfaces[0].Material!.Textures[BakedArmourPaint.PaintDiffuseKey];
            Assert.Equal(first.Textures[mask], second.Textures[mask]);   // read back: the very bytes
            Assert.Equal(first.Textures[grey], second.Textures[grey]);
            Assert.Single(Directory.GetFiles(_cache, "*.bm"));
            BakedArmourPaint.Apply(Model(Baked("a"), Baked("b"), Textures(red: 150)));   // another install's texture: another key
            Assert.Equal(2, Directory.GetFiles(_cache, "*.bm").Length);
            // A damaged entry is built again, not trusted.
            File.WriteAllBytes(files[0], [1, 2, 3]);
            var rebuilt = BakedArmourPaint.Apply(Model(Baked("a"), Baked("b"), Textures()));
            Assert.Equal(first.Textures[mask], rebuilt.Textures[mask]);
        }
        finally { BakedArmourPaint.CacheDirectory = before; }
        // The key covers the builder version, the source bytes and the coverage.
        var (d, s) = RedPlate();
        Assert.Equal(BakedArmourPaint.CacheKey(d, s, null, null), BakedArmourPaint.CacheKey(d, s, null, null));
        Assert.NotEqual(BakedArmourPaint.CacheKey(d, s, null, null), BakedArmourPaint.CacheKey(d, null, null, null));
        Assert.NotEqual(BakedArmourPaint.CacheKey(d, s, null, null), BakedArmourPaint.CacheKey(d, s, null, new bool[W * H]));
    }

    [Fact]
    public void The_old_dark_plates_path_handles_a_pure_black_plate()
    {
        // Finding 7: the old path divided by a zero median.
        var dif = new byte[W * H * 4]; var spc = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            byte v = (byte)(i % 37 == 0 ? 40 : 0);
            dif[i * 4] = dif[i * 4 + 1] = dif[i * 4 + 2] = v; dif[i * 4 + 3] = 255;
            spc[i * 4] = spc[i * 4 + 1] = spc[i * 4 + 2] = 60; spc[i * 4 + 3] = 255;
        }
        BuiltPaintMask old;
        try { PaintMaskBuilder.OldDarkPlates = true; old = PaintMaskBuilder.Build(new DdsImage(W, H, dif), new DdsImage(W, H, spc), null); }
        finally { PaintMaskBuilder.OldDarkPlates = false; }
        Assert.True(old.Primary > 0.5f);
        Assert.True(float.IsFinite(old.PlateMedian));
    }

    [Fact]
    public void The_piece_switches_read_the_command_line_or_the_environment()
    {
        // Finding 4: reachable without code edits (server and client flags, or the variable a server passes to its children).
        string? None(string _) => null;
        Assert.False(CustomizationCatalog.SwitchOn(CustomizationCatalog.NoPredefinedPiecesSwitch, CustomizationCatalog.NoPredefinedPiecesVariable, ["--connect", "x"], None));
        Assert.True(CustomizationCatalog.SwitchOn(CustomizationCatalog.NoPredefinedPiecesSwitch, CustomizationCatalog.NoPredefinedPiecesVariable, ["--no-predefined-pieces"], None));
        Assert.True(CustomizationCatalog.SwitchOn(CustomizationCatalog.NoExtraPiecesSwitch, CustomizationCatalog.NoExtraPiecesVariable, [], v => v == "SM_NO_EXTRA_PIECES" ? "1" : null));
        Assert.False(CustomizationCatalog.SwitchOn(CustomizationCatalog.NoExtraPiecesSwitch, CustomizationCatalog.NoExtraPiecesVariable, [], v => "0"));
    }
}
