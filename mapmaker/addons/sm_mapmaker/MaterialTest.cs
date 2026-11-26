using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using SmClient;
using SmMapmaker;
using SysNum = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Test aid (agents, never the owner's editor): <c>godot.cmd [--windowed] --editor --path mapmaker -- --mm-materials --mm-lib &lt;dir&gt; [--mm-out &lt;dir&gt;]</c> with
/// <c>SM_MAPMAKER_CONTROL</c> set to a scratch folder. Walks the material path of the owner-approved brief (2026-11-25 "Textures and shader nodes"): five synthetic pictures
/// (base colour, a DirectX normal map, roughness, metallic, AO; generated here) imported and wired by name, the node editor's connect / disconnect / refusal / mapping steps, the
/// surface the game's builder (<see cref="MaterialBuild"/>) makes (channels, green flip, strength, UV scale, tangents against Godot's own), assigning to one of two walls with undo,
/// save (pictures beside the map), pack, and opening the pack in a clean library. Windowed, it also saves stills of the node editor and its preview. Steps go to
/// <c>materials-result.txt</c>; it prints <c>MATERIALS RESULT PASS|FAIL</c> and quits.
/// </summary>
public sealed class MaterialTest(MapmakerPlugin plugin, MapmakerDock dock)
{
    public static bool Wanted => OS.GetCmdlineUserArgs().Contains("--mm-materials");
    static string Arg(string name, string fallback) { var a = OS.GetCmdlineUserArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }

    SceneTree Tree => (SceneTree)Engine.GetMainLoop();
    readonly List<string> _log = new();
    bool _failed;
    string _out = "";

    async Task Frames(int n) { for (int i = 0; i < n; i++) await plugin.ToSignal(Tree, SceneTree.SignalName.ProcessFrame); }
    void Say(string m) { _log.Add(m); GD.Print("MATERIALS " + m); }
    void Check(bool ok, string what) { if (!ok) _failed = true; Say((ok ? "ok   " : "FAIL ") + what); }

    public async Task Run()
    {
        try { await RunCore(); }
        catch (Exception e) { _failed = true; Say("EXCEPTION " + e); }
        Say(_failed ? "RESULT FAIL" : "RESULT PASS");
        GD.Print(_failed ? "MATERIALS RESULT FAIL" : "MATERIALS RESULT PASS");
        try { File.WriteAllLines(Path.Combine(_out, "materials-result.txt"), _log); } catch (Exception) { }
        // the handles this test made and dropped are finalized now, while the engine is whole (see MaterialBuild.Cache)
        GC.Collect(); GC.WaitForPendingFinalizers();
        Tree.Quit(_failed ? 1 : 0);
    }

    // ---- synthetic pictures: a brick wall (64 x 32 px bricks, 4 px mortar, rows offset by half a brick), 256 x 256 ----

    const int Px = 256;

    static float Height(int x, int y)
    {
        int row = y / 32; int bx = (x + (row % 2) * 32) % 64; int by = y % 32;
        int edge = Math.Min(Math.Min(bx, 63 - bx), Math.Min(by, 31 - by));
        return edge < 2 ? 0f : edge < 6 ? (edge - 2) / 4f : 1f;   // mortar, a 4 px bevel, the brick face
    }

    static bool Mortar(int x, int y) => Height(x, y) <= 0f;

    static string Save(string dir, string file, Func<int, int, Color> pixel)
    {
        using var img = Image.CreateEmpty(Px, Px, false, Image.Format.Rgba8);
        for (int y = 0; y < Px; y++) for (int x = 0; x < Px; x++) img.SetPixel(x, y, pixel(x, y));
        string path = Path.Combine(dir, file);
        img.SavePng(path);
        return path;
    }

    /// <summary>The normal map in the OpenGL convention (green = up in the picture).</summary>
    static Color NormalGl(int x, int y)
    {
        float dx = (Height(Math.Min(Px - 1, x + 1), y) - Height(Math.Max(0, x - 1), y)) * 2f;
        float dyDown = (Height(x, Math.Min(Px - 1, y + 1)) - Height(x, Math.Max(0, y - 1))) * 2f;
        var n = new Vector3(-dx, dyDown, 1f).Normalized();   // up in the picture is -y: d(h)/d(up) = -dyDown, the normal's up part is its negative
        return new Color(n.X * 0.5f + 0.5f, n.Y * 0.5f + 0.5f, n.Z * 0.5f + 0.5f);
    }

    static IReadOnlyList<string> MakePictures(string dir)
    {
        Directory.CreateDirectory(dir);
        var rng = new Random(7);
        var tint = Enumerable.Range(0, 64).Select(_ => (float)rng.NextDouble() * 0.12f).ToArray();
        return
        [
            Save(dir, "brick_BaseColor.png", (x, y) => Mortar(x, y) ? new Color(0.62f, 0.6f, 0.55f) : new Color(0.55f + tint[(y / 32 * 4 + (x + y / 32 % 2 * 32) / 64) % 64], 0.24f, 0.17f)),
            Save(dir, "brick_Normal_DX.png", (x, y) => { var c = NormalGl(x, y); return new Color(c.R, 1f - c.G, c.B); }),   // DirectX: green down
            Save(dir, "brick_Roughness.png", (x, y) => Mortar(x, y) ? new Color(0.95f, 0.95f, 0.95f) : new Color(0.55f, 0.55f, 0.55f)),
            Save(dir, "brick_Metallic.png", (x, y) => new Color(0, 0, 0)),
            Save(dir, "brick_AO.png", (x, y) => { float h = Height(x, y); return new Color(0.45f + 0.55f * h, 0.45f + 0.55f * h, 0.45f + 0.55f * h); }),
        ];
    }

    static bool Same(StandardMaterial3D x, StandardMaterial3D y)
    {
        static string Pic(Texture2D? t) { if (t is null) return "-"; using var image = t.GetImage(); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.GetData())); }
        return Pic(x.AlbedoTexture) == Pic(y.AlbedoTexture) && Pic(x.NormalTexture) == Pic(y.NormalTexture) && Pic(x.RoughnessTexture) == Pic(y.RoughnessTexture)
            && Pic(x.MetallicTexture) == Pic(y.MetallicTexture) && Pic(x.AOTexture) == Pic(y.AOTexture)
            && x.RoughnessTextureChannel == y.RoughnessTextureChannel && x.MetallicTextureChannel == y.MetallicTextureChannel && x.AOTextureChannel == y.AOTextureChannel
            && x.AlbedoColor == y.AlbedoColor && x.Metallic == y.Metallic && x.Roughness == y.Roughness && x.NormalScale == y.NormalScale && x.Uv1Scale == y.Uv1Scale && x.Uv1Offset == y.Uv1Offset;
    }

    async Task RunCore()
    {
        _out = Arg("--mm-out", Path.Combine(Path.GetTempPath(), "mm-materials-" + System.Environment.ProcessId));
        Directory.CreateDirectory(_out);
        string lib = Arg("--mm-lib", Path.Combine(Path.GetTempPath(), "mm-materials-lib-" + System.Environment.ProcessId));
        string work = Path.Combine(lib, "work");
        MapHost.Library = new MapLibrary(Path.Combine(lib, "library-a"));
        await Frames(30);
        if (!plugin.EnsureSceneRoot()) { await Frames(60); plugin.EnsureSceneRoot(); }
        plugin.NewMap("Material test");
        await Frames(10);
        Check(plugin.Session is not null, "a map is open");
        var pics = MakePictures(Path.Combine(work, "pictures"));
        Check(pics.All(File.Exists), $"{pics.Count} synthetic pictures written");

        // two walls, 4 x 0.5 x 3 m
        JsonObject Wall(string name, float x) => MapmakerPlugin.Op("place", ("piece", new JsonObject { ["kind"] = "box" }), ("name", name),
            ("transform", new JsonObject { ["position"] = MapmakerPlugin.Vec(new SysNum.Vector3(x, 0, 0)), ["scale"] = MapmakerPlugin.Vec(new SysNum.Vector3(4, 0.5f, 3)) }));
        var placed = plugin.Do("Walls", Wall("Wall A", 0), Wall("Wall B", 6));
        Check(placed.Ok && placed.Created.Count == 2, "two walls placed");
        string a = placed.Created[0], b = placed.Created[1];
        var doc = () => plugin.Session!.Document;

        // a material, the pictures imported into it and wired by name (one step)
        var id = dock.NewMaterial("Brick", open: true);
        Check(id is not null && doc().FindMaterial(id) is not null, $"material created ({id})");
        var ed = dock.MaterialEditorWindow;
        Check(ed.MaterialId == id, "the node editor shows it");
        // Import images lives in the node editor window, not in the Mapmaker panel (owner 2026-11-27)
        Check(ed.FindChildren("*", "Button", true, false).OfType<Button>().Any(x => x.Name == MaterialEditor.ImportButtonName && x.Text == "Import images...") && ed.ImportButton.IsInsideTree(),
            "the node editor window has the Import images button");
        Check(!dock.FindChildren("*", "Button", true, false).OfType<Button>().Any(x => x.Text.StartsWith("Import images", StringComparison.Ordinal) && !ed.IsAncestorOf(x)),
            "the Mapmaker panel has no Import images button");
        var imported = ed.ImportImages(pics) ?? OpResult.Fail("no-editor", "the window imported nothing");
        Check(imported.Ok && doc().Images.Count == 5, $"5 pictures in the map ({string.Join("; ", imported.Errors.Select(e => e.Message))})");
        var g = doc().FindMaterial(id!)!;
        var m = g.Resolve();
        Check(m.BaseColorTexture?.Channel == TextureChannel.Rgb && m.MetallicTexture?.Channel == TextureChannel.R && m.RoughnessTexture?.Channel == TextureChannel.R
            && m.AoTexture?.Channel == TextureChannel.R && m.NormalTexture is not null, "all five channels wired by name: base colour, metallic, roughness, AO, normal");
        Check(m.NormalDirectX, "brick_Normal_DX is read as a DirectX normal map (green flip)");
        await Frames(5);
        Check(ed.Graph.GetChildren().OfType<GraphNode>().Count() == g.Nodes.Count, $"the graph view shows every node ({g.Nodes.Count})");
        Check(ed.Graph.GetConnectionList().Count == g.Links.Count, $"the graph view shows every link ({g.Links.Count})");

        // disconnect and connect again (one material-edit each)
        string metalTex = g.Links.First(l => l.To == "surface" && l.In == "metallic").From;
        Check(ed.Disconnect("surface", "metallic") && doc().FindMaterial(id!)!.Resolve().MetallicTexture is null, "disconnect metallic: the surface uses its value");
        Check(ed.Connect(metalTex, "color", "surface", "metallic") && doc().FindMaterial(id!)!.Resolve().MetallicTexture is not null, "connect it again");
        // refusals: nothing changes, the message is shown on the node editor
        string before = doc().FindMaterial(id!)!.ToNode().ToJsonString();
        Check(ed.AddNode(NodeTypes.SeparateRgb, new Vector2(-300, 700)), "add a Separate RGB node");
        var sep = doc().FindMaterial(id!)!.Nodes.Last(n => n.Type == NodeTypes.SeparateRgb).Id;
        string colourTex = doc().FindMaterial(id!)!.Links.First(l => l.To == "surface" && l.In == "base-color").From;
        ed.Connect(colourTex, "color", sep, "color");
        Check(!ed.Connect(sep, "r", "surface", "base-color") && ed.ErrorText.Contains("cannot feed"), $"a channel (float) into base colour is refused with a message ({ed.ErrorText})");
        Check(!ed.Connect(colourTex, "color", "surface", "normal") && ed.ErrorText.Length > 0, $"a colour into the normal input is refused ({ed.ErrorText})");
        Check(ed.DeleteNodes([sep]) && doc().FindMaterial(id!)!.ToNode().ToJsonString() == before, "delete the node: the graph is as before");
        // one mapping for the material: linked into one picture, it feeds all of them
        Check(ed.AddNode(NodeTypes.Mapping, new Vector2(-1000, 0)), "add a Mapping node");
        var map = doc().FindMaterial(id!)!.Nodes.Last(n => n.Type == NodeTypes.Mapping).Id;
        Check(ed.SetParams(map, new JsonObject { ["scale"] = new JsonArray(0.5, 0.5) }), "mapping scale 0.5 repeats per metre");
        Check(ed.Connect(map, "vector", colourTex, "vector"), $"link the mapping ({ed.ErrorText})");
        var g2 = doc().FindMaterial(id!)!;
        Check(g2.Links.Count(l => l.From == map) == 5 && g2.Resolve().UvScale == new SysNum.Vector2(0.5f, 0.5f), "the mapping feeds all five pictures; UV scale 0.5");
        // a second mapping linked into one picture only (a raw step, as a script could send it): the material would need two UV mappings, refused as unsupported-graph with its message
        Check(ed.AddNode(NodeTypes.Mapping, new Vector2(-1000, 300)), "add a second Mapping node");
        var map2 = doc().FindMaterial(id!)!.Nodes.Last(n => n.Type == NodeTypes.Mapping).Id;
        ed.SetParams(map2, new JsonObject { ["scale"] = new JsonArray(2, 2) });
        string before2 = doc().FindMaterial(id!)!.ToNode().ToJsonString();
        Check(!ed.Edit("link", new JsonObject { ["link"] = new JsonArray(new JsonObject { ["from"] = map2, ["out"] = "vector", ["to"] = colourTex, ["in"] = "vector" }) })
            && ed.ErrorText.Contains("same mapping") && doc().FindMaterial(id!)!.ToNode().ToJsonString() == before2, $"two mappings in one material are refused, nothing changes ({ed.ErrorText})");
        Check(ed.DeleteNodes([map2]), "delete the second mapping");
        // a picture added later picks up the material's mapping when it is linked
        Check(ed.AddNode(NodeTypes.ImageTexture, new Vector2(-700, 1200)), "add an Image Texture node");
        var late = doc().FindMaterial(id!)!.Nodes.Last(n => n.Type == NodeTypes.ImageTexture).Id;
        ed.SetParams(late, new JsonObject { ["image"] = doc().Images.First(i => i.Name == "brick_Roughness").Id }, "image");
        string roughTex = doc().FindMaterial(id!)!.Links.First(l => l.To == "surface" && l.In == "roughness").From;
        Check(ed.Connect(late, "color", "surface", "roughness") && doc().FindMaterial(id!)!.Links.Any(l => l.From == map && l.To == late), $"a later picture linked into roughness takes the mapping too ({ed.ErrorText})");
        Check(ed.Connect(roughTex, "color", "surface", "roughness") && ed.DeleteNodes([late]), "the first roughness picture back; the extra node deleted");
        g2 = doc().FindMaterial(id!)!;
        var nm = g2.Nodes.First(n => n.Type == NodeTypes.NormalMap).Id;
        Check(ed.SetParams(nm, new JsonObject { ["strength"] = 1.5 }), "normal strength 1.5");
        await Frames(5);
        Check(ed.Graph.GetConnectionList().Count == doc().FindMaterial(id!)!.Links.Count, "the graph view follows the document after the steps");

        // the surface the game builds
        using var cache = new MaterialBuild.Cache(doc(), plugin.Session!.Path, MapHost.Library);
        var mat = cache.For(id);
        Check(mat is not null && cache.Notes.Count == 0, $"the game's builder makes the surface ({string.Join("; ", cache.Notes)})");
        if (mat is not null)
        {
            Check(mat.AlbedoTexture is not null && mat.AlbedoColor.IsEqualApprox(Colors.White), "albedo: the picture (colour 1)");
            Check(mat.MetallicTexture is not null && mat.MetallicTextureChannel == BaseMaterial3D.TextureChannel.Red && mat.Metallic == 1f, "metallic: the picture's red channel");
            Check(mat.RoughnessTexture is not null && mat.RoughnessTextureChannel == BaseMaterial3D.TextureChannel.Red, "roughness: the picture's red channel");
            Check(mat.AOEnabled && mat.AOTexture is not null && mat.AOTextureChannel == BaseMaterial3D.TextureChannel.Red, "AO: the picture's red channel");
            Check(mat.NormalEnabled && mat.NormalTexture is not null && Mathf.IsEqualApprox(mat.NormalScale, 1.5f), "normal map on, strength 1.5");
            Check(mat.Uv1Scale.IsEqualApprox(new Vector3(0.5f, 0.5f, 1f)), $"uv1 scale {mat.Uv1Scale}");
            using var built = mat.NormalTexture!.GetImage();
            using var source = Image.LoadFromFile(pics[1]);
            int probeX = 20, probeY = 35;   // on a horizontal bevel: the green channel is far from 0.5
            float gotG = built.GetPixel(probeX, probeY).G, srcG = source.GetPixel(probeX, probeY).G;
            Check(Math.Abs(gotG - (1f - srcG)) < 0.01f && Math.Abs(srcG - 0.5f) > 0.05f, $"DirectX green flipped: picture {srcG:0.000}, built {gotG:0.000}");
            Check(built.HasMipmaps() && mat.TextureFilter == BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic, "mipmaps and anisotropic filtering");
        }

        // tangents: ours (MaterialBuild.Mesh from RenderMesh) against Godot's own MikkTSpace on the same triangles
        var wall = doc().Find(a)!;
        var rm = RenderMesh.Build(EditMesh.Of(wall), wall.Transform.Scale, SysNum.Matrix4x4.CreateScale(wall.Transform.Scale));
        var ours = MaterialBuild.Mesh(rm);
        var arrays = ours.SurfaceGetArrays(0);
        using var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array(); var nors = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array(); var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        for (int i = 0; i < verts.Length; i++) { st.SetNormal(nors[i]); st.SetUV(uvs[i]); st.AddVertex(verts[i]); }
        st.GenerateTangents();
        var godotTan = st.CommitToArrays()[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
        var ourTan = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
        int agree = 0;
        for (int i = 0; i < verts.Length; i++)
        {
            var tg = new Vector3(godotTan[i * 4], godotTan[i * 4 + 1], godotTan[i * 4 + 2]); var to = new Vector3(ourTan[i * 4], ourTan[i * 4 + 1], ourTan[i * 4 + 2]);
            var bg = nors[i].Cross(tg) * godotTan[i * 4 + 3]; var bo = nors[i].Cross(to) * ourTan[i * 4 + 3];
            if (tg.Dot(to) > 0.9f && bg.Dot(bo) > 0.9f) agree++;
        }
        Check(agree == verts.Length, $"tangents agree with Godot's: {agree} of {verts.Length} corners (tangent and binormal)");

        // assign to one of two walls, undo, assign again
        plugin.Select([a]);
        await Frames(2);
        var assigned = dock.AssignSelected(id);
        Check(assigned.Ok && doc().Find(a)!.Material == id && doc().Find(b)!.Material is null, "assigned to Wall A only (object level)");
        Check(plugin.UndoLast() && doc().Find(a)!.Material is null, "undo takes it off again");
        await Frames(2);
        Check(plugin.NodeOf(a)?.GetNodeOrNull<MeshInstance3D>("Content/Shape")?.MaterialOverride is StandardMaterial3D { AlbedoTexture: null }, "and the editor draws it plain again");
        Check(dock.AssignSelected(id, [a]).Ok && doc().Find(a)!.Material == id, "assigned again");
        await Frames(2);
        var shapeA = plugin.NodeOf(a)?.GetNodeOrNull<MeshInstance3D>("Content/Shape");
        var shapeB = plugin.NodeOf(b)?.GetNodeOrNull<MeshInstance3D>("Content/Shape");
        Check(shapeA?.MaterialOverride is StandardMaterial3D { NormalTexture: not null, AlbedoTexture: not null } && shapeA.Mesh is ArrayMesh am && am.SurfaceGetFormat(0).HasFlag(Mesh.ArrayFormat.FormatTangent),
            "the editor draws Wall A with the material (the game's builder: textures, tangents)");
        Check(shapeB?.MaterialOverride is StandardMaterial3D { AlbedoTexture: null }, "Wall B keeps the default look");
        // Edit mode: while a face drag previews, the piece is drawn with its material on the preview shape; cancel keeps it textured
        plugin.Select([a]);
        await Frames(2);
        if (plugin.SetEditMode(true) && plugin.SelectComponent(a, MeshTopology.Box.FaceNamed("+x")) && plugin.BeginEdit(MeshEditKind.Move, 0))
        {
            plugin.SetEditAmount(new SysNum.Vector3(1, 0, 0));
            await Frames(2);
            var dragged = plugin.NodeOf(a)?.GetNodeOrNull<MeshInstance3D>("Content/Shape");
            var aabb = dragged?.Mesh?.GetAabb() ?? new Aabb();
            Check(dragged?.MaterialOverride is StandardMaterial3D { AlbedoTexture: not null } && aabb.Size.X > 1.2f, $"during a drag the preview shape is drawn with the material (unit width {aabb.Size.X:0.00})");
            plugin.CancelEdit();
            await Frames(2);
            Check(plugin.NodeOf(a)?.GetNodeOrNull<MeshInstance3D>("Content/Shape")?.MaterialOverride is StandardMaterial3D { AlbedoTexture: not null } && doc().Find(a)!.Mesh is null,
                "after cancel the stored shape is drawn with the material again");
            plugin.SetEditMode(false);
            await Frames(2);
        }
        else Check(false, "could not start an Edit-mode drag on Wall A");
        var notDrawn = plugin.Do("Spawn", MapmakerPlugin.Op("place", ("piece", new JsonObject { ["kind"] = "spawn-area", ["side"] = "marines" }), ("name", "Spawn"),
            ("transform", new JsonObject { ["position"] = MapmakerPlugin.Vec(new SysNum.Vector3(40, 0, 0)) })));
        var refused = dock.AssignSelected(id, notDrawn.Created);
        Check(!refused.Ok, "a spawn area takes no material (only drawn pieces)");

        // stills: the node editor with all five channels connected and its preview (windowed only)
        if (Thumbnails.CanRender)
        {
            ed.Open(id!);
            await Frames(30);
            ed.FitView();
            await Frames(40);
            ed.GetTexture().GetImage().SavePng(Path.Combine(_out, "node-editor.png"));
            ed.PreviewViewport.GetTexture().GetImage().SavePng(Path.Combine(_out, "node-editor-preview.png"));
            ed.Hide();
            // the Mapmaker panel's Library tab with its Materials section (no Import images button there any more)
            dock.ShowTab(DockTab.Library);
            await Frames(20);
            if (dock.GetParent() is Control && dock.IsVisibleInTree())
            {
                var r = dock.GetGlobalRect();
                var whole = ((SceneTree)Engine.GetMainLoop()).Root.GetTexture().GetImage();
                var clip = new Rect2I((Vector2I)r.Position, (Vector2I)r.Size).Intersection(new Rect2I(0, 0, whole.GetWidth(), whole.GetHeight()));
                if (clip.Size.X > 0 && clip.Size.Y > 0) whole.GetRegion(clip).SavePng(Path.Combine(_out, "panel-materials.png"));
                whole.SavePng(Path.Combine(_out, "editor-window.png"));
            }
            plugin.Select([]);
            await plugin.Frame3D(new SysNum.Vector3(-2.2f, -0.5f, 0f), new SysNum.Vector3(8.2f, 0.5f, 3.1f));
            await Frames(60);
            if (plugin.Viewport3D is { } vp) vp.GetTexture().GetImage().SavePng(Path.Combine(_out, "editor-walls.png"));
            Say("shots node-editor.png, node-editor-preview.png, editor-walls.png (Wall A textured, Wall B default)");
        }
        else Say("headless: stills skipped");

        // save: the pictures go beside the map
        string mapsA = Path.Combine(lib, "maps-a");
        Directory.CreateDirectory(mapsA);
        string mapPath = Path.Combine(mapsA, "material-test.smmap");
        Check(plugin.SaveMap(mapPath), "saved");
        Check(Directory.Exists(MapImages.BesideDir(mapPath)) && Directory.GetFiles(MapImages.BesideDir(mapPath)).Length == 5, "five pictures beside the map (images/)");
        Check(MapImages.Missing(doc(), mapPath, MapHost.Library).Count == 0, "every picture resolves");
        // preview = saved = in game: the surface the client builds from the saved file (MaterialBuild.Cache, as CustomMapView would) equals the one the editor draws
        var savedDoc = MapDocument.Parse(File.ReadAllText(mapPath));
        using var gameCache = new MaterialBuild.Cache(savedDoc, mapPath, new MapLibrary(Path.Combine(lib, "empty-library")));
        var game = gameCache.For(savedDoc.Objects.First(o => o.Name == "Wall A").Material);
        var view = plugin.NodeOf(a)?.GetNodeOrNull<MeshInstance3D>("Content/Shape")?.MaterialOverride as StandardMaterial3D;
        Check(game is not null && view is not null && Same(game, view), "the saved map builds the surface the editor shows (same pictures, channels, values), from beside the map alone");

        // pack, then open it with a clean library on a "second PC"
        string? pack = dock.PackMap(Path.Combine(work, "material-test.smpack"));
        Check(pack is not null && File.Exists(pack), $"packed ({pack})");
        if (pack is not null)
        {
            using (var z = ZipFile.OpenRead(pack)) Check(z.Entries.Count == 6, $"the pack holds the map and five pictures ({z.Entries.Count} entries)");
            var cleanLib = new MapLibrary(Path.Combine(lib, "library-b"));
            MapHost.Library = cleanLib;
            string cleanMaps = Path.Combine(lib, "maps-b");
            string? opened = dock.OpenPack(pack, cleanMaps);
            await Frames(5);
            Check(opened is not null && plugin.Session?.Path is { } p2 && p2.StartsWith(Path.GetFullPath(cleanMaps), StringComparison.OrdinalIgnoreCase), $"the pack opened from the clean folder ({opened})");
            var d2 = plugin.Session!.Document;
            Check(d2.Materials.Count == 1 && d2.Objects.First(o => o.Name == "Wall A").Material == id && d2.Objects.First(o => o.Name == "Wall B").Material is null, "the material and its assignment came along");
            Check(MapImages.Missing(d2, plugin.Session.Path, cleanLib).Count == 0, "every picture resolves in the clean library");
            Check(Directory.Exists(MapImages.LibraryDir(cleanLib)) && Directory.GetFiles(MapImages.LibraryDir(cleanLib)).Length == 5, "the clean library now holds the five pictures");
            using var cache2 = new MaterialBuild.Cache(d2, plugin.Session.Path, cleanLib);
            var mat2 = cache2.For(id);
            Check(mat2 is not null && cache2.Notes.Count == 0 && mat2.NormalTexture is not null && mat2.AlbedoTexture is not null, "the same surface builds on the second PC");
            Check(CustomMap.HashOf(d2) == CustomMap.HashOf(MapDocument.Parse(File.ReadAllText(mapPath))), "the opened map hashes as the saved one (server and clients agree)");
        }
    }
}
