using System.Numerics;
using SmMapmaker;
using SmSim;
using Xunit;

namespace SmMapmaker.Tests;

/// <summary>Contract v4 (re-review of v3): push-out of raised doors, inside-out shapes, the coplanar tolerance, fresh format-3 backups.</summary>
public sealed class EditContractV4Tests
{
    static OpResult Run(MapSession s, string json)
    {
        var r = s.Apply(json);
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Code + ": " + e.Message)));
        return r;
    }

    static MeshVolume Box(Vector3 size, Vector3 at, Func<int, Vector3, Vector3>? bend = null, bool insideOut = false)
    {
        var (v, i, _) = EditMesh.Stock(MeshTopology.Box).Triangulate();
        if (insideOut) for (int t = 0; t < i.Length; t += 3) (i[t + 1], i[t + 2]) = (i[t + 2], i[t + 1]);
        return new MeshVolume("box", v.Select((p, k) => { var q = p * size + at; return bend is null ? q : bend(k, q); }).ToArray(), i);
    }

    static float OutsideBy(Vector3 p) => MathF.Sqrt(MathF.Pow(MathF.Max(MathF.Abs(p.X) - 3, 0), 2) + MathF.Pow(MathF.Max(MathF.Abs(p.Y) - 0.5f, 0), 2));

    [Fact]
    public void A_raised_door_pushes_out_to_the_radius_beyond_its_authored_outline()
    {
        var raised = Box(new Vector3(6, 1, 3), Vector3.Zero).ReachingAbove(25f);
        Assert.Equal(12, raised.FeatureEdges().Count);   // the authored outline, not the prisms' edges
        foreach (var at in new[] { new Vector3(0.6f, 0.099f, 0), new Vector3(0, 0, 0), new Vector3(-2.2f, 0.3f, 10), new Vector3(2.95f, -0.2f, 20), new Vector3(1, 0.45f, 2.5f) })
        {
            var p = raised.PushOut(at, 0.4f, 1.9f);
            Assert.Equal(0.41f, OutsideBy(p), 3);
            Assert.False(raised.Crosses(p, p + new Vector3(0, 0, 1.9f)));
        }
    }

    [Fact]
    public void An_inside_out_shape_is_turned_right_way_out_and_raises_solid()
    {
        var door = Box(new Vector3(6, 1, 3), Vector3.Zero, insideOut: true);
        Assert.True(door.Contains(new Vector3(0, 0, 1.5f)));
        Assert.False(door.Contains(new Vector3(0, 2, 1.5f)));
        var raised = door.ReachingAbove(25f);
        foreach (var z in new[] { 0f, 1.5f, 2.9f, 3f, 10f, 24.9f }) Assert.True(raised.Contains(new Vector3(0.3f, 0.1f, z)), $"z {z}");
        Assert.False(raised.Contains(new Vector3(0.3f, 0.1f, 25f)));
        Assert.Equal(0.41f, OutsideBy(raised.PushOut(new Vector3(0.6f, 0.099f, 1), 0.4f, 1.9f)), 3);
    }

    [Fact]
    public void A_mirrored_or_pulled_through_marker_compiles_to_a_right_way_out_volume()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"blocker","side":"marines"},"transform":{"position":[0,0,0],"scale":[-6,1,3]}}""");
        Run(s, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[1],"move":[0,0,0.5]}""");
        Run(s, """{"op":"place","piece":{"kind":"kill-zone"},"transform":{"position":[20,0,0],"scale":[4,4,2]}}""");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"face","ids":[5],"move":[-6,0,0]}""");   // the +X face pulled through the -X face: x from -2 to -4
        var map = CustomMap.Compile(s.Document);
        var door = map.Volumes["marines_blocker_1"];
        Assert.True(door.Contains(new Vector3(0, 0, 1.5f)));
        Assert.True(door.ReachingAbove(25f).Contains(new Vector3(0, 0, 20)));
        var kill = map.Volumes["cm_kill_1"];
        Assert.True(kill.Contains(new Vector3(17, 0, 1)));
        Assert.False(kill.Contains(new Vector3(20, 0, 1)));
    }

    static Vector3 Grid(Vector3 p) => new(MathF.Round(p.X * 4096) / 4096, MathF.Round(p.Y * 4096) / 4096, MathF.Round(p.Z * 4096) / 4096);

    [Fact]
    public void A_yawed_leaning_box_and_a_yawed_cylinder_have_only_their_real_edges_as_outline()
    {
        float c = MathF.Cos(0.6457718f), s = MathF.Sin(0.6457718f);   // 37 degrees
        Vector3 Turn(Vector3 p) => Grid(new Vector3(p.X * c - p.Y * s + 13.37f, p.X * s + p.Y * c - 7.11f, p.Z + 0.73f));
        var leaning = Box(new Vector3(6, 1, 3), Vector3.Zero, (k, p) => Turn((k & 4) != 0 ? p + new Vector3(2, 0.4f, 0) : p));
        Assert.Equal(12, leaning.FeatureEdges().Count);

        using var lab = new Lab();
        var ses = lab.Session(withSm1: false);
        Run(ses, """{"op":"place","piece":{"kind":"capture-point"},"transform":{"position":[13.37,-7.11,0.73],"rotation":[0,0,37],"scale":[8,6,4]}}""");
        Run(ses, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[1],"move":[0,0,0.5]}""");
        var vol = CustomMap.Compile(ses.Document).Volumes["mkr_objective_1"];
        Assert.Equal(48, vol.FeatureEdges().Count);   // 16 bottom, 16 top, 16 upright: no cap or side diagonals
    }

    [Fact]
    public void A_format_3_file_that_changed_since_its_backup_gets_a_fresh_one_and_an_unchanged_one_does_not()
    {
        using var lab = new Lab();
        var s = lab.Session(withSm1: false);
        Run(s, """{"op":"place","piece":{"kind":"box"}}""");
        var file = Path.Combine(lab.Dir, "flip.smmap");
        s.Save(file);
        Run(s, """{"op":"edit-mesh","id":"o1","mode":"vertex","ids":[7],"move":[0,0,1]}"""); s.Save();   // 3 -> 4: .bak-v3
        Run(s, """{"op":"reset-mesh","ids":["o1"]}"""); s.Save();                                        // 4 -> 3
        Run(s, """{"op":"rename","id":"o1","name":"format 3 work"}"""); s.Save();                        // more format-3 work
        var v3 = File.ReadAllText(file);
        Run(s, """{"op":"edit-mesh","id":"o1","mode":"vertex","ids":[7],"move":[0,0,1]}"""); s.Save();   // 3 -> 4 again: the changed file is kept too
        Assert.Equal(2, Directory.GetFiles(lab.Dir, "flip.smmap.bak-*").Length);
        Assert.Equal(v3, File.ReadAllText(s.LastBackup!));
        Run(s, """{"op":"reset-mesh","ids":["o1"]}"""); s.Save();
        Run(s, """{"op":"edit-mesh","id":"o1","mode":"vertex","ids":[7],"move":[0,0,1]}"""); s.Save();   // unchanged format-3 file: no third backup
        Assert.Equal(2, Directory.GetFiles(lab.Dir, "flip.smmap.bak-*").Length);
    }

    [Fact]
    public void A_refused_unpack_leaves_no_folder_it_made()
    {
        using var lab = new Lab();
        var bad = Path.Combine(lab.Dir, "bad.smpack");
        using (var zip = System.IO.Compression.ZipFile.Open(bad, System.IO.Compression.ZipArchiveMode.Create))
        {
            var d = MapDocument.Create("x");
            var png = new byte[64];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 8);
            var good = MapImage.Inspect(png, "a.png");
            var other = (byte[])png.Clone(); other[40] = 9;
            var second = MapImage.Inspect(other, "b.png");
            d.Images.AddRange(new[] { good, second }.OrderBy(x => x.Id, StringComparer.Ordinal));
            using (var w = zip.CreateEntry("m.smmap").Open()) w.Write(new System.Text.UTF8Encoding(false).GetBytes(d.ToJson()));
            using (var w = zip.CreateEntry("images/" + good.FileName).Open()) w.Write(png);
            using (var w = zip.CreateEntry("images/" + second.FileName).Open()) w.Write(png);   // wrong bytes
        }
        var into = Path.Combine(lab.Dir, "new-folder");
        Assert.Equal("bad-pack", Assert.Throws<MapException>(() => MapPack.Unpack(bad, into)).Code);
        Assert.False(Directory.Exists(into));
    }
}
