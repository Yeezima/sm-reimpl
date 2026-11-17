using System.Numerics;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>A static object placed in a map layer: its definition (<c>.O3d</c>) and its world transform.</summary>
/// <param name="Depth">0 for objects listed directly in the layer; 1 and more for objects nested as <c>ChildObjects</c>.</param>
/// <param name="Package">The placement's <c>PackageName</c> (the package its definition comes from; empty when absent).</param>
/// <param name="Variant">classic-maps-fidelity: set when the archive holds more than one definition of <see cref="Definition"/> (the same
/// file name from different packages; 5 of the 11 maps) and the placement's <see cref="Package"/> names one of them: the key in
/// <see cref="MapScene.Variants"/> (use <see cref="MapScene.TryGetDefinition"/>). Null: the definition of that name in <see cref="MapScene.Definitions"/>.</param>
public sealed record PlacedObject(string Name, string Definition, string Layer, Matrix4x4 Transform, int Depth = 0, string Package = "", string? Variant = null);

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
    /// <summary>
    /// Every definition of a name the archive holds twice or more (the same <c>.O3d</c> file name from different packages), by
    /// <c>name@packageId</c> (<see cref="PlacedObject.Variant"/>); <see cref="Definitions"/> keeps the first of each name. Their meshes are in
    /// <see cref="Meshes"/> by <c>name@packageId</c> too when a mesh name is held twice.
    /// </summary>
    public IReadOnlyDictionary<string, ObjectDefinition> Variants { get; init; } = new Dictionary<string, ObjectDefinition>();

    /// <summary>The definition a placement draws: its <see cref="PlacedObject.Variant"/> when it has one, else the one of its name.</summary>
    public bool TryGetDefinition(PlacedObject placed, out ObjectDefinition definition)
    {
        if (placed.Variant is not null && Variants.TryGetValue(placed.Variant, out var variant)) { definition = variant; return true; }
        return Definitions.TryGetValue(placed.Definition, out definition!);
    }
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
        MapLighting? lighting = null;
        var definitions = new Dictionary<string, ObjectDefinition>(StringComparer.OrdinalIgnoreCase);
        var o3d = new Dictionary<string, List<(uint Id, ObjectDefinition Definition)>>(StringComparer.OrdinalIgnoreCase);
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
                    if (extendedLighting ? e.FileName.StartsWith("l_lights", StringComparison.OrdinalIgnoreCase) : e.FileName.Equals("l_lights", StringComparison.OrdinalIgnoreCase))
                    {
                        var read = MapLighting.Read(root, extendedLighting);
                        lighting = lighting is null ? read : MapLighting.Combine([lighting, read]);
                    }
                }
                else
                {
                    var read = Definition(e.FileName, root);
                    if (!o3d.TryGetValue(e.FileName, out var list)) o3d[e.FileName] = list = [];
                    list.Add((PackageId(archive, e), read));
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                // Navmesh and elevator layers use value types not decoded yet; they hold no static objects in the maps read.
                notes.Add($"{e.FileName}.{archive.GroupName(e)} skipped: {ex.Message}");
            }
        });

        // The same file name can be held more than once (from different packages, told apart by the entry's package id list): the
        // definition (and mesh) of the package a placement names is the one it draws.
        var duplicateMeshes = archive.Entries.Where(e => archive.GroupName(e) == "dcm").GroupBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(e => PackageId(archive, e)).Distinct().Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var meshKeys = archive.Entries.Where(e => archive.GroupName(e) == "dcm" && duplicateMeshes.Contains(e.FileName)).Select(e => MeshKey(e.FileName, PackageId(archive, e))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var variants = new Dictionary<string, ObjectDefinition>(StringComparer.OrdinalIgnoreCase);
        ResolveDuplicates(o3d, objects, meshKeys, definitions, variants);

        var used = objects.Select(o => o.Definition).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in used.Where(n => !definitions.ContainsKey(n)).Order(StringComparer.Ordinal))
            notes.Add($"object definition '{name}' not in the archive; its {objects.Count(o => o.Definition.Equals(name, StringComparison.OrdinalIgnoreCase))} placements are not drawn");
        var meshNames = definitions.Values.Concat(variants.Values).Where(d => used.Contains(d.Name)).SelectMany(d => d.Visuals).Select(v => v.MeshName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pass 2: meshes and materials.
        var meshes = new Dictionary<string, DcmMesh>(StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<string, BmatMaterial>(StringComparer.OrdinalIgnoreCase);
        archive.ReadEntries(e => (archive.GroupName(e) == "dcm" && (meshNames.Contains(e.FileName) || meshNames.Contains(MeshKey(e.FileName, PackageId(archive, e))))) || archive.GroupName(e) == "bmat", (e, data) =>
        {
            try
            {
                if (archive.GroupName(e) == "dcm")
                {
                    var mesh = DcmMesh.Read(data);
                    if (duplicateMeshes.Contains(e.FileName)) meshes.TryAdd(MeshKey(e.FileName, PackageId(archive, e)), mesh);
                    meshes.TryAdd(e.FileName, mesh);
                }
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
            Map = map, Objects = objects, Definitions = definitions, Variants = variants, Meshes = meshes, Materials = materials, Textures = textures, Lighting = lighting, Notes = notes,
        };
    }

    /// <summary>The package id of an archive entry: the first value of its id list (an entry's <c>Unknown</c> indexes the archive's id lists; one id each on every entry read), or <see cref="uint.MaxValue"/> when it has none.</summary>
    static uint PackageId(ObpkArchive archive, ObpkEntry e) =>
        e.Unknown < (uint)archive.IdLists.Count && archive.IdLists[(int)e.Unknown].Length > 0 ? archive.IdLists[(int)e.Unknown][0] : uint.MaxValue;

    /// <summary>The key of a definition or mesh that its name does not identify: <c>name@packageId</c>.</summary>
    static string MeshKey(string name, uint package) => $"{name}@{package}";

    /// <summary>
    /// Definitions of one name held by several entries of the archive: which one each placement draws. classic-maps-fidelity (2026-11-18; spec
    /// "Same-name definitions"): the archive keeps every package's <c>.O3d</c> and <c>.dcm</c> files, so a name can occur twice with different
    /// contents (Shattered Bridge 4 names, Desolation <c>GEO_wall</c>, Dome Mechanicus <c>High_Gothic_wall_A</c>, Manufactorum
    /// <c>Hab_wall_pipes</c>), an entry's package id (its id list) telling them apart; a placement's <c>PackageName</c> names its package. The
    /// package name to id pairs are learned from the placements of names that only one package defines (every such name gave one id per
    /// package name on the maps read, except <c>mp_maps_outreach</c> on Dome Mechanicus: 1,064 placements of one id and 54 of another, where
    /// the commoner id wins); a placement of a duplicated name whose package maps to one of its entries draws that entry. Without such a pair it
    /// keeps drawing the first, as before. The first entry of each name stays in <paramref name="definitions"/> (its meshes by package too).
    /// </summary>
    /// <param name="meshKeys">The <c>name@packageId</c> of every mesh entry whose name the archive holds more than once: a definition draws its own package's copy.</param>
    static void ResolveDuplicates(Dictionary<string, List<(uint Id, ObjectDefinition Definition)>> o3d, List<PlacedObject> objects, HashSet<string> meshKeys,
        Dictionary<string, ObjectDefinition> definitions, Dictionary<string, ObjectDefinition> variants)
    {
        ObjectDefinition WithOwnMeshes(ObjectDefinition d, uint id) => meshKeys.Count == 0 || !d.Visuals.Any(v => meshKeys.Contains(MeshKey(v.MeshName, id))) ? d
            : d with { Visuals = [.. d.Visuals.Select(v => meshKeys.Contains(MeshKey(v.MeshName, id)) ? v with { MeshName = MeshKey(v.MeshName, id) } : v)] };
        var duplicated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, list) in o3d)
        {
            definitions.TryAdd(name, WithOwnMeshes(list[0].Definition, list[0].Id));
            if (list.Select(x => x.Id).Distinct().Count() <= 1) continue;
            duplicated.Add(name);
            foreach (var (id, def) in list) variants.TryAdd(MeshKey(name, id), WithOwnMeshes(def, id));
        }
        if (duplicated.Count == 0) return;
        var learned = new Dictionary<string, Dictionary<uint, int>>(StringComparer.Ordinal);
        foreach (var o in objects)
            if (o.Package.Length > 0 && o3d.TryGetValue(o.Definition, out var one) && one.Select(x => x.Id).Distinct().Count() == 1 && one[0].Id != uint.MaxValue)
            {
                if (!learned.TryGetValue(o.Package, out var counts)) learned[o.Package] = counts = [];
                counts[one[0].Id] = counts.GetValueOrDefault(one[0].Id) + 1;
            }
        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (!duplicated.Contains(o.Definition) || !learned.TryGetValue(o.Package, out var counts)) continue;
            var best = o3d[o.Definition].Select(x => x.Id).Distinct().Where(counts.ContainsKey).OrderByDescending(id => counts[id]).ThenBy(id => id).Cast<uint?>().FirstOrDefault();
            if (best is { } id) objects[i] = o with { Variant = MeshKey(o.Definition, id) };
        }
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
                    objects.Add(new PlacedObject((s["Name"] as BodString)?.Text ?? "", definition, layer, world, childDepth, (s["PackageName"] as BodString)?.Text ?? ""));
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
