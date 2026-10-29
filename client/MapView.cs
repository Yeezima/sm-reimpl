using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmFormats.Dcm;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// M8: draws a multiplayer map with its original meshes and textures from <see cref="MapScene"/>. One Godot mesh
/// per distinct (mesh, block) used by a placed object's nearest LOD, one surface per material range, and one
/// <see cref="MultiMeshInstance3D"/> per mesh holding every placement. Lives in game space (Z up, metres): the parent
/// converts to Godot's Y up. Placements with a mirroring transform would get their own MultiMesh with reversed triangles
/// (assumption: Godot does not flip face culling per MultiMesh instance; no multiplayer map has a mirrored placement).
/// Parts the definition marks as not casting shadows get their own MultiMesh with shadows off.
/// Materials follow <see cref="MapMaterials"/>: original diffuse, normal, specular and emissive maps, the vertex-blended
/// second layer, vertex tint and alpha cut-outs, with provisional roughness, specular and blend rules (**provisional
/// approximation**: the original shaders are not reproduced). The map's own lighting is applied by <see cref="MapLightingView"/>.
/// </summary>
public partial class MapView : Node3D
{
    /// <summary>Placements with at least one part drawn.</summary>
    public int Objects { get; private set; }
    /// <summary>Distinct (mesh, block) pairs drawn.</summary>
    public int Meshes { get; private set; }
    public int Instances { get; private set; }
    public int Surfaces { get; private set; }
    public int TexturedSurfaces { get; private set; }
    public int Mirrored { get; private set; }

    /// <summary>Builds the materials (diffuse, normal, specular, emissive maps); its counters say how many used each.</summary>
    public MapMaterials Materials { get; } = new();
    readonly Dictionary<(string, bool), Material> _materials = new();
    StandardMaterial3D? _neutral;

    /// <summary>Builds the map's visuals under a new node. Problems are added to <paramref name="notes"/>; nothing throws for one bad part.</summary>
    public static MapView Build(MapScene scene, List<string> notes)
    {
        var view = new MapView { Name = "MapVisuals" };
        try { view.Populate(scene, notes); }
        catch { view.Free(); throw; }   // not in the tree yet: free it (and the meshes built so far) here
        return view;
    }

