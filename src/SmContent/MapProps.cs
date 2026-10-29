using System.Numerics;
using SmFormats.Bod;
using SmFormats.Obpk;
using SmFormats.Ppk;
using SmSim;

namespace SmContent;

/// <summary>
/// A movable prop placed in a map (M39), in world space: the placement's name, its definition, the layer it is in, the body's
/// surface id (<c>CShapeMesh.MaterialID</c>) and its shape's triangles placed as the object (node transform, then placement).
/// </summary>
public sealed record MapProp(string Name, string Definition, string Layer, int Surface, Vector3[] Vertices, int[] Indices)
{
    public CollisionProp ToSim() => new(Name, Vertices, Indices, Surface);
}

/// <summary>
/// Reads a region archive's movable props (M39): every enabled <c>StaticObject</c> in any layer (nested <c>ChildObjects</c>
/// placed relative to their parent, as <see cref="MapScene"/>) whose <c>.O3d</c> definition has a rigid body with a
/// <c>MotionType</c> other than 0 (a moving body; the level's static bodies have 0) whose <c>CShapeMesh.MeshName</c> is a
/// single-part physics shape mesh (<c>ppk</c> entry, <see cref="PpkMesh"/>) in the archive. Those shapes are not in the
/// level's baked collision (<c>&lt;region&gt;.hkt</c>). Manufactorum: the green <c>ammo_crate_A</c> (49 in <c>e_cover</c>; body
/// <c>MotionType</c> 2, <c>Mass</c> 70, <c>BodyType</c> 3, shape <c>ammo_crate_A__d</c>, a 1.17 × 3.02 × 1.47 m box) and
/// <c>accessories_doorbutton</c> (4 wall panels about 6.6 m up; <c>BodyType</c> 2 and 8 on one shape). Bodies sharing a node
/// and shape (the door button's two) give one prop. Placements with <c>DisableCollision</c>,
/// <c>DisableBulletCollision</c> or <c>DisableCharacterCollision</c> set are skipped with a note (none on Manufactorum).
/// </summary>
public static class MapProps
{
    /// <summary>The props, in layer name order then tree order, and the bytes they were read from (for the map hash: the layers that hold them, their definitions and shapes, by name).</summary>
    public static (List<MapProp> Props, List<byte[]> Sources) Read(ObpkArchive archive, List<string> notes)
    {
        var placements = new List<Placement>();
        var layerBytes = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        archive.ReadEntries(e => archive.GroupName(e) == "Layer", (e, data) =>
        {
            BodValue root;
            try { root = BodDocument.Read(data).Root; }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or NotSupportedException or OverflowException)
            {
                return;   // navmesh and elevator layers use value types not decoded yet; they hold no static objects in the maps read
            }
            var found = new List<Placement>();
            Collect(root, e.FileName, Matrix4x4.Identity, found, 0);
            if (found.Count > 0) { layerBytes[e.FileName] = data.ToArray(); placements.AddRange(found); }
        });
        placements = [.. placements.OrderBy(p => p.Layer, StringComparer.Ordinal)];   // a stable sort: tree order within a layer

        var wanted = placements.Select(p => p.Definition).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = new SortedDictionary<string, (List<Body> Bodies, byte[] Bytes)>(StringComparer.OrdinalIgnoreCase);
        archive.ReadEntries(e => archive.GroupName(e) == "O3d" && wanted.Contains(e.FileName), (e, data) =>
        {
            try
            {
                var bodies = Bodies(BodDocument.Read(data).Root);
                if (bodies.Count > 0) definitions.TryAdd(e.FileName, (bodies, data.ToArray()));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or NotSupportedException or OverflowException)
            {
                notes.Add($"{e.FileName}.O3d unreadable: {ex.Message}");
            }
        });

        var shapeNames = definitions.Values.SelectMany(d => d.Bodies).Select(b => b.Shape).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shapes = new SortedDictionary<string, (PpkMesh Mesh, byte[] Bytes)>(StringComparer.OrdinalIgnoreCase);
        archive.ReadEntries(e => archive.GroupName(e) == "ppk" && shapeNames.Contains(e.FileName), (e, data) =>
        {
            try { shapes.TryAdd(e.FileName, (PpkMesh.Read(data), data.ToArray())); }
            catch (InvalidDataException ex) { notes.Add($"{e.FileName}.ppk not read: {ex.Message}"); }
        });

