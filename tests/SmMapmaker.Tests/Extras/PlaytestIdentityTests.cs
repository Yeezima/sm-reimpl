using System.Text.Json.Nodes;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>
/// Owner's pass 3 (items 1 and 13): a play test plays exactly the chosen file, and an older or shared map file (a tester's "My Map": SM1 placements and prefab instances, a few pieces,
/// prefabs only in its maker's library) opens in the current Mapmaker. Synthetic documents only; the shape is that of the tester's file.
/// </summary>
public sealed class PlaytestIdentityTests : IDisposable
{
    readonly Lab _lab = new();
    public void Dispose() => _lab.Dispose();

    /// <summary>A map in the shape of the tester's file: named "My Map" (the default name), two pieces, SM1 placements, instances of a prefab that exists only in the maker's library.</summary>
    string MakeTesterShapedMap(string file, MapLibrary maker, bool asVersion1)
    {
        var s = MapSession.New("My Map", maker, _lab.Sm1);
        Assert.True(s.Apply(Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"wall a"}""")).Ok);
        Assert.True(s.Apply(Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"wall b","transform":{"position":[4,0,0]}}""")).Ok);
        Assert.True(s.Apply(Lab.Op("""{"op":"group-to-prefab","ids":["o1","o2"],"prefab":"girder_join-group","name":"girder join group","origin":"first","replace":true}""")).Ok);
        Assert.True(s.Apply(Lab.Op("""{"op":"place","prefab":"girder_join-group","name":"girder_join-group","transform":{"position":[10,0,0]}}""")).Ok);
        Assert.True(s.Apply(Lab.Op("""{"op":"place","piece":{"kind":"box"},"name":"floor","transform":{"position":[0,0,-1],"scale":[40,40,1]}}""")).Ok);
        string path = Path.Combine(_lab.Dir, file);
        s.Save(path);
        if (asVersion1)
        {
            // what an older Mapmaker wrote: formatVersion 1 and no prefab definitions in the file
            var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            o["formatVersion"] = 1;
            o.Remove("prefabs");
            File.WriteAllText(path, CanonicalJson.Write(o));
        }
        return path;
    }

    [Fact]
    public void An_older_map_opens_is_backed_up_then_upgraded_and_the_users_data_survives()
    {
        string path = MakeTesterShapedMap("old.smmap", _lab.Library, asVersion1: true);
        string original = File.ReadAllText(path);
        Assert.Equal(1, MapUpgrade.VersionOfFile(path));
        // reading it is the same everywhere: in memory the file is upgraded, deterministically (server, client and editor get one hash)
        var a = CustomMaps.Load(path, new CompileOptions()); var b = CustomMaps.Load(path, new CompileOptions());   // no content source: this test is about the file upgrade
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(1, a.Document.LoadedVersion);

        var s = MapSession.Load(path, _lab.Library, _lab.Sm1);
        Assert.Contains(s.OpenNotes, n => n.Contains("older Mapmaker") && n.Contains("old.smmap.bak-v1"));
        Assert.Equal(original, File.ReadAllText(path + ".bak-v1"));                    // the original, byte for byte
        Assert.Equal(MapDocument.FormatVersion, MapUpgrade.VersionOfFile(path));      // the file itself is now current
        Assert.Equal(3, s.Document.Objects.Count);                                    // nothing was lost
        Assert.False(s.Dirty);
        // the backup is never a map to search for or play, and a second upgrade never replaces it
        Assert.DoesNotContain(CustomMaps.LocateAll("cm_my_map"), f => f.Contains(".bak-"));
        File.WriteAllText(path, original);
        MapSession.Load(path, _lab.Library, _lab.Sm1);
        Assert.True(File.Exists(path + ".bak-v1-2"));
        Assert.Equal(original, File.ReadAllText(path + ".bak-v1"));
    }

    [Fact]
    public void A_map_newer_than_this_build_is_refused_in_plain_words_and_a_missing_step_is_named()
    {
        var o = JsonNode.Parse(MapDocument.Create("x").ToJson())!.AsObject();
        o["formatVersion"] = MapDocument.FormatVersion + 1;
        var ex = Assert.Throws<MapException>(() => MapDocument.Parse(o.ToJsonString()));
        Assert.Contains("newer Mapmaker", ex.Message);
        var gap = Assert.Throws<MapException>(() => MapUpgrade.ToCurrent(new JsonObject(), 1, 3, new Dictionary<int, Func<JsonObject, JsonObject>> { [1] = x => x }));
        Assert.Contains("no step from 2 to 3", gap.Message);
        // a real step runs in order and stamps the version
        var steps = new Dictionary<int, Func<JsonObject, JsonObject>> { [1] = x => { x["a"] = 1; return x; }, [2] = x => { x["b"] = (int)x["a"]! + 1; return x; } };
        var up = MapUpgrade.ToCurrent(new JsonObject(), 1, 3, steps);
        Assert.Equal(2, (int)up["b"]!);
        Assert.Equal(3, (int)up["formatVersion"]!);
    }

    [Fact]
    public void A_shared_map_brings_its_prefabs_so_the_instances_are_not_unknown_in_another_library()
    {
        string path = MakeTesterShapedMap("shared.smmap", _lab.Library, asVersion1: false);
        var doc = MapDocument.Parse(File.ReadAllText(path));
        Assert.Contains(doc.EmbeddedPrefabs, p => p.Id == "girder_join-group");

        var other = new MapLibrary(Path.Combine(_lab.Dir, "other-lib"));
        // the tester's case: opening the file where the prefab is not in the library used to be an unknown-prefab error on every instance
        Assert.False(doc.Validate(other).Ok);
        Assert.Contains(doc.Validate(other).Errors, e => e.Code == "unknown-prefab");
        var s = MapSession.Load(path, other, _lab.Sm1);
        Assert.Contains(s.OpenNotes, n => n.Contains("girder_join-group") && n.Contains("added to your library"));
        Assert.True(s.Validate().Ok, string.Join("; ", s.Validate().Errors.Select(e => e.Message)));
        Assert.Equal(1, other.ListPrefabs().Count);

        // a library prefab of the same id is never overwritten by the file's copy
        var third = new MapLibrary(Path.Combine(_lab.Dir, "third-lib"));
        third.SavePrefab(doc.EmbeddedPrefabs[0] with { Name = "My own girder" });
        var s3 = MapSession.Load(path, third, _lab.Sm1);
        Assert.Contains(s3.OpenNotes, n => n.Contains("differs"));
        Assert.Equal("My own girder", third.GetPrefab("girder_join-group")!.Name);
    }

    [Fact]
    public void Maps_that_share_a_name_are_told_apart_by_file_and_the_newest_is_the_guess_without_one()
    {
        string dir = Path.Combine(_lab.Dir, "maps");
        Directory.CreateDirectory(dir);
        string oldest = Path.Combine(dir, "my-map.smmap"), newest = Path.Combine(dir, "my-map-3.smmap"), alone = Path.Combine(dir, "other.smmap");
        File.WriteAllText(oldest, MapDocument.Create("My Map").ToJson());
        File.WriteAllText(newest, MapDocument.Parse(MapDocument.Create("My Map").ToJson()).ToJson().Replace("\"nextId\": 1", "\"nextId\": 2"));
        File.WriteAllText(alone, MapDocument.Create("Solo").ToJson());
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow);
        Environment.SetEnvironmentVariable("SM_MAPS_DIR", dir);
        try
        {
            // the alphabetically first file used to win; now it is the newest save (the map just tested), and a client is told when it is a guess
            Assert.Equal(new[] { newest, oldest }, CustomMaps.LocateAll("cm_my_map").Where(f => f.StartsWith(dir)).ToArray());
            Assert.Equal(newest, CustomMaps.LocateAll("cm_my_map").First(f => f.StartsWith(dir)));
            Assert.Contains(oldest, CustomMaps.SameNameAs(newest));
            Assert.Empty(CustomMaps.SameNameAs(alone));
            // a file named with --custom-map is the file; a path that does not exist is an error, never a lookup by name (which played another map)
            Assert.Equal(CustomMap.Compile(MapDocument.Parse(File.ReadAllText(oldest))).Hash, CustomMaps.LoadForPlay("cm_my_map", oldest).Hash);
            var missing = Assert.Throws<MapException>(() => CustomMaps.LoadForPlay("cm_my_map", Path.Combine(dir, "typo.smmap")));
            Assert.Equal("missing-map", missing.Code);
            Assert.Contains("typo.smmap", missing.Message);
        }
        finally { Environment.SetEnvironmentVariable("SM_MAPS_DIR", null); }
    }

    [Fact]
    public void A_tester_shaped_map_plays_its_SM1_pieces_as_solid_and_a_PC_without_the_content_refuses()
    {
        // The tester's file: SM1 placements and prefab instances plus a few pieces. Since pass 3 the SM1 pieces are drawn and solid in play (docs/mapmaker.md "SM1 pieces in play"); this PC (the tests have none
        // of the install's maps) is refused in plain words, never given a world without them.
        string path = MakeTesterShapedMap("shape.smmap", _lab.Library, asVersion1: false);
        var refused = Assert.Throws<MapException>(() => CustomMaps.Load(path, new CompileOptions { Strict = true }));
        Assert.Equal("missing-install", refused.Code);
        Assert.Contains("mp_fake", refused.Message);
        var map = CustomMaps.Load(path, new CompileOptions { Scenes = new FakeScenes(), Library = _lab.Library, Strict = true });
        Assert.Equal(5, map.SolidCount);   // the floor piece and the two instances of the wall pair (2 parts each)
        Assert.Equal("cm_my_map", map.Name);
        Assert.Equal(3, map.Document.Objects.Count);
    }
}
