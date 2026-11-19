using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;
using SmMapmaker;
using Xunit.Abstractions;

namespace SmMapmaker.Tests;

/// <summary>
/// The owner's pass 3 play proof (docs/mapmaker.md "SM1 pieces in play"): a map shaped like a tester's file, built here from the real install's placements (never committed, never copied: only
/// references and transforms go into the file). It needs the game install; without one these tests do nothing. Set <c>SM_FIXTURE_OUT</c> to a folder to keep the generated <c>.smmap</c>.
/// </summary>
public sealed class RealInstallFixtureTests(ITestOutputHelper output) : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "smmapmaker-real-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    public const int PlatformTop = 6;

    /// <summary>164 SM1 placements from three maps (props near the middle of each, set down on the open field), 34 prefab instances, the field's Floor box, spawn areas, doors and three capture points.</summary>
    public static MapDocument BuildTesterShapedMap(InstalledSm1Source install, MapLibrary library, Action<string>? log = null)
    {
        var session = MapSession.New("Tester Map", library, install);
        Apply(session, MapTemplate.Find("open-field")!.Ops().ToArray());
        var picked = new List<(string Map, Sm1Object Object, Matrix4x4 Moved)>();
        // (map, how many, where on the field): props of each source map around the field's middle, on the field's floor (its top is z = 0).
        foreach (var (map, count, at) in new[] { ("mp_tdm_17", 62, new Vector2(-14, 14)), ("mp_dlc1_map2", 56, new Vector2(14, 14)), ("mp_tdm_09", 52, new Vector2(0, -14)) })
        {
            var sm1 = install.LoadMap(map);
            var candidates = sm1.Objects.Select((o, i) => (o, i)).Where(x => sm1.Hidden?.Contains(x.i) != true && sm1.Definitions.TryGetValue(x.o.Definition, out var d) && d.Bounds is { } b
                && (b.Max - b.Min) is var s && s.X < 9 && s.Y < 9 && s.Z < 8 && s.X * s.Y > 0.3f && x.o.Transform.GetDeterminant() > 0.01f && Finite(x.o.Transform)).Select(x => x.o).ToList();
            var centre = new Vector3(candidates.Average(o => o.Transform.Translation.X), candidates.Average(o => o.Transform.Translation.Y), 0);
            var near = candidates.OrderBy(o => Vector2.Distance(new Vector2(o.Transform.Translation.X, o.Transform.Translation.Y), new Vector2(centre.X, centre.Y))).ThenBy(o => o.Name, StringComparer.Ordinal).Take(count).ToList();
            float floor = near.Select(o => sm1.Definitions[o.Definition].Bounds!.Value.Transformed(o.Transform).Min.Z).Where(float.IsFinite).Min();
            near = near.Where(o => float.IsFinite(sm1.Definitions[o.Definition].Bounds!.Value.Transformed(o.Transform).Min.Z) && float.IsFinite(sm1.Definitions[o.Definition].Bounds!.Value.Transformed(o.Transform).Max.Z)).ToList();
            var shift = Matrix4x4.CreateTranslation(at.X - centre.X, at.Y - centre.Y, -floor);
            foreach (var o in near) picked.Add((map, o, o.Transform * shift));
            log?.Invoke($"{map}: {near.Count} props placed around ({at.X}, {at.Y})");
        }
        var ops = new List<JsonObject>();
        int n = 0;
        foreach (var (map, o, moved) in picked)
        {
            var t = TransformMath.Decompose(moved);
            ops.Add(new JsonObject
            {
                ["op"] = "place", ["name"] = $"{o.Definition} {++n}", ["asset"] = new JsonObject { ["source"] = "sm1", ["map"] = map, ["definition"] = o.Definition },
                ["transform"] = JsonNode.Parse(t.ToNode().ToJsonString()),   // doubles, as the operations take them
            });
        }
        foreach (var op in ops) if (op.ToJsonString().Contains("NaN") || op.ToJsonString().Contains("Infinity")) log?.Invoke("non-finite: " + op.ToJsonString());
        Apply(session, ops.ToArray());
        // The marines' spawn area stands on a raised SM1 roof slab (no Floor box under it): a player or bot spawns on, and is held up by, SM1 collision.
        {
            var sm1 = install.LoadMap("mp_tdm_09");
            var roof = sm1.Objects.First(o => o.Name == "roof_20lx20w_flat_edge_05");
            var rb = sm1.Definitions[roof.Definition].Bounds!.Value.Transformed(roof.Transform);
            var moved = roof.Transform * Matrix4x4.CreateTranslation(-36 - (rb.Min.X + rb.Max.X) / 2, -(rb.Min.Y + rb.Max.Y) / 2, PlatformTop - rb.Max.Z);
            Apply(session, new JsonObject { ["op"] = "place", ["name"] = "marine platform (SM1 roof)", ["asset"] = new JsonObject { ["source"] = "sm1", ["map"] = "mp_tdm_09", ["definition"] = roof.Definition },
                ["transform"] = JsonNode.Parse(TransformMath.Decompose(moved).ToNode().ToJsonString()) });
            string area = session.Document.Objects.First(o => o.Piece is { Kind: PieceKind.SpawnArea, Side: PieceSpec.Marines }).Id;
            Apply(session, Lab.Op($$$"""{"op":"transform","id":"{{{area}}}","position":[-36,0,{{{PlatformTop}}}]}"""));
            Apply(session, Lab.Op($$$"""{"op":"place","piece":{"kind":"ramp"},"name":"marine ramp","transform":{"position":[-17.5,0,0],"rotation":[0,0,90],"scale":[6,12,{{{PlatformTop}}}]}}"""));
        }
        // 34 prefab instances of two prefabs made from placed props (two collision modes), set out in a row.
        var objects = session.Document.Objects.Where(o => o.Asset is { IsSm1: true }).ToList();
        var a = objects.Where(o => o.Asset!.Map == "mp_tdm_17").Take(3).Select(o => o.Id).ToArray();
        var b = objects.Where(o => o.Asset!.Map == "mp_dlc1_map2").Take(3).Select(o => o.Id).ToArray();
        Apply(session, Lab.Op($$$"""{"op":"group-to-prefab","ids":[{{{string.Join(",", a.Select(i => $"\"{i}\""))}}}],"prefab":"tester-a","name":"tester a","origin":"bounds-bottom-center","replace":true}"""));
        Apply(session, Lab.Op($$$"""{"op":"group-to-prefab","ids":[{{{string.Join(",", b.Select(i => $"\"{i}\""))}}}],"prefab":"tester-b","name":"tester b","origin":"bounds-bottom-center","collision":"original-cut","replace":true}"""));
        var instances = new List<JsonObject>();
        for (int i = 0; i < 32; i++)
            instances.Add(Lab.Op($$$"""{"op":"place","prefab":"{{{(i % 2 == 0 ? "tester-a" : "tester-b")}}}","transform":{"position":[{{{(-40 + i * 2.5).ToString(System.Globalization.CultureInfo.InvariantCulture)}}},{{{(i % 2 == 0 ? 34 : 38)}}},0]}}"""));
        Apply(session, instances.ToArray());
        // saving embeds the prefabs the objects use (formatVersion 2): the file is what a tester sends
        string file = Path.Combine(Path.GetDirectoryName(library.Root)!, "tester-map.smmap");
        session.Save(file);
        return MapDocument.Parse(File.ReadAllText(file));
    }

    static bool Finite(Matrix4x4 m)
    {
        try { var t = TransformMath.Decompose(m); return new[] { t.Position.X, t.Position.Y, t.Position.Z, t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Scale.X, t.Scale.Y, t.Scale.Z }.All(float.IsFinite) && TransformMath.Close(t.ToMatrix(), m, 0.01f); }
        catch (MapException) { return false; }
    }

    static void Apply(MapSession s, params JsonObject[] ops)
    {
        var r = s.Apply(ops);
        Assert.True(r.Ok, ops[0].ToJsonString() + " -> " + r.ToJson());
    }

    [Fact]
    public void Explore_slabs()
    {
        var install = InstalledSm1Source.TryCreate();
        if (install is null || Environment.GetEnvironmentVariable("SM_EXPLORE") is null) return;
        foreach (var map in new[] { "mp_tdm_09", "mp_tdm_17", "mp_dlc1_map2" })
        {
            var sm1 = install.LoadMap(map);
            foreach (var (o, i) in sm1.Objects.Select((o, i) => (o, i)).Where(x => sm1.Hidden?.Contains(x.i) != true && sm1.Definitions.TryGetValue(x.o.Definition, out var d) && d.Bounds is not null)
                .Select(x => (x.o, x.i, b: sm1.Definitions[x.o.Definition].Bounds!.Value.Transformed(x.o.Transform))).Where(x => (x.b.Max.X - x.b.Min.X) is > 8 and < 40 && (x.b.Max.Y - x.b.Min.Y) is > 8 and < 40 && (x.b.Max.Z - x.b.Min.Z) < 2.5f)
                .OrderByDescending(x => (x.b.Max.X - x.b.Min.X) * (x.b.Max.Y - x.b.Min.Y)).Take(6).Select(x => (x.o, x.i)))
                output.WriteLine($"{map}: {o.Name} def {o.Definition} #{i} size {sm1.Definitions[o.Definition].Bounds!.Value.Transformed(o.Transform).Max - sm1.Definitions[o.Definition].Bounds!.Value.Transformed(o.Transform).Min}");
        }
    }

    [Fact]
    public void A_tester_shaped_map_from_the_real_install_compiles_in_seconds_and_is_solid()
    {
        var install = InstalledSm1Source.TryCreate();
        if (install is null) return;   // no install on this PC: nothing to prove here
        Directory.CreateDirectory(_dir);
        var library = new MapLibrary(Path.Combine(_dir, "lib"));
        var built = Stopwatch.StartNew();
        var doc = BuildTesterShapedMap(install, library, output.WriteLine);
        built.Stop();
        output.WriteLine($"fixture built in {built.Elapsed.TotalSeconds:0.0} s: {doc.Objects.Count(o => o.Asset is { IsSm1: true })} SM1 placements, {doc.Objects.Count(o => o.Prefab is not null)} prefab instances, {doc.Objects.Count(o => o.Piece is not null)} pieces");
        if (Environment.GetEnvironmentVariable("SM_FIXTURE_OUT") is { Length: > 0 } outDir)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "tester-map.smmap"), doc.ToJson());
        }
        var timer = Stopwatch.StartNew();
        var map = CustomMap.Compile(MapDocument.Parse(doc.ToJson()), CompileOptions.ForPlay(textures: false));
        timer.Stop();
        output.WriteLine($"compiled in {timer.Elapsed.TotalSeconds:0.0} s: {map.Decor!.Placements.Count} placements, {map.Decor.Triangles:N0} triangles, hash {map.Hash:X16}");
        Assert.True(map.Decor.Placements.Count >= 164);
        Assert.True(map.Decor.Triangles > 1000);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), $"compiled in {timer.Elapsed}");
        var again = CustomMap.Compile(MapDocument.Parse(doc.ToJson()), CompileOptions.ForPlay(textures: false));
        Assert.Equal(map.Hash, again.Hash);   // two compiles (server and client) agree
        var report = MapCheck.Run(map);
        output.WriteLine(report.ToText());
        var area = map.Content.StartAreas.First(a => a.Side == SmSim.Team.SpaceMarines);
        foreach (var n in map.Notes) output.WriteLine("note: " + n);
        { var d = map.Decor!.Placements.First(x => x.Name.StartsWith("marine platform")); output.WriteLine($"platform placement: {d.Asset.Definition} world {d.World.Translation} solid {d.Solid}"); }
        { var h = map.ToCollisionWorld().Character.Raycast(new Vector3(-36, 0, 12), -Vector3.UnitZ, 30f); output.WriteLine($"ray at platform: {(h is { } hh ? $"z {hh.Point.Z:0.000} normal {hh.Normal}" : "no hit")}; area at {area.Position} scale {area.Scale}"); }
        var spots = MapCheck.SpotsIn(area, map.ToCollisionWorld().Character);
        output.WriteLine($"marine spawn spots: {spots.Count}, heights {string.Join(",", spots.Select(s => s.Z.ToString("0.00")).Distinct())}");
        Assert.NotEmpty(spots);
        Assert.All(spots, s => Assert.InRange(s.Z, PlatformTop - 0.3f, PlatformTop + 0.3f));   // they stand on the SM1 roof slab, 6 m above the field's floor
    }
}
