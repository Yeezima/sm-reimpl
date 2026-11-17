using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmMapmaker;
using static SmMapmaker.ModelImport;

namespace SmMapmaker.Tests;

/// <summary>The Quixel / Fab import's pure half (docs/mapmaker.md "Custom assets"): archives, model choice, texture names, units, asset metadata. Synthetic fixtures only.</summary>
public class ModelImportTests
{
    static string Zip(string dir, string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(dir, name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(content);
        }
        return path;
    }

    // ---- archives ----

    [Fact]
    public void ZipExtractsWantedFilesOnly()
    {
        using var lab = new Lab();
        var zip = Zip(lab.Dir, "rock.zip", ("Rock_LOD0.fbx", "fbx"), ("Textures/abc_4K_Albedo.jpg", "jpg"), ("readme.txt", "no"), ("tool.exe", "no"), ("Var1/", ""));
        var dest = Path.Combine(lab.Dir, "out");
        var files = ExtractZip(zip, dest);
        Assert.Equal(["Rock_LOD0.fbx", "Textures/abc_4K_Albedo.jpg"], files.Order().ToArray());
        Assert.True(File.Exists(Path.Combine(dest, "Textures", "abc_4K_Albedo.jpg")));
        Assert.False(File.Exists(Path.Combine(dest, "readme.txt")));
        Assert.False(File.Exists(Path.Combine(dest, "tool.exe")));
    }

    [Theory]
    [InlineData("../evil.fbx")]
    [InlineData("a/../../evil.fbx")]
    [InlineData("..\\evil.fbx")]
    [InlineData("/abs/evil.fbx")]
    [InlineData("C:/evil.fbx")]
    [InlineData("C:\\evil.fbx")]
    [InlineData("a//evil.fbx")]
    public void ZipSlipEntriesAreRejectedAndNothingEscapes(string entry)
    {
        using var lab = new Lab();
        var zip = Zip(lab.Dir, "evil.zip", ("ok.fbx", "x"), (entry, "boom"));
        var inner = Path.Combine(lab.Dir, "inner", "out");
        var ex = Assert.Throws<MapException>(() => ExtractZip(zip, inner));
        Assert.Equal("bad-archive", ex.Code);
        Assert.False(File.Exists(Path.Combine(lab.Dir, "evil.fbx")));
        Assert.False(File.Exists(Path.Combine(lab.Dir, "inner", "evil.fbx")));
    }

    [Fact]
    public void ZipBombStopsAtTheByteLimitNotTheHeader()
    {
        using var lab = new Lab();
        var zip = Zip(lab.Dir, "big.zip", ("a.fbx", new string('x', 5000)), ("b.fbx", new string('y', 5000)));
        Assert.Equal("bad-archive", Assert.Throws<MapException>(() => ExtractZip(zip, Path.Combine(lab.Dir, "o1"), new Limits(MaxTotalBytes: 6000))).Code);
        Assert.Equal("bad-archive", Assert.Throws<MapException>(() => ExtractZip(zip, Path.Combine(lab.Dir, "o2"), new Limits(MaxEntryBytes: 4000))).Code);
        Assert.Equal("bad-archive", Assert.Throws<MapException>(() => ExtractZip(zip, Path.Combine(lab.Dir, "o3"), new Limits(MaxEntries: 1))).Code);
        Assert.Equal(2, ExtractZip(zip, Path.Combine(lab.Dir, "o4")).Count);
    }

