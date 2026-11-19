using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner's editor): <c>godot.cmd --windowed --editor --path mapmaker -- --mm-libtest --mm-out &lt;dir&gt;</c> (with
/// <c>SM_MAPMAKER_CONTROL</c> set to a scratch folder) checks the Library: previews for SM1 definitions, custom assets and prefabs, the placeholder icons, the
/// Quixel / Fab drop import (synthetic zip with an ASCII FBX at two LODs and Megascans-named textures, a loose glb, a bad zip, a Draco file) and rename.
/// Screenshots of the dock and the 3D view go to <c>--mm-out</c>; the steps to <c>libtest-result.txt</c>; it prints <c>LIBTEST RESULT PASS|FAIL</c> and quits.
/// All fixtures are generated here; nothing is downloaded or taken from the game.
/// </summary>
public sealed class LibraryTest(MapmakerPlugin plugin, MapmakerDock dock)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-libtest");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }

    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    readonly List<string> _log = new();
    bool _failed;
    string _out = "";

    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
    void Say(string m) { _log.Add(m); GD.Print("LIBTEST " + m); }
    void Check(bool ok, string what) { if (!ok) _failed = true; Say((ok ? "ok   " : "FAIL ") + what); }

    async Task<bool> Until(Func<bool> done, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var tick = DateTime.UtcNow;
        while (!done()) { if (DateTime.UtcNow > until) return false; await Frames(5); if ((DateTime.UtcNow - tick).TotalSeconds > 15) { tick = DateTime.UtcNow; Say($"waiting... stats {dock.LibraryStats()} pending {MapmakerPlugin.Thumbs?.Pending}"); } }
        return true;
    }

    void ShotDock(string name)
    {
        if (!Thumbnails.CanRender) { Say($"shot {name}: headless, skipped"); return; }
        var rect = dock.GetGlobalRect();
        var window = Tree.Root.GetTexture().GetImage();
        var region = new Rect2I((int)rect.Position.X, (int)rect.Position.Y, (int)rect.Size.X, (int)rect.Size.Y).Intersection(new Rect2I(Vector2I.Zero, window.GetSize()));
        window.GetRegion(region).SavePng(Path.Combine(_out, name + ".png"));
        window.SavePng(Path.Combine(_out, name + "-window.png"));
        Say($"shot {name} ({region.Size.X}x{region.Size.Y})");
    }

    // ---- synthetic fixtures ----

    static string Fbx(float size, float height)
    {
        float h = size / 2;
        float[][] v = [[-h, 0, -h], [h, 0, -h], [h, 0, h], [-h, 0, h], [-h, height, -h], [h, height, -h], [h, height, h], [-h, height, h]];
        int[][] quads = [[0, 1, 2, 3], [4, 7, 6, 5], [0, 4, 5, 1], [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0]];
        var verts = string.Join(",", v.SelectMany(p => p).Select(x => x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        var idx = string.Join(",", quads.SelectMany(q => new[] { q[0], q[1], q[2], ~q[3] }));
        var uv = string.Join(",", Enumerable.Repeat(new[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 6).SelectMany(x => x));
        return $$"""
            ; FBX 7.4.0 project file
            FBXHeaderExtension:  {
            	FBXHeaderVersion: 1003
            	FBXVersion: 7400
            }
            GlobalSettings:  {
            	Version: 1000
            	Properties70:  {
            		P: "UpAxis", "int", "Integer", "",1
            		P: "UpAxisSign", "int", "Integer", "",1
            		P: "FrontAxis", "int", "Integer", "",2
            		P: "FrontAxisSign", "int", "Integer", "",1
            		P: "CoordAxis", "int", "Integer", "",0
            		P: "CoordAxisSign", "int", "Integer", "",1
            		P: "UnitScaleFactor", "double", "Number", "",100
            	}
            }
            Objects:  {
            	Geometry: 1000, "Geometry::Box", "Mesh" {
            		Vertices: *24 {
            			a: {{verts}}
            		}
            		PolygonVertexIndex: *24 {
            			a: {{idx}}
            		}
            		GeometryVersion: 124
            		LayerElementUV: 0 {
            			Version: 101
            			Name: "UVMap"
            			MappingInformationType: "ByPolygonVertex"
            			ReferenceInformationType: "Direct"
            			UV: *48 {
            				a: {{uv}}
            			}
            		}
            		Layer: 0 {
            			Version: 100
            			LayerElement:  {
            				Type: "LayerElementUV"
            				TypedIndex: 0
            			}
            		}
            	}
            	Model: 2000, "Model::Box", "Mesh" {
            		Version: 232
            		Properties70:  {
            			P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0
            		}
            	}
            	Material: 3000, "Material::Stone", "" {
            		Version: 102
            		ShadingModel: "phong"
            		Properties70:  {
            		}
            	}
            }
            Connections:  {
            	C: "OO",2000,0
            	C: "OO",1000,2000
            	C: "OO",3000,2000
            }
            """;
    }

    static void WritePng(string path, Func<int, int, Color> pixel)
    {
        var image = Image.CreateEmpty(64, 64, false, Image.Format.Rgba8);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) image.SetPixel(x, y, pixel(x, y));
        image.SavePng(path);
    }

    static string MakeQuixelZip(string dir)
    {
        string tex = Path.Combine(dir, "tex"); Directory.CreateDirectory(tex);
        WritePng(Path.Combine(tex, "vcjkdbkfw_4K_Albedo.png"), (x, y) => ((x / 8 + y / 8) % 2 == 0) ? new Color(0.75f, 0.45f, 0.2f) : new Color(0.35f, 0.2f, 0.1f));
        WritePng(Path.Combine(tex, "vcjkdbkfw_4K_Normal.png"), (x, y) => new Color(0.5f, 0.5f, 1f));
        WritePng(Path.Combine(tex, "vcjkdbkfw_4K_Roughness.png"), (x, y) => new Color(0.7f, 0.7f, 0.7f));
        WritePng(Path.Combine(tex, "vcjkdbkfw_4K_Displacement.png"), (x, y) => new Color(0.5f, 0.5f, 0.5f));
        string zip = Path.Combine(dir, "Rock_Pillar.zip");
        using (var fs = File.Create(zip))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string entry, byte[] bytes) { using var s = z.CreateEntry(entry).Open(); s.Write(bytes); }
            Add("Rock_Pillar_LOD0.fbx", Encoding.UTF8.GetBytes(Fbx(200, 400)));
            Add("Rock_Pillar_LOD1.fbx", Encoding.UTF8.GetBytes(Fbx(100, 200)));
            Add("Rock_Pillar_LOD2.fbx", Encoding.UTF8.GetBytes(Fbx(50, 100)));
            foreach (var f in Directory.GetFiles(tex)) Add("Textures/" + Path.GetFileName(f), File.ReadAllBytes(f));
            Add("Rock_Pillar.json", Encoding.UTF8.GetBytes("{}"));
        }
        return zip;
    }

    static string MakeGlb(string dir, string name, Color colour, Vector3 size)
    {
        var root = new Node3D { Name = name };
        var mesh = new MeshInstance3D { Name = "Body", Mesh = new BoxMesh { Size = size }, Position = new Vector3(0, size.Y / 2, 0) };
        mesh.MaterialOverride = null;
        ((BoxMesh)mesh.Mesh).Material = new StandardMaterial3D { AlbedoColor = colour };
        root.AddChild(mesh); mesh.Owner = root;
        var doc = new GltfDocument(); var state = new GltfState();
        doc.AppendFromScene(root, state);
        string path = Path.Combine(dir, name + ".glb");
        doc.WriteToFilesystem(state, path);
        root.Free();
        return path;
    }

    static string MakeBadZip(string dir)
    {
        string zip = Path.Combine(dir, "evil.zip");
        using var fs = File.Create(zip);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        using (var s = z.CreateEntry("ok.fbx").Open()) s.Write("x"u8);
        using (var s = z.CreateEntry("../escape.fbx").Open()) s.Write("x"u8);
        return zip;
    }

    static string MakeDracoGltf(string dir)
    {
        string p = Path.Combine(dir, "packed.gltf");
        File.WriteAllText(p, """{"asset":{"version":"2.0"},"extensionsRequired":["KHR_draco_mesh_compression"],"scenes":[{"nodes":[]}],"nodes":[]}""");
        return p;
    }

    public async Task Run()
    {
        try { await RunCore(); }
        catch (Exception e) { _failed = true; Say("EXCEPTION " + e); }
        Say(_failed ? "RESULT FAIL" : "RESULT PASS");
        GD.Print(_failed ? "LIBTEST RESULT FAIL" : "LIBTEST RESULT PASS");
        try { File.WriteAllLines(Path.Combine(_out, "libtest-result.txt"), _log); } catch (Exception) { }
        Tree.Quit(_failed ? 1 : 0);
    }

    async Task RunCore()
    {
        _out = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-libtest-" + System.Environment.ProcessId));
        Directory.CreateDirectory(_out);
        string work = Path.Combine(Path.GetTempPath(), "mm-libtest-work-" + System.Environment.ProcessId);
        Directory.CreateDirectory(work);
        MapHost.Library = new MapLibrary(Arg("--mm-lib", Path.Combine(work, "lib")));
        await Frames(30);
        if (!plugin.EnsureSceneRoot()) { await Frames(60); plugin.EnsureSceneRoot(); }
        plugin.NewMap("libtest");
        await Frames(10);
        Check(plugin.Session is not null, "a map is open");

        // SM1 definitions (needs the owner's install; skipped when there is none)
        bool sm1 = Sm1Source.GameDir() is not null;
        if (sm1)
        {
            dock.OpenSm1("mp_tdm_09", true);
            sm1 = await Until(() => MapHost.Sm1 is not null, 240);
            Check(sm1, "SM1 map mp_tdm_09 opened");
        }
        else Say("no game install: SM1 source skipped");

        if (sm1 && Thumbnails.CanRender) await PartDemo();

        // Imports by drop: zip with two LODs, a loose glb, a bad zip, a Draco file
        await Drop(new[] { MakeQuixelZip(work) });
        var pillar = MapHost.Library.ListAssets().FirstOrDefault(a => a.Id == "rock_pillar" || a.Id == "rock-pillar");
        Check(pillar is not null, $"zip imported as an asset ({dock.LastImportMessage})");
        if (pillar is not null)
        {
            Check(pillar.Import?["lod"]?.GetValue<int>() == 0, "LOD0 chosen over LOD1/LOD2");
            double scale = pillar.Import?["scale"]?.GetValue<double>() ?? 0;
            Check(Math.Abs(scale - 0.01) < 1e-6, $"centimetre scale detected (scale {scale})");
            Check((pillar.Import?["textures"]?.GetValue<int>() ?? 0) == 3, $"3 textures used, displacement ignored ({pillar.Import?["textures"]})");
            var b = pillar.Bounds!.Value;
            Check(b.Max.Y - b.Min.Y is > 3.5f and < 4.5f, $"bounds measured from the loaded mesh, height {b.Max.Y - b.Min.Y:0.00} m");
            Check(File.Exists(MapHost.Library.AssetFilePath(pillar)) && pillar.File.EndsWith(".glb"), "self-contained .glb in the library");
        }
        await Drop(new[] { MakeGlb(work, "Crate", new Color(0.3f, 0.55f, 0.8f), new Vector3(1.2f, 1f, 1.2f)) });
        Check(MapHost.Library.GetAsset("crate") is not null, $"glb dropped ({dock.LastImportMessage})");
        await Drop(new[] { MakeBadZip(work) });
        Check(dock.LastImportMessage.Contains("Could not import"), $"zip-slip zip refused with a message ({dock.LastImportMessage})");
        await Drop(new[] { MakeDracoGltf(work) });
        Check(dock.LastImportMessage.Contains("Draco"), $"Draco file refused with a clear message ({dock.LastImportMessage})");
        await Drop(new[] { MakeQuixelZip(work) });
        Check(dock.LastImportMessage.Contains("already"), $"the same zip again is a no-op ({dock.LastImportMessage})");

        // An asset that loads as nothing and one that does not load at all: placeholders that say why
        var info = new AssetInfo("empty-model", "Empty model", "empty.gltf", null, "e1", "test");
        Directory.CreateDirectory(MapHost.Library.AssetDir("empty-model"));
        File.WriteAllText(Path.Combine(MapHost.Library.AssetDir("empty-model"), "empty.gltf"), """{"asset":{"version":"2.0"},"scenes":[{"nodes":[0]}],"nodes":[{"name":"nothing"}]}""");
        File.WriteAllText(Path.Combine(MapHost.Library.AssetDir("empty-model"), "asset.json"), info.ToNode().ToJsonString());
        var broken = new AssetInfo("broken-model", "Broken model", "broken.glb", null, "b1", "test");
        Directory.CreateDirectory(MapHost.Library.AssetDir("broken-model"));
        File.WriteAllBytes(Path.Combine(MapHost.Library.AssetDir("broken-model"), "broken.glb"), [1, 2, 3, 4, 5, 6, 7, 8]);
        File.WriteAllText(Path.Combine(MapHost.Library.AssetDir("broken-model"), "asset.json"), broken.ToNode().ToJsonString());
        MapHost.NotifyLibraryChanged();

        // Prefabs: a pair of crates and an empty-looking one
        var placed = plugin.Do("place", MapmakerPlugin.Op("place", ("asset", new System.Text.Json.Nodes.JsonObject { ["source"] = "custom", ["id"] = "crate" }), ("name", "a"), ("transform", new System.Text.Json.Nodes.JsonObject { ["position"] = MapmakerPlugin.Vec(new Num.Vector3(0, 0, 0)) })),
            MapmakerPlugin.Op("place", ("asset", new System.Text.Json.Nodes.JsonObject { ["source"] = "custom", ["id"] = "crate" }), ("name", "b"), ("transform", new System.Text.Json.Nodes.JsonObject { ["position"] = MapmakerPlugin.Vec(new Num.Vector3(2, 0, 0)) })));
        Check(placed.Ok, "two crates placed");
        var grouped = plugin.Do("group", MapmakerPlugin.Op("group-to-prefab", ("ids", MapmakerPlugin.Strs(placed.Created.Take(2))), ("prefab", "crate-pair"), ("name", "Crate pair"), ("origin", "first"), ("replace", true)));
        Check(grouped.Ok, "prefab crate-pair made");

        // Previews for every source
        dock.ShowTab((int)DockTab.Library);
        await Frames(20);
        foreach (var (source, name) in new[] { (1, "library-custom-assets"), (2, "library-prefabs"), (0, "library-sm1") })
        {
            if (source == 0 && !sm1) continue;
            dock.TestShowSource(source);
            if (source == 0 && MapHost.Sm1 is { } opened) dock.TestFilterMap(opened.Map);   // pictures need the map in memory: list just the opened one
            var stats = (0, 0, 0);
            bool settled = await Until(() => { stats = dock.LibraryStats(); return stats.Item1 > 0 && stats.Item2 + 0 >= Math.Min(stats.Item1, source == 0 ? 80 : stats.Item1) && true; }, source == 0 ? 120 : 60);
            await Frames(20);
            stats = dock.LibraryStats();
            Say($"source {source}: {stats.Item1} rows, {stats.Item2} with a picture, {stats.Item3} placeholders");
            if (source != 0) Check(stats.Item1 > 0 && stats.Item2 == stats.Item1, $"every row of source {source} has a picture ({stats.Item2}/{stats.Item1})");
            if (source == 1) Check(stats.Item3 >= 2, $"the empty and the broken model show placeholders ({stats.Item3})");
            else Check(stats.Item2 >= Math.Min(stats.Item1, 80), $"SM1 rows in view and the first background ones have pictures ({stats.Item2}/{stats.Item1}, settled={settled})");
            ShotDock(name);
        }
        // Background fill reaches rows that were never scrolled into view
        if (sm1 && Thumbnails.CanRender)
        {
            dock.TestShowSource(0);
            if (MapHost.Sm1 is { } opened0) dock.TestFilterMap(opened0.Map);
            await Until(() => dock.LibraryStats() is var st && st.Item2 >= Math.Min(st.Item1, 300), 120);
            var st2 = dock.LibraryStats();
            Check(st2.Item2 >= Math.Min(st2.Item1, 300), $"background queue goes past the old 60-row limit ({st2.Item2}/{st2.Item1})");
        }

        // Rename
        dock.TestShowSource(1);
        await Frames(5);
        int row = dock.TestRowOf("crate");
        Check(row >= 0, "crate row found");
        string msg = dock.TestRename(row, "  Blue   crate ");
        Check(MapHost.Library.GetAsset("crate")?.Name == "Blue crate", $"renamed ({msg})");
        Check(MapHost.Library.GetAsset("crate") is not null, "id unchanged");
        msg = dock.TestRename(dock.TestRowOf("crate"), "   ");
        Check(MapHost.Library.GetAsset("crate")?.Name == "Blue crate" && msg.Contains("empty"), $"empty name rejected ({msg})");
        msg = dock.TestRename(dock.TestRowOf("crate"), "rock_pillar");
        Check(MapHost.Library.GetAsset("crate")?.Name == "Blue crate" && msg.Contains("already"), $"duplicate name rejected ({msg})");

        // Delete (right-click menu): refused while a prefab uses the asset; an unused one goes, with its placed copy, and Ctrl+Z brings both back
        msg = dock.ConfirmDeleteAsset("crate");
        Check(MapHost.Library.GetAsset("crate") is not null && msg.Contains("crate-pair"), $"deleting an asset a prefab uses is refused ({msg})");
        {
            var ph = plugin.Do("place", MapmakerPlugin.Op("place", ("asset", new System.Text.Json.Nodes.JsonObject { ["source"] = "custom", ["id"] = "broken-model" }), ("name", "doomed")));
            int objs = plugin.Session!.Document.Objects.Count;
            msg = dock.ConfirmDeleteAsset("broken-model");
            await Frames(5);
            Check(ph.Ok && MapHost.Library.GetAsset("broken-model") is null && plugin.Session.Document.Objects.Count == objs - 1 && dock.TestRowOf("broken-model") < 0, $"unused asset deleted with its placed copy ({msg})");
            plugin.History.GetHistoryUndoRedo(plugin.History.GetObjectHistoryId(MapHost.Root!)).Undo();
            await Frames(5);
            Check(MapHost.Library.GetAsset("broken-model") is not null && plugin.Session.Document.Objects.Count == objs, "undo brings the asset and its placed copy back");
        }

        // The imported FBX asset in the viewport
        if (pillar is not null && Thumbnails.CanRender)
        {
            var item = new LibraryItem(LibraryKind.Asset, pillar.Id, pillar.Name);
            var res = plugin.PlaceAt(item, new Godot.Vector3(0, 8, 0));
            Check(res.Ok, "imported FBX asset placed");
            var bb = pillar.BoundsInDocumentFrame!.Value;
            await plugin.Frame3D(bb.Min + new Num.Vector3(0, 8, 0) - new Num.Vector3(3), bb.Max + new Num.Vector3(0, 8, 0) + new Num.Vector3(3));
            await Frames(40);
            var vp = plugin.Viewport3D;
            if (vp is not null) { vp.GetTexture().GetImage().SavePng(Path.Combine(_out, "viewport-imported-fbx.png")); Say("shot viewport-imported-fbx"); }
        }
        try { Directory.Delete(work, true); } catch (Exception) { }
    }

    /// <summary>The owner's report: an extracted single part must not bring its neighbours. Before = the old build of one part (a copy of the whole definition), after = the part alone.</summary>
    async Task PartDemo()
    {
        var source = MapHost.Sm1!;
        int tarmac = source.IndexOf("tarmacgroundplane");
        string defn = tarmac >= 0 ? source.Scene.Objects[tarmac].Definition : "";
        int parts = tarmac >= 0 && source.Scene.Definitions.TryGetValue(defn, out var tdef) ? tdef.NearestLod().Count() : 0;
        int tall = Enumerable.Range(0, parts).FirstOrDefault(k => source.WorldBounds(new PickRef(tarmac, k)) is { } pb && pb.Max.Z - pb.Min.Z > 8f, -1);
        Check(tall >= 0, $"a tall part of tarmacgroundplane found ({tall} of {parts})");
        if (tall < 0) return;
        int Count(Node? n) => n is null ? 0 : n.GetChildren().OfType<MultiMeshInstance3D>().Sum(m => m.Multimesh?.InstanceCount ?? 0) + n.GetChildren().Sum(c => c is MultiMeshInstance3D ? 0 : Count(c));
        var whole = ContentFactory.BuildAsset(SmMapmaker.AssetRef.Sm1(source.Map, defn));
        var before = source.Instantiate(defn, tall);   // the old way: a copy of the whole definition
        var after = ContentFactory.BuildAsset(SmMapmaker.AssetRef.Sm1(source.Map, defn, tall));
        Say($"part {tall} instances: whole {Count(whole)}, old build {Count(before)}, now {Count(after)}");
        Check(Count(after) == 1 && Count(after) < Count(whole), $"one part draws only itself ({Count(after)} of {Count(whole)})");
        Check(Count(before) > Count(after), $"(the old build drew {Count(before)}: the neighbours came along)");
        MapHost.PickedSm1.Clear(); MapHost.PickedSm1.Add(new PickRef(tarmac, tall));
        var gr = dock.Extract("Generator block", "generator-block", "bounds-bottom-center", "mesh");
        var prefab = MapHost.Library.GetPrefab("generator-block");
        Check(gr.Ok && prefab?.Parts.Count == 1, "extracted one part to a prefab");
        MapHost.PickedSm1.Clear();
        var thumbs = MapmakerPlugin.Thumbs!;
        // Same camera, bigger than a list icon
        async Task<Image?> Shoot(Node3D? n) { if (n is null) return null; var b = ContentFactory.MeasureBounds(n); return await thumbs.RenderLarge(n, b, 360); }
        var imgBefore = await Shoot(before); var imgAfter = await Shoot(after);
        if (imgBefore is not null && imgAfter is not null)
        {
            imgBefore.SavePng(Path.Combine(_out, "part-before.png")); imgAfter.SavePng(Path.Combine(_out, "part-after.png"));
            Say($"part images {imgBefore.GetFormat()} {imgBefore.GetSize()} / {imgAfter.GetFormat()} {imgAfter.GetSize()}");
            imgBefore.Convert(Image.Format.Rgba8); imgAfter.Convert(Image.Format.Rgba8);
            var both = Image.CreateEmpty(360 * 2 + 12, 360, false, Image.Format.Rgba8);
            both.Fill(new Color(0.1f, 0.1f, 0.1f));
            both.BlitRect(imgBefore, new Rect2I(0, 0, 360, 360), new Vector2I(0, 0));
            both.BlitRect(imgAfter, new Rect2I(0, 0, 360, 360), new Vector2I(372, 0));
            both.SavePng(Path.Combine(_out, "part-before-after.png"));
            Say("shot part-before-after (left: before, the part with its neighbours; right: after, the part alone)");
        }
        whole?.Free();
    }

    async Task Drop(string[] files)
    {
        dock.HandleDrop(files);
        await Frames(3);
        await Until(() => !dock.Importing, 60);
        await Frames(5);
    }
}
