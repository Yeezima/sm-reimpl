using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SmMapmaker;
using SmSim;
using Xunit;

namespace SmMapmaker.Tests;

/// <summary>
/// Contract v2 (after the M0 review): the volume's boundary, winding and segment rules and the M2 helpers, image repair, the lowest-format rule and backups, pack hardening, the capture-point
/// readout, mirrored drawing, and golden pins of the Classic-path collision and a Seize Ground run taken before M2 touches the simulation. Synthetic data only.
/// </summary>
public sealed class EditContractV2Tests
{
    static OpResult Run(MapSession s, string json)
    {
        var r = s.Apply(json);
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Code + ": " + e.Message)));
        return r;
    }

    static MeshVolume Cube(Vector3 offset = default)
    {
        var (v, i, _) = EditMesh.Stock(MeshTopology.Box).Triangulate();
        return new MeshVolume("cube", v.Select(p => p + offset).ToArray(), i);
    }

    // ---- MeshVolume ----

    [Fact]
    public void Boundary_points_follow_the_half_open_rule_on_every_face_edge_and_corner()
    {
        var vol = Cube();
        float[] xs = [-0.5f, 0f, 0.5f], zs = [0f, 0.5f, 1f];
        foreach (var x in xs) foreach (var y in xs) foreach (var z in zs)
        {
            bool expected = x < 0.5f && y > -0.5f && z < 1f;   // -X, +Y and the bottom are in; +X, -Y and the top are out
            Assert.True(expected == vol.Contains(new Vector3(x, y, z)), $"({x}, {y}, {z}) should be {(expected ? "in" : "out")}");
        }
        // two cubes side by side never both hold their shared face
        var right = Cube(Vector3.UnitX);
        foreach (var y in new[] { -0.25f, 0f, 0.5f }) Assert.True(vol.Contains(new Vector3(0.5f, y, 0.5f)) ^ right.Contains(new Vector3(0.5f, y, 0.5f)));
        Assert.False(vol.Contains(new Vector3(float.NaN, 0, 0.5f)));
        Assert.False(vol.Contains(new Vector3(0, float.PositiveInfinity, 0.5f)));
    }

    [Fact]
    public void A_self_overlapping_mesh_is_inside_where_any_part_covers_the_point()
    {
        var a = Cube(); var b = Cube(new Vector3(0.5f, 0, 0));
        var both = new MeshVolume("two", [.. a.Vertices, .. b.Vertices], [.. a.Indices, .. b.Indices.Select(i => i + a.Vertices.Length)]);
        Assert.True(both.Contains(new Vector3(0.25f, 0, 0.5f)));   // in the overlap (parity would call it outside)
        Assert.True(both.Contains(new Vector3(-0.25f, 0, 0.5f)));
        Assert.True(both.Contains(new Vector3(0.8f, 0, 0.5f)));
        Assert.False(both.Contains(new Vector3(1.2f, 0, 0.5f)));
    }

    [Fact]
    public void A_fast_mover_crossing_a_thin_volume_is_caught_by_the_segment_test()
    {
        var (v, i, _) = EditMesh.Stock(MeshTopology.Box).Triangulate();
        var slab = new MeshVolume("slab", v.Select(p => p with { Z = p.Z * 0.1f }).ToArray(), i);   // 1 x 1 x 0.1 m
        Assert.True(slab.Crosses(new Vector3(0, 0, 5), new Vector3(0, 0, -5)));          // straight through in one tick
        Assert.True(slab.Crosses(new Vector3(-3, 0.2f, 0.05f), new Vector3(3, 0.2f, 0.05f)));
        Assert.True(slab.Crosses(new Vector3(0, 0, 0.05f), new Vector3(0, 0, 0.05f)));   // a point segment is Contains
        Assert.False(slab.Crosses(new Vector3(2, 0, 5), new Vector3(2, 0, -5)));
        Assert.False(slab.Crosses(new Vector3(0, 0, 0.5f), new Vector3(0, 0, 3)));
        Assert.False(slab.Crosses(new Vector3(float.NaN, 0, 5), new Vector3(0, 0, -5)));
    }

    [Fact]
    public void The_runtime_helpers_find_an_inside_point_stretch_up_push_out_and_outline()
    {
        // two cubes far apart as one shape: the box centre is outside, the inside point is not
        var a = Cube(); var b = Cube(new Vector3(10, 0, 0));
        var apart = new MeshVolume("apart", [.. a.Vertices, .. b.Vertices], [.. a.Indices, .. b.Indices.Select(i => i + a.Vertices.Length)]);
        Assert.False(apart.Contains(apart.BoundsCenter));
        var inside = apart.InsidePoint();
        Assert.NotNull(inside);
        Assert.True(apart.Contains(inside!.Value));

        var tall = Cube().ReachingAbove(25f);
        Assert.Equal(0f, tall.Min.Z);
        Assert.Equal(25f, tall.Max.Z);
        Assert.True(tall.Contains(new Vector3(0, 0, 20)));
        Assert.Same(tall, tall.ReachingAbove(10f));

        var cube = Cube();
        var pushed = cube.PushOut(new Vector3(0.3f, 0.1f, 0), 0.4f, 1.9f);
        Assert.Equal(0.5f + 0.4f + 0.01f, pushed.X, 4);
        Assert.False(cube.Crosses(pushed, pushed + new Vector3(0, 0, 1.9f)));
        Assert.Equal(new Vector3(3, 0, 0), cube.PushOut(new Vector3(3, 0, 0), 0.4f, 1.9f));   // outside: unchanged

        Assert.Equal(12, cube.FeatureEdges().Count);   // a box's outline: its 12 edges, no face diagonals
        Assert.NotEqual(cube.Hash(1), Cube(Vector3.UnitZ).Hash(1));
    }

    // ---- images ----

    static string Png(string dir, string name, int seed, int side = 256)
    {
        var d = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), (uint)side);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), (uint)side);
        d[40] = (byte)seed;
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, d);
        return path;
    }

    static string AddImage(string path) => """{"op":"image-add","path":@P@}""".Replace("@P@", JsonValue.Create(path).ToJsonString());

    [Fact]
    public void Re_adding_an_image_whose_library_copy_is_lost_repairs_it_without_a_new_undo_step()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        var png = Png(lab.Dir, "a.png", 1);
        var id = Run(s, AddImage(png)).CreatedImages[0];
        var lib = Path.Combine(MapImages.LibraryDir(lab.Library), id + ".png");
        Assert.True(File.Exists(lib));
        File.Delete(lib);
        int depth = s.UndoDepth;
        var r = Run(s, AddImage(png));
        Assert.Equal(id, r.CreatedImages[0]);
        Assert.True(File.Exists(lib));          // written back
        Assert.Equal(depth, s.UndoDepth);       // the document did not change
        Assert.Equal("bad-image", s.Apply("""{"op":"image-add","path":"bad\u0000:name?.png"}""").Errors[0].Code);   // an impossible path is a plain refusal
    }

    [Fact]
    public void The_decoded_pixel_budget_and_the_listed_size_are_enforced()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        for (int k = 0; k < 4; k++) Run(s, AddImage(Png(lab.Dir, $"big{k}.png", k + 1, 4096)));   // 4 x 16 Mpx = the whole budget
        Assert.Equal("too-big", s.Apply(AddImage(Png(lab.Dir, "one-more.png", 9, 16))).Errors[0].Code);
        // a copy whose bytes hash right but whose listed size is wrong is not used
        var img = s.Document.Images[0];
        var wrong = img with { Width = 2048 };
        Assert.NotNull(MapImages.Read(img, null, lab.Library));
        Assert.Null(MapImages.Read(wrong, null, lab.Library));
    }

    // ---- format ----

    [Fact]
    public void An_unedited_map_is_written_as_format_3_and_a_format_3_file_is_kept_as_bak_v3_when_an_edit_needs_format_4()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"box"},"transform":{"scale":[4,2,1]}}""");
        var file = Path.Combine(lab.Dir, "v3.smmap");
        s.Save(file);
        Assert.Equal(3, MapUpgrade.VersionOfFile(file));
        var original = File.ReadAllText(file);
        var stamp = File.GetLastWriteTimeUtc(file);

        var opened = MapSession.Load(file, lab.Library);
        Assert.Equal(original, File.ReadAllText(file));                // opening never rewrites a format 3 file
        Assert.Empty(Directory.GetFiles(lab.Dir, "v3.smmap.bak-*"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));

        Run(opened, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[5],"move":[1,0,0]}""");
        opened.Save();
        Assert.Equal(4, MapUpgrade.VersionOfFile(file));
        Assert.Equal(original, File.ReadAllText(file + ".bak-v3"));     // the copy a format-3 build can still open
        Assert.Equal(file + ".bak-v3", opened.LastBackup);
        Run(opened, """{"op":"reset-mesh","ids":["o1"]}""");
        opened.Save();
        Assert.Equal(3, MapUpgrade.VersionOfFile(file));                // back to the lowest format that holds it
        Assert.Null(opened.LastBackup);
    }

    [Fact]
    public void Set_mesh_needs_its_mesh_key_and_null_resets()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"box"}}""");
        Run(s, """{"op":"edit-mesh","id":"o1","mode":"vertex","ids":[7],"move":[0,0,1]}""");
        Assert.Equal("bad-op", s.Apply("""{"op":"set-mesh","id":"o1"}""").Errors[0].Code);
        Assert.NotNull(s.Document.Get("o1").Mesh);
        Run(s, """{"op":"set-mesh","id":"o1","mesh":null}""");
        Assert.Null(s.Document.Get("o1").Mesh);
    }

    // ---- packs ----

    static string Doc(params MapImage[] images)
    {
        var d = MapDocument.Create("packed");
        d.Images.AddRange(images);
        return d.ToJson();
    }

    static (MapImage Info, byte[] Data) Image(int seed)
    {
        var d = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), 64);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), 64);
        d[40] = (byte)seed;
        return (MapImage.Inspect(d, $"img{seed}.png"), d);
    }

    static string Zip(string dir, string name, params (string Entry, byte[] Data)[] entries)
    {
        var path = Path.Combine(dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (e, data) in entries) { using var w = zip.CreateEntry(e).Open(); w.Write(data); }
        return path;
    }

    static byte[] Utf8(string s) => new System.Text.UTF8Encoding(false).GetBytes(s);

    static string Refused(string pack, string into) => Assert.Throws<MapException>(() => MapPack.Unpack(pack, into)).Code;

    [Fact]
    public void Unpacking_refuses_every_malformed_pack_and_writes_nothing()
    {
        using var lab = new Lab();
        var (a, ad) = Image(1);
        var (b, bd) = Image(2);
        string into = Path.Combine(lab.Dir, "into");
        Assert.Equal("bad-pack", Refused(Zip(lab.Dir, "two.smpack", ("a.smmap", Utf8(Doc())), ("b.smmap", Utf8(Doc()))), into));
        Assert.Equal("bad-pack", Refused(Zip(lab.Dir, "nested.smpack", ("m.smmap", Utf8(Doc())), ($"images/x/{a.FileName}", ad)), into));
        Assert.Equal("bad-pack", Refused(Zip(lab.Dir, "backslash.smpack", ("m.smmap", Utf8(Doc(a))), ($"images\\{a.FileName}", ad)), into));
        Assert.Equal("bad-pack", Refused(Zip(lab.Dir, "stream.smpack", ("m.smmap:evil", Utf8(Doc()))), into));
        Assert.Equal("bad-pack", Refused(Zip(lab.Dir, "wronghash.smpack", ("m.smmap", Utf8(Doc(a))), ($"images/{a.FileName}", bd)), into));
        Assert.Equal("missing-image", Refused(Zip(lab.Dir, "missing.smpack", ("m.smmap", Utf8(Doc(a, b))), ($"images/{a.FileName}", ad)), into));
        var notZip = Path.Combine(lab.Dir, "not.smpack"); File.WriteAllText(notZip, "not a zip");
        Assert.Equal("bad-pack", Refused(notZip, into));
        // an image entry beyond the size limit (its listed id is only a name here: the size is refused first)
        var big = a with { Bytes = 1 };
        Assert.Equal("too-big", Refused(Zip(lab.Dir, "big.smpack", ("m.smmap", Utf8(Doc(big))), ($"images/{big.FileName}", new byte[MapImage.MaxBytes + 1])), into));
        Assert.False(Directory.Exists(into));

        // a good pack (with one extra image the map does not list, which is skipped), then the same again onto the existing map
        var good = Zip(lab.Dir, "good.smpack", ("m.smmap", Utf8(Doc(a))), ($"images/{a.FileName}", ad), ($"images/{b.FileName}", bd));
        var map = MapPack.Unpack(good, into);
        Assert.True(File.Exists(Path.Combine(into, "images", a.FileName)));
        Assert.False(File.Exists(Path.Combine(into, "images", b.FileName)));
        Assert.Equal("file-exists", Refused(good, into));
        Assert.Equal(a.Id, MapDocument.Parse(File.ReadAllText(map)).Images.Single().Id);
    }

    // ---- readout, check, drawing ----

    [Fact]
    public void An_unedited_capture_point_reads_as_the_circle_it_plays_and_the_check_says_so()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"box"},"transform":{"position":[0,0,-1],"scale":[40,40,1]}}""");
        Run(s, """{"op":"place","piece":{"kind":"capture-point"},"transform":{"scale":[8,6,4]}}""");
        Assert.Equal(new Vector3(8, 8, 4), MeshDims.Of(s.Document.Get("o2")));
        var report = MapCheck.Run(CustomMap.Compile(s.Document), null, null);
        Assert.Contains(report.Issues, i => i.Code == "point-round");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"vertex","ids":[0],"move":[0,0,0.5]}""");   // edited: its own shape from now on (the 16-gon of 8 x 6)
        Assert.Equal(new Vector3(8, 6, 4), MeshDims.Of(s.Document.Get("o2")));
        Assert.DoesNotContain(MapCheck.Run(CustomMap.Compile(s.Document), null, null).Issues, i => i.Code == "point-round");
    }

    [Fact]
    public void A_mirrored_piece_draws_with_outward_normals()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"box"},"transform":{"position":[3,0,0],"scale":[-4,2,1]}}""");
        var p = CustomMap.Compile(s.Document).Pieces.Single();
        var r = p.Render();
        var centre = new Vector3(3, 0, 0.5f);
        for (int t = 0; t < r.Positions.Length / 3; t++)
        {
            var mid = (r.Positions[3 * t] + r.Positions[3 * t + 1] + r.Positions[3 * t + 2]) / 3;
            Assert.True(Vector3.Dot(r.Normals[3 * t], mid - centre) > 0, $"triangle {t} faces inward");
            var n = Vector3.Normalize(Vector3.Cross(r.Positions[3 * t + 1] - r.Positions[3 * t], r.Positions[3 * t + 2] - r.Positions[3 * t]));
            Assert.True(Vector3.Dot(n, r.Normals[3 * t]) > 0.99f, "the winding agrees with the normal");
        }
    }

    // ---- golden pins (before M2 changes the simulation) ----

    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static ClassLoadout Tac(string name, Team side) => new(name, 0, 1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };

    static MapDocument GoldenMap()
    {
        var s = MapSession.New("Golden", new MapLibrary(Path.Combine(Path.GetTempPath(), "smmapmaker-test-lib")));
        Assert.True(s.Apply(MapTemplate.Find("arena")!.Ops()).Ok);
        Run(s, """{"op":"place","piece":{"kind":"move-wall","shape":"cylinder"},"transform":{"position":[5,9,0],"rotation":[0,0,17],"scale":[2,2,3]}}""");
        Run(s, """{"op":"place","piece":{"kind":"cover"},"transform":{"position":[-6,7,0],"rotation":[0,0,33],"scale":[1.2,3,1.5]}}""");
        Run(s, """{"op":"place","piece":{"kind":"kill-zone"},"transform":{"position":[0,30,-3],"scale":[6,4,2]}}""");
        return s.Document;
    }

    /// <summary>
    /// The unedited path's collision world and a Seize Ground run on it, pinned. Captured with this branch, whose simulation and content code is main d4db1299's (the only SmSim change is the new,
    /// unused <see cref="MeshVolume"/> file) and whose unedited custom-map compile is bit-identical to main's (<c>EditContractTests</c>): M2 must keep these values.
    /// </summary>
    [Fact]
    public void Golden_collision_hash_and_seize_ground_run_of_an_unedited_map()
    {
        var map = CustomMap.Compile(GoldenMap());
        var collision = map.ToCollisionWorld();
        var config = new SimConfig
        {
            Weapons = [Rifle, Sidearm], Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)],
            HealthMax = 40f, ShieldMax = 0f, HealthRegenDelay = 1e6f, RespawnTime = 60f,
        };
        var points = map.Content.SimControlPoints();
        var a = points[0];
        var floorA = a.Center - new Vector3(0, 0, a.HalfHeight);
        var w = new World(config, collision, map.Content.SpawnPoints())
        {
            Rules = MatchRules.For(GameMode.SeizeGround) with { TimeLimit = 0f }, ControlPoints = points,
            StartAreas = [new StartArea(Team.SpaceMarines, floorA + new Vector3(0, 0, 1), new Vector3(1, 1, 2), 0f), new StartArea(Team.Chaos, map.Content.StartAreas[1].Position, new Vector3(1, 1, 2), MathF.PI)],
        };
        w.Spawn(1, 0); w.Spawn(2, 1);
        for (int i = 0; i < 120; i++) w.Step(id => new PlayerInput(0, i % 40 < 20 ? 1 : 0, 0, w.Players[id].Yaw, InputButtons.None));
        var p1 = w.Players[1].Position;
        string got = $"{collision.ContentHash:X16} {w.Points[0].Owner} {w.Points[0].Side} {w.Points[0].Progress} {BitConverter.SingleToUInt32Bits(p1.X):X8} {BitConverter.SingleToUInt32Bits(p1.Y):X8} {BitConverter.SingleToUInt32Bits(p1.Z):X8}";
        Assert.True(GoldenValues == got, "golden: " + got);
    }

    const string GoldenValues = "5B1F2D25D31287EA None SpaceMarines 45 BF22A2C4 41093958 38D1B087";
}
