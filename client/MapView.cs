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
    /// <summary>Static level decals built (<see cref="MapDecalView"/>).</summary>
    public int LevelDecals { get; private set; }
    /// <summary>Orthographic projector lights built (<see cref="MapOrthoLightView"/>).</summary>
    public int OrthoLights { get; private set; }

    /// <summary>Builds the materials (diffuse, normal, specular, emissive maps); its counters say how many used each.</summary>
    public MapMaterials Materials { get; } = new();
    readonly Dictionary<(string, bool), Material> _materials = new();
    StandardMaterial3D? _neutral;
    readonly List<(MultiMeshInstance3D Node, Vector3 Pivot, float DegreesPerSecond)> _spinners = new();   // classic-maps-fidelity round 2, fix 3: BezierCurve-turned fan blades
    double _spinClock;
    /// <summary>Spinning objects built (the ceiling fans' blades).</summary>
    public int Spinners => _spinners.Count;

    public override void _Process(double delta)
    {
        _spinClock += delta;
        if (MapDecor.MovingParts) foreach (var (material, su, sv) in Materials.Scrolling) material.Uv1Offset = new Vector3((float)((_spinClock * su) % 1.0), (float)((_spinClock * sv) % 1.0), 0f);
        if (_spinners.Count == 0) return;
        foreach (var (node, pivot, rate) in _spinners)
            node.Transform = new Transform3D(Basis.Identity, pivot) * new Transform3D(new Basis(Vector3.Back, Mathf.DegToRad((float)(_spinClock * rate) % 360f)), Vector3.Zero) * new Transform3D(Basis.Identity, -pivot);
    }

    bool _skyOnly;          // BuildLook (a custom map's original look): only the sky surfaces are drawn, never the source map's geometry
    float? _voidFloorZ;     // BuildLook: where the void floor goes (the custom map's lowest solid), as the source geometry is not drawn

    readonly HashSet<ArrayMesh> _skyMeshes = new();   // meshes with a sky-box surface: drawn round the camera, so never frustum-culled by their world box

    /// <summary>Builds the map's visuals under a new node. Problems are added to <paramref name="notes"/>; nothing throws for one bad part.</summary>
    public static MapView Build(MapScene scene, List<string> notes)
    {
        var view = new MapView { Name = "MapVisuals" };
        try { view.Populate(scene, notes); }
        catch { view.Free(); throw; }   // not in the tree yet: free it (and the meshes built so far) here
        return view;
    }

    /// <summary>
    /// Mapmaker (docs/mapmaker.md "Look in play"): the non-geometry look of <paramref name="source"/> for a custom map drawn with it: the sky, the void floor at <paramref name="voidFloorZ"/> (none when null), the level decals and
    /// the ortho lights, exactly as the Classic view builds them (the same code, <see cref="AddLevelLook"/>); the source map's own geometry is not drawn (the custom map's pieces are the geometry).
    /// </summary>
    public static MapView BuildLook(MapScene source, float? voidFloorZ, List<string> notes)
    {
        var view = new MapView { Name = "MapLook", _skyOnly = true, _voidFloorZ = voidFloorZ };
        try { view.Populate(source, notes); }
        catch { view.Free(); throw; }
        return view;
    }

    /// <summary>The void floor, the level decals and the ortho lights of <paramref name="scene"/> (shared by the Classic view and a custom map's look); <paramref name="minZ"/> is the lowest drawn point (float.MaxValue: none).</summary>
    void AddLevelLook(MapScene scene, float minZ, List<string> notes)
    {
        if (MapDecor.HasVoidFloor(scene.Map) && minZ < float.MaxValue)
        {
            // maps-visual-qa: a dark unlit plane just under the lowest drawn geometry (see MapDecorLists.VoidFloorMaps); ours.
            AddChild(new MeshInstance3D
            {
                Name = "VoidFloor", Mesh = new QuadMesh { Size = new Vector2(6000f, 6000f) }, Position = new Vector3(0f, 0f, minZ - 1.5f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.012f, 0.010f, 0.009f), CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            });
        }
        if (MapDecor.DrawsLevelDecals(scene.Map) && MapDecalView.Build(scene, notes) is { } decals) { AddChild(decals); LevelDecals = decals.Count; }
        if (MapDecor.DrawsOrthoLights(scene.Map) && MapOrthoLightView.Build(scene, notes) is { } ortho) { AddChild(ortho); OrthoLights = ortho.Count; }
    }

    void Populate(MapScene scene, List<string> notes)
    {
        // Group world transforms by (mesh, block, mirrored, casts shadows), remembering which placements each group draws.
        Materials.NewMapRules = MapDecorLists.IsNewClassicMap(scene.Map);
        var groups = new Dictionary<(string Mesh, int Block, bool Mirrored, bool Shadows, int Spin), List<(Transform3D Transform, int Object)>>();
        var decorHidden = MapDecor.Hidden(scene);   // fix/manufactorum-fluorescent: the olive boards and salmon fittings are not drawn
        if (decorHidden.Count > 0 && !_skyOnly) notes.Add($"{decorHidden.Count} placements of Manufactorum's wall stations and salmon fittings are not drawn (owner's requests 2026-11-04, 2026-11-07, 2026-11-14; --old-armoury-racks draws them)");
        int layerHidden = 0;
        for (int o = 0; o < scene.Objects.Count; o++)
        {
            if (decorHidden.Contains(o)) continue;
            var placed = scene.Objects[o];
            if (MapDecor.HiddenLayers.Count > 0 && MapDecor.HiddenLayers.Contains(placed.Layer)) { layerHidden++; continue; }   // pass 2 test aid (--hide-layer)
            if (!scene.TryGetDefinition(placed, out var definition)) continue;
            if (!MapDecor.MovingParts && scene.Spinning.ContainsKey(o)) continue;   // --no-moving-parts: the turned blades were not drawn before
            foreach (var part in scene.DrawnParts(definition, MapDecor.OldLodRule, MapDecorLists.KeepsUncoveredLodMembers(scene.Map, MapDecor.OldLodCover)))   // classic-maps-fidelity: one member per LOD-suffix family (--old-lod-rule: the old rule)
            {
                var world = part.NodeTransform * placed.Transform;
                bool mirrored = world.GetDeterminant() < 0f;
                var key = (part.MeshName.ToLowerInvariant(), part.MeshId, mirrored, part.CastShadows, scene.Spinning.ContainsKey(o) ? o : -1);   // a spinning object is a group of its own (its node turns)
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(Transform3D, int)>();
                list.Add((ToGodot(world), o));
            }
        }
        if (layerHidden > 0) notes.Add($"test aid --hide-layer: {layerHidden} placements of {string.Join(",", MapDecor.HiddenLayers)} not drawn");
        if (!MapDecor.OldLodRule && !_skyOnly) notes.Add("LOD rule: one member per LOD-suffix family is drawn (--old-lod-rule restores the earlier rule)");
        var drawnObjects = new HashSet<int>();
        var meshCache = new Dictionary<(string, int, bool), ArrayMesh?>();

        float minZ = float.MaxValue;
        var drawn = new HashSet<(string, int)>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((meshName, block, mirrored, shadows, spinObject), placements) in groups.OrderBy(g => g.Key.Mesh, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Block).ThenBy(g => g.Key.Mirrored).ThenBy(g => g.Key.Shadows).ThenBy(g => g.Key.Spin))
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
            if (!_skyOnly) { var box = mesh.GetAabb(); foreach (var t in transforms) minZ = Mathf.Min(minZ, (t * box).Position.Z); }   // the lowest drawn point (game Z), for the void floor
            var instance = new MultiMeshInstance3D
            {
                Name = $"{meshName}_{block}{(mirrored ? "_mirrored" : "")}{(shadows ? "" : "_noshadow")}", Multimesh = multimesh,
                CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            };
            if (_skyMeshes.Contains(mesh)) { instance.CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)); instance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off; }
            AddChild(instance);
            if (spinObject >= 0)
            {
                var at = scene.Objects[spinObject].Transform.Translation;
                _spinners.Add((instance, new Vector3(at.X, at.Y, at.Z), scene.Spinning[spinObject]));
            }
            Instances += transforms.Count;
            if (mirrored) Mirrored += transforms.Count;
        }
        AddLevelLook(scene, _skyOnly ? _voidFloorZ ?? float.MaxValue : minZ, notes);
        if (Materials.Scrolling.Count > 0) notes.Add($"{Materials.Scrolling.Count} scrolling materials (ScrollSpeedU/V of the data; provisional approximation)");
        if (_spinners.Count > 0) notes.Add($"{_spinners.Count} fan blades turned by their BezierCurve (provisional approximation)");
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
            if (MapDecor.HidesSurface(scene.Map, meshName, range.Name)) continue;
            // classic-maps-all: the engine's sky boxes (seq_NN_sky; shader sky_ycocg_bloomcontrol, YCoCg textures, no DiffuseMap) are drawn by the engine around the camera. Read as map geometry they
            // got the grey placeholder and stood in the level as big flat lit boxes (tan sides, violet top on Shattered Bridge). Not drawn on the new maps (never on Manufactorum, whose drawing stays as it was: MapDecorLists.SkipsSkyBoxes); the lighting's sky shows (MapLightingView).
            bool isSky = false;
            if (MapDecor.SkipsSkyBoxes(scene.Map) && scene.Materials.TryGetValue(range.Name, out var skyMaterial) && MapDecorLists.IsSkyShader(skyMaterial.ShaderName))
            {
                // maps-visual-qa: drawn as a sky around the camera from its YCoCg texture (MapSky) unless --no-sky-textures or the texture does not decode; else not drawn at all.
                if (!MapDecor.DrawsSkyTextures(scene.Map)) continue;
                isSky = true;
            }
            if (_skyOnly && !isSky) continue;   // BuildLook: the sky only
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
            // maps-visual-qa: shaders named diffuv1_* map their base maps through the second UV set (MapDecorLists.UsesSecondUvSet); --old-uv-sets restores the first-set reading.
            if (!MapDecor.OldUvSets && MapDecorLists.IsNewClassicMap(scene.Map) && block.Uv1.Length == vertexCount && scene.Materials.TryGetValue(range.Name, out var uvMaterial) && MapDecorLists.UsesSecondUvSet(uvMaterial.ShaderName)) uv = (Vector2[])uv2.Clone();
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
            Material? skyDrawn = null;
            if (isSky)
            {
                skyDrawn = SkyMaterialFor(scene, range.Name, vertices);
                if (skyDrawn is null) continue;   // no texture: the surface is not drawn at all (never the lit grey placeholder in the level)
                _skyMeshes.Add(mesh);
            }
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var (material, textured) = skyDrawn is not null ? (skyDrawn, true) : MaterialFor(scene, range.Name, notes, block.Tangents.Length == vertexCount && block.Normals.Length == vertexCount);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
            if (countSurfaces) { Surfaces++; if (textured) TexturedSurfaces++; }
        }
        if (mesh.GetSurfaceCount() == 0) { if (!_skyOnly) notes.Add($"mesh '{meshName}': no drawable range"); return null; }
        return mesh;
    }

    /// <summary>The sky-box surface's material (<see cref="MapSky"/>: the box's own centre as the draw centre); null when its texture is missing or does not decode.</summary>
    Material? SkyMaterialFor(MapScene scene, string materialName, Vector3[] vertices)
    {
        Vector3 lo = vertices[0], hi = vertices[0];
        foreach (var v in vertices) { lo = lo.Min(v); hi = hi.Max(v); }
        return scene.Materials.TryGetValue(materialName, out var bmat) ? MapSky.Build(bmat, scene.Textures, (lo + hi) / 2f) : null;
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
