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

    /// <summary>The parts the client draws: <see cref="HighestLod"/>, or <see cref="NearestLod"/> when <paramref name="oldRule"/> (<c>--old-lod-rule</c>).</summary>
    public IEnumerable<VisualPart> DrawnParts(bool oldRule) => oldRule ? NearestLod() : HighestLod();

    // A LOD suffix on a node name: _LODH, _LODM, _LODC, _LODL, with an optional underscore before the kind (lod_l), an optional number after it (_LODL01, _LODH2),
    // a bare _LOD (read as low, like the old rule did), and a variant tag after it (_LODL_b, _LODH_A, _LODH_interior_B, LODHB: equal kinds with equal stems stay together); the stem is the name before it, possibly empty (a node named just LODH).
    static readonly System.Text.RegularExpressions.Regex LodSuffix =
        new(@"^(?<stem>.*?)_?lod_?(?<kind>[hmcl])?\d*[a-z]?(_[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The LOD family of a node name: its stem (lower case, without the suffix) and rank (0 H, 1 M, 2 C, 3 L or a bare LOD); null when the name carries no LOD suffix.</summary>
    public static (string Stem, int Rank)? LodFamily(string node)
    {
        var m = LodSuffix.Match(node);
        if (!m.Success) return null;
        var kind = m.Groups["kind"].Success ? char.ToLowerInvariant(m.Groups["kind"].Value[0]) : 'l';
        return (m.Groups["stem"].Value.ToLowerInvariant(), kind switch { 'h' => 0, 'm' => 1, 'c' => 2, _ => 3 });
    }

    /// <summary>
    /// classic-maps-fidelity, **the client's default since the owner's choice of 2026-11-17** (<c>--old-lod-rule</c> uses <see cref="NearestLod"/> again): the parts of <see cref="NearestLod"/>, except that parts
    /// whose node names share a stem and differ only in a LOD suffix (<see cref="LodFamily"/>: <c>_LODH</c>, <c>_LODM</c>, <c>_LODC</c>, <c>_LODL</c>, numbered or underscored variants) are one family of which only
    /// the best-ranked member is kept (H, else M, else C, else L). Parts without such a suffix are kept as they are; a family of one member is kept.
    /// Whether the suffixes name levels of detail of the same object is **not established** (spec "LOD-suffix family rule", medium). Computed once per definition.
    /// </summary>
    public IReadOnlyList<VisualPart> HighestLod() => _highest ??= ComputeHighestLod();
    IReadOnlyList<VisualPart>? _highest;

    List<VisualPart> ComputeHighestLod()
    {
        var parts = Visuals.Where(v => (v.LodLevel & 1) != 0).ToList();
        var best = new Dictionary<string, int>();
        foreach (var v in parts)
            if (LodFamily(v.RefNode) is { } f) best[f.Stem] = best.TryGetValue(f.Stem, out var r) ? Math.Min(r, f.Rank) : f.Rank;
        return parts.Where(v => LodFamily(v.RefNode) is not { } f || f.Rank == best[f.Stem]).ToList();
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
    /// <summary>The material texture parameters loaded into <see cref="Textures"/>; the others are not read (maps-visual-qa: the sky boxes' <c>YCoCg</c> texture is, for the sky drawing).</summary>
    public static readonly IReadOnlyList<string> TextureParameters = ["DiffuseMap", "NormalMap", "SpecularMap", "EmissiveMap", "DiffuseMap2", "NormalMap2", "AlphaMaskMap", "YCoCg"];
    /// <summary>The <c>l_lights</c> layer's sun, environment regions and placed lights (null: the map has none or it could not be read).</summary>
    public MapLighting? Lighting { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>The static level decals (<c>ScreenSpaceDecal</c> objects of the layers); empty when a map has none or the scene was built without them.</summary>
    public IReadOnlyList<PlacedDecal> Decals { get; init; } = [];
    /// <summary>The <c>ssdecal</c> entries of the archive by name (their textures are in <see cref="Textures"/>).</summary>
    public IReadOnlyDictionary<string, MapDecalDefinition> DecalDefinitions { get; init; } = new Dictionary<string, MapDecalDefinition>();

    /// <summary>The raw DDS bytes of a colour-grade material's <c>lookup</c> volume (a region's <see cref="ColourGradeDesc"/>); null when the material or texture is not in the archive.</summary>
    public byte[]? GradeLookup(string material) =>
        Materials.TryGetValue(material, out var m) && m.Textures.TryGetValue("lookup", out var name) && Textures.TryGetValue(name, out var bytes) ? bytes : null;

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

    public static MapScene Load(string gameDir, string map, bool extendedLighting = true)
    {
        if (map.Length == 0 || map.Length > 64 || map.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException($"'{map}' is not a map name.", nameof(map));
        foreach (var region in new[] { "wrld", "r000_main" })
        {
            string path = Path.Combine(gameDir, "data", "chunks", $"{map}__{region}.oppc");
            if (File.Exists(path)) return Read(path, map, extendedLighting);
        }
        throw new FileNotFoundException($"No region archive for '{map}'.");
    }

    public static MapScene Read(string archivePath, string map, bool extendedLighting = true)
    {
        var notes = new List<string>();
        var objects = new List<PlacedObject>();
        var decals = new List<PlacedDecal>();
        var decalDefinitions = new Dictionary<string, MapDecalDefinition>(StringComparer.OrdinalIgnoreCase);
        MapLighting? lighting = null;
        var definitions = new Dictionary<string, ObjectDefinition>(StringComparer.OrdinalIgnoreCase);
        using var archive = ObpkArchive.Open(archivePath);

        // Pass 1: layers and object definitions.
        archive.ReadEntries(e => archive.GroupName(e) is "Layer" or "O3d" or "ssdecal", (e, data) =>
        {
            try
            {
                var root = BodDocument.Read(data).Root;
                if (archive.GroupName(e) == "Layer")
                {
                    CollectObjects(root, e.FileName, Matrix4x4.Identity, objects, 0, decals);
                    if (extendedLighting ? e.FileName.StartsWith("l_lights", StringComparison.OrdinalIgnoreCase) : e.FileName.Equals("l_lights", StringComparison.OrdinalIgnoreCase))
                    {
                        var read = MapLighting.Read(root, extendedLighting);
                        lighting = lighting is null ? read : MapLighting.Combine([lighting, read]);
                    }
                }
                else if (archive.GroupName(e) == "ssdecal")
                {
                    if (MapDecalDefinition.Read(e.FileName, root) is { } decal) decalDefinitions.TryAdd(e.FileName, decal);
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
        // The level decals' textures (the definitions the placements name).
        var usedDecals = decals.Select(d => d.Decal).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in usedDecals.Order(StringComparer.Ordinal))
            if (decalDefinitions.TryGetValue(name, out var dd)) foreach (var t in dd.TextureNames) wanted.Add(t);
            else notes.Add($"decal '{name}' not in the archive; its {decals.Count(d => d.Decal.Equals(name, StringComparison.OrdinalIgnoreCase))} placements are not drawn");
        // The colour grades' look-up volumes (environment regions' ColourGradeEffectDesc: the material's "lookup" texture).
        foreach (var grade in lighting?.Environments.Select(e => e.Grade).OfType<ColourGradeDesc>() ?? [])
            if (materials.TryGetValue(grade.Material, out var gm) && gm.Textures.TryGetValue("lookup", out var lut)) wanted.Add(lut);
        var textures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        archive.ReadEntries(e => archive.GroupName(e) == "dds" && wanted.Contains(e.FileName), (e, data) => textures.TryAdd(e.FileName, data.ToArray()));

        return new MapScene
        {
            Map = map, Objects = objects, Definitions = definitions, Meshes = meshes, Materials = materials, Textures = textures, Lighting = lighting, Notes = notes, Decals = decals, DecalDefinitions = decalDefinitions,
        };
    }

    /// <summary>
    /// Enabled <c>StaticObject</c>s anywhere in a layer's tree. Objects nested as <c>ChildObjects</c> of a group are
    /// placed relative to it (the group's own transform, when it has one).
    /// </summary>
    static void CollectObjects(BodValue value, string layer, Matrix4x4 parent, List<PlacedObject> objects, int depth, List<PlacedDecal> decals, int childDepth = 0)
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
                else if (s.Class.Text == "ScreenSpaceDecal" && s["SSDName"] is BodString { Text: { Length: > 0 } decal } && PlacedDecalFrom(s, decal, layer, world) is { } placed)
                    decals.Add(placed);
                foreach (var f in s.Fields)
                    if (f.Name.Text is "Root" or "Objects" or "ChildObjects")
                        CollectObjects(f.Value, layer, f.Name.Text == "ChildObjects" ? world : parent, objects, depth + 1, decals, f.Name.Text == "ChildObjects" ? childDepth + 1 : childDepth);
                break;
            }
            case BodList l: foreach (var item in l.Items) CollectObjects(item.Value, layer, parent, objects, depth + 1, decals, childDepth); break;
            case BodRef r: CollectObjects(r.Value, layer, parent, objects, depth + 1, decals, childDepth); break;
        }
    }

    /// <summary>A <c>ScreenSpaceDecal</c>'s box: the world transform split into rotation and translation and the size (the scale's magnitudes); null for a degenerate transform.</summary>
    static PlacedDecal? PlacedDecalFrom(BodStruct s, string decal, string layer, Matrix4x4 world)
    {
        if (!Matrix4x4.Decompose(world, out var scale, out var rotation, out var translation)) return null;
        var size = Vector3.Abs(scale);
        if (!(size.X > 1e-4f && size.Y > 1e-4f && size.Z > 1e-4f)) return null;
        static bool Flag(BodStruct s, string name) => s[name] is BodBool { Value: true };
        return new PlacedDecal((s["Name"] as BodString)?.Text ?? "", decal, layer, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation), size,
            Flag(s, "OverrideColor") ? MapDecalDefinition.Rgb(s["Color"], Vector3.One) : null,
            Flag(s, "OverrideEmissiveColor") ? MapDecalDefinition.Rgb(s["EmissiveColor"], Vector3.One) : null,
            Flag(s, "OverrideOpacity") ? MapDecalDefinition.Number(s["Opacity"], 1f) : null,
            s["DrawOrder"] is BodInt { Value: var order } ? order : 0);
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
