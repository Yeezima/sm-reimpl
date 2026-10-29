using System.Numerics;
using SmFormats.Bod;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>A node of an <c>.O3d</c> object tree: its parent and its transform relative to the parent (row-vector).</summary>
public sealed record ObjectTreeNode(string Name, string? Parent, Matrix4x4 Local);

/// <summary>A mesh visual of an <c>.O3d</c> (<c>StaticMeshVisual</c> or <c>SkinMeshVisual</c>): which block of which mesh hangs from which node.</summary>
/// <param name="LodLevel">The visual's LODLevel: -1 or a bit mask of detail levels; bit 0 (the nearest level) is drawn
/// (as for map objects, <see cref="ObjectDefinition"/>).</param>
public sealed record ObjectTreeVisual(string Class, string RefNode, string MeshName, int MeshId, int LodLevel, bool Renderable)
{
    public bool IsSkinned => Class == "SkinMeshVisual";
    /// <summary>Whether the nearest detail level draws this visual (LODLevel -1 or bit 0 set; "_LODL" nodes carry 16).</summary>
    public bool NearLevel => LodLevel == -1 || (LodLevel & 1) != 0;
}

/// <summary>
/// An <c>.O3d</c> object definition read as a node hierarchy (Skeleton → Children, each with Position, Rotation in degrees
/// about X then Y then Z, and Scale; <see cref="MapScene.Transform"/>) plus its mesh visuals. Node names are matched
/// ignoring case (the wargear names nodes in lower case, the trees in mixed case). Read-only; nothing is written.
/// </summary>
public sealed class ObjectTree
{
    readonly Dictionary<string, ObjectTreeNode> _nodes;
    readonly Dictionary<string, Matrix4x4> _model = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; }
    /// <summary>Every named node, by name (case-insensitive; the first of duplicate names wins).</summary>
    public IReadOnlyDictionary<string, ObjectTreeNode> Nodes => _nodes;
    /// <summary>Node names in file order (depth first).</summary>
    public IReadOnlyList<string> Order { get; }
    public IReadOnlyList<ObjectTreeVisual> Visuals { get; }

    ObjectTree(string name, Dictionary<string, ObjectTreeNode> nodes, IReadOnlyList<string> order, IReadOnlyList<ObjectTreeVisual> visuals)
    {
        Name = name; _nodes = nodes; Order = order; Visuals = visuals;
    }

    /// <summary>Reads the <c>.O3d</c> entry <paramref name="name"/> of an archive; null when the archive has none by that name.</summary>
    /// <param name="notes">Receives a note for each node whose Position, Rotation or Scale is malformed (the default is used).</param>
    /// <exception cref="FileNotFoundException">The archive does not exist.</exception>
    /// <exception cref="InvalidDataException">The archive or the object tree is malformed, or the root is not an Object3D.</exception>
    /// <exception cref="NotSupportedException">The document uses a BOD layout not decoded yet.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ObjectTree? Load(string archivePath, string name, List<string>? notes = null)
    {
        BodValue? root = null;
        using (var archive = ObpkArchive.Open(archivePath))
            archive.ReadEntries(e => root is null && archive.GroupName(e).Equals("O3d", StringComparison.OrdinalIgnoreCase)
                    && e.FileName.Equals(name, StringComparison.OrdinalIgnoreCase),
                (_, data) => root = BodDocument.Read(data).Root);
        return root is null ? null : From(name, root, notes);
    }

    /// <exception cref="InvalidDataException">The root is not an Object3D.</exception>
    public static ObjectTree From(string name, BodValue root, List<string>? notes = null)
    {
        if (Unref(root) is not BodStruct { Class.Text: "Object3D" } o) throw new InvalidDataException($"'{name}' is not an Object3D.");
        var nodes = new Dictionary<string, ObjectTreeNode>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        void Walk(BodValue? value, string? parent, int depth)
        {
            if (depth > 64 || value is null || Unref(value) is not BodStruct s || s["Name"] is not BodString { Text: { Length: > 0 } n }) return;
            var local = MapScene.Transform(Floats(s["Position"], 0f, name, n, "Position", notes), Floats(s["Rotation"], 0f, name, n, "Rotation", notes),
                Floats(s["Scale"], 1f, name, n, "Scale", notes));
            if (nodes.TryAdd(n, new ObjectTreeNode(n, parent, local))) order.Add(n);
            foreach (var c in Items(s["Children"])) Walk(c, n, depth + 1);
        }
        Walk(o["Skeleton"], null, 0);
        var visuals = new List<ObjectTreeVisual>();
        foreach (var v in Items(o["Visuals"]).OfType<BodStruct>())
        {
            if (v["MeshName"] is not BodString { Text: { Length: > 0 } mesh } || v["MeshID"] is not BodInt { Value: var id }) continue;
            visuals.Add(new ObjectTreeVisual(v.Class.Text ?? "", (v["RefNode"] as BodString)?.Text ?? "", mesh, id,
                (v["LODLevel"] as BodInt)?.Value ?? -1, v["Renderable"] is not BodBool { Value: false }));
        }
        return new ObjectTree(name, nodes, order, visuals);
    }

    public bool Contains(string node) => _nodes.ContainsKey(node);

    /// <summary>The node's transform in the tree's root space (row-vector: its local, then each parent's); identity for an unknown name.</summary>
    public Matrix4x4 ModelSpace(string node)
    {
        if (_model.TryGetValue(node, out var cached)) return cached;
        var m = Matrix4x4.Identity;
        int guard = 0;
        for (string? at = node; at is not null && guard++ < 128 && _nodes.TryGetValue(at, out var n); at = n.Parent) m *= n.Local;
        return _model[node] = m;
    }

    /// <summary>
    /// The nearest ancestor of <paramref name="node"/> (or the node itself) that <paramref name="isBone"/> accepts, and the node's
    /// transform relative to it; null when no ancestor is accepted or the node is unknown.
    /// </summary>
    public (string Bone, Matrix4x4 Local)? UnderBone(string node, Func<string, bool> isBone)
    {
        var m = Matrix4x4.Identity;
        int guard = 0;
        for (string? at = node; at is not null && guard++ < 128 && _nodes.TryGetValue(at, out var n); at = n.Parent)
        {
            if (isBone(n.Name)) return (n.Name, m);
            m *= n.Local;
        }
        return null;
    }

    static BodValue Unref(BodValue v) { while (v is BodRef r) v = r.Value; return v; }
    static IEnumerable<BodValue> Items(BodValue? v) => v is null ? [] : Unref(v) switch
    {
        BodList l => l.Items.Select(i => Unref(i.Value)),
        BodArray a => a.Items.Select(Unref),
        _ => [],
    };
    /// <summary>
    /// Three numbers (floats, or integers as some writers store whole values); otherwise the default, with a note when the field
    /// exists but is malformed (a missing field defaults silently, as in the map layers).
    /// </summary>
    static Vector3 Floats(BodValue? v, float fallback, string tree, string node, string field, List<string>? notes)
    {
        if (v is null || Unref(v) is BodNull) return new Vector3(fallback);
        var items = Items(v).ToList();
        var f = items.Select(x => x switch { BodFloat bf => bf.Value, BodInt bi => (float?)bi.Value, _ => null }).ToList();
        if (f.Count == 3 && f.All(x => x is { } y && float.IsFinite(y))) return new Vector3(f[0]!.Value, f[1]!.Value, f[2]!.Value);
        notes?.Add($"{tree}: node '{node}' {field} is not three numbers ({items.Count} items); {fallback} is used");
        return new Vector3(fallback);
    }
}
