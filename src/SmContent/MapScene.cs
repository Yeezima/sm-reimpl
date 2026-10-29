using System.Numerics;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>A static object placed in a map layer: its definition (<c>.O3d</c>) and its world transform.</summary>
/// <param name="Depth">0 for objects listed directly in the layer; 1 and more for objects nested as <c>ChildObjects</c>.</param>
public sealed record PlacedObject(string Name, string Definition, string Layer, Matrix4x4 Transform, int Depth = 0);

/// <summary>One drawable part of an object definition: a mesh block, where it hangs in the object, and its LOD mask.</summary>
/// <param name="MeshName">The <c>.dcm</c> entry.</param>
/// <param name="MeshId">Which block of that mesh (blocks are the object's LODs).</param>
/// <param name="NodeTransform">The <c>RefNode</c>'s transform within the object (its node chain composed).</param>
/// <param name="LodLevel">Bit mask of the LODs this part draws in; bit 0 is the nearest (-1: all).</param>
public sealed record VisualPart(string MeshName, int MeshId, string RefNode, Matrix4x4 NodeTransform, int LodLevel, bool CastShadows);

/// <param name="CollisionNodes">Node transforms of the definition's rigid bodies (to check placements against level collision).</param>
public sealed record ObjectDefinition(string Name, IReadOnlyList<VisualPart> Visuals, IReadOnlyList<(string Node, Matrix4x4 Transform)> CollisionNodes)
{
    /// <summary>
    /// The parts drawn up close. **Provisional approximation**: parts whose LOD mask has bit 0 (the nearest LOD), minus
    /// parts whose node is named as a low LOD ("LODL", "_lod") when another part qualifies. Evidence: in definitions
    /// with complementary masks the high-detail node has the low bits ({15 LODH, 16 LODL}, {7 LODH, 24 lod}), so bit 0
    /// is the nearest; but some definitions also give their LODL node a mask with bit 0 ({31 LODH, 15 LODL},
    /// {31 LODH, 7 LODL}), which the name rule removes. How the engine resolves those overlaps is not known.
    /// </summary>
    public IEnumerable<VisualPart> NearestLod()
    {
        var near = Visuals.Where(v => (v.LodLevel & 1) != 0).ToList();
        var high = near.Where(v => !IsLowLodName(v.RefNode)).ToList();
        return high.Count > 0 ? high : near;
    }

    static bool IsLowLodName(string node) =>
        node.Contains("lodl", StringComparison.OrdinalIgnoreCase) || node.EndsWith("_lod", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The look of a multiplayer map, from its region archive: every enabled <c>StaticObject</c> in the region's layers, the
/// <c>.O3d</c> definitions they name, and those definitions' meshes, materials and diffuse textures (all in the same
/// archive on the 11 maps read). Rotations in layers and O3d nodes are degrees about X, then Y, then Z (fixed axes);
/// established against the level collision's quaternions (docs/discovery.md). Engine-neutral: Z up, metres.
/// </summary>
public sealed class MapScene
{
    public required string Map { get; init; }
    public required IReadOnlyList<PlacedObject> Objects { get; init; }
    public required IReadOnlyDictionary<string, ObjectDefinition> Definitions { get; init; }
    public required IReadOnlyDictionary<string, DcmMesh> Meshes { get; init; }
    public required IReadOnlyDictionary<string, BmatMaterial> Materials { get; init; }
    /// <summary>
    /// Raw DDS bytes of every texture a used material names in one of the <see cref="TextureParameters"/> (diffuse,
    /// normal, specular and emissive maps, a second layer's diffuse and normal maps, and the blend mask).
    /// </summary>
    public required IReadOnlyDictionary<string, byte[]> Textures { get; init; }
    /// <summary>The material texture parameters loaded into <see cref="Textures"/>; the others (second layers, masks, sky) are not read.</summary>
    public static readonly IReadOnlyList<string> TextureParameters = ["DiffuseMap", "NormalMap", "SpecularMap", "EmissiveMap", "DiffuseMap2", "NormalMap2", "AlphaMaskMap"];
    /// <summary>The <c>l_lights</c> layer's sun, environment regions and placed lights (null: the map has none or it could not be read).</summary>
    public MapLighting? Lighting { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Degrees about X, then Y, then Z (fixed axes).</summary>
    public static Quaternion Rotation(float xDegrees, float yDegrees, float zDegrees)
    {
        const float k = MathF.PI / 180f;
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, xDegrees * k);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yDegrees * k);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, zDegrees * k);
        // Hamilton product qz·qy·qx: x is applied first. System.Numerics' Concatenate(a, b) means "a, then b" = b·a.
        return Quaternion.Concatenate(Quaternion.Concatenate(qx, qy), qz);
    }