        var props = new List<MapProp>();
        var usedDefinitions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedShapes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (layer, name, definition, world, off) in placements)
        {
            if (!definitions.TryGetValue(definition, out var d)) continue;
            if (off.Length > 0) { notes.Add($"prop {name} ({definition}): {off} set; skipped"); continue; }
            foreach (var body in d.Bodies)
            {
                if (!shapes.TryGetValue(body.Shape, out var shape)) { notes.Add($"{name} ({definition}): shape {body.Shape} not in the archive"); continue; }
                var m = body.Node * world;
                var vertices = shape.Mesh.Vertices.Select(v => Vector3.Transform(v, m)).ToArray();
                if (vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))) { notes.Add($"{name}: placed shape is not finite; skipped"); continue; }
                props.Add(new MapProp(name, definition, layer, body.Surface, vertices, [.. shape.Mesh.Indices]));
                usedDefinitions.Add(definition);
                usedShapes.Add(body.Shape);
            }
        }
        var sources = new List<byte[]>();
        sources.AddRange(layerBytes.Where(kv => props.Any(p => p.Layer == kv.Key)).Select(kv => kv.Value));
        sources.AddRange(usedDefinitions.Select(n => definitions[n].Bytes));
        sources.AddRange(usedShapes.Select(n => shapes[n].Bytes));
        return (props, sources);
    }

    /// <summary>A moving rigid body: its node's transform within the object, its shape mesh name and surface id.</summary>
    readonly record struct Body(Matrix4x4 Node, string Shape, int Surface);

    /// <summary>Rigid bodies with a <c>MotionType</c> other than 0 and a named shape mesh; one per (node, shape).</summary>
    static List<Body> Bodies(BodValue root)
    {
        if (root is not BodStruct { Class.Text: "Object3D" } o) return [];
        var nodes = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        CollectNodes(Unwrap(o["Skeleton"]), Matrix4x4.Identity, nodes, 0);
        var bodies = new List<Body>();
        var seen = new HashSet<(string, string)>();
        foreach (var b in Items(o["Bodies"]).OfType<BodStruct>())
        {
            if (b["MotionType"] is not BodInt { Value: not 0 }) continue;
            if (Unwrap(b["Shape"]) is not BodStruct { Class.Text: "CShapeMesh" } shape || shape["MeshName"] is not BodString { Text: { Length: > 0 } mesh }) continue;
            string node = (b["NodeName"] as BodString)?.Text ?? "";
            if (!seen.Add((node.ToLowerInvariant(), mesh.ToLowerInvariant()))) continue;
            bodies.Add(new Body(nodes.GetValueOrDefault(node, Matrix4x4.Identity), mesh, shape["MaterialID"] is BodInt { Value: var id } ? id : -1));
        }
        return bodies;
    }

    /// <summary>Enabled <c>StaticObject</c>s anywhere in a layer's tree (a disabled object hides its children too), as <see cref="MapScene"/> places them.</summary>
    static void Collect(BodValue value, string layer, Matrix4x4 parent, List<Placement> found, int depth)
    {
        if (depth > 64) return;
        switch (value)
        {
            case BodStruct s:
            {
                if (s["Disabled"] is BodBool { Value: true }) return;
                var world = MapScene.Local(s) * parent;
                if (s.Class.Text == "StaticObject" && s["ObjectName"] is BodString { Text: { Length: > 0 } definition })
                {
                    string name = (s["Name"] as BodString)?.Text ?? "";
                    var off = new[] { "DisableCollision", "DisableBulletCollision", "DisableCharacterCollision" }.Where(f => s[f] is BodBool { Value: true }).ToList();
                    found.Add(new Placement(layer, name, definition, world, string.Join(", ", off)));
                }
                foreach (var f in s.Fields)
                    if (f.Name.Text is "Root" or "Objects" or "ChildObjects")
                        Collect(f.Value, layer, f.Name.Text == "ChildObjects" ? world : parent, found, depth + 1);
                break;
            }
            case BodList l: foreach (var item in l.Items) Collect(item.Value, layer, parent, found, depth + 1); break;
            case BodRef r: Collect(r.Value, layer, parent, found, depth + 1); break;
        }
    }

    /// <summary>A placed object: layer, name, definition, world transform, and the collision switches set on it (empty: none).</summary>
    readonly record struct Placement(string Layer, string Name, string Definition, Matrix4x4 World, string Off);

    static void CollectNodes(BodValue? value, Matrix4x4 parent, Dictionary<string, Matrix4x4> nodes, int depth)
    {
        if (depth > 64 || value is not BodStruct s) return;
        var world = MapScene.Local(s) * parent;
        if (s["Name"] is BodString { Text: { } name }) nodes.TryAdd(name, world);
        foreach (var child in Items(s["Children"])) CollectNodes(child, world, nodes, depth + 1);
    }

    static BodValue? Unwrap(BodValue? v) => v is BodRef r ? r.Value : v;
    static IEnumerable<BodValue> Items(BodValue? v) => v is BodList l ? l.Items.Select(i => i.Value) : [];
}
