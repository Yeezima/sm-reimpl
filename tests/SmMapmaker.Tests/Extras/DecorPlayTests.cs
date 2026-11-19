using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SmContent;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>A synthetic SM1 source of meshes for the compiler (no original bytes): map <c>mp_fake</c> with a wall, a composite floor with a gate, and the "original collision" of a flat ground.</summary>
sealed class FakeScenes : ISm1Scenes
{
    public float WallHeight { get; init; } = 3f;
    public int SceneLoads;
    public bool HasWall { get; init; } = true;
    public bool Explode { get; init; }

    /// <summary>A 40 x 40 vertex grid at z = 0 (7,500 triangles).</summary>
    static DcmBlock Grid()
    {
        var p = new List<Vector3>(); var t = new List<ushort>();
        for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++) p.Add(new Vector3(x, y, 0));
        for (int y = 0; y < 39; y++) for (int x = 0; x < 39; x++) { ushort a = (ushort)(y * 40 + x); t.AddRange([a, (ushort)(a + 1), (ushort)(a + 40), (ushort)(a + 1), (ushort)(a + 41), (ushort)(a + 40)]); }
        return new DcmBlock(0, Vector3.Zero, new(39, 39, 0), [new DcmMaterialRange("mat", 0, 0, 1600, 0, t.Count, 0, 0)], [.. t], [.. p], [], [], [], [], [], null);
    }

    static DcmBlock Box(Vector3 lo, Vector3 hi)
    {
        var p = new Vector3[8];
        for (int i = 0; i < 8; i++) p[i] = new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
        ushort[] t = [0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5];
        return new DcmBlock(0, lo, hi, [new DcmMaterialRange("mat", 0, 0, 8, 0, 36, 0, 0)], t, p, [], [], [], [], [], null);
    }

    public MapScene Scene(string map, IReadOnlySet<string> definitions)
    {
        if (Explode) throw new InvalidOperationException("boom");
        if (map != "mp_fake") throw new MapException("unknown-map", $"The Space Marine install on this PC has no map '{map}'.");
        SceneLoads++;
        var meshes = new Dictionary<string, DcmMesh>(StringComparer.OrdinalIgnoreCase)
        {
            ["wall.dcm"] = DcmMesh.FromBlocks([Box(new(-2, -0.5f, 0), new(2, 0.5f, WallHeight))]),
            ["big.dcm"] = DcmMesh.FromBlocks([Grid()]),
            ["empty.dcm"] = DcmMesh.FromBlocks([new DcmBlock(0, Vector3.Zero, Vector3.One, [], [], [Vector3.Zero], [], [], [], [], [], null)]),
            ["floor.dcm"] = DcmMesh.FromBlocks([Box(new(-50, -50, 0), new(50, 50, 0.1f)), Box(new(10, -2, 0), new(14, 2, 8))]),
        };
        var defs = new Dictionary<string, ObjectDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["def_composite"] = new("def_composite", [new("floor.dcm", 0, "Floor", Matrix4x4.Identity, -1, true), new("floor.dcm", 1, "Gate_a", Matrix4x4.Identity, -1, true)], []),
        };
        defs["def_big"] = new("def_big", [new("big.dcm", 0, "Big", Matrix4x4.Identity, -1, true)], []);
        defs["def_empty"] = new("def_empty", [new("empty.dcm", 0, "Empty", Matrix4x4.Identity, -1, true)], []);
        if (HasWall) defs["def_wall"] = new("def_wall", [new("wall.dcm", 0, "Wall", Matrix4x4.Identity, -1, true)], []);
        return new MapScene { Map = map, Objects = [], Definitions = defs, Meshes = meshes, Materials = new Dictionary<string, BmatMaterial>(), Textures = new Dictionary<string, byte[]>(), Notes = [] };
    }

    public (Vector3[] Vertices, int[] Indices) OriginalCollision(string map)
    {
        // The original map's ground: two big triangles at z = 0 and a small slab far away.
        Vector3[] v = [new(-100, -100, 0), new(100, -100, 0), new(100, 100, 0), new(-100, 100, 0), new(300, 300, 0), new(310, 300, 0), new(310, 310, 0)];
        return (v, [0, 1, 2, 0, 2, 3, 4, 5, 6]);
    }
}