    /// <summary>Scale, then rotate, then translate (row-vector matrices, as System.Numerics uses).</summary>
    public static Matrix4x4 Transform(Vector3 position, Vector3 rotationDegrees, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Rotation(rotationDegrees.X, rotationDegrees.Y, rotationDegrees.Z))
        * Matrix4x4.CreateTranslation(position);

    public static MapScene Load(string gameDir, string map)
    {
        if (map.Length == 0 || map.Length > 64 || map.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException($"'{map}' is not a map name.", nameof(map));
        foreach (var region in new[] { "wrld", "r000_main" })
        {
            string path = Path.Combine(gameDir, "data", "chunks", $"{map}__{region}.oppc");
            if (File.Exists(path)) return Read(path, map);
        }
        throw new FileNotFoundException($"No region archive for '{map}'.");
    }

    public static MapScene Read(string archivePath, string map)
    {
        var notes = new List<string>();
        var objects = new List<PlacedObject>();
        MapLighting? lighting = null;
        var definitions = new Dictionary<string, ObjectDefinition>(StringComparer.OrdinalIgnoreCase);
        using var archive = ObpkArchive.Open(archivePath);

        // Pass 1: layers and object definitions.
        archive.ReadEntries(e => archive.GroupName(e) is "Layer" or "O3d", (e, data) =>
        {
            try
            {
                var root = BodDocument.Read(data).Root;
                if (archive.GroupName(e) == "Layer")
                {
                    CollectObjects(root, e.FileName, Matrix4x4.Identity, objects, 0);
                    if (e.FileName.Equals("l_lights", StringComparison.OrdinalIgnoreCase)) lighting = MapLighting.Read(root);
                }
                else definitions.TryAdd(e.FileName, Definition(e.FileName, root));
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                // Navmesh and elevator layers use value types not decoded yet; they hold no static objects in the maps read.
                notes.Add($"{e.FileName}.{archive.GroupName(e)} skipped: {ex.Message}");
            }
        });

        var used = objects.Select(o => o.Definition).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in used.Where(n => !definitions.ContainsKey(n)).Order(StringComparer.Ordinal))
            notes.Add($"object definition '{name}' not in the archive; its {objects.Count(o => o.Definition.Equals(name, StringComparison.OrdinalIgnoreCase))} placements are not drawn");
        var meshNames = definitions.Values.Where(d => used.Contains(d.Name)).SelectMany(d => d.Visuals).Select(v => v.MeshName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pass 2: meshes and materials.
        var meshes = new Dictionary<string, DcmMesh>(StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<string, BmatMaterial>(StringComparer.OrdinalIgnoreCase);
        archive.ReadEntries(e => (archive.GroupName(e) == "dcm" && meshNames.Contains(e.FileName)) || archive.GroupName(e) == "bmat", (e, data) =>
        {
            try
            {
                if (archive.GroupName(e) == "dcm") meshes.TryAdd(e.FileName, DcmMesh.Read(data));
                else materials.TryAdd(e.FileName, BmatMaterial.From(BodDocument.Read(data)));
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                notes.Add($"{e.FileName}.{archive.GroupName(e)} unreadable: {ex.Message}");
            }
        });
        foreach (var missing in meshNames.Where(n => !meshes.ContainsKey(n)).Order(StringComparer.Ordinal)) notes.Add($"mesh '{missing}' not in the archive");

        // Pass 3: the textures those materials name (diffuse, normal, specular, emissive).
        var wanted = meshes.Values.SelectMany(m => m.Blocks).SelectMany(b => b.Materials).Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => materials.GetValueOrDefault(n)).OfType<BmatMaterial>()
            .SelectMany(m => TextureParameters.Select(p => m.Textures.GetValueOrDefault(p))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        archive.ReadEntries(e => archive.GroupName(e) == "dds" && wanted.Contains(e.FileName), (e, data) => textures.TryAdd(e.FileName, data.ToArray()));

        return new MapScene
        {
            Map = map, Objects = objects, Definitions = definitions, Meshes = meshes, Materials = materials, Textures = textures, Lighting = lighting, Notes = notes,
        };
    }

    /// <summary>
    /// Enabled <c>StaticObject</c>s anywhere in a layer's tree. Objects nested as <c>ChildObjects</c> of a group are
    /// placed relative to it (the group's own transform, when it has one).
    /// </summary>
    static void CollectObjects(BodValue value, string layer, Matrix4x4 parent, List<PlacedObject> objects, int depth, int childDepth = 0)
    {
        if (depth > 64) return;
        switch (value)
        {
            case BodStruct s:
            {
                if (s["Disabled"] is BodBool { Value: true }) return;   // a disabled object hides its children too
                var world = Local(s) * parent;
                if (s.Class.Text == "StaticObject" && s["ObjectName"] is BodString { Text: { Length: > 0 } definition })
                    objects.Add(new PlacedObject((s["Name"] as BodString)?.Text ?? "", definition, layer, world, childDepth));
                foreach (var f in s.Fields)
                    if (f.Name.Text is "Root" or "Objects" or "ChildObjects")
                        CollectObjects(f.Value, layer, f.Name.Text == "ChildObjects" ? world : parent, objects, depth + 1, f.Name.Text == "ChildObjects" ? childDepth + 1 : childDepth);
                break;
            }
            case BodList l: foreach (var item in l.Items) CollectObjects(item.Value, layer, parent, objects, depth + 1, childDepth); break;
            case BodRef r: CollectObjects(r.Value, layer, parent, objects, depth + 1, childDepth); break;
        }
    }

    /// <summary>Position, Rotation and Scale of a struct; each missing or malformed one defaults (0, 0, 1) on its own.</summary>
    internal static Matrix4x4 Local(BodStruct s) => Transform(
        Floats(s["Position"]) is [var px, var py, var pz, ..] ? new Vector3(px, py, pz) : Vector3.Zero,
        Floats(s["Rotation"]) is [var rx, var ry, var rz, ..] ? new Vector3(rx, ry, rz) : Vector3.Zero,
        Floats(s["Scale"]) is [var sx, var sy, var sz, ..] ? new Vector3(sx, sy, sz) : Vector3.One);

    static ObjectDefinition Definition(string name, BodValue root)
    {
        if (root is not BodStruct { Class.Text: "Object3D" } o) throw new InvalidDataException($"'{name}' is not an Object3D.");
        var nodes = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        CollectNodes(Unwrap(o["Skeleton"]), Matrix4x4.Identity, nodes, 0);
        var visuals = new List<VisualPart>();
        foreach (var v in Items(o["Visuals"]).OfType<BodStruct>().Where(v => v.Class.Text == "StaticMeshVisual"))
        {
            if (v["MeshName"] is not BodString { Text: { Length: > 0 } mesh } || v["Renderable"] is BodBool { Value: false }) continue;
            string node = (v["RefNode"] as BodString)?.Text ?? "";
            visuals.Add(new VisualPart(mesh, (v["MeshID"] as BodInt)?.Value ?? 0, node, nodes.GetValueOrDefault(node, Matrix4x4.Identity),
                (v["LODLevel"] as BodInt)?.Value ?? -1, v["CastShadows"] is not BodBool { Value: false }));
        }
        var bodies = Items(o["Bodies"]).OfType<BodStruct>()
            .Select(b => (b["NodeName"] as BodString)?.Text).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (n, nodes.GetValueOrDefault(n, Matrix4x4.Identity))).ToList();
        return new ObjectDefinition(name, visuals, bodies);
    }

    static void CollectNodes(BodValue? value, Matrix4x4 parent, Dictionary<string, Matrix4x4> nodes, int depth)
    {
        if (depth > 64 || value is not BodStruct s) return;
        var world = Local(s) * parent;
        if (s["Name"] is BodString { Text: { } name }) nodes.TryAdd(name, world);
        foreach (var child in Items(s["Children"])) CollectNodes(child, world, nodes, depth + 1);
    }

    static BodValue? Unwrap(BodValue? v) => v is BodRef r ? r.Value : v;
    static IEnumerable<BodValue> Items(BodValue? v) => v is BodList l ? l.Items.Select(i => i.Value) : [];
    static float[]? Floats(BodValue? v) =>
        v is BodArray { Items: var items } && items.All(i => i is BodFloat) ? items.Select(i => ((BodFloat)i).Value).ToArray() : null;
}