    void Populate(MapScene scene, List<string> notes)
    {
        // Group world transforms by (mesh, block, mirrored, casts shadows), remembering which placements each group draws.
        var groups = new Dictionary<(string Mesh, int Block, bool Mirrored, bool Shadows), List<(Transform3D Transform, int Object)>>();
        for (int o = 0; o < scene.Objects.Count; o++)
        {
            var placed = scene.Objects[o];
            if (!scene.Definitions.TryGetValue(placed.Definition, out var definition)) continue;
            foreach (var part in definition.NearestLod())
            {
                var world = part.NodeTransform * placed.Transform;
                bool mirrored = world.GetDeterminant() < 0f;
                var key = (part.MeshName.ToLowerInvariant(), part.MeshId, mirrored, part.CastShadows);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(Transform3D, int)>();
                list.Add((ToGodot(world), o));
            }
        }
        var drawnObjects = new HashSet<int>();
        var meshCache = new Dictionary<(string, int, bool), ArrayMesh?>();

        var drawn = new HashSet<(string, int)>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((meshName, block, mirrored, shadows), placements) in groups.OrderBy(g => g.Key.Mesh, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Block).ThenBy(g => g.Key.Mirrored).ThenBy(g => g.Key.Shadows))
        {
            var transforms = placements.Select(p => p.Transform).ToList();
            if (!scene.Meshes.TryGetValue(meshName, out var dcm)) { missing.Add(meshName); continue; }
            if (block < 0 || block >= dcm.Blocks.Count)
            {
                notes.Add($"mesh '{meshName}' has no block {block} ({dcm.Blocks.Count} blocks); {transforms.Count} placements not drawn");
                continue;
            }
            if (!meshCache.TryGetValue((meshName, block, mirrored), out var mesh))
                meshCache[(meshName, block, mirrored)] = mesh = BuildMesh(scene, meshName, dcm.Blocks[block], mirrored, countSurfaces: !drawn.Contains((meshName, block)), notes);
            if (mesh is null) continue;
            drawn.Add((meshName, block));
            foreach (var p in placements) drawnObjects.Add(p.Object);
            var multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = mesh,
                InstanceCount = transforms.Count,
            };
            for (int i = 0; i < transforms.Count; i++) multimesh.SetInstanceTransform(i, transforms[i]);
            AddChild(new MultiMeshInstance3D
            {
                Name = $"{meshName}_{block}{(mirrored ? "_mirrored" : "")}{(shadows ? "" : "_noshadow")}", Multimesh = multimesh,
                CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            });
            Instances += transforms.Count;
            if (mirrored) Mirrored += transforms.Count;
        }
        Meshes = drawn.Count;
        Objects = drawnObjects.Count;
        foreach (var m in missing.Order(StringComparer.Ordinal)) notes.Add($"mesh '{m}' not loaded; its placements are not drawn");
    }

    /// <summary>
    /// One surface per material range, each with only the vertices it uses (indices are rebased). Triangles stay
    /// clockwise from the front as in the files, which is Godot's front face; <paramref name="mirrored"/> reverses them.
    /// </summary>
    ArrayMesh? BuildMesh(MapScene scene, string meshName, DcmBlock block, bool mirrored, bool countSurfaces, List<string> notes)
    {
        var mesh = new ArrayMesh();
        int vertexCount = block.Positions.Length;
        foreach (var range in block.Materials)
        {
            int count = range.IndexCount - range.IndexCount % 3;
            if (count < 3 || range.StartIndex < 0 || range.StartIndex + count > block.Indices.Length) continue;
            // Vertex window of this range; fall back to the whole block if an index falls outside the declared window.
            int first = range.FirstVertex, last = range.FirstVertex + range.VertexCount;
            for (int i = range.StartIndex; i < range.StartIndex + count; i++)
            {
                int index = block.Indices[i];
                if (index < first || index >= last) { first = 0; last = vertexCount; break; }
            }
            if (first < 0 || last > vertexCount || last <= first) continue;
            int n = last - first;
            var vertices = new Vector3[n];
            var normals = new Vector3[n];
            var tangents = new float[4 * n];
            var uv = new Vector2[n];
            var uv2 = new Vector2[n];
            var colours = new Color[n];
            for (int v = 0; v < n; v++)
            {
                // The DCM vertex colour u32: bytes B, G, R, A (D3DCOLOR order; the alpha byte is the layer blend weight of two-layer materials).
                var c4 = VertexColour.Unpack(block.Colors.Length == vertexCount ? block.Colors[first + v] : VertexColour.None);
                colours[v] = new Color(c4.X, c4.Y, c4.Z, c4.W);
                var p = block.Positions[first + v];
                vertices[v] = new Vector3(p.X, p.Y, p.Z);
                if (block.Normals.Length == vertexCount) { var q = block.Normals[first + v]; normals[v] = new Vector3(q.X, q.Y, q.Z); }
                if (block.Tangents.Length == vertexCount)
                {
                    var t = block.Tangents[first + v];
                    (tangents[4 * v], tangents[4 * v + 1], tangents[4 * v + 2], tangents[4 * v + 3]) = (t.X, t.Y, t.Z, t.W);
                }
                if (block.Uv0.Length == vertexCount) uv[v] = new Vector2(block.Uv0[first + v].X, block.Uv0[first + v].Y);
                if (block.Uv1.Length == vertexCount) uv2[v] = new Vector2(block.Uv1[first + v].X, block.Uv1[first + v].Y);
            }
            var indices = new int[count];
            for (int i = 0; i < count; i += 3)
            {
                int a = block.Indices[range.StartIndex + i] - first, b = block.Indices[range.StartIndex + i + 1] - first, c = block.Indices[range.StartIndex + i + 2] - first;
                (indices[i], indices[i + 1], indices[i + 2]) = mirrored ? (a, c, b) : (a, b, c);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.Tangent] = tangents;
            arrays[(int)Mesh.ArrayType.TexUV] = uv;
            arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
            arrays[(int)Mesh.ArrayType.Color] = colours;
            arrays[(int)Mesh.ArrayType.Index] = indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var (material, textured) = MaterialFor(scene, range.Name, notes, block.Tangents.Length == vertexCount && block.Normals.Length == vertexCount);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
            if (countSurfaces) { Surfaces++; if (textured) TexturedSurfaces++; }
        }
        if (mesh.GetSurfaceCount() == 0) { notes.Add($"mesh '{meshName}': no drawable range"); return null; }
        return mesh;
    }

    /// <param name="tangents">The block has normals and tangents for every vertex; without them a normal map would shade wrongly, so it is left off.</param>
    (Material Material, bool Textured) MaterialFor(MapScene scene, string materialName, List<string> notes, bool tangents)
    {
        var key = (materialName.ToLowerInvariant(), tangents);
        if (_materials.TryGetValue(key, out var cached)) return (cached, cached != _neutral);
        Material? built = scene.Materials.TryGetValue(materialName, out var bmat)
            ? Materials.Build(bmat, scene.Textures, notes, vertexColours: true, normalMaps: tangents) : null;
        // Untextured surfaces (no material, no DiffuseMap, or texture missing): a neutral placeholder grey.
        Material material = built ?? (_neutral ??= new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.55f) });
        _materials[key] = material;
        return (material, built is not null);
    }

    /// <summary>System.Numerics matrices are row-vector (p' = p·M): rows 1–3 are the images of the axes, row 4 the translation.</summary>
    static Transform3D ToGodot(Num.Matrix4x4 m) => new(
        new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33)),
        new Vector3(m.M41, m.M42, m.M43));
}
