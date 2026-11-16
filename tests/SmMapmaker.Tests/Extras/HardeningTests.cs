using System.Text.Json;
using System.Text.Json.Nodes;
using SmMapmaker;

namespace SmMapmaker.Tests;

public class HardeningTests
{
    static string Q(string s) => JsonSerializer.Serialize(s);

    static string WriteWithBuffer(Lab lab, string name, string bufferUriJson)
    {
        var path = Path.Combine(lab.Dir, name);
        File.WriteAllText(path, $$$"""
            {"asset":{"version":"2.0"},"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
             "buffers":[{"byteLength":4,"uri":{{{bufferUriJson}}}}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
             "accessors":[{"min":[0,0,0],"max":[1,1,1]}]}
            """);
        return path;
    }

    [Theory]
    [InlineData("\"../outside.bin\"")]
    [InlineData("\"C:/windows/win.ini\"")]
    [InlineData("\"/etc/passwd\"")]
    [InlineData("\"box.bin:stream\"")]
    [InlineData("\"sub/../box.bin\"")]
    [InlineData("\"missing.bin\"")]
    [InlineData("42")]
    [InlineData("[1]")]
    public void BadCompanionsAreBadFile(string uri)
    {
        using var lab = new Lab();
        File.WriteAllBytes(Path.Combine(lab.Dir, "box.bin"), [1, 2, 3, 4]);
        var path = WriteWithBuffer(lab, "bad.gltf", uri);
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => GltfReader.Inspect(path)).Code);
        Assert.Equal("bad-file", lab.Session(false).Apply(Lab.Op($$"""{"op":"import-asset","path":{{Q(path)}}}""")).Errors[0].Code);
    }

    [Fact]
    public void NonObjectBufferEntryIsBadFile()
    {
        using var lab = new Lab();
        var path = Path.Combine(lab.Dir, "weird.gltf");
        File.WriteAllText(path, """{"asset":{"version":"2.0"},"buffers":[7],"nodes":[{"translation":["a",0,0],"mesh":0}],"scenes":[{"nodes":[0]}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"accessors":[{"min":[0,0,0],"max":[1,1,1]}]}""");
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => GltfReader.Inspect(path)).Code);
    }

    [Fact]
    public void CyclicNodeTreeTerminates()
    {
        using var lab = new Lab();
        var path = Path.Combine(lab.Dir, "cycle.gltf");
        File.WriteAllText(path, """{"asset":{"version":"2.0"},"scenes":[{"nodes":[0]}],"nodes":[{"children":[1,1]},{"children":[0,0]}]}""");
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => GltfReader.Inspect(path)).Code);
    }

    [Fact]
    public void CompanionsAreCopiedAndCountInTheHash()
    {
        using var lab = new Lab();
        Directory.CreateDirectory(Path.Combine(lab.Dir, "data"));
        File.WriteAllBytes(Path.Combine(lab.Dir, "data", "box.bin"), [1, 2, 3, 4]);
        var path = WriteWithBuffer(lab, "withbin.gltf", "\"data/box.bin\"");
        var s = lab.Session(false);
        Assert.True(s.Apply(Lab.Op($$"""{"op":"import-asset","path":{{Q(path)}},"id":"withbin"}""")).Ok);
        var a = lab.Library.GetAsset("withbin")!;
        Assert.Equal("withbin.gltf", a.File);
        Assert.Equal("withbin.gltf", a.ImportedFrom);   // a file name, not a path
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(lab.Library.AssetDir("withbin"), "data", "box.bin")));
        // the same file again is a no-op; a changed .bin is different content
        Assert.Empty(s.Apply(Lab.Op($$"""{"op":"import-asset","path":{{Q(path)}},"id":"withbin"}""")).Warnings.Where(w => !w.Contains("already")));
        File.WriteAllBytes(Path.Combine(lab.Dir, "data", "box.bin"), [9, 9, 9, 9]);
        Assert.Equal("duplicate-id", s.Apply(Lab.Op($$"""{"op":"import-asset","path":{{Q(path)}},"id":"withbin"}""")).Errors[0].Code);
    }

    [Theory]
    [InlineData("../evil.glb")]
    [InlineData("sub/evil.glb")]
    [InlineData("C:evil.glb")]
    [InlineData("..")]
    public void AssetJsonFileMustBeAPlainName(string file)
    {
        var json = $$"""{"id":"x","name":"x","file":{{Q(file)}},"bounds":null,"sha256":"","importedFrom":""}""";
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => AssetInfo.Parse(json)).Code);
        Assert.Equal("ok.glb", AssetInfo.Parse(json.Replace(Q(file), "\"ok.glb\"")).File);
    }

    [Fact]
    public void PathLikeIdsAreRejected()
    {
        using var lab = new Lab();
        foreach (var id in new[] { "../x", "a/b", "a\\b", "C:x", "", "UPPER" })
        {
            Assert.Equal("bad-id", Assert.Throws<MapException>(() => lab.Library.PrefabPath(id)).Code);
            Assert.Equal("bad-id", Assert.Throws<MapException>(() => lab.Library.AssetDir(id)).Code);
        }
        Assert.Null(lab.Library.GetPrefab("../x"));
    }

    [Fact]
    public void CorruptLibraryFilesDoNotBreakLaterBatches()
    {
        using var lab = new Lab();
        Directory.CreateDirectory(lab.Library.PrefabsDir);
        File.WriteAllText(Path.Combine(lab.Library.PrefabsDir, "broken.smprefab"), "{ not json");
        File.WriteAllText(Path.Combine(lab.Library.PrefabsDir, "mismatch.smprefab"),
            new Prefab("other", "Other", [], default, "mesh", 1, [], new PrefabProvenance("custom", null, []), null).ToJson());
        Directory.CreateDirectory(Path.Combine(lab.Library.AssetsDir, "brokenasset"));
        File.WriteAllText(Path.Combine(lab.Library.AssetsDir, "brokenasset", "asset.json"), "nope");

        var skipped = new List<string>();
        Assert.Empty(lab.Library.ListPrefabs(skipped));
        Assert.Equal(2, skipped.Count);

        var s = lab.Session();
        Assert.True(s.Apply([
            Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"}}"""),
            Lab.Op("""{"op":"group-to-prefab","ids":["o1"],"prefab":"good","origin":"first"}"""),
        ]).Ok);
        Assert.NotNull(lab.Library.GetPrefab("good"));
        var index = JsonNode.Parse(File.ReadAllText(lab.Library.IndexPath))!;
        Assert.Equal(["good"], index["prefabs"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()));
        // a prefab-writing batch after that still works, and so does undo
        Assert.True(s.Apply(Lab.Op("""{"op":"prefab-edit","prefab":"good","name":"Good"}""")).Ok);
        Assert.True(s.Undo().Ok);
        // a corrupt file under the id being written is replaced and restored byte for byte on undo
        var path = lab.Library.PrefabPath("fresh");
        File.WriteAllText(path, "garbage");
        var r = s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":["wall_a"],"prefab":"fresh"}"""));
        Assert.Equal("bad-file", r.Errors[0].Code);   // the corrupt file is reported, not overwritten
        Assert.Equal("garbage", File.ReadAllText(path));
    }

    [Fact]
    public void CliReportsIoAndJsonErrorsAsJson()
    {
        using var lab = new Lab();
        var blocker = Path.Combine(lab.Dir, "file.txt");
        File.WriteAllText(blocker, "x");
        var o = new StringWriter();
        Assert.Equal(1, SmTool.MapCommand.Run(["new", Path.Combine(blocker, "sub", "m.smmap"), "--library", lab.Library.Root], o));
        Assert.Contains("io-error", o.ToString());
        o = new StringWriter();
        var map = Path.Combine(lab.Dir, "m.smmap");
        Assert.Equal(0, SmTool.MapCommand.Run(["new", map, "--library", lab.Library.Root], new StringWriter()));
        Assert.Equal(1, SmTool.MapCommand.Run(["apply", map, "-", "--library", lab.Library.Root], o, new StringReader("{ broken")));
        Assert.Contains("bad-file", o.ToString());
    }
}