/// <summary>SM1 placements, prefab instances and imported models are drawn and solid in play (docs/mapmaker.md "SM1 pieces in play"). Synthetic meshes only.</summary>
public sealed class DecorPlayTests : IDisposable
{
    readonly Lab _lab = new();
    public void Dispose() => _lab.Dispose();

    CompileOptions Options(FakeScenes? scenes = null, bool strict = true) => new() { Scenes = scenes ?? new FakeScenes(), Library = _lab.Library, Strict = strict };

    MapDocument Doc(params string[] ops)
    {
        var s = _lab.Session();
        foreach (var op in ops) { var r = s.Apply(Lab.Op(op)); Assert.True(r.Ok, r.ToJson()); }
        return s.Document;
    }

    const string Wall = """{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"}""";

    static float? Down(CustomMap map, float x, float y, float fromZ = 20f) =>
        map.ToCollisionWorld().Character.Raycast(new Vector3(x, y, fromZ), -Vector3.UnitZ, 100f) is { } h ? h.Point.Z : null;

    [Fact]
    public void An_SM1_placement_is_solid_where_it_is_placed_and_drawn()
    {
        var doc = Doc(Wall + ""","transform":{"position":[10,20,5],"rotation":[0,0,90]}}""");
        var map = CustomMap.Compile(doc, Options());
        Assert.Equal(1, map.SolidCount);
        Assert.Equal(12, map.Decor!.Triangles);
        Assert.Single(map.Decor.Placements);
        Assert.Equal(8f, Down(map, 10, 20)!.Value, 3);                      // the wall's top: 5 + 3
        // turned 90 degrees about Z, the wall's 4 m length runs along Y: x = 10 +- 0.5, y = 20 +- 2
        Assert.NotNull(Down(map, 10.2f, 21.5f));
        Assert.Null(Down(map, 11.5f, 20));                                  // beside it: nothing (4 m long walls would have reached here unturned)
        Assert.Null(Down(map, 10, 22.5f));
    }