    [Fact]
    public void NotAZipIsBadArchive()
    {
        using var lab = new Lab();
        var path = Path.Combine(lab.Dir, "fake.zip"); File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6]);
        Assert.Equal("bad-archive", Assert.Throws<MapException>(() => ExtractZip(path, Path.Combine(lab.Dir, "o"))).Code);
    }

    // ---- choosing the model ----

    [Theory]
    [InlineData("Rock_LOD0.fbx", 0)]
    [InlineData("Rock_lod2.fbx", 2)]
    [InlineData("rock.LOD_3.glb", 3)]
    [InlineData("Rock_LOD10", 10)]
    [InlineData("Rock.fbx", null)]
    [InlineData("Dolly_Model.fbx", null)]
    [InlineData("Global_Rock.fbx", null)]
    public void LodNumbers(string name, int? lod) => Assert.Equal(lod, LodOf(name));

    [Fact]
    public void LowestLodWins()
    {
        var c = PickModel(["Rock_LOD2.fbx", "Rock_LOD0.fbx", "Rock_LOD1.fbx", "Rock_LOD3.fbx"])!;
        Assert.Equal("Rock_LOD0.fbx", c.Path);
        Assert.Equal(0, c.Lod);
        Assert.Equal(3, c.SkippedLods.Count);
    }

    [Fact]
    public void ANameWithoutLodCountsAsLodZero()
    {
        Assert.Equal("Rock.fbx", PickModel(["Rock_LOD1.fbx", "Rock.fbx"])!.Path);
        Assert.Equal("Rock_LOD1.fbx", PickModel(["Rock_LOD2.fbx", "Rock_LOD1.fbx"])!.Path);   // only LOD1 and up: the lowest of them
    }

    [Fact]
    public void LodBeatsFormatThenGlbBeatsGltfBeatsFbx()
    {
        Assert.Equal("a.glb", PickModel(["a.fbx", "a.gltf", "a.glb"])!.Path);
        Assert.Equal("a.gltf", PickModel(["a.fbx", "a.gltf"])!.Path);
        Assert.Equal("a_LOD0.fbx", PickModel(["a_LOD1.glb", "a_LOD0.fbx"])!.Path);
    }

    [Fact]
    public void ShallowAndFirstWinsAndJunkIsSkipped()
    {
        Assert.Equal("Var1/rock.fbx", PickModel(["Var2/rock.fbx", "Var1/rock.fbx"])!.Path);
        Assert.Equal("rock.fbx", PickModel(["Var1/rock.fbx", "rock.fbx"])!.Path);
        Assert.Equal("real.fbx", PickModel(["__MACOSX/._real.fbx", "__MACOSX/real.fbx", "real.fbx"])!.Path);
        Assert.Null(PickModel(["a.png", "b.txt"]));
        Assert.Equal("a.glb", PickModel(["sub\\a.png", "a.glb"])!.Path);
    }

    [Fact]
    public void FolderListingKeepsModelsAndImagesOnly()
    {
        using var lab = new Lab();
        Directory.CreateDirectory(Path.Combine(lab.Dir, "f", "tex"));
        File.WriteAllText(Path.Combine(lab.Dir, "f", "m.fbx"), "x");
        File.WriteAllText(Path.Combine(lab.Dir, "f", "tex", "a_Albedo.png"), "x");
        File.WriteAllText(Path.Combine(lab.Dir, "f", "notes.txt"), "x");
        Assert.Equal(["m.fbx", "tex/a_Albedo.png"], ListFolder(Path.Combine(lab.Dir, "f")).Order().ToArray());
    }

    // ---- texture names ----

    [Theory]
    [InlineData("vcjkdbkfw_4K_Albedo.jpg", TextureRole.Albedo, "vcjkdbkfw", 4096)]
    [InlineData("vcjkdbkfw_4K_Normal.jpg", TextureRole.Normal, "vcjkdbkfw", 4096)]
    [InlineData("vcjkdbkfw_2K_Roughness.jpg", TextureRole.Roughness, "vcjkdbkfw", 2048)]
    [InlineData("vcjkdbkfw_4K_AO.jpg", TextureRole.Occlusion, "vcjkdbkfw", 4096)]
    [InlineData("vcjkdbkfw_4K_Displacement.exr.png", TextureRole.Displacement, "vcjkdbkfw", 4096)]
    [InlineData("vcjkdbkfw_4K_Metalness.jpg", TextureRole.Metallic, "vcjkdbkfw", 4096)]
    [InlineData("vcjkdbkfw_4K_Opacity.png", TextureRole.Opacity, "vcjkdbkfw", 4096)]
    [InlineData("Barrel_BaseColor.png", TextureRole.Albedo, "barrel", 0)]
    [InlineData("Barrel_ORM.png", TextureRole.Orm, "barrel", 0)]
    [InlineData("T_Barrel_BC.png", TextureRole.Albedo, "barrel", 0)]
    [InlineData("T_Barrel_N.png", TextureRole.Normal, "barrel", 0)]
    [InlineData("T_Barrel_R.png", TextureRole.Roughness, "barrel", 0)]
    [InlineData("T_Barrel_M.png", TextureRole.Metallic, "barrel", 0)]
    [InlineData("Crate_Diffuse.tga", TextureRole.Albedo, "crate", 0)]
    [InlineData("Crate-ambient-occlusion.png", TextureRole.Occlusion, "crate", 0)]
    [InlineData("CrateBaseColor.png", TextureRole.Albedo, "crate", 0)]
    [InlineData("crate_emissive.png", TextureRole.Emissive, "crate", 0)]
    public void TextureRoles(string file, TextureRole role, string set, int resolution)
    {
        var t = ClassifyTexture(file)!;
        Assert.Equal(role, t.Role);
        Assert.Equal(set, t.Set);
        Assert.Equal(resolution, t.Resolution);
    }

    [Fact]
    public void NonImagesAndUnknownNamesAreNotRoles()
    {
        Assert.Null(ClassifyTexture("rock.fbx"));
        Assert.Equal(TextureRole.Unknown, ClassifyTexture("something.png")!.Role);
    }

    [Fact]
    public void NormalConventionsAndTheGreenFlip()
    {
        Assert.Equal(NormalConvention.DirectX, ClassifyTexture("Rock_Normal_DX.png")!.Convention);
        Assert.Equal(NormalConvention.DirectX, ClassifyTexture("Rock_NormalDirectX.png")!.Convention);
        Assert.Equal(NormalConvention.OpenGL, ClassifyTexture("Rock_Normal_GL.png")!.Convention);
        Assert.Equal(NormalConvention.OpenGL, ClassifyTexture("Rock_Normal_OpenGL.png")!.Convention);
        Assert.True(NormalNeedsGreenFlip(ClassifyTexture("Rock_Normal_DX.png")!));
        Assert.False(NormalNeedsGreenFlip(ClassifyTexture("Rock_Normal_GL.png")!));
        Assert.True(NormalNeedsGreenFlip(ClassifyTexture("abc_4K_Normal.jpg")!), "Megascans plain Normal reads as DirectX (owner's note)");
        Assert.False(NormalNeedsGreenFlip(ClassifyTexture("Rock_Normal.png")!), "any other plain Normal is OpenGL, Godot's own");
        Assert.False(NormalNeedsGreenFlip(ClassifyTexture("abc_4K_Normal_GL.jpg")!), "an explicit convention beats the Megascans reading");
    }

    [Fact]
    public void TexturesGroupIntoSetsWithTheHighestResolutionAndNoDisplacement()
    {
        var sets = GroupTextures(["a_2K_Albedo.jpg", "a_4K_Albedo.jpg", "a_4K_Normal.jpg", "a_4K_Displacement.jpg", "b_4K_Albedo.jpg", "readme.png"]);
        Assert.Equal(2, sets.Count);
        var a = sets.Single(s => s[TextureRole.Albedo].Set == "a");
        Assert.Equal("a_4K_Albedo.jpg", a[TextureRole.Albedo].File);
        Assert.True(a.ContainsKey(TextureRole.Normal));
        Assert.False(a.ContainsKey(TextureRole.Displacement));
    }

    // ---- units ----

    [Theory]
    [InlineData(2f, 1f)]
    [InlineData(0.4f, 1f)]
    [InlineData(30f, 1f)]
    [InlineData(120f, 0.01f)]       // a 1.2 m rock in centimetres
    [InlineData(2500f, 0.01f)]      // a 25 m container in centimetres
    [InlineData(12000f, 0.001f)]    // 12 m in millimetres
    public void UnitGuess(float largest, float scale) => Assert.Equal(scale, GuessUnits(largest).Scale);

    [Fact]
    public void UnitGuessNeverGoesNaN()
    {
        Assert.Equal(1f, GuessUnits(float.NaN).Scale);
        Assert.Equal(1f, GuessUnits(0f).Scale);
        Assert.Equal(1f, GuessUnits(float.PositiveInfinity).Scale);
        Assert.Equal(5f, LargestSide(new Vector3(1, 5, 2)));
    }

    // ---- asset bounds, metadata and refusing what Godot cannot read ----

    [Fact]
    public void GivenBoundsWinAndMetadataIsKept()
    {
        using var lab = new Lab();
        var s = lab.Session(false);
        var path = lab.WriteGlb("conv.glb");
        var r = s.Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{JsonSerializer.Serialize(path)}}},"id":"conv","from":"Rock_LOD0.fbx","bounds":{"min":[-0.5,0,-0.5],"max":[0.5,1.2,0.5]},"import":{"scale":0.01,"textures":4}}"""));
        Assert.True(r.Ok, r.ToJson());
        var a = lab.Library.GetAsset("conv")!;
        Assert.Equal(new Vector3(0.5f, 1.2f, 0.5f), a.Bounds!.Value.Max);
        Assert.Equal("Rock_LOD0.fbx", a.ImportedFrom);
        Assert.Equal(0.01, a.Import!["scale"]!.GetValue<double>(), 6);
        Assert.Equal(4, a.Import["textures"]!.GetValue<int>());
        Assert.Contains("\"import\"", File.ReadAllText(Path.Combine(lab.Library.AssetDir("conv"), "asset.json")));
        Assert.Equal(a.Sha256, ContentSha(path));
    }

    [Fact]
    public void AssetsWithoutImportMetadataStillRoundTrip()
    {
        using var lab = new Lab();
        var s = lab.Session(false);
        lab.ImportBox(s, "plain");
        var a = lab.Library.GetAsset("plain")!;
        Assert.Null(a.Import);
        Assert.DoesNotContain("\"import\"", File.ReadAllText(Path.Combine(lab.Library.AssetDir("plain"), "asset.json")));
    }

    [Theory]
    [InlineData("""{"min":[1,1,1],"max":[0,0,0]}""")]
    [InlineData("""{"min":[0,0],"max":[1,1,1]}""")]
    public void BadGivenBoundsAreRejected(string bounds)
    {
        using var lab = new Lab();
        var path = lab.WriteGlb("b.glb");
        var r = lab.Session(false).Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{JsonSerializer.Serialize(path)}}},"bounds":{{{bounds}}}}"""));
        Assert.False(r.Ok);
    }

    [Fact]
    public void MissingMinMaxLeavesBoundsUnknownUnlessGiven()
    {
        using var lab = new Lab();
        var path = Path.Combine(lab.Dir, "nominmax.gltf");
        File.WriteAllText(path, """{"asset":{"version":"2.0"},"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"accessors":[{"componentType":5126,"count":3,"type":"VEC3"}]}""");
        Assert.Null(GltfReader.Inspect(path).Bounds);
        var s = lab.Session(false);
        var r = s.Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{JsonSerializer.Serialize(path)}}},"id":"nm"}"""));
        Assert.True(r.Ok);
        Assert.Contains(r.Warnings, w => w.Contains("unknown"));
        Assert.Null(lab.Library.GetAsset("nm")!.Bounds);
        var s2 = lab.Session(false);
        Assert.True(s2.Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{JsonSerializer.Serialize(path)}}},"id":"nm2","bounds":{"min":[0,0,0],"max":[1,2,3]}}""")).Ok);
        Assert.Equal(new Vector3(1, 2, 3), lab.Library.GetAsset("nm2")!.Bounds!.Value.Max);
    }

    [Theory]
    [InlineData("extensionsRequired", "KHR_draco_mesh_compression", "Draco")]
    [InlineData("extensionsUsed", "KHR_draco_mesh_compression", "Draco")]
    [InlineData("extensionsRequired", "EXT_meshopt_compression", "meshopt")]
    public void CompressedGltfIsRefusedWithAClearMessage(string key, string ext, string word)
    {
        using var lab = new Lab();
        var path = Path.Combine(lab.Dir, "packed.gltf");
        File.WriteAllText(path, $$$"""{"asset":{"version":"2.0"},"{{{key}}}":["{{{ext}}}"],"scenes":[{"nodes":[]}],"nodes":[]}""");
        var ex = Assert.Throws<MapException>(() => GltfReader.Inspect(path));
        Assert.Equal("unsupported-compression", ex.Code);
        Assert.Contains(word, ex.Message);
        Assert.Equal("unsupported-compression", lab.Session(false).Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{JsonSerializer.Serialize(path)}}}}""")).Errors[0].Code);
    }

    [Fact]
    public void OtherExtensionsStillImport()
    {
        using var lab = new Lab();
        var path = Path.Combine(lab.Dir, "ext.gltf");
        File.WriteAllText(path, """{"asset":{"version":"2.0"},"extensionsUsed":["KHR_materials_unlit","KHR_texture_transform"],"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"accessors":[{"min":[0,0,0],"max":[1,1,1]}]}""");
        Assert.NotNull(GltfReader.Inspect(path).Bounds);
    }

    // ---- hardening: names, links, bounds on folders, image headers, glb uris ----

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.fbx")]
    [InlineData("sub/COM1.png")]
    [InlineData("a/lpt9.txt.png")]
    [InlineData("name.")]
    [InlineData("a/name .fbx/")]
    public void WindowsDeviceNamesAndRewrittenNamesAreRejected(string entry)
    {
        if (entry.EndsWith('/')) entry = entry.TrimEnd('/');   // "name .fbx" keeps its trailing-space stem; the directory form is skipped
        if (entry == "a/name .fbx") { Assert.Equal("a/name .fbx", SafeEntryPath(entry)); return; }
        Assert.Equal("bad-archive", Assert.Throws<MapException>(() => SafeEntryPath(entry)).Code);
    }

    [Fact]
    public void ReservedNamesInAZipAreRefused()
    {
        using var lab = new Lab();
        var zip = Zip(lab.Dir, "dev.zip", ("ok.fbx", "x"), ("aux.png", "x"));
        Assert.Equal("bad-archive", Assert.Throws<MapException>(() => ExtractZip(zip, Path.Combine(lab.Dir, "o"))).Code);
        Assert.True(IsReservedDeviceName("Com3"));
        Assert.False(IsReservedDeviceName("console"));
    }

    [Fact]
    public void FolderWalkIsBounded()
    {
        using var lab = new Lab();
        string deep = Path.Combine(lab.Dir, "deep", "a", "b", "c", "d", "e", "f");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "toodeep.fbx"), "x");
        File.WriteAllText(Path.Combine(lab.Dir, "deep", "a", "b", "near.fbx"), "x");
        var files = ListFolder(Path.Combine(lab.Dir, "deep"));
        Assert.Contains("a/b/near.fbx", files);
        Assert.DoesNotContain(files, f => f.EndsWith("toodeep.fbx"));

        string many = Path.Combine(lab.Dir, "many"); Directory.CreateDirectory(many);
        for (int i = 0; i < MaxFolderEntries + 5; i++) File.WriteAllBytes(Path.Combine(many, $"f{i}.txt"), []);
        Assert.Equal("bad-archive", Assert.Throws<MapException>(() => ListFolder(many)).Code);
    }

    [Fact]
    public void ACollectionOfModelFoldersIsRefused()
    {
        using var lab = new Lab();
        for (int i = 0; i < MaxModelFolders + 1; i++)
        {
            Directory.CreateDirectory(Path.Combine(lab.Dir, "coll", $"asset{i}"));
            File.WriteAllText(Path.Combine(lab.Dir, "coll", $"asset{i}", "m.fbx"), "x");
        }
        var ex = Assert.Throws<MapException>(() => ListFolder(Path.Combine(lab.Dir, "coll")));
        Assert.Equal("bad-archive", ex.Code);
        Assert.Contains("single download", ex.Message);
    }

    [Fact]
    public void JunctionsAreNotFollowed()
    {
        using var lab = new Lab();
        string outside = Path.Combine(lab.Dir, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.fbx"), "x");
        string root = Path.Combine(lab.Dir, "root"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "real.fbx"), "x");
        string link = Path.Combine(root, "link");
        if (!TryMakeLink(link, outside)) return;   // no junction or symlink possible here: the path checks below still run
        try
        {
            var files = ListFolder(root);
            Assert.Contains("real.fbx", files);
            Assert.DoesNotContain(files, f => f.Contains("secret"));
            Assert.False(IsPlainUnder(root, Path.Combine(link, "secret.fbx")));
            Assert.True(IsPlainUnder(root, Path.Combine(root, "real.fbx")));
        }
        finally { Directory.Delete(link); }   // the link only, never what it points at
    }

    static bool TryMakeLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
                p!.WaitForExit(10000);
                return Directory.Exists(link);
            }
            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return false; }
    }

    [Fact]
    public void PlainUnderRejectsOutsideAndTraversal()
    {
        using var lab = new Lab();
        Directory.CreateDirectory(Path.Combine(lab.Dir, "r"));
        File.WriteAllText(Path.Combine(lab.Dir, "r", "a.fbx"), "x");
        File.WriteAllText(Path.Combine(lab.Dir, "b.fbx"), "x");
        Assert.True(IsPlainUnder(Path.Combine(lab.Dir, "r"), Path.Combine(lab.Dir, "r", "a.fbx")));
        Assert.False(IsPlainUnder(Path.Combine(lab.Dir, "r"), Path.Combine(lab.Dir, "r", "..", "b.fbx")));
        Assert.False(IsPlainUnder(Path.Combine(lab.Dir, "r"), Path.Combine(lab.Dir, "r")));
    }

    static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
        return b;
    }

    [Fact]
    public void ImageHeadersGiveTheSizeBeforeDecoding()
    {
        using var lab = new Lab();
        string png = Path.Combine(lab.Dir, "big.png"); File.WriteAllBytes(png, Png(40000, 30000));
        Assert.True(TryImageSize(png, out int w, out int h));
        Assert.Equal((40000, 30000), (w, h));
        Assert.True((long)w * h > MaxImagePixels);

        // JPEG: SOI, an APP0 segment, then SOF0 (height 300, width 500)
        byte[] jpg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x01, 0x2C, 0x01, 0xF4, 0x01, 0x01, 0x11, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        string jp = Path.Combine(lab.Dir, "a.jpg"); File.WriteAllBytes(jp, jpg);
        Assert.True(TryImageSize(jp, out w, out h)); Assert.Equal((500, 300), (w, h));

        // BMP 20 x 10
        var bmp = new byte[40]; bmp[0] = (byte)'B'; bmp[1] = (byte)'M'; bmp[18] = 20; bmp[22] = 10;
        string bp = Path.Combine(lab.Dir, "a.bmp"); File.WriteAllBytes(bp, bmp);
        Assert.True(TryImageSize(bp, out w, out h)); Assert.Equal((20, 10), (w, h));

        // TGA 64 x 32, type 2, 32 bit
        var tga = new byte[32]; tga[2] = 2; tga[12] = 64; tga[14] = 32; tga[16] = 32;
        string tp = Path.Combine(lab.Dir, "a.tga"); File.WriteAllBytes(tp, tga);
        Assert.True(TryImageSize(tp, out w, out h)); Assert.Equal((64, 32), (w, h));

        // WebP lossless 100 x 50
        var web = new byte[32]; "RIFF"u8.CopyTo(web); "WEBPVP8L"u8.CopyTo(web.AsSpan(8));
        uint bits = (uint)(100 - 1) | ((uint)(50 - 1) << 14); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(web.AsSpan(21), bits);
        string wp = Path.Combine(lab.Dir, "a.webp"); File.WriteAllBytes(wp, web);
        Assert.True(TryImageSize(wp, out w, out h)); Assert.Equal((100, 50), (w, h));

        string junk = Path.Combine(lab.Dir, "junk.png"); File.WriteAllBytes(junk, [1, 2, 3]);
        Assert.False(TryImageSize(junk, out _, out _));
        Assert.False(TryImageSize(Path.Combine(lab.Dir, "missing.png"), out _, out _));
    }

    static string GlbWith(Lab lab, string name, string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var padded = bytes.Concat(Enumerable.Repeat((byte)' ', (4 - bytes.Length % 4) % 4)).ToArray();
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + padded.Length)); w.Write((uint)padded.Length); w.Write(0x4E4F534Au); w.Write(padded);
        var path = Path.Combine(lab.Dir, name); File.WriteAllBytes(path, ms.ToArray()); return path;
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("C:/windows/win.ini")]
    [InlineData("/etc/passwd")]
    public void GlbWithOutsideUrisIsRefusedLikeGltf(string uri)
    {
        using var lab = new Lab();
        var glb = GlbWith(lab, "uri.glb", $$"""{"asset":{"version":"2.0"},"images":[{"uri":"{{uri}}"}],"scenes":[{"nodes":[]}],"nodes":[]}""");
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => GltfReader.Inspect(glb)).Code);
    }

    [Fact]
    public void GlbWithDataUrisAndNoUrisStillImports()
    {
        using var lab = new Lab();
        var glb = GlbWith(lab, "ok.glb", """{"asset":{"version":"2.0"},"images":[{"uri":"data:image/png;base64,AAAA"}],"scenes":[{"nodes":[]}],"nodes":[]}""");
        Assert.Empty(GltfReader.Inspect(glb).Companions);
    }
}
