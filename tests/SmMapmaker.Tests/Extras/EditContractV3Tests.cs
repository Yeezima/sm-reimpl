using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;
using SmMapmaker;
using SmSim;
using Xunit;

namespace SmMapmaker.Tests;

/// <summary>Contract v3 (after the focused re-review of v2): exact flat faces, record heights from the gridded volume, the stacked door extension, outline push-out, pack names, and the lows.</summary>
public sealed class EditContractV3Tests
{
    static OpResult Run(MapSession s, string json)
    {
        var r = s.Apply(json);
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Code + ": " + e.Message)));
        return r;
    }

    /// <summary>The box topology placed by an off-grid size and offset (no 1/4096 rounding).</summary>
    static MeshVolume Box(Vector3 size, Vector3 at, Func<int, Vector3, Vector3>? bend = null)
    {
        var (v, i, _) = EditMesh.Stock(MeshTopology.Box).Triangulate();
        return new MeshVolume("box", v.Select((p, k) => { var q = p * size + at; return bend is null ? q : bend(k, q); }).ToArray(), i);
    }

    [Fact]
    public void Flat_bottoms_are_inside_and_flat_tops_outside_exactly_at_any_height_and_position()
    {
        var rng = new Random(1125);
        for (int b = 0; b < 20; b++)
        {
            var size = new Vector3(0.3f + (float)rng.NextDouble() * 7, 0.3f + (float)rng.NextDouble() * 7, 0.3f + (float)rng.NextDouble() * 5);
            var at = new Vector3((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 30 + 0.123));
            var vol = Box(size, at);
            for (int k = 0; k < 100; k++)
            {
                float x = vol.Min.X + (vol.Max.X - vol.Min.X) * (0.01f + 0.98f * (float)rng.NextDouble());
                float y = vol.Min.Y + (vol.Max.Y - vol.Min.Y) * (0.01f + 0.98f * (float)rng.NextDouble());
                Assert.True(vol.Contains(new Vector3(x, y, vol.Min.Z)), $"bottom ({x}, {y}, {vol.Min.Z}) of box {b}");
                Assert.False(vol.Contains(new Vector3(x, y, vol.Max.Z)), $"top ({x}, {y}, {vol.Max.Z}) of box {b}");
            }
        }
    }

    [Fact]
    public void An_edited_spawn_areas_record_bottom_and_its_spawn_are_exactly_the_volumes_lowest_point()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        float[] heights = [0.9f, 2.0001f, 2.3f];
        float[] bases = [0f, 1.7f, -0.35f];
        int k = 0;
        foreach (var h in heights)
            foreach (var z in bases)
            {
                var side = k++ % 2 == 0 ? "marines" : "chaos";
                var id = Run(s, $$$"""{"op":"place","piece":{"kind":"spawn-area","side":"{{{side}}}"},"transform":{"position":[{{{k * 30}}},0.37,{{{z}}}],"rotation":[0,0,{{{k * 11}}}],"scale":[7.3,5.1,{{{h}}}]}}""").Created[0];
                Run(s, $$$"""{"op":"edit-mesh","id":"{{{id}}}","mode":"face","ids":[5],"move":[1.3,0,0]}""");
            }
        var map = CustomMap.Compile(s.Document);
        Assert.Equal(heights.Length * bases.Length, map.Content.StartAreas.Count);
        foreach (var a in map.Content.StartAreas)
        {
            var vol = map.Volumes[a.Name];
            Assert.Equal(vol.Min.Z, a.Position.Z - a.Scale.Z / 2f);
            Assert.Equal(vol.Max.Z, a.Position.Z + a.Scale.Z / 2f);
            var spawn = map.Content.Spawns.Single(p => p.Name == "cm_spawn_" + a.Name);
            Assert.Equal(vol.Min.Z, spawn.Position.Z);
            var floor = vol.InsidePoint()!.Value with { Z = vol.Min.Z };
            Assert.True(vol.Contains(floor), $"{a.Name}: a floor point is inside its own volume");
        }
    }

    [Fact]
    public void A_leaning_door_reaching_above_keeps_its_shape_and_blocks_everything_straight_above_it()
    {
        // a 6 x 1 x 3 door whose top leans 2 m toward +X
        var door = Box(new Vector3(6, 1, 3), Vector3.Zero, (k, p) => (k & 4) != 0 ? p + new Vector3(2, 0, 0) : p);
        var tall = door.ReachingAbove(25f);
        Assert.Equal(25f, tall.Max.Z);
        Assert.Equal(door.Vertices, tall.Vertices.Take(door.Vertices.Length));   // the authored shape is unchanged
        for (float x = -3.9f; x <= 5.9f; x += 0.37f)
            for (float z = 0.05f; z < 3f; z += 0.29f)
            {
                var p = new Vector3(x, 0.1f, z);
                if (door.Contains(p)) Assert.True(tall.Contains(p), $"{p} was in the door");
            }
        Assert.True(tall.Contains(new Vector3(2, 0, 20)));      // above the leaning top
        Assert.True(tall.Contains(new Vector3(4.5f, 0, 24)));
        Assert.True(tall.Contains(new Vector3(-2.5f, 0, 10)));  // above the leaning -X face (the door is below this point)
        Assert.False(tall.Contains(new Vector3(-3.5f, 0, 1)));  // beside the foot: open, as authored
        Assert.False(tall.Contains(new Vector3(6, 0, 10)));     // beyond the top's far edge: nothing below it
        Assert.False(tall.Contains(new Vector3(0, 2, 10)));
        Assert.False(tall.Contains(new Vector3(2, 0, 25)));     // the extension's top is out, as every top
    }

    [Fact]
    public void A_door_with_an_uneven_top_reaches_the_height_everywhere_above_it()
    {
        var door = Box(new Vector3(6, 1, 3), Vector3.Zero, (k, p) => k == 7 ? p + new Vector3(0, 0, 1.5f) : p);   // one top corner 1.5 m higher
        var tall = door.ReachingAbove(25f);
        foreach (var (x, y) in new[] { (-2.9f, -0.4f), (2.9f, -0.4f), (-2.9f, 0.4f), (2.9f, 0.4f), (0f, 0f) })
        {
            Assert.True(tall.Contains(new Vector3(x, y, 24.9f)), $"({x}, {y}) near the top");
            Assert.True(tall.Contains(new Vector3(x, y, 3.2f)), $"({x}, {y}) just above the low corner's height");
        }
    }

    static float OutsideBy(Vector3 p) => MathF.Sqrt(MathF.Pow(MathF.Max(MathF.Abs(p.X) - 3, 0), 2) + MathF.Pow(MathF.Max(MathF.Abs(p.Y) - 0.5f, 0), 2));

    [Fact]
    public void Push_out_of_a_thin_door_goes_to_the_radius_beyond_its_outline_from_any_place_inside()
    {
        var door = Box(new Vector3(6, 1, 3), Vector3.Zero);
        foreach (var at in new[] { new Vector3(0, 0.2f, 0), new Vector3(2.5f, -0.3f, 0.5f), new Vector3(-2.9f, 0, 1), new Vector3(0, 0, 0), new Vector3(1.1f, 0.45f, 2) })
        {
            var p = door.PushOut(at, 0.4f, 1.9f);
            Assert.Equal(0.41f, OutsideBy(p), 3);
            Assert.False(door.Crosses(p, p + new Vector3(0, 0, 1.9f)));
            Assert.Equal(at.Z, p.Z);
        }
        Assert.Equal(-3.41f, door.PushOut(new Vector3(-2.9f, 0, 1), 0.4f, 1.9f).X, 3);
    }

    // ---- packs ----

    [Fact]
    public void A_map_with_any_file_name_packs_under_a_plain_name_and_unpacks()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"box"}}""");
        var file = Path.Combine(lab.Dir, "Nick's arena #1 é.smmap");
        s.Save(file);
        var pack = Path.Combine(lab.Dir, "n.smpack");
        MapPack.Pack(file, pack, lab.Library);
        var map = MapPack.Unpack(pack, Path.Combine(lab.Dir, "into"));
        Assert.Equal("Nick_s arena _1 _.smmap", Path.GetFileName(map));
        Assert.Equal(s.Document.ToJson(), MapDocument.Parse(File.ReadAllText(map)).ToJson());
        Assert.False(MapPack.IsPlainMapName("NUL.smmap"));
        Assert.False(MapPack.IsPlainMapName("con.smmap"));
        Assert.False(MapPack.IsPlainMapName("COM1.v2.smmap"));
        Assert.True(MapPack.IsPlainMapName("console.smmap"));
        Assert.Equal("CONmap.smmap", MapPack.EntryName("CON.smmap"));
        Assert.Equal("___.smmap", MapPack.EntryName("###.smmap"));
        Assert.True(MapPack.IsPlainMapName(MapPack.EntryName("é.smmap")));
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

    [Fact]
    public void A_pack_refused_half_way_leaves_no_image_behind()
    {
        using var lab = new Lab();
        var (a, ad) = Image(1); var (b, _) = Image(2); var (_, cd) = Image(3);
        var doc = MapDocument.Create("half");
        doc.Images.AddRange(new[] { a, b }.OrderBy(x => x.Id, StringComparer.Ordinal));
        var path = Path.Combine(lab.Dir, "half.smpack");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = zip.CreateEntry("m.smmap").Open()) w.Write(new System.Text.UTF8Encoding(false).GetBytes(doc.ToJson()));
            using (var w = zip.CreateEntry("images/" + a.FileName).Open()) w.Write(ad);
            using (var w = zip.CreateEntry("images/" + b.FileName).Open()) w.Write(cd);   // the wrong bytes under b's name
        }
        var into = Path.Combine(lab.Dir, "into");
        Assert.Equal("bad-pack", Assert.Throws<MapException>(() => MapPack.Unpack(path, into)).Code);
        Assert.False(File.Exists(Path.Combine(into, "images", a.FileName)));
        Assert.False(File.Exists(Path.Combine(into, "m.smmap")));
    }

    // ---- lows ----

    [Fact]
    public void A_picture_that_cannot_be_copied_beside_the_map_is_a_save_note_not_a_failed_save()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        var (_, data) = Image(5);
        var png = Path.Combine(lab.Dir, "p.png"); File.WriteAllBytes(png, data);
        Run(s, """{"op":"image-add","path":@P@}""".Replace("@P@", JsonValue.Create(png).ToJsonString()));
        var dir = Path.Combine(lab.Dir, "maps");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "images"), "a file where the images folder would go");
        var file = Path.Combine(dir, "m.smmap");
        s.Save(file);
        Assert.True(File.Exists(file));
        Assert.Contains(s.SaveNotes, n => n.Contains("could not be copied"));
    }

    [Fact]
    public void A_batch_that_would_leave_the_file_over_8_mb_is_refused()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"box"}}""");
        var before = s.Document.ToJson();
        var op = new JsonObject { ["op"] = "rename", ["id"] = "o1", ["name"] = new string('é', MapDocument.MaxChars / 2 + 10) };   // under 8 M characters, over 8 MB in UTF-8
        var r = s.Apply(op.ToJsonString());
        Assert.False(r.Ok);
        Assert.Equal("too-big", r.Errors[0].Code);
        Assert.Equal(before, s.Document.ToJson());
    }

    [Fact]
    public void Flipping_between_format_3_and_4_keeps_one_backup()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"box"}}""");
        var file = Path.Combine(lab.Dir, "flip.smmap");
        s.Save(file);
        for (int i = 0; i < 3; i++)
        {
            Run(s, """{"op":"edit-mesh","id":"o1","mode":"vertex","ids":[7],"move":[0,0,1]}"""); s.Save();
            Run(s, """{"op":"reset-mesh","ids":["o1"]}"""); s.Save();
        }
        Assert.Single(Directory.GetFiles(lab.Dir, "flip.smmap.bak-*"));
    }

    [Fact]
    public void The_round_point_note_names_the_right_point()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"box"},"transform":{"position":[0,0,-1],"scale":[60,60,1]}}""");
        Run(s, """{"op":"place","piece":{"kind":"capture-point"},"transform":{"position":[-10,0,0],"scale":[8,8,4]}}""");
        Run(s, """{"op":"place","piece":{"kind":"capture-point"},"transform":{"position":[10,0,0],"scale":[6,9,4]}}""");
        var note = Assert.Single(MapCheck.Run(CustomMap.Compile(s.Document), null, null).Issues, i => i.Code == "point-round");
        Assert.Equal(["o3"], note.Ids);
        Assert.Contains("6 m across", note.Message);
        Assert.Contains("(9 m)", note.Message);
    }
}
