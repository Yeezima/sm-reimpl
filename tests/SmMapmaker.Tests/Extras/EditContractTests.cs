using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using SmMapmaker;
using SmSim;
using Xunit;

namespace SmMapmaker.Tests;

/// <summary>
/// The edit-mode contract (docs/mapmaker.md "Edit mode contract"; owner-approved brief 2026-11-25 "Blender-style controls"): the brief's acceptance table at the data level, on synthetic maps only.
/// </summary>
public sealed class EditContractTests
{
    const string Floor = """{"op":"place","piece":{"kind":"box"},"name":"floor","transform":{"position":[0,0,-1],"scale":[60,60,1]}}""";
    const string Wall = """{"op":"place","piece":{"kind":"box"},"name":"wall","transform":{"position":[0,0,0],"scale":[4,2,1]}}""";
    const string Neighbour = """{"op":"place","piece":{"kind":"box"},"name":"neighbour","transform":{"position":[6,0,0],"scale":[2,2,2]}}""";

    static OpResult Run(MapSession s, string json)
    {
        var r = s.Apply(json);
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Code + ": " + e.Message)));
        return r;
    }

    static OpResult Fails(MapSession s, string json, string code)
    {
        var r = s.Apply(json);
        Assert.False(r.Ok);
        Assert.Equal(code, r.Errors[0].Code);
        return r;
    }

    static MapSession Map(Lab lab, params string[] ops)
    {
        var s = lab.Session(withSm1: false);
        foreach (var op in ops) Run(s, op);
        return s;
    }

    static Vector3 Sized(MapObject o, int vertex) => EditMesh.Of(o).Vertices[vertex] * o.Transform.Scale;

    static void Near(Vector3 expected, Vector3 actual, float tol = 1e-5f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    // ---- one-sided wall edit ----

    [Fact]
    public void Pulling_the_plus_x_face_of_a_4x2x1_wall_by_1_m_makes_it_5x2x1_and_keeps_the_opposite_face_and_the_neighbour()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, Wall, Neighbour);   // o1 floor, o2 wall, o3 neighbour
        var before = s.Document.ToJson();
        var neighbour = s.Document.Get("o3");
        var wall = s.Document.Get("o2");
        var minusX = MeshTopology.Box.Faces[MeshTopology.Box.FaceNamed("-x")].Select(v => Sized(wall, v)).ToArray();
        Near(new Vector3(4, 2, 1), MeshDims.Of(wall));

        // a drag: previews only, the readout reaches 5 x 2 x 1, the document does not change
        var drag = new MeshDrag(s, [("o2", new ComponentSelection(ComponentMode.Face, [MeshTopology.Box.FaceNamed("+x")]))], MeshEdit.Move(new Vector3(0.3f, 0, 0), 0));
        Near(new Vector3(4.3f, 2, 1), drag.Dims("o2"));
        Assert.True(drag.Update(MeshEdit.Move(new Vector3(0.7f, 0.4f, 0), 0)));   // the axis constraint keeps only X
        Near(new Vector3(4.7f, 2, 1), drag.Dims("o2"));
        Assert.True(drag.Update(MeshEdit.Move(new Vector3(1, 0, 0), 0)));
        Near(new Vector3(5, 2, 1), drag.Dims("o2"));
        Assert.Equal(before, s.Document.ToJson());

        // cancel: nothing changed, no undo step
        int depth = s.UndoDepth;
        drag.Cancel();
        Assert.Equal(before, s.Document.ToJson());
        Assert.Equal(depth, s.UndoDepth);

        // the same drag committed: one undo step, the preview's exact shape
        drag = new MeshDrag(s, [("o2", new ComponentSelection(ComponentMode.Face, [5]))], MeshEdit.Move(new Vector3(0.5f, 0, 0), 0));
        drag.Update(MeshEdit.Move(new Vector3(1, 0, 0), 0));
        var preview = drag.Preview["o2"];
        Assert.True(drag.Commit().Ok);
        Assert.Equal(depth + 1, s.UndoDepth);
        wall = s.Document.Get("o2");
        Assert.True(preview.SameAs(wall.Mesh));
        Near(new Vector3(5, 2, 1), MeshDims.Of(wall));
        Assert.Equal(new Vector3(4, 2, 1), wall.Transform.Scale);   // the Scale fields are untouched; the readout is the geometry
        Assert.Equal(minusX, MeshTopology.Box.Faces[4].Select(v => Sized(wall, v)).ToArray());   // the opposite face keeps its coordinates bit for bit
        Near(new Vector3(3, 1, 1), Sized(wall, 7));
        Assert.Equal(neighbour, s.Document.Get("o3"));

        // undo is the stock shape again (no mesh stored, the original text); redo the edit
        Assert.True(s.Undo().Ok);
        Assert.Equal(before, s.Document.ToJson());
        Assert.Null(s.Document.Get("o2").Mesh);
        Assert.True(s.Redo().Ok);
        Near(new Vector3(5, 2, 1), MeshDims.Of(s.Document.Get("o2")));

        // save and reopen: the same shape and dimensions
        var file = Path.Combine(lab.Dir, "wall.smmap");
        s.Save(file);
        var reopened = MapSession.Load(file, lab.Library);
        Assert.Equal(s.Document.ToJson(), reopened.Document.ToJson());
        Near(new Vector3(5, 2, 1), MeshDims.Of(reopened.Document.Get("o2")));

        // in play: the wall's collision now reaches x = 3; the opposite end stays at x = -2
        var map = CustomMap.Compile(reopened.Document);
        var layer = map.Content.Layers.First(l => l.Name == SmContent.MapContent.CharacterLayer);
        var wallVerts = layer.Vertices.Where(v => v.Z >= 0 && v.Z <= 1 && MathF.Abs(v.Y) <= 1 && v.X <= 3.0001f && v.X >= -2.0001f).ToArray();   // the neighbour starts at x = 5, the floor at x = -30
        Assert.Contains(wallVerts, v => v.X == 3);
        Assert.Contains(wallVerts, v => v.X == -2);
    }

    [Fact]
    public void A_typed_edit_through_edit_mesh_equals_the_drag_and_an_edit_and_its_reverse_restore_the_hash()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, Wall);
        ulong hash0 = CustomMap.Compile(s.Document).Hash;
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"face","ids":[5],"move":[1,0,0]}""");
        Assert.NotEqual(hash0, CustomMap.Compile(s.Document).Hash);   // the edited shape is in the map's identity
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"face","ids":[5],"move":[-1,0,0]}""");
        Assert.Null(s.Document.Get("o2").Mesh);   // back to the stock shape: stored as no mesh
        Assert.Equal(hash0, CustomMap.Compile(s.Document).Hash);
    }

    // ---- component selection ----

    [Fact]
    public void Moving_one_ramp_vertex_moves_only_that_vertex()
    {
        using var lab = new Lab();
        var s = Map(lab, """{"op":"place","piece":{"kind":"ramp"},"transform":{"position":[0,0,0],"scale":[4,8,2]}}""");
        var before = EditMesh.Of(s.Document.Get("o1")).Vertices.ToArray();
        Run(s, """{"op":"edit-mesh","id":"o1","mode":"vertex","ids":[4],"move":[0,0,1]}""");
        var after = s.Document.Get("o1").Mesh!.Vertices;
        for (int i = 0; i < before.Length; i++)
            if (i == 4) Near(new Vector3(2, 4, 3), after[i] * new Vector3(4, 8, 2)); else Assert.Equal(before[i], after[i]);
        Near(new Vector3(4, 8, 3), MeshDims.Of(s.Document.Get("o1")));
    }

    [Fact]
    public void Scaling_one_edge_numerically_scales_it_about_its_midpoint()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall);
        int edge = MeshTopology.Box.EdgeId(4, 5);   // the top edge on the -Y side
        Assert.True(edge >= 0);
        Run(s, $$"""{"op":"edit-mesh","id":"o1","mode":"edge","ids":[{{edge}}],"scale":[0.5,1,1],"axis":"x"}""");
        var o = s.Document.Get("o1");
        Near(new Vector3(-1, -1, 1), Sized(o, 4));
        Near(new Vector3(1, -1, 1), Sized(o, 5));
        foreach (var v in new[] { 0, 1, 2, 3, 6, 7 }) Assert.Equal(MeshTopology.Box.StockVertices[v], o.Mesh!.Vertices[v]);
    }

    [Fact]
    public void Scaling_a_columns_top_face_tapers_it_and_its_base_stays_planted()
    {
        using var lab = new Lab();
        var s = Map(lab, """{"op":"place","piece":{"kind":"column"},"transform":{"position":[0,0,0],"scale":[1.5,1.5,4]}}""");
        Run(s, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[1],"scale":0.5}""");
        var o = s.Document.Get("o1");
        var m = o.Mesh!;
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(MeshTopology.Column12.StockVertices[i], m.Vertices[i]);   // the base is untouched
            var top = Sized(o, 12 + i);
            var stock = MeshTopology.Column12.StockVertices[12 + i] * o.Transform.Scale;
            Near(new Vector3(stock.X * 0.5f, stock.Y * 0.5f, 4), top);              // the top ring at half its radius, same height
        }
        Near(new Vector3(1.5f, 1.5f, 4), MeshDims.Of(o));
    }

    [Fact]
    public void Faces_that_share_vertices_move_them_once_and_unselected_objects_never_move()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall, Neighbour, """{"op":"place","piece":{"kind":"box"},"name":"outside","transform":{"position":[-8,0,0],"scale":[2,2,2]}}""");
        var outside = s.Document.Get("o3");
        // two objects in Edit mode, each with two faces that share an edge (+X and +Y share vertices 3 and 7); the third object is outside the selection
        Run(s, """{"op":"edit-mesh","targets":[{"id":"o1","mode":"face","ids":[5,3]},{"id":"o2","mode":"face","ids":[5,3]}],"move":[0,0,1]}""");
        foreach (var id in new[] { "o1", "o2" })
        {
            var o = s.Document.Get(id);
            var sz = o.Transform.Scale;
            Near(new Vector3(0.5f * sz.X, 0.5f * sz.Y, sz.Z + 1), Sized(o, 7));   // moved once (+1), not twice
            Near(new Vector3(0.5f * sz.X, 0.5f * sz.Y, 1), Sized(o, 3));
            foreach (var v in new[] { 0, 4 }) Assert.Equal(MeshTopology.Box.StockVertices[v], o.Mesh!.Vertices[v]);
        }
        Assert.Equal(outside, s.Document.Get("o3"));
    }

    [Fact]
    public void Rotating_a_face_turns_it_about_its_centre_in_the_objects_axes()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall);
        Run(s, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[1],"rotate":90,"axis":"z"}""");
        var o = s.Document.Get("o1");
        Near(new Vector3(1, -2, 1), Sized(o, 4), 1e-4f);   // (-2, -1, 1) turned 90 degrees about the top face's centre
    }

    [Fact]
    public void Bad_selections_and_flat_shapes_are_refused_and_change_nothing()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall);
        var before = s.Document.ToJson();
        Fails(s, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[9],"move":[1,0,0]}""", "unknown-component");
        Fails(s, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[5],"move":[-4,0,0]}""", "bad-mesh");   // the +X face pushed onto the -X face: no inside
        Fails(s, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[],"move":[1,0,0]}""", "empty-selection");
        Fails(s, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[5],"rotate":10}""", "bad-op");          // a rotation needs an axis
        Assert.Equal(before, s.Document.ToJson());
        Assert.Throws<MapException>(() => new MeshDrag(s, [("o1", new ComponentSelection(ComponentMode.Vertex, [99]))], MeshEdit.Move(Vector3.UnitX)));
    }

    // ---- every Build category ----

    [Fact]
    public void Every_piece_kind_takes_an_edit_and_compiles_from_its_edited_shape()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor);
        foreach (var kind in PieceKind.All)
        {
            var side = kind.HasSide ? ",\"side\":\"marines\"" : "";
            var r = Run(s, """{"op":"place","piece":{"kind":"@K@"@S@},"transform":{"position":[0,0,0]}}""".Replace("@K@", kind.Id).Replace("@S@", side));
            var id = r.Created[0];
            Run(s, $$"""{"op":"edit-mesh","id":"{{id}}","mode":"face","ids":[1],"move":[0,0,1]}""");   // every topology's face 1 is its top (the ramp's slope)
            Assert.NotNull(s.Document.Get(id).Mesh);
        }
        var wall = Run(s, """{"op":"place","piece":{"kind":"move-wall","shape":"cylinder"}}""").Created[0];
        Run(s, $$"""{"op":"edit-mesh","id":"{{wall}}","mode":"vertex","ids":[0],"move":[1,0,0]}""");
        Assert.Equal("cylinder16", s.Document.Get(wall).Mesh!.Topology.Name);
        var map = CustomMap.Compile(s.Document);
        Assert.DoesNotContain(map.Notes, n => n.Contains("left out"));
        Assert.Contains("cm_start_marines_1", map.Volumes.Keys);
        Assert.Contains("marines_blocker_1", map.Volumes.Keys);
        Assert.Contains("mkr_objective_1", map.Volumes.Keys);
        Assert.Contains("cm_kill_1", map.Volumes.Keys);
        Assert.All(map.Content.Walls, w => Assert.NotNull(w.Vertices));
        Assert.NotEmpty(map.Content.Props);
        // a shape change of a round wall back to a box resets its (cylinder) mesh, with a warning
        var r2 = Run(s, $$"""{"op":"set-piece","id":"{{wall}}","shape":"box"}""");
        Assert.Null(s.Document.Get(wall).Mesh);
        Assert.Single(r2.Warnings);
    }

    [Fact]
    public void An_edited_players_only_wall_and_cover_keep_their_kind_and_take_the_edited_shape()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, """{"op":"place","piece":{"kind":"move-wall"},"transform":{"position":[0,0,0],"scale":[4,1,3]}}""",
            """{"op":"place","piece":{"kind":"cover"},"transform":{"position":[10,0,0],"scale":[1.2,3,1.5]}}""");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"face","ids":[5],"move":[1,0,0]}""");
        Run(s, """{"op":"edit-mesh","id":"o3","mode":"face","ids":[5],"move":[1,0,0]}""");
        var map = CustomMap.Compile(s.Document);
        var wall = Assert.Single(map.Content.Walls);
        Assert.Equal(3f, wall.Vertices!.Max(v => v.X));      // movement only (CollisionWorld.Walls), its edited end at x = 3
        Assert.Equal(-2f, wall.Vertices!.Min(v => v.X));
        var cover = Assert.Single(map.Content.Props);
        Assert.Equal(11.6f, cover.Vertices.Max(v => v.X), 3);  // a prop: the penetration rule of Classic's cover is unchanged
        var character = map.Content.Layers.First(l => l.Name == SmContent.MapContent.CharacterLayer);
        Assert.DoesNotContain(character.Vertices, v => v.X > 2.5f && v.X < 3.5f && v.Z > 0.5f);   // the players-only wall is not in the shot mesh
    }

    // ---- gameplay volumes ----

    [Fact]
    public void An_asymmetric_capture_point_activates_inside_its_shape_and_not_inside_only_its_bounding_box()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, """{"op":"place","piece":{"kind":"capture-point"},"transform":{"position":[0,0,0],"scale":[8,8,4]}}""");
        // vertex 0 of the bottom ring and 16 above it (angle 0, at x = +4) pulled out by 4 m: a thin spike toward +X
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"vertex","ids":[0,16],"move":[4,0,0]}""");
        Near(new Vector3(12, 8, 4), MeshDims.Of(s.Document.Get("o2")), 1e-4f);
        var map = CustomMap.Compile(s.Document);
        var vol = map.Volumes["mkr_objective_1"];
        var point = Assert.Single(map.Content.ControlPoints);
        Near(new Vector3(12, 8, 4), point.Scale, 1e-4f);   // the record is the mesh's box (both horizontal sizes, not X twice)
        Assert.True(vol.Contains(new Vector3(0, 0, 2)));        // inside the shape
        Assert.True(vol.Contains(new Vector3(6, 0, 2)));        // on the spike's axis
        var inBoxOnly = new Vector3(6, 3.5f, 2);                // inside the box (x -4..8, y -4..4) but beside the thin spike
        Assert.True(inBoxOnly.X <= vol.Max.X && inBoxOnly.Y <= vol.Max.Y && inBoxOnly.X >= vol.Min.X && inBoxOnly.Y >= vol.Min.Y);
        Assert.False(vol.Contains(inBoxOnly));
        Assert.False(vol.Contains(new Vector3(0, 0, 4.5f)));    // above it
    }

    [Fact]
    public void A_tapered_kill_zone_and_an_edited_spawn_area_bound_by_their_shapes()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, """{"op":"place","piece":{"kind":"kill-zone"},"transform":{"position":[0,0,0],"scale":[10,10,4]}}""",
            """{"op":"place","piece":{"kind":"spawn-area","side":"chaos"},"transform":{"position":[20,0,0],"scale":[10,10,3]}}""");
        Run(s, """{"op":"edit-mesh","id":"o2","mode":"face","ids":[1],"scale":0.2}""");
        Run(s, """{"op":"edit-mesh","id":"o3","mode":"face","ids":[5],"move":[5,0,0]}""");
        var map = CustomMap.Compile(s.Document);
        var kill = map.Volumes["cm_kill_1"];
        Assert.True(kill.Contains(new Vector3(0, 0, 3.5f)));
        Assert.False(kill.Contains(new Vector3(4.5f, 4.5f, 3.5f)));   // a top corner of the box, outside the pyramid
        var area = Assert.Single(map.Content.StartAreas);
        Assert.Equal(new Vector3(15, 10, 3), area.Scale);
        Assert.Equal(22.5f, area.Position.X);   // the box of the edited area: x 15..30
        Assert.True(map.Volumes["cm_start_chaos_1"].Contains(new Vector3(28, 0, 1)));
    }

    [Fact]
    public void The_volume_test_counts_a_point_on_a_shared_diagonal_once_and_is_closed_under_the_tie_rule()
    {
        // a unit cube made of the box topology: points on the top and side faces' diagonals, edges and corners
        var (v, i, _) = EditMesh.Stock(MeshTopology.Box).Triangulate();
        var vol = new MeshVolume("cube", v, i);
        Assert.True(vol.Contains(new Vector3(0, 0, 0.5f)));
        Assert.True(vol.Contains(new Vector3(0.1f, 0.1f, 0.5f)));   // over the top and bottom faces' diagonals
        Assert.False(vol.Contains(new Vector3(0, 0, 1.0001f)));
        Assert.False(vol.Contains(new Vector3(0.6f, 0, 0.5f)));
        for (float x = -0.45f; x < 0.5f; x += 0.15f)
            for (float y = -0.45f; y < 0.5f; y += 0.15f)
                Assert.True(vol.Contains(new Vector3(x, y, 0.5f)), $"({x}, {y})");
    }

    // ---- materials and images ----

    /// <summary>A synthetic PNG: a valid signature and IHDR with the given size and a seed byte so each has its own SHA-256 (the core reads headers only; never original bytes).</summary>
    static string Png(Lab lab, string name, int seed)
    {
        var d = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), 256);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), 256);
        d[40] = (byte)seed;
        var path = Path.Combine(lab.Dir, name);
        File.WriteAllBytes(path, d);
        return path;
    }

    static string FiveChannelGraph(IReadOnlyList<string> img) => """
        {"nodes":[
          {"id":"map","type":"mapping","params":{"scale":[0.5,0.5]}},
          {"id":"albedo","type":"image-texture","params":{"image":"@0@"}},
          {"id":"normal-img","type":"image-texture","params":{"image":"@1@"}},
          {"id":"rough","type":"image-texture","params":{"image":"@2@"}},
          {"id":"orm","type":"image-texture","params":{"image":"@3@"}},
          {"id":"ao-img","type":"image-texture","params":{"image":"@4@"}},
          {"id":"nm","type":"normal-map","params":{"convention":"directx","strength":1.5}},
          {"id":"split","type":"separate-rgb"},
          {"id":"surface","type":"pbr-surface"},
          {"id":"out","type":"output"}],
         "links":[
          {"from":"map","out":"vector","to":"albedo","in":"vector"},{"from":"map","out":"vector","to":"normal-img","in":"vector"},
          {"from":"map","out":"vector","to":"rough","in":"vector"},{"from":"map","out":"vector","to":"orm","in":"vector"},{"from":"map","out":"vector","to":"ao-img","in":"vector"},
          {"from":"albedo","out":"color","to":"surface","in":"base-color"},
          {"from":"normal-img","out":"color","to":"nm","in":"color"},{"from":"nm","out":"normal","to":"surface","in":"normal"},
          {"from":"rough","out":"color","to":"surface","in":"roughness"},
          {"from":"orm","out":"color","to":"split","in":"color"},{"from":"split","out":"b","to":"surface","in":"metallic"},
          {"from":"ao-img","out":"alpha","to":"surface","in":"ao"},
          {"from":"surface","out":"surface","to":"out","in":"surface"}]}
        """.Replace("@0@", img[0]).Replace("@1@", img[1]).Replace("@2@", img[2]).Replace("@3@", img[3]).Replace("@4@", img[4]);

    [Fact]
    public void A_five_channel_material_assigned_to_one_of_two_walls_resolves_saves_packs_and_opens_from_a_clean_library()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, Wall, """{"op":"place","piece":{"kind":"box"},"name":"twin","transform":{"position":[0,6,0],"scale":[4,2,1]}}""");
        var images = new List<string>();
        foreach (var (n, i) in new[] { "albedo.png", "normal.png", "rough.png", "orm.png", "ao.png" }.Select((x, i) => (x, i)))
            images.Add(Run(s, """{"op":"image-add","path":@P@}""".Replace("@P@", JsonValue.Create(Png(lab, n, i + 1)).ToJsonString())).CreatedImages[0]);
        var graph = JsonNode.Parse(FiveChannelGraph(images))!.AsObject();
        graph["op"] = "material-add"; graph["name"] = "Brick";
        var mid = Run(s, graph.ToJsonString()).CreatedMaterials[0];
        Assert.Equal("m1", mid);
        ulong unassigned = CustomMap.Compile(s.Document).Hash;
        Run(s, """{"op":"set-material","ids":["o2"],"material":"m1"}""");
        Assert.NotEqual(unassigned, CustomMap.Compile(s.Document).Hash);   // assignment is in the map's identity
        Assert.Null(s.Document.Get("o3").Material);

        var pbr = s.Document.FindMaterial("m1")!.Resolve();
        Assert.Equal(new TextureSlot(images[0], TextureChannel.Rgb), pbr.BaseColorTexture);
        Assert.Equal(new TextureSlot(images[1], TextureChannel.Rgb), pbr.NormalTexture);
        Assert.Equal(new TextureSlot(images[2], TextureChannel.R), pbr.RoughnessTexture);
        Assert.Equal(new TextureSlot(images[3], TextureChannel.B), pbr.MetallicTexture);
        Assert.Equal(new TextureSlot(images[4], TextureChannel.A), pbr.AoTexture);
        Assert.True(pbr.NormalDirectX);
        Assert.Equal(1.5f, pbr.NormalStrength);
        Assert.Equal(new Vector2(0.5f, 0.5f), pbr.UvScale);

        // the drawn piece carries its material; another identical wall does not
        var map = CustomMap.Compile(s.Document);
        Assert.Equal("m1", map.Pieces.Single(p => p.Id == "o2").Material);
        Assert.Null(map.Pieces.Single(p => p.Id == "o3").Material);

        // save: images beside the map; pack; unpack into a clean folder with a clean library (no import folder, no first library)
        var file = Path.Combine(lab.Dir, "maps", "brick.smmap");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        s.Save(file);
        Assert.Empty(s.SaveNotes);
        foreach (var id in images) Assert.True(File.Exists(Path.Combine(lab.Dir, "maps", "images", id + ".png")));
        var pack = Path.Combine(lab.Dir, "brick.smpack");
        MapPack.Pack(file, pack, lab.Library);
        foreach (var n in new[] { "albedo.png", "normal.png", "rough.png", "orm.png", "ao.png" }) File.Delete(Path.Combine(lab.Dir, n));   // the import folder is gone
        var clean = new MapLibrary(Path.Combine(lab.Dir, "clean-lib"));
        var unpacked = MapPack.Unpack(pack, Path.Combine(lab.Dir, "elsewhere"));
        var opened = MapSession.Load(unpacked, clean);
        Assert.Empty(MapImages.Missing(opened.Document, unpacked, clean));
        Assert.Equal(map.Hash, CustomMap.Compile(opened.Document).Hash);   // the other tester's map identity agrees
        foreach (var img in opened.Document.Images) Assert.NotNull(MapImages.Read(img, null, clean));   // opening added them to the clean library
    }

    [Fact]
    public void Graph_edits_connect_and_disconnect_and_unsupported_or_bad_graphs_are_refused()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall, """{"op":"place","piece":{"kind":"spawn-area","side":"marines"}}""");
        var a = Run(s, $$"""{"op":"image-add","path":{{JsonValue.Create(Png(lab, "a.png", 1)).ToJsonString()}},"name":"Albedo"}""").CreatedImages[0];
        var b = Run(s, """{"op":"image-add","path":@P@}""".Replace("@P@", JsonValue.Create(Png(lab, "b.png", 2)).ToJsonString())).CreatedImages[0];
        Run(s, """{"op":"material-add","name":"Plain"}""");
        Assert.Equal(PbrMaterial.Plain, s.Document.FindMaterial("m1")!.Resolve());
        Run(s, """{"op":"material-edit","id":"m1","addNodes":[{"type":"image-texture","params":{"image":"@A@"}}],"link":[{"from":"image-texture-1","out":"color","to":"surface","in":"base-color"}]}""".Replace("@A@", a));
        Assert.Equal(a, s.Document.FindMaterial("m1")!.Resolve().BaseColorTexture!.ImageId);
        Run(s, """{"op":"material-edit","id":"m1","unlink":[{"to":"surface","in":"base-color"}],"setParams":[{"node":"surface","params":{"roughness":0.9}}]}""");
        var plain = s.Document.FindMaterial("m1")!.Resolve();
        Assert.Null(plain.BaseColorTexture);
        Assert.Equal(0.9f, plain.Roughness);
        var before = s.Document.ToJson();
        // two different mappings: cannot be one surface
        Fails(s, """
            {"op":"material-edit","id":"m1","addNodes":[{"id":"m-a","type":"mapping","params":{"scale":[2,2]}},{"id":"m-b","type":"mapping"},{"id":"t2","type":"image-texture","params":{"image":"@B@"}}],
             "link":[{"from":"m-a","out":"vector","to":"image-texture-1","in":"vector"},{"from":"m-b","out":"vector","to":"t2","in":"vector"},
                     {"from":"image-texture-1","out":"color","to":"surface","in":"base-color"},{"from":"t2","out":"color","to":"surface","in":"roughness"}]}
            """.Replace("@B@", b), "unsupported-graph");
        Fails(s, """{"op":"material-edit","id":"m1","link":[{"from":"surface","out":"surface","to":"surface","in":"normal"}]}""", "bad-graph");   // wrong socket type
        Fails(s, """{"op":"set-material","ids":["o2"],"material":"m1"}""", "not-renderable");   // a spawn area is not drawn
        Fails(s, """{"op":"set-material","ids":["o1"],"material":"m9"}""", "unknown-material");
        Assert.Equal(before, s.Document.ToJson());
        Run(s, """{"op":"set-material","ids":["o1"],"material":"m1"}""");
        Fails(s, """{"op":"material-remove","id":"m1"}""", "material-in-use");
        Fails(s, $$"""{"op":"image-remove","id":"{{a}}"}""", "image-in-use");
        Run(s, $$"""{"op":"image-remove","id":"{{b}}"}""");
        Run(s, """{"op":"set-material","ids":["o1"],"material":null}""");
        Run(s, """{"op":"material-remove","id":"m1"}""");
        Assert.Empty(s.Document.Materials);
    }

    [Fact]
    public void A_pack_with_anything_but_its_map_and_its_images_is_refused()
    {
        using var lab = new Lab();
        var bad = Path.Combine(lab.Dir, "bad.smpack");
        using (var zip = System.IO.Compression.ZipFile.Open(bad, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("m.smmap").Open())) w.Write(MapDocument.Create("x").ToJson());
            using (var w = new StreamWriter(zip.CreateEntry("../evil.txt").Open())) w.Write("x");
        }
        Assert.Equal("bad-pack", Assert.Throws<MapException>(() => MapPack.Unpack(bad, Path.Combine(lab.Dir, "out"))).Code);
        Assert.False(Directory.Exists(Path.Combine(lab.Dir, "out")));
    }

    // ---- UVs, tangents, old maps ----

    [Fact]
    public void Uvs_are_metres_along_each_face_so_a_pulled_face_adds_texture_and_tangents_are_usable()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall);
        Run(s, """{"op":"edit-mesh","id":"o1","mode":"face","ids":[5],"move":[1,0,0]}""");
        var piece = CustomMap.Compile(s.Document).Pieces.Single();
        var r = piece.Render();
        var front = Enumerable.Range(0, r.TriangleFace.Length).Where(t => r.TriangleFace[t] == 2).SelectMany(t => new[] { r.Uvs[t * 3], r.Uvs[t * 3 + 1], r.Uvs[t * 3 + 2] }).ToArray();
        Assert.Equal(5f, front.Max(u => u.X) - front.Min(u => u.X), 4);   // the -Y face is 5 m long: 5 UV units
        Assert.Equal(1f, front.Max(u => u.Y) - front.Min(u => u.Y), 4);
        for (int i = 0; i < r.Positions.Length; i++)
        {
            Assert.Equal(1f, r.Normals[i].Length(), 3);
            Assert.Equal(1f, new Vector3(r.Tangents[i].X, r.Tangents[i].Y, r.Tangents[i].Z).Length(), 3);
            Assert.True(MathF.Abs(Vector3.Dot(r.Normals[i], new Vector3(r.Tangents[i].X, r.Tangents[i].Y, r.Tangents[i].Z))) < 1e-4f);
        }
        Near(-Vector3.UnitY, r.Normals[Array.IndexOf(r.TriangleFace, 2) * 3]);   // faces wound counter-clockwise from outside
    }

    [Fact]
    public void An_older_map_reads_with_no_meshes_and_compiles_the_stock_collision_bit_for_bit()
    {
        using var lab = new Lab();
        var s = Map(lab, Floor, Wall, """{"op":"place","piece":{"kind":"ramp"},"transform":{"position":[8,0,0],"rotation":[0,0,30],"scale":[4,8,2]}}""",
            """{"op":"place","piece":{"kind":"capture-point"},"transform":{"position":[-10,0,0],"scale":[8,6,4]}}""");
        var node = JsonNode.Parse(s.Document.ToJson())!.AsObject();
        node["formatVersion"] = 3;
        var old = MapDocument.Parse(node.ToJsonString());
        Assert.Equal(3, old.LoadedVersion);
        Assert.All(old.Objects, o => Assert.Null(o.Mesh));
        var a = CustomMap.Compile(old);
        var b = CustomMap.Compile(s.Document);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Empty(a.Volumes);
        Assert.Equal(new Vector3(8, 8, 4), a.Content.ControlPoints[0].Scale);   // an unedited point keeps the old rule (X for both horizontal sizes)
        // the stock collision: each piece's unit shape through the deterministic placement, as before the edit contract
        var expected = new List<Vector3>();
        foreach (var o in old.Objects)
        {
            var (v, _) = PieceMesh.Unit(o.Piece!.Kind) is { } u ? u : (Array.Empty<Vector3>(), Array.Empty<int>());
            var w = DecorGeometry.WorldOf(old, o);
            expected.AddRange(v.Select(p => { var (x, y, z) = DetMath.Transform(p.X, p.Y, p.Z, w); return DetMath.Grid(x, y, z); }));
        }
        Assert.Equal(expected, a.Content.Layers.First(l => l.Name == SmContent.MapContent.CharacterLayer).Vertices);
        // a stored mesh that is exactly the stock shape is read as no mesh (one way to write it)
        var withStock = JsonNode.Parse(s.Document.ToJson())!.AsObject();
        withStock["objects"]![1]!["mesh"] = EditMesh.Stock(MeshTopology.Box).ToNode();
        Assert.Null(MapDocument.Parse(withStock.ToJsonString()).Get("o2").Mesh);
    }

    [Fact]
    public void A_mesh_on_a_non_piece_or_of_the_wrong_topology_is_refused_in_a_file()
    {
        using var lab = new Lab();
        var s = Map(lab, Wall);
        var node = JsonNode.Parse(s.Document.ToJson())!.AsObject();
        node["objects"]![0]!["mesh"] = EditMesh.Stock(MeshTopology.Ramp).ToNode();
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => MapDocument.Parse(node.ToJsonString())).Code);
        node["objects"]![0]!["mesh"] = new JsonObject { ["topology"] = "box", ["vertices"] = new JsonArray(new JsonArray(0, 0, 0)) };
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => MapDocument.Parse(node.ToJsonString())).Code);
        node["objects"]![0]!.AsObject().Remove("mesh");
        node["objects"]![0]!["material"] = "m1";   // no such material
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => MapDocument.Parse(node.ToJsonString())).Code);
    }

    // ---- smtool map ----

    [Fact]
    public void Smtool_map_edits_a_face_reads_dims_and_runs_a_script_with_undo_redo_save_and_reopen()
    {
        using var lab = new Lab();
        var file = Path.Combine(lab.Dir, "cli.smmap");
        var lib = Path.Combine(lab.Dir, "lib");
        string Cli(params string[] args)
        {
            var w = new StringWriter();
            int code = SmTool.MapCommand.Run([.. args, "--library", lib], w);
            Assert.True(code == 0, w.ToString());
            return w.ToString();
        }
        Cli("new", file);
        var ops = Path.Combine(lab.Dir, "ops.json");
        File.WriteAllText(ops, "[" + Wall + "," + Neighbour + "]");
        Cli("apply", file, ops);
        var edit = JsonNode.Parse(Cli("edit-face", file, "o1", "--face", "+x", "--move", "1,0,0"))!;
        Assert.Equal("X 5.00 m   Y 2.00 m   Z 1.00 m", (string)edit["dims"]!["label"]!);
        var dims = JsonNode.Parse(Cli("dims", file, "o2"))!;
        Assert.Equal("X 2.00 m   Y 2.00 m   Z 2.00 m", (string)dims["dims"]![0]!["label"]!);
        var steps = Path.Combine(lab.Dir, "steps.json");
        File.WriteAllText(steps, """
            [{"apply":{"op":"edit-mesh","id":"o1","mode":"face","ids":[4],"move":[-1,0,0]}},{"dims":"o1"},{"undo":true},{"dims":"o1"},{"redo":true},{"save":true},{"reopen":true},{"dims":"o1"}]
            """);
        var run = JsonNode.Parse(Cli("script", file, steps))!["steps"]!.AsArray();
        Assert.Equal("X 6.00 m   Y 2.00 m   Z 1.00 m", (string)run[1]!["dims"]!["label"]!);
        Assert.Equal("X 5.00 m   Y 2.00 m   Z 1.00 m", (string)run[3]!["dims"]!["label"]!);
        Assert.Equal("X 6.00 m   Y 2.00 m   Z 1.00 m", (string)run[7]!["dims"]!["label"]!);
        var mesh = JsonNode.Parse(Cli("mesh", file, "o1"))!;
        Assert.Equal(6, mesh["faces"]!.AsArray().Count);
        Assert.Equal(12, mesh["edges"]!.AsArray().Count);
        var inside = JsonNode.Parse(Cli("inside", file, "0,0,0.5"))!;
        Assert.True((bool)inside["ok"]!);
    }
}