    [Fact]
    public void A_part_of_a_composite_placement_is_the_only_part_drawn_and_solid()
    {
        var doc = Doc("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_composite","part":1}}""");
        var map = CustomMap.Compile(doc, Options());
        Assert.Equal(12, map.Decor!.Triangles);                             // the gate only, not the 100 m floor
        Assert.Null(Down(map, 0, 0));
        Assert.Equal(8f, Down(map, 12, 0)!.Value, 3);
        var whole = CustomMap.Compile(Doc("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_composite"}}"""), Options());
        Assert.Equal(24, whole.Decor!.Triangles);
        Assert.Equal(0.1f, Down(whole, 0, 0)!.Value, 3);
    }

    [Fact]
    public void Prefab_instances_are_unfolded_with_their_overrides_and_hidden_parts_are_neither_drawn_nor_solid()
    {
        var doc = Doc(Wall + ""","name":"a"}""", Wall + ""","name":"b","transform":{"position":[10,0,0]}}""",
            """{"op":"group-to-prefab","ids":["o1","o2"],"prefab":"pair","name":"pair","origin":[0,0,0],"replace":true}""",
            """{"op":"place","prefab":"pair","transform":{"position":[0,50,0]}}""");
        var map = CustomMap.Compile(doc, Options());
        Assert.Equal(4, map.Decor!.Placements.Count(p => p.Solid));         // the replaced instance at the origin and the second one: 2 parts each
        Assert.Equal(4, map.Decor.Placements.Count);
        Assert.Equal(3f, Down(map, 0, 50)!.Value, 3);
        Assert.Equal(3f, Down(map, 10, 50)!.Value, 3);
        var hidden = Doc(Wall + ""","name":"a"}""", """{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"transform":{"position":[30,0,0]}}""", """{"op":"set-visible","id":"o2","visible":false}""");
        var m2 = CustomMap.Compile(hidden, Options());
        Assert.Single(m2.Decor!.Placements);
        Assert.Null(Down(m2, 30, 0));
    }

    [Fact]
    public void An_original_cut_prefab_uses_the_original_collision_inside_its_box_and_its_meshes_are_not_solid()
    {
        var doc = Doc(Wall + ""","name":"a","transform":{"position":[5,5,0]}}""",
            """{"op":"group-to-prefab","ids":["o1"],"prefab":"cutme","name":"cutme","origin":[0,0,0],"collision":"original-cut","replace":true}""");
        var map = CustomMap.Compile(doc, Options());
        Assert.Single(map.Decor!.Placements);
        Assert.False(map.Decor.Placements[0].Solid);
        // the ground under the prefab's box (x 3..7, y 4.5..5.5 and a quarter metre), clipped out of the two big ground triangles: not the whole ground, not the wall mesh
        Assert.True(map.Decor.Triangles is > 0 and <= 8);
        Assert.Null(Down(map, 20, 20));                                     // the big ground triangle goes on only where the prefab is
        Assert.Null(Down(map, 5, 8));
        Assert.Equal(0f, Down(map, 5, 5)!.Value, 3);                        // the original ground, not the wall's top (3)
    }

    [Fact]
    public void A_PC_that_lacks_the_content_refuses_in_plain_words_and_never_plays_another_world()
    {
        var doc = Doc(Wall + "}");
        Assert.Equal("missing-install", Assert.Throws<MapException>(() => CustomMap.Compile(doc, new CompileOptions { Strict = true })).Code);
        var unknownMap = Assert.Throws<MapException>(() => CustomMap.Compile(Doc("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"}}"""), Options(new FakeScenes { HasWall = false })));
        Assert.Equal("missing-sm1-definition", unknownMap.Code);
        Assert.Contains("def_wall", unknownMap.Message);
        // a map the install has not: the scene provider's own refusal comes through, naming the map
        var otherMap = MapDocument.Parse(doc.ToJson().Replace("mp_fake", "mp_elsewhere"));
        var ex = Assert.Throws<MapException>(() => CustomMap.Compile(otherMap, Options()));
        Assert.Equal("unknown-map", ex.Code);
        Assert.Contains("mp_elsewhere", ex.Message);
        // a prefab neither in the file nor in the library
        var withPrefab = MapDocument.Parse(Doc(Wall + "}", """{"op":"group-to-prefab","ids":["o1"],"prefab":"gone","name":"gone","origin":[0,0,0],"replace":true}""").ToJson());
        withPrefab.EmbeddedPrefabs.Clear();
        var otherPc = new CompileOptions { Scenes = new FakeScenes(), Library = new MapLibrary(Path.Combine(_lab.Dir, "other-pc")), Strict = true };
        Assert.Equal("missing-prefab", Assert.Throws<MapException>(() => CustomMap.Compile(withPrefab, otherPc)).Code);
        // lenient (the editor): the same document compiles and says what is left out
        var lenient = CustomMap.Compile(otherMap, Options(strict: false));
        Assert.Contains(lenient.Notes, n => n.Contains("mp_elsewhere"));
        Assert.Equal(0, lenient.Decor!.Triangles);
        // and a map of pieces only needs nothing at all
        Assert.Equal(0, CustomMap.Compile(Doc("""{"op":"place","piece":{"kind":"box"},"transform":{"scale":[4,4,1]}}"""), new CompileOptions { Strict = true }).Decor!.Triangles);
    }

    [Fact]
    public void The_map_hash_covers_the_triangles_so_a_PC_with_different_content_is_refused_by_the_handshake()
    {
        var doc = Doc(Wall + "}");
        var a = CustomMap.Compile(doc, Options());
        var b = CustomMap.Compile(MapDocument.Parse(doc.ToJson()), Options());
        var taller = CustomMap.Compile(doc, Options(new FakeScenes { WallHeight = 3.5f }));
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Decor!.Digest(), b.Decor!.Digest());
        Assert.NotEqual(a.Hash, taller.Hash);                                // same file, a different install: another world
        Assert.NotEqual(CustomMap.HashOf(doc), a.Hash);                       // the document alone no longer names a decorated map's world
        Assert.Equal(3, CustomMap.CompilerVersion);
        // a map of pieces only keeps hashing the document alone
        var pieces = Doc("""{"op":"place","piece":{"kind":"box"},"transform":{"scale":[4,4,1]}}""");
        Assert.Equal(CustomMap.HashOf(pieces), CustomMap.Compile(pieces, Options()).Hash);
    }

    [Fact]
    public void Characters_stand_on_an_SM1_piece_and_the_check_finds_the_floor_without_an_invisible_wall()
    {
        var doc = Doc("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_composite","part":0},"name":"floor","transform":{"position":[0,0,-0.1]}}""",
            """{"op":"place","piece":{"kind":"spawn-area","side":"marines"},"transform":{"position":[-20,0,0],"scale":[12,12,3]}}""",
            """{"op":"place","piece":{"kind":"spawn-area","side":"chaos"},"transform":{"position":[20,0,0],"scale":[12,12,3]}}""",
            """{"op":"place","piece":{"kind":"blocker","side":"marines"},"transform":{"position":[-12,0,0],"scale":[1,12,6]}}""",
            """{"op":"place","piece":{"kind":"blocker","side":"chaos"},"transform":{"position":[12,0,0],"scale":[1,12,6]}}""");
        var map = CustomMap.Compile(doc, Options());
        var report = MapCheck.Run(map);
        Assert.DoesNotContain(report.Issues, i => i.Code == "spawn-no-floor");
        Assert.True(report.AnnihilationReady, report.ToText());
        var withoutDecor = CustomMap.Compile(doc);
        Assert.Contains(MapCheck.Run(withoutDecor).Issues, i => i.Code == "spawn-no-floor");
    }

    [Fact]
    public void An_imported_model_is_solid_where_it_is_placed()
    {
        string path = Path.Combine(_lab.Dir, "crate.gltf");
        File.WriteAllText(path, CrateGltf());
        var s = _lab.Session(withSm1: false);
        Assert.True(s.Apply(Lab.Op($$"""{"op":"import-asset","path":{{System.Text.Json.JsonSerializer.Serialize(path)}},"id":"crate"}""")).Ok);
        Assert.True(s.Apply(Lab.Op("""{"op":"place","asset":{"source":"custom","id":"crate"},"transform":{"position":[0,10,2]}}""")).Ok);
        var map = CustomMap.Compile(s.Document, Options());
        Assert.Equal(12, map.Decor!.Triangles);
        // a glTF box 2 x 2 x 2 (Y up) centred on its origin: in the document frame it spans z 2 +- 1 above the placement
        Assert.Equal(3f, Down(map, 0, 10)!.Value, 3);
        Assert.Null(Down(map, 1.5f, 10));
        // the copy of the library a client does not have: refused
        var elsewhere = new CompileOptions { Scenes = new FakeScenes(), Library = new MapLibrary(Path.Combine(_lab.Dir, "other-pc")), Strict = true };
        Assert.Equal("missing-asset", Assert.Throws<MapException>(() => CustomMap.Compile(s.Document, elsewhere)).Code);
    }

    static string CrateGltf()
    {
        var bytes = new List<byte>();
        float[] v = [-1, -1, -1, 1, -1, -1, -1, 1, -1, 1, 1, -1, -1, -1, 1, 1, -1, 1, -1, 1, 1, 1, 1, 1];
        foreach (var f in v) bytes.AddRange(BitConverter.GetBytes(f));
        ushort[] t = [0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5];
        int indexOffset = bytes.Count;
        foreach (var i in t) bytes.AddRange(BitConverter.GetBytes(i));
        string b64 = Convert.ToBase64String(bytes.ToArray());
        return $$"""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],
             "buffers":[{"byteLength":{{bytes.Count}},"uri":"data:application/octet-stream;base64,{{b64}}"}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":96},{"buffer":0,"byteOffset":{{indexOffset}},"byteLength":72}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":8,"type":"VEC3","min":[-1,-1,-1],"max":[1,1,1]},{"bufferView":1,"componentType":5123,"count":36,"type":"SCALAR"}]}
            """;
    }

    [Fact]
    public void Two_thousand_placements_compile_in_a_second_or_two()
    {
        var s = _lab.Session();
        var ops = new List<JsonObject>();
        for (int i = 0; i < 2000; i++)
            ops.Add(Lab.Op($$$"""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"transform":{"position":[{{{i % 50 * 6}}},{{{i / 50 * 6}}},0],"rotation":[0,0,{{{i % 4 * 90}}}]}}"""));
        Assert.True(s.Apply(ops).Ok);
        var scenes = new FakeScenes();
        var timer = Stopwatch.StartNew();
        var map = CustomMap.Compile(s.Document, Options(scenes));
        timer.Stop();
        Assert.Equal(2000 * 12, map.Decor!.Triangles);
        Assert.Equal(1, scenes.SceneLoads);                                  // the scene is asked for once per map, not per placement
        Assert.True(timer.Elapsed < TimeSpan.FromMinutes(2), $"took {timer.Elapsed}");   // bounded by the budget, not by time; this only catches a hang
    }

    // ---- hostile files, budgets, determinism (review of 2026-11-19) ----

    static JsonObject WithPrefabs(MapDocument doc, Action<JsonArray> edit)
    {
        var o = (JsonObject)JsonNode.Parse(doc.ToJson())!;
        edit((JsonArray)o["prefabs"]!);
        return o;
    }

    MapDocument PairDoc()
    {
        var s = _lab.Session();
        foreach (var op in new[] { Wall + ""","name":"a"}""", Wall + ""","name":"b","transform":{"position":[10,0,0]}}""",
            """{"op":"group-to-prefab","ids":["o1","o2"],"prefab":"pair","name":"pair","origin":[0,0,0],"replace":true}""" }) Assert.True(s.Apply(Lab.Op(op)).Ok);
        string file = Path.Combine(_lab.Dir, "pair.smmap");
        s.Save(file);   // saving embeds the prefab
        return MapDocument.Parse(File.ReadAllText(file));
    }

    [Fact]
    public void A_prefab_that_contains_itself_is_refused_when_the_file_is_read_and_when_it_comes_from_the_library()
    {
        var doc = PairDoc();
        var cyc = WithPrefabs(doc, a => ((JsonArray)a[0]!["parts"]!).Add(JsonNode.Parse("""{"id":"p9","name":"self","prefab":"pair","transform":{"position":[0,0,0]}}""")));
        Assert.Equal("prefab-cycle", Assert.Throws<MapException>(() => MapDocument.Parse(cyc.ToJsonString())).Code);
        // through two prefabs
        var twoStep = WithPrefabs(doc, a =>
        {
            var b = (JsonObject)JsonNode.Parse(a[0]!.ToJsonString())!;
            b["id"] = "other"; b["parts"] = JsonNode.Parse("""[{"id":"p1","name":"x","prefab":"pair","transform":{"position":[0,0,0]}}]""");
            ((JsonArray)a[0]!["parts"]!).Add(JsonNode.Parse("""{"id":"p9","name":"loop","prefab":"other","transform":{"position":[0,0,0]}}"""));
            a.Add(b);
        });
        Assert.Equal("prefab-cycle", Assert.Throws<MapException>(() => MapDocument.Parse(twoStep.ToJsonString())).Code);
        // a library prefab (not in the file) that contains itself: the compiler refuses it too
        var plain = (JsonObject)JsonNode.Parse(doc.ToJson())!;
        var prefab = (JsonObject)JsonNode.Parse(plain["prefabs"]![0]!.ToJsonString())!;
        ((JsonArray)prefab["parts"]!).Add(JsonNode.Parse("""{"id":"p9","name":"self","prefab":"pair","transform":{"position":[0,0,0]}}"""));
        Directory.CreateDirectory(_lab.Library.PrefabsDir);
        File.WriteAllText(_lab.Library.PrefabPath("pair"), prefab.ToJsonString());
        plain.Remove("prefabs");
        var noEmbedded = MapDocument.Parse(plain.ToJsonString());
        Assert.Equal("prefab-cycle", Assert.Throws<MapException>(() => CustomMap.Compile(noEmbedded, Options())).Code);
    }

    [Fact]
    public void A_fan_out_bomb_of_nested_prefabs_is_refused_with_too_big_and_fast()
    {
        var doc = PairDoc();
        var o = WithPrefabs(doc, a =>
        {
            for (int k = 1; k <= 14; k++)
            {
                var f = (JsonObject)JsonNode.Parse(a[0]!.ToJsonString())!;
                f["id"] = $"f{k}"; f["name"] = $"f{k}";
                string child = k == 1 ? "pair" : $"f{k - 1}";
                f["parts"] = JsonNode.Parse("[" + string.Join(",", Enumerable.Range(1, 6).Select(i => $$$"""{"id":"p{{{i}}}","name":"x","prefab":"{{{child}}}","transform":{"position":[{{{i}}},0,0]}}""")) + "]");
                a.Add(f);
            }
        });
        o["objects"]![0]!["prefab"] = "f14";   // the one instance now stands for the top of the pyramid: 6^14 parts
        var bomb = MapDocument.Parse(o.ToJsonString());
        var timer = Stopwatch.StartNew();
        Assert.Equal("too-big", Assert.Throws<MapException>(() => CustomMap.Compile(bomb, Options())).Code);
        Assert.True(timer.Elapsed < TimeSpan.FromMinutes(2), $"took {timer.Elapsed}");   // the work is bounded by the budget, not by time; this only catches a hang
        // the editor's lenient compile does not throw: it leaves the decoration out and the check says why
        var lenient = CustomMap.Compile(bomb, Options(strict: false));
        Assert.Equal(0, lenient.Decor!.Placements.Count);
        Assert.Contains(MapCheck.Run(lenient).Issues, i => i.Code == "decoration-missing");
    }

    [Fact]
    public void Twenty_thousand_placements_of_a_big_definition_are_refused_with_too_big_and_fast()
    {
        var one = Doc("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_big"}}""");
        var o = (JsonObject)JsonNode.Parse(one.ToJson())!;
        var objects = (JsonArray)o["objects"]!;
        var template = objects[0]!.ToJsonString();
        objects.Clear();
        for (int i = 1; i <= 20000; i++)
        {
            var n = (JsonObject)JsonNode.Parse(template)!;
            n["id"] = $"o{i}"; n["name"] = $"big {i}";
            objects.Add(n);
        }
        o["nextId"] = 20001;
        var doc = MapDocument.Parse(o.ToJsonString());
        var timer = Stopwatch.StartNew();
        var e = Assert.Throws<MapException>(() => CustomMap.Compile(doc, Options()));
        Assert.Equal("too-big", e.Code);
        Assert.True(timer.Elapsed < TimeSpan.FromMinutes(2), $"took {timer.Elapsed}");   // bounded by the budget, not by time; this only catches a hang
    }

    [Fact]
    public void A_scene_source_that_throws_something_odd_is_a_clean_refusal_not_a_crash()
    {
        var doc = Doc(Wall + "}");
        var e = Assert.Throws<MapException>(() => CustomMap.Compile(doc, Options(new FakeScenes { Explode = true })));
        Assert.Equal("bad-file", e.Code);
        Assert.Contains("boom", e.Message);
        // lenient: noted, and the check refuses to call the map ready
        var lenient = CustomMap.Compile(doc, Options(new FakeScenes { Explode = true }, strict: false));
        Assert.Contains(MapCheck.Run(lenient).Issues, i => i.Code == "decoration-missing");
    }

    [Fact]
    public void A_definition_with_no_triangles_is_a_refusal_and_a_mirrored_placement_is_still_solid()
    {
        var empty = Doc("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"}}""");
        var json = empty.ToJson().Replace("def_wall", "def_empty");
        Assert.Equal("empty-sm1-definition", Assert.Throws<MapException>(() => CustomMap.Compile(MapDocument.Parse(json), Options())).Code);
        var mirrored = Doc(Wall + ""","transform":{"position":[0,0,0],"scale":[-1,1,1]}}""");
        var map = CustomMap.Compile(mirrored, Options());
        Assert.Equal(3f, Down(map, 1, 0)!.Value, 3);                        // two-sided collision: mirroring needs no winding flip
        Assert.Equal(3f, Down(map, -1, 0)!.Value, 3);
    }

    [Fact]
    public void The_digest_does_not_move_with_last_bit_float_noise_but_does_with_the_content()
    {
        var a = Doc(Wall + ""","transform":{"position":[10.123456,20.654321,5],"rotation":[0,0,33.3333]}}""");
        var b = MapDocument.Parse(a.ToJson().Replace("10.123456", "10.1234561").Replace("33.3333", "33.33330001"));
        var one = CustomMap.Compile(a, Options()); var two = CustomMap.Compile(b, Options());
        Assert.Equal(one.Decor!.Digest(), two.Decor!.Digest());
        Assert.NotEqual(one.Decor.Digest(), CustomMap.Compile(a, Options(new FakeScenes { WallHeight = 3.25f })).Decor!.Digest());
        // positions are on the 1/4096 m grid before they reach the collision
        Assert.All(one.Decor.Vertices, v => { Assert.Equal(v.X, MathF.Round(v.X * 4096f) / 4096f); Assert.Equal(v.Z, MathF.Round(v.Z * 4096f) / 4096f); });
        Assert.Equal(DecorGeometry.Quantise(new Vector3(1.00001f, 2.00001f, 3f)), DecorGeometry.Quantise(new Vector3(1.0000101f, 2.0000099f, 3f)));
    }

    [Fact]
    public void The_editors_cache_keeps_the_decoration_while_only_pieces_change()
    {
        var scenes = new FakeScenes();
        var cache = new DecorCache();
        CompileOptions Opt() => new() { Scenes = scenes, Library = _lab.Library, Cache = cache, CacheTag = "x" };
        var s = _lab.Session();
        Assert.True(s.Apply(Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"}}""")).Ok);
        Assert.True(s.Apply(Lab.Op("""{"op":"place","piece":{"kind":"box"},"transform":{"scale":[4,4,1]}}""")).Ok);
        var first = CustomMap.Compile(s.Document, Opt());
        Assert.True(s.Apply(Lab.Op("""{"op":"transform","id":"o2","position":[5,5,0]}""")).Ok);
        var second = CustomMap.Compile(s.Document, Opt());
        Assert.Same(first.Decor, second.Decor);
        Assert.Equal(1, scenes.SceneLoads);
        Assert.True(s.Apply(Lab.Op("""{"op":"transform","id":"o1","position":[1,1,0]}""")).Ok);
        Assert.NotSame(first.Decor, CustomMap.Compile(s.Document, Opt()).Decor);
    }
    [Fact]
    public void Quick_edits_make_one_background_build_and_a_stale_result_is_never_applied()
    {
        var cache = new DecorCache();
        var gate = new ManualResetEventSlim(false);
        var started = new List<string>();
        var ready = new List<string>();
        using var worker = new DecorWorker(cache, TimeSpan.FromMilliseconds(80), a => a(), () => ready.Add("ready"), (doc, _) =>
        {
            lock (started) started.Add(doc.Name);
            if (doc.Name == "slow") gate.Wait(TimeSpan.FromSeconds(20));
            return new DecorResult();
        });
        var o = new CompileOptions();
        // ten edits within the debounce: one build, for the last
        for (int i = 0; i < 10; i++) worker.Request(MapDocument.Create($"edit{i}"), o, $"k{i}");
        SpinWait.SpinUntil(() => ready.Count == 1, TimeSpan.FromSeconds(20));
        Assert.Equal(["edit9"], started.ToArray());
        Assert.Equal(1, worker.Builds);
        Assert.True(cache.TryGet("k9", out _));
        Assert.False(cache.TryGet("k0", out _));
        // an edit while a build runs: the running build's result is dropped, the newest is built
        worker.Request(MapDocument.Create("slow"), o, "slow");
        SpinWait.SpinUntil(() => { lock (started) return started.Contains("slow"); }, TimeSpan.FromSeconds(20));
        worker.Request(MapDocument.Create("newest"), o, "newest");
        gate.Set();
        SpinWait.SpinUntil(() => ready.Count == 2 && !worker.Pending, TimeSpan.FromSeconds(20));
        Assert.False(cache.TryGet("slow", out _));
        Assert.True(cache.TryGet("newest", out _));
        Assert.Equal(2, ready.Count);
        Assert.False(worker.Pending);
    }
}