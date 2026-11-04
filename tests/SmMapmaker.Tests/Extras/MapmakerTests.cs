using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SmContent;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>Synthetic fixtures only: a fake SM1 source, generated glTF files and a temporary library.</summary>
sealed class Lab : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "smmapmaker-test-" + Guid.NewGuid().ToString("N"));
    public MapLibrary Library { get; }
    public FakeSm1 Sm1 { get; } = new();

    public Lab()
    {
        Directory.CreateDirectory(Dir);
        Library = new MapLibrary(Path.Combine(Dir, "lib"));
    }

    public MapSession Session(bool withSm1 = true) => MapSession.New("test", Library, withSm1 ? Sm1 : null);

    public static JsonObject Op(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>A one-mesh glTF: a box 2 x 4 x 2 m centred on the origin, the node moved by (0, 1, 0).</summary>
    public string WriteGltf(string name = "box.gltf", string translation = "[0,1,0]")
    {
        var path = Path.Combine(Dir, name);
        File.WriteAllText(path, GltfJson(translation));
        return path;
    }

    public string WriteGlb(string name = "box.glb")
    {
        var json = Encoding.UTF8.GetBytes(GltfJson("[0,1,0]"));
        var padded = json.Concat(Enumerable.Repeat((byte)' ', (4 - json.Length % 4) % 4)).ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + padded.Length));
        w.Write((uint)padded.Length); w.Write(0x4E4F534Au); w.Write(padded);
        var path = Path.Combine(Dir, name);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    static string GltfJson(string translation) => $$$"""
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
         "nodes":[{"mesh":0,"translation":{{{translation}}}}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
         "accessors":[{"componentType":5126,"count":8,"type":"VEC3","min":[-1,-2,-1],"max":[1,2,1]}]}
        """;

    public string ImportBox(MapSession s, string id)
    {
        var r = s.Apply(Op($$$"""{"op":"import-asset","path":{{{System.Text.Json.JsonSerializer.Serialize(WriteGltf(id + ".gltf"))}}},"id":"{{{id}}}"}"""));
        Assert.True(r.Ok, r.ToJson());
        return id;
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

sealed class FakeSm1 : ISm1Source
{
    public static readonly Matrix4x4 WallA = MapScene.Transform(new Vector3(10, 20, 0), new Vector3(0, 0, 90), Vector3.One);
    public static readonly Matrix4x4 WallB = MapScene.Transform(new Vector3(14, 20, 0), new Vector3(0, 0, 90), Vector3.One);
    public static readonly Matrix4x4 Tilted = MapScene.Transform(new Vector3(-3.25f, 8.5f, 1.5f), new Vector3(30, 45, 60), new Vector3(1, 2, 1.5f));

    public Sm1Map LoadMap(string map)
    {
        if (map != "mp_fake") throw new MapException("unknown-map", $"No map '{map}'.");
        Sm1Object[] objects =
        [
            new("wall_a", "def_wall", "l_main", WallA, 0),
            new("wall_b", "def_wall", "l_main", WallB, 0),
            new("tilted", "def_pipe", "l_main", Tilted, 0),
            new("twin", "def_wall", "l_main", MapScene.Transform(new Vector3(50, 0, 0), Vector3.Zero, Vector3.One), 0),
            new("twin", "def_wall", "l_main", MapScene.Transform(new Vector3(60, 0, 0), Vector3.Zero, Vector3.One), 0),
            new("shear", "def_pipe", "l_main", new Matrix4x4(1, 0, 0, 0, 0.5f, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1), 0),
        ];
        var defs = new Dictionary<string, Sm1Definition>(StringComparer.OrdinalIgnoreCase)
        {
            ["def_wall"] = new("def_wall", 1, new Bounds(new Vector3(-2, -0.5f, 0), new Vector3(2, 0.5f, 3))),
            ["def_pipe"] = new("def_pipe", 1, null),
        };
        return new Sm1Map(map, objects, defs, []);
    }
}

public class MapmakerTests
{
    static readonly JsonObject Place = Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"transform":{"position":[1,2,3],"rotation":[0,0,45]}}""");

    static Matrix4x4 Recomposed(MapTransform t) => t.ToMatrix();

    // ---- transforms ----

    [Theory]
    [InlineData(0, 0, 0, 1, 1, 1)]
    [InlineData(0, 90, 0, 1, 1, 1)]
    [InlineData(0, -90, 20, 1, 1, 1)]
    [InlineData(30, 45, 60, 1, 2, 1.5f)]
    [InlineData(-170, 10, 179, 2, 2, 2)]
    [InlineData(12.5f, 89.9f, -33, 0.5f, 1, 3)]
    public void DecomposeRecomposesWithinTolerance(float rx, float ry, float rz, float sx, float sy, float sz)
    {
        var m = MapScene.Transform(new Vector3(-3.25f, 880.5f, 1.5f), new Vector3(rx, ry, rz), new Vector3(sx, sy, sz));
        var t = TransformMath.Decompose(m);
        Assert.True(TransformMath.Close(Recomposed(t), m, 1e-4f), $"{t}");
    }

    [Fact]
    public void ShearIsRejected() =>
        Assert.Equal("bad-transform", Assert.Throws<MapException>(() => TransformMath.Decompose(new Matrix4x4(1, 0, 0, 0, 0.5f, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1))).Code);

    [Fact]
    public void SnapHelpers()
    {
        Assert.Equal(1.5f, Snap.Grid(1.62f, 0.5f));
        Assert.Equal(90f, Snap.Angle(87f, 15f));
        Assert.Equal(1.234f, Snap.Grid(1.234f, 0));
    }

    // ---- determinism, persistence ----

    [Fact]
    public void SameOpsGiveIdenticalBytes()
    {
        using var lab = new Lab();
        var ops = new[] { Place, Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"b"}"""),
                          Lab.Op("""{"op":"transform","id":"o2","move":[0.1,0.2,0.3]}""") };
        var a = lab.Session(); var b = lab.Session();
        Assert.True(a.Apply(ops).Ok); Assert.True(b.Apply(ops).Ok);
        Assert.Equal(a.Document.ToJson(), b.Document.ToJson());
        var pa = Path.Combine(lab.Dir, "a.smmap"); var pb = Path.Combine(lab.Dir, "b.smmap");
        a.Save(pa); b.Save(pb);
        Assert.Equal(File.ReadAllBytes(pa), File.ReadAllBytes(pb));
        var text = File.ReadAllText(pa);
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("}\n", text);
        Assert.Contains("\"position\": [1, 2, 3]", text);
    }

    [Fact]
    public void CliAndApiProduceTheSameFile()
    {
        using var lab = new Lab();
        var opsJson = """[{"op":"place","asset":{"source":"custom","id":"box"},"transform":{"position":[1.5,0,-2]}},{"op":"rename","id":"o1","name":"crate"}]""";
        var s = lab.Session(false);
        lab.ImportBox(s, "box");
        Assert.True(s.Apply(opsJson).Ok);
        var api = Path.Combine(lab.Dir, "api.smmap"); s.Save(api);

        var cli = Path.Combine(lab.Dir, "cli.smmap");
        var lib = new[] { "--library", lab.Library.Root };
        Assert.Equal(0, SmTool.MapCommand.Run(["new", cli, "--name", "test", .. lib], new StringWriter()));
        var opsFile = Path.Combine(lab.Dir, "ops.json"); File.WriteAllText(opsFile, opsJson);
        var output = new StringWriter();
        Assert.Equal(0, SmTool.MapCommand.Run(["apply", cli, opsFile, .. lib], output));
        Assert.Contains("\"ok\": true", output.ToString());
        Assert.Equal(File.ReadAllBytes(api), File.ReadAllBytes(cli));
    }

    [Fact]
    public void SaveLoadRoundTripKeepsTransformsExactly()
    {
        using var lab = new Lab();
        var s = lab.Session();
        Assert.True(s.Apply(Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"transform":{"position":[0.1,-2.3333333,880.125],"rotation":[10.5,-20.25,359.99],"scale":[1,0.5,2]}}""")).Ok);
        var path = Path.Combine(lab.Dir, "m.smmap"); s.Save(path);
        var loaded = MapSession.Load(path, lab.Library);
        Assert.Equal(s.Document.Objects[0].Transform, loaded.Document.Objects[0].Transform);
        Assert.Equal(s.Document.ToJson(), loaded.Document.ToJson());
        Assert.False(loaded.Dirty);
    }

    [Fact]
    public void BadFilesAreRejected()
    {
        using var lab = new Lab();
        var path = Path.Combine(lab.Dir, "bad.smmap"); File.WriteAllText(path, "{ not json");
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => MapSession.Load(path, lab.Library)).Code);
        File.WriteAllText(path, """{"format":"other","formatVersion":1}""");
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => MapSession.Load(path, lab.Library)).Code);
    }

    // ---- operations ----

    [Fact]
    public void FailingOpRollsBackTheWholeBatch()
    {
        using var lab = new Lab();
        var s = lab.Session();
        var before = s.Document.ToJson();
        var r = s.Apply([Place, Lab.Op("""{"op":"transform","id":"o99","move":[1,0,0]}""")]);
        Assert.False(r.Ok);
        Assert.Equal("unknown-id", r.Errors[0].Code);
        Assert.Equal("transform", r.Errors[0].Op);
        Assert.Equal(1, r.Errors[0].Index);
        Assert.Equal(before, s.Document.ToJson());
        Assert.False(s.CanUndo);
        Assert.Equal(1, s.Document.NextId);
    }

    [Fact]
    public void ErrorCodes()
    {
        using var lab = new Lab();
        var s = lab.Session();
        string Code(string json) { var r = s.Apply(Lab.Op(json)); Assert.False(r.Ok); return r.Errors[0].Code; }
        Assert.Equal("unknown-asset", Code("""{"op":"place","asset":{"source":"custom","id":"nope"}}"""));
        Assert.Equal("unknown-prefab", Code("""{"op":"place","prefab":"nope"}"""));
        Assert.Equal("bad-transform", Code("""{"op":"place","asset":{"source":"sm1","map":"m","definition":"d"},"transform":{"scale":[0,1,1]}}"""));
        Assert.Equal("bad-op", Code("""{"op":"frobnicate"}"""));
        Assert.Equal("bad-op", Code("""{"op":"place"}"""));
        Assert.Equal("missing-install", lab.Session(false).Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":["wall_a"],"prefab":"x"}""")).Errors[0].Code);
        Assert.Equal("bad-file", Code("""{"op":"import-asset","path":"/no/such/file.glb"}"""));
        Assert.Equal("unknown-map", Code("""{"op":"extract-sm1","map":"other","objects":["wall_a"],"prefab":"x"}"""));
        Assert.Equal("unknown-sm1-object", Code("""{"op":"extract-sm1","map":"mp_fake","objects":["ghost"],"prefab":"x"}"""));
        Assert.Equal("bad-id", Code("""{"op":"extract-sm1","map":"mp_fake","objects":["wall_a"],"prefab":"Bad Id"}"""));
    }

    [Fact]
    public void MissingInstallFromInstalledSource()
    {
        var src = new InstalledSm1Source(Path.Combine(Path.GetTempPath(), "smmapmaker-no-install-" + Guid.NewGuid().ToString("N")));
        Assert.Equal("missing-install", Assert.Throws<MapException>(() => src.LoadMap("mp_tdm_09")).Code);
    }

    [Fact]
    public void DryRunChangesNothing()
    {
        using var lab = new Lab();
        var s = lab.Session();
        var r = s.Apply([Place], dryRun: true);
        Assert.True(r.Ok);
        Assert.Equal(["o1"], r.Created);
        Assert.Empty(s.Document.Objects);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void TransformSetAndDeltaWorkOnComponents()
    {
        using var lab = new Lab();
        var s = lab.Session();
        s.Apply(Place);
        s.Apply(Lab.Op("""{"op":"transform","id":"o1","move":[1,1,1],"rotate":[0,0,10]}"""));
        s.Apply(Lab.Op("""{"op":"transform","id":"o1","scaleBy":2}"""));
        var t = s.Document.Get("o1").Transform;
        Assert.Equal(new Vector3(2, 3, 4), t.Position);
        Assert.Equal(new Vector3(0, 0, 55), t.Rotation);
        Assert.Equal(new Vector3(2, 2, 2), t.Scale);
        Assert.Equal("bad-op", s.Apply(Lab.Op("""{"op":"transform","id":"o1","position":[0,0,0],"move":[1,0,0]}""")).Errors[0].Code);
    }

    [Fact]
    public void DuplicateDeleteAndSetParentKeepWorldPositions()
    {
        using var lab = new Lab();
        var s = lab.Session();
        Assert.True(s.Apply([
            Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"root","transform":{"position":[10,0,0],"rotation":[0,0,90]}}"""),
            Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"kid","transform":{"position":[0,5,0]}}"""),
        ]).Ok);
        var worldBefore = s.Document.World("o2");
        Assert.True(s.Apply(Lab.Op("""{"op":"set-parent","id":"o2","parent":"o1"}""")).Ok);
        Assert.Equal("o1", s.Document.Get("o2").Parent);
        Assert.True(TransformMath.Close(worldBefore, s.Document.World("o2"), 1e-4f));
        Assert.True(s.Apply(Lab.Op("""{"op":"set-parent","id":"o2","parent":null}""")).Ok);
        Assert.True(TransformMath.Close(worldBefore, s.Document.World("o2"), 1e-4f));
        Assert.Equal("parent-cycle", s.Apply([Lab.Op("""{"op":"set-parent","id":"o2","parent":"o1"}"""), Lab.Op("""{"op":"set-parent","id":"o1","parent":"o2"}""")]).Errors[0].Code);

        s.Apply(Lab.Op("""{"op":"set-parent","id":"o2","parent":"o1"}"""));
        var dup = s.Apply(Lab.Op("""{"op":"duplicate","ids":["o1"],"offset":[0,0,10]}"""));
        Assert.True(dup.Ok);
        Assert.Equal(["o3"], dup.Created);
        Assert.Equal(4, s.Document.Objects.Count);
        Assert.Equal("o3", s.Document.Get("o4").Parent);
        Assert.Equal(new Vector3(10, 0, 10), s.Document.Get("o3").Transform.Position);
        Assert.Equal(s.Document.Get("o2").Transform, s.Document.Get("o4").Transform);

        Assert.True(s.Apply(Lab.Op("""{"op":"delete","ids":["o1"]}""")).Ok);
        Assert.Equal(["o3", "o4"], s.Document.Objects.Select(o => o.Id));
        s.Apply(Place);
        Assert.Equal("o5", s.Document.Objects[^1].Id);   // ids are never reused
    }

    // ---- undo / redo ----

    [Fact]
    public void UndoRedoRestoreExactBytes()
    {
        using var lab = new Lab();
        var s = lab.Session();
        var empty = s.Document.ToJson();
        s.Apply([Place, Place]);
        var one = s.Document.ToJson();
        s.Apply(Lab.Op("""{"op":"transform","id":"o1","position":[5,5,5]}"""));
        var two = s.Document.ToJson();
        Assert.True(s.Undo().Ok); Assert.Equal(one, s.Document.ToJson());
        Assert.True(s.Undo().Ok); Assert.Equal(empty, s.Document.ToJson());
        Assert.False(s.Undo().Ok);
        Assert.True(s.Redo().Ok); Assert.Equal(one, s.Document.ToJson());
        Assert.True(s.Redo().Ok); Assert.Equal(two, s.Document.ToJson());
        Assert.Equal("nothing-to-redo", s.Redo().Errors[0].Code);
        s.Undo();
        s.Apply(Place);
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void UndoRevertsPrefabFilesToo()
    {
        using var lab = new Lab();
        var s = lab.Session();
        s.Apply([Place, Place]);
        Assert.True(s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o1","o2"],"prefab":"wall-pair","name":"Wall pair","origin":"first"}""")).Ok);
        Assert.NotNull(lab.Library.GetPrefab("wall-pair"));
        s.Undo();
        Assert.Null(lab.Library.GetPrefab("wall-pair"));
        s.Redo();
        Assert.NotNull(lab.Library.GetPrefab("wall-pair"));
    }

    [Fact]
    public void ChangedEventAndVersion()
    {
        using var lab = new Lab();
        var s = lab.Session();
        int n = 0; s.Changed += (_, _) => n++;
        s.Apply(Place); s.Undo();
        Assert.Equal(2, n); Assert.Equal(2, s.Version);
        s.Apply(Lab.Op("""{"op":"bogus"}"""));
        Assert.Equal(2, n);
    }

    // ---- prefabs ----

    static MapSession PairScene(Lab lab)
    {
        var s = lab.Session();
        lab.ImportBox(s, "pillar");
        Assert.True(s.Apply([
            Lab.Op("""{"op":"place","asset":{"source":"custom","id":"pillar"},"name":"left","transform":{"position":[100,50,0],"rotation":[0,0,90]}}"""),
            Lab.Op("""{"op":"place","asset":{"source":"custom","id":"pillar"},"name":"right","transform":{"position":[104,50,0],"rotation":[0,0,90]}}"""),
        ]).Ok);
        return s;
    }

    [Fact]
    public void GroupToPrefabKeepsRelativePositionsAndReplaces()
    {
        using var lab = new Lab();
        var s = PairScene(lab);
        var r = s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o1","o2"],"prefab":"pair","name":"Pair","origin":"first","replace":true}"""));
        Assert.True(r.Ok, r.ToJson());
        Assert.Equal(["pair"], r.CreatedPrefabs);
        var p = lab.Library.GetPrefab("pair")!;
        Assert.Equal(2, p.Parts.Count);
        Assert.Equal(new Vector3(0, 0, 0), p.Parts[0].Transform.Position);
        Assert.Equal(new Vector3(4, 0, 0), p.Parts[1].Transform.Position);
        Assert.Equal(new Vector3(100, 50, 0), p.Origin);
        Assert.Equal(new Vector3(0, 0, 90), p.Parts[1].Transform.Rotation);
        Assert.Single(s.Document.Objects);
        var inst = s.Document.Objects[0];
        Assert.Equal("pair", inst.Prefab);
        Assert.Equal(new Vector3(100, 50, 0), inst.Transform.Position);
        // The instance draws the pieces where they were.
        var parts = s.Resolve();
        Assert.Equal(2, parts.Count);
        Assert.Equal(100f, parts[0].World.M41, 3); Assert.Equal(104f, parts[1].World.M41, 3);
    }

    [Fact]
    public void BottomCenterOriginUsesAssetBounds()
    {
        using var lab = new Lab();
        var s = PairScene(lab);
        Assert.True(s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o1","o2"],"prefab":"pair"}""")).Ok);
        var p = lab.Library.GetPrefab("pair")!;
        Assert.NotNull(p.Bounds);
        // glTF box is 2 x 4 x 2 with Y up; document Z up gives a height of 4 above the foot... the foot is the min Z of the world box.
        Assert.Equal(1f, p.Parts.Min(x => x.Transform.Position.Z), 3);   // the foot (box min Z = -1) is the origin, so parts sit 1 m above it
        Assert.Equal(102f, p.Origin.X, 3);
    }

    [Fact]
    public void LinkedInstancesFollowMasterAndKeepValidOverrides()
    {
        using var lab = new Lab();
        var s = PairScene(lab);
        s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o1","o2"],"prefab":"pair","origin":"first","replace":true}"""));
        // two instances, with overrides on the first
        Assert.True(s.Apply([
            Lab.Op("""{"op":"place","prefab":"pair","transform":{"position":[0,0,0]}}"""),
            Lab.Op("""{"op":"instance-override","id":"o4","part":"p1","visible":false}"""),
            Lab.Op("""{"op":"instance-override","id":"o4","part":"p2","transform":{"position":[9,0,0]}}"""),
        ]).Ok);
        var inst2 = s.Document.Get("o3");
        Assert.Empty(inst2.Overrides);
        Assert.Equal(2, s.Document.Get("o4").Overrides.Count);
        // master edit: move part p1 in the prefab; p2 removed
        var r = s.Apply(Lab.Op("""{"op":"prefab-edit","prefab":"pair","setPart":[{"id":"p1","transform":{"position":[0,7,0]}}],"removeParts":["p2"]}"""));
        Assert.True(r.Ok, r.ToJson());
        Assert.Contains(r.Warnings, w => w.Contains("p2") && w.Contains("o4"));
        var o4 = s.Document.Get("o4");
        Assert.Single(o4.Overrides);
        Assert.False(o4.Overrides[0].Visible);
        var parts = s.Resolve();
        Assert.Equal(2, parts.Count);   // o3 (p1) and o4 (p1, hidden)
        Assert.Equal(57f, parts.First(p => p.ObjectId == "o3").World.M42, 3);   // the instance sits at y = 50
        Assert.False(parts.First(p => p.ObjectId == "o4").Visible);
        // a new part gets a fresh id, not p2 again
        s.Apply(Lab.Op("""{"op":"prefab-edit","prefab":"pair","addParts":[{"asset":{"source":"custom","id":"pillar"}}]}"""));
        Assert.Equal(["p1", "p3"], lab.Library.GetPrefab("pair")!.Parts.Select(p => p.Id));
        Assert.True(s.Validate().Ok);
    }

    [Fact]
    public void PrefabCycleIsRejected()
    {
        using var lab = new Lab();
        var s = PairScene(lab);
        s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o1"],"prefab":"a","origin":"first"}"""));
        s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o2"],"prefab":"b","origin":"first"}"""));
        s.Apply(Lab.Op("""{"op":"place","prefab":"a"}"""));
        Assert.True(s.Apply(Lab.Op("""{"op":"prefab-edit","prefab":"a","addParts":[{"prefab":"b"}]}""")).Ok);
        var r = s.Apply(Lab.Op("""{"op":"prefab-edit","prefab":"b","addParts":[{"prefab":"a"}]}"""));
        Assert.Equal("prefab-cycle", r.Errors[0].Code);
        Assert.Equal("prefab-cycle", s.Apply(Lab.Op("""{"op":"prefab-edit","prefab":"a","addParts":[{"prefab":"a"}]}""")).Errors[0].Code);
        Assert.Single(lab.Library.GetPrefab("b")!.Parts);
        Assert.Equal("unknown-part", s.Apply(Lab.Op("""{"op":"instance-override","id":"o3","part":"p9","visible":false}""")).Errors[0].Code);
    }

    [Fact]
    public void PrefabSurvivesLibraryRoundTripAndImport()
    {
        using var lab = new Lab();
        var s = PairScene(lab);
        s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o1","o2"],"prefab":"pair","name":"Pair","tags":["wall"]}"""));
        var file = lab.Library.PrefabPath("pair");
        // import into a fresh library: unknown asset is an error, then works once the asset exists
        var other = new Lab();
        try
        {
            var r = other.Session(false).Apply(Lab.Op($$$"""{"op":"import-prefab","path":{{{System.Text.Json.JsonSerializer.Serialize(file)}}}}"""));
            Assert.Equal("unknown-asset", r.Errors[0].Code);
            var s2 = other.Session(false);
            other.ImportBox(s2, "pillar");
            r = s2.Apply(Lab.Op($$$"""{"op":"import-prefab","path":{{{System.Text.Json.JsonSerializer.Serialize(file)}}}}"""));
            Assert.True(r.Ok, r.ToJson());
            Assert.Equal(File.ReadAllText(file), File.ReadAllText(other.Library.PrefabPath("pair")));
            Assert.Contains("pair", File.ReadAllText(other.Library.IndexPath));
        }
        finally { other.Dispose(); }
    }

    // ---- SM1 ----

    [Fact]
    public void ExtractSm1KeepsRelationshipAndRecomposes()
    {
        using var lab = new Lab();
        var s = lab.Session();
        var r = s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":["wall_a","wall_b","tilted"],"prefab":"walls","name":"Walls","origin":"first"}"""));
        Assert.True(r.Ok, r.ToJson());
        var p = lab.Library.GetPrefab("walls")!;
        Assert.Equal("sm1", p.Provenance.Source);
        Assert.Equal("mp_fake", p.Provenance.Map);
        Assert.Equal(["wall_a", "wall_b", "tilted"], p.Provenance.Objects);
        Assert.Equal(new Vector3(10, 20, 0), p.Origin);
        Assert.Equal(new Vector3(4, 0, 0), p.Parts[1].Transform.Position);
        // recomposition: part matrix * translation(origin) equals the original placement
        var origin = Matrix4x4.CreateTranslation(p.Origin);
        Assert.True(TransformMath.Close(p.Parts[0].Transform.ToMatrix() * origin, FakeSm1.WallA, 1e-4f));
        Assert.True(TransformMath.Close(p.Parts[1].Transform.ToMatrix() * origin, FakeSm1.WallB, 1e-4f));
        Assert.True(TransformMath.Close(p.Parts[2].Transform.ToMatrix() * origin, FakeSm1.Tilted, 1e-4f));
        Assert.Null(p.Bounds);   // def_pipe has no bounds, so the prefab's are unknown
        Assert.Equal("def_wall", p.Parts[0].Asset!.Definition);
    }

    [Fact]
    public void ExtractSm1BoundsBottomCenterAndShearError()
    {
        using var lab = new Lab();
        var s = lab.Session();
        Assert.True(s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":["wall_a","wall_b"],"prefab":"w2"}""")).Ok);
        var p = lab.Library.GetPrefab("w2")!;
        Assert.NotNull(p.Bounds);
        Assert.Equal(12f, p.Origin.X, 3); Assert.Equal(20f, p.Origin.Y, 3); Assert.Equal(0f, p.Origin.Z, 3);
        var r = s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":["shear"],"prefab":"bad"}"""));
        Assert.Equal("bad-transform", r.Errors[0].Code);
        // unknown bounds fall back to the first object, with a warning
        var r2 = s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":["tilted"],"prefab":"t"}"""));
        Assert.True(r2.Ok); Assert.NotEmpty(r2.Warnings);
    }

    [Fact]
    public void ExtractSm1ByIndexPicksExactlyOne()
    {
        using var lab = new Lab();
        var s = lab.Session();
        var byName = s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":["twin"],"prefab":"both","origin":"first"}"""));
        Assert.True(byName.Ok); Assert.NotEmpty(byName.Warnings);
        Assert.Equal(2, lab.Library.GetPrefab("both")!.Parts.Count);
        var r = s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":[{"index":4}],"prefab":"second","origin":"first"}"""));
        Assert.True(r.Ok, r.ToJson()); Assert.Empty(r.Warnings);
        var p = lab.Library.GetPrefab("second")!;
        Assert.Single(p.Parts);
        Assert.Equal(new Vector3(60, 0, 0), p.Origin);
        Assert.Equal(["twin"], p.Provenance.Objects);
        Assert.Equal([4], p.Provenance.Indices);
        Assert.Equal([3, 4], lab.Library.GetPrefab("both")!.Provenance.Indices);
        Assert.Equal("unknown-sm1-object", s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":[{"index":99}],"prefab":"x"}""")).Errors[0].Code);
        Assert.Equal("unknown-sm1-object", s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":[{"index":-1}],"prefab":"x"}""")).Errors[0].Code);
        Assert.Equal("bad-op", s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":[{"name":"twin"}],"prefab":"x"}""")).Errors[0].Code);
    }

    [Fact]
    public void PlacedSm1AssetIsOnlyAReference()
    {
        using var lab = new Lab();
        var s = lab.Session();
        s.Apply(Place);
        s.Apply(Lab.Op("""{"op":"extract-sm1","map":"mp_fake","objects":["wall_a"],"prefab":"one"}"""));
        s.Save(Path.Combine(lab.Dir, "m.smmap"));
        var all = Directory.EnumerateFiles(lab.Dir, "*", SearchOption.AllDirectories).ToList();
        Assert.All(all, f => Assert.True(Path.GetExtension(f) is ".smmap" or ".smprefab" or ".json", f));
        foreach (var f in all) { JsonNode.Parse(File.ReadAllText(f)); Assert.True(new FileInfo(f).Length < 8192, f); }
    }

    // ---- assets ----

    [Fact]
    public void GltfAndGlbImportBounds()
    {
        using var lab = new Lab();
        var s = lab.Session(false);
        Assert.True(s.Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{System.Text.Json.JsonSerializer.Serialize(lab.WriteGltf())}}},"id":"box-gltf","name":"Box"}""")).Ok);
        Assert.True(s.Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{System.Text.Json.JsonSerializer.Serialize(lab.WriteGlb())}}}}""")).Ok);
        var a = lab.Library.GetAsset("box-gltf")!;
        Assert.Equal(new Vector3(-1, -1, -1), a.Bounds!.Value.Min);   // node translation (0,1,0) applied
        Assert.Equal(new Vector3(1, 3, 1), a.Bounds!.Value.Max);
        Assert.Equal(a.Bounds, lab.Library.GetAsset("box")!.Bounds);
        Assert.Equal("Box", a.Name);
        Assert.Equal(64, a.Sha256.Length);
        Assert.True(File.Exists(lab.Library.AssetFilePath(a)));
        // document frame is Z up
        Assert.Equal(new Vector3(-1, -1, -1), a.BoundsInDocumentFrame!.Value.Min);
        Assert.Equal(new Vector3(1, 1, 3), a.BoundsInDocumentFrame!.Value.Max);
        // same content again is a no-op; other content under the same id is an error
        Assert.True(s.Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{System.Text.Json.JsonSerializer.Serialize(lab.WriteGltf())}}},"id":"box-gltf"}""")).Ok);
        var other = lab.WriteGltf("other.gltf", "[5,5,5]");
        Assert.Equal("duplicate-id", s.Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{System.Text.Json.JsonSerializer.Serialize(other)}}},"id":"box-gltf"}""")).Errors[0].Code);
        Assert.Contains("box-gltf", File.ReadAllText(lab.Library.IndexPath));
    }

    [Fact]
    public void BrokenGltfIsBadFile()
    {
        using var lab = new Lab();
        var p = Path.Combine(lab.Dir, "broken.glb"); File.WriteAllBytes(p, [1, 2, 3, 4]);
        Assert.Equal("bad-file", lab.Session(false).Apply(Lab.Op($$$"""{"op":"import-asset","path":{{{System.Text.Json.JsonSerializer.Serialize(p)}}}}""")).Errors[0].Code);
    }

    // ---- validation and preview ----

    [Fact]
    public void ValidateReportsMissingLibraryEntries()
    {
        using var lab = new Lab();
        var s = PairScene(lab);
        s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o1"],"prefab":"solo","origin":"first","replace":true}"""));
        Assert.True(s.Validate().Ok);
        File.Delete(lab.Library.PrefabPath("solo"));
        var v = s.Validate();
        Assert.False(v.Ok);
        Assert.Equal("unknown-prefab", v.Errors[0].Code);
    }

    [Fact]
    public void PreviewIsDeterministicSvgWithLabels()
    {
        using var lab = new Lab();
        var s = lab.Session();
        Assert.Contains("(empty)", s.PreviewSvg());
        s.Apply([Place, Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_pipe"},"name":"pipe <1>","transform":{"position":[20,5,0]}}""")]);
        var svg = s.PreviewSvg();
        Assert.StartsWith("<svg", svg);
        Assert.Contains("pipe &lt;1&gt;", svg);
        Assert.Equal(2, svg.Split("<polygon").Length - 1);
        Assert.Equal(svg, lab.Session().Also(x => x.Apply([Place, Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_pipe"},"name":"pipe <1>","transform":{"position":[20,5,0]}}""")])).PreviewSvg());
    }

    [Fact]
    public void CliExitCodesAndQuery()
    {
        using var lab = new Lab();
        var lib = new[] { "--library", lab.Library.Root };
        var path = Path.Combine(lab.Dir, "c.smmap");
        Assert.Equal(2, SmTool.MapCommand.Run([], new StringWriter()));
        Assert.Equal(2, SmTool.MapCommand.Run(["bogus"], new StringWriter()));
        Assert.Equal(0, SmTool.MapCommand.Run(["new", path, .. lib], new StringWriter()));
        Assert.Equal(1, SmTool.MapCommand.Run(["new", path, .. lib], new StringWriter()));
        var ops = new StringReader("""[{"op":"place","asset":{"source":"sm1","map":"m","definition":"d"}}]""");
        var o = new StringWriter();
        Assert.Equal(0, SmTool.MapCommand.Run(["apply", path, "-", "--dry-run", .. lib], o, ops));
        Assert.Contains("\"dryRun\": true", o.ToString());
        Assert.Empty(MapSession.Load(path, lab.Library).Document.Objects);
        o = new StringWriter();
        Assert.Equal(1, SmTool.MapCommand.Run(["apply", path, "-", .. lib], o, new StringReader("""[{"op":"delete","ids":["o9"]}]""")));
        Assert.Contains("unknown-id", o.ToString());
        o = new StringWriter();
        Assert.Equal(0, SmTool.MapCommand.Run(["query", path, "objects", .. lib], o));
        Assert.Contains("\"objects\": []", o.ToString());
        Assert.Equal(0, SmTool.MapCommand.Run(["validate", path, .. lib], new StringWriter()));
        var svg = Path.Combine(lab.Dir, "p.svg");
        Assert.Equal(0, SmTool.MapCommand.Run(["preview", path, "--out", svg, .. lib], new StringWriter()));
        Assert.True(File.Exists(svg));
        o = new StringWriter();
        Assert.Equal(0, SmTool.MapCommand.Run(["import-asset", lab.WriteGlb(), "--id", "crate", .. lib], o));
        Assert.Equal(0, SmTool.MapCommand.Run(["query", path, "assets", .. lib], o = new StringWriter()));
        Assert.Contains("crate", o.ToString());
    }
}

static class TestExt
{
    public static T Also<T>(this T value, Action<T> act) { act(value); return value; }
}
