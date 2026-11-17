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
}
