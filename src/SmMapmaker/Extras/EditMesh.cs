using System.Numerics;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// The editable shape of a piece (docs/mapmaker.md "Edit mode contract", owner-approved brief 2026-11-25 "Blender-style controls"). A piece without a stored mesh is its stock shape
/// (<see cref="Stock"/>) sized by the object's scale, exactly as before; a piece with one draws, collides and (for markers) bounds by these vertices instead.
/// <para><b>Space.</b> Vertices are in the piece's <i>unit</i> space: the object's local frame before its scale (origin at the bottom centre of the footprint, Z up; the stock shapes
/// span -0.5..0.5 in X and Y and 0..1 in Z). The object's scale multiplies them, as it multiplies the stock unit shape, so an unedited piece is unchanged and the Scale fields keep
/// their meaning (Blender's object scale). <b>Sized space</b> is unit space times the object's scale: metres along the object's own axes. Every edit and every dimension is measured
/// there (<see cref="MeshOps"/>, <see cref="MeshDims"/>).</para>
/// <para><b>Topology.</b> Fixed per piece kind and frozen (<see cref="Topology"/>; the stored <c>topology</c> name says which), so vertex, edge and face ids are stable for the
/// object's life: no extrusion or subdivision (out of the brief). Faces are polygons (a wall face is one quad, drawn as two triangles), wound counter-clockwise seen from outside.
/// Edge ids are the unique undirected edges in the order the faces first walk them (<see cref="Edges"/>).</para>
/// </summary>
public sealed class EditMesh
{
    /// <summary>Largest magnitude of a unit-space coordinate a stored mesh may have (a shared file is somebody else's input).</summary>
    public const float MaxUnitCoordinate = 10_000f;

    public MeshTopology Topology { get; }
    public IReadOnlyList<Vector3> Vertices { get; }
    /// <summary>Reserved (per-face material slots, proposed addition of the brief): slot per face, 0 = the object's material. Null: every face uses slot 0. Not used by any renderer yet.</summary>
    public IReadOnlyList<int>? FaceSlots { get; }

    public IReadOnlyList<int[]> Faces => Topology.Faces;
    public IReadOnlyList<(int A, int B)> Edges => Topology.Edges;

    public EditMesh(MeshTopology topology, IReadOnlyList<Vector3> vertices, IReadOnlyList<int>? faceSlots = null)
    {
        if (vertices.Count != topology.VertexCount) throw new MapException("bad-mesh", $"A '{topology.Name}' mesh has {topology.VertexCount} vertices, not {vertices.Count}.");
        foreach (var v in vertices)
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || MathF.Abs(v.X) > MaxUnitCoordinate || MathF.Abs(v.Y) > MaxUnitCoordinate || MathF.Abs(v.Z) > MaxUnitCoordinate)
                throw new MapException("bad-mesh", $"A mesh vertex is not finite or lies beyond ±{MaxUnitCoordinate:0} (unit space).");
        if (faceSlots is not null && (faceSlots.Count != topology.Faces.Count || faceSlots.Any(s => s is < 0 or > 15)))
            throw new MapException("bad-mesh", $"faceSlots must list one slot (0..15) per face ({topology.Faces.Count}).");
        Topology = topology;
        Vertices = vertices.ToArray();
        FaceSlots = faceSlots is null || faceSlots.All(s => s == 0) ? null : faceSlots.ToArray();   // all zero is the default: one way to write it
    }

    /// <summary>The stock shape of a piece (what it is without a stored mesh), in unit space.</summary>
    public static EditMesh Stock(PieceSpec piece) => Stock(MeshTopology.For(piece));

    public static EditMesh Stock(MeshTopology t) => new(t, t.StockVertices);

    /// <summary>The stored mesh of a piece, or its stock shape.</summary>
    public static EditMesh Of(MapObject o) =>
        o.Mesh ?? (o.Piece is { } p ? Stock(p) : throw new MapException("not-editable", $"'{o.Name}' ({o.Id}) is not a piece; only Build and gameplay pieces have an editable shape.", o.Id));

    public EditMesh WithVertices(IReadOnlyList<Vector3> vertices) => new(Topology, vertices, FaceSlots);

    /// <summary>True when this is exactly the stock shape (an edit that returns to it is stored as no mesh, so the map hashes as it did before).</summary>
    public bool IsStock => FaceSlots is null && Vertices.SequenceEqual(Topology.StockVertices);

    public int EdgeId(int a, int b) => Topology.EdgeId(a, b);

    /// <summary>The axis-aligned box of the unit-space vertices.</summary>
    public Bounds UnitBounds() => new(Vertices.Aggregate(Vector3.Min), Vertices.Aggregate(Vector3.Max));

    /// <summary>Triangles of the faces (each face a fan from its first vertex, in face order), and the face each triangle came from (picking: a hit triangle selects its whole face).</summary>
    public (Vector3[] Vertices, int[] Indices, int[] TriangleFace) Triangulate()
    {
        var idx = new List<int>();
        var tf = new List<int>();
        for (int f = 0; f < Faces.Count; f++)
        {
            var face = Faces[f];
            for (int k = 1; k + 1 < face.Length; k++) { idx.Add(face[0]); idx.Add(face[k]); idx.Add(face[k + 1]); tf.Add(f); }
        }
        return (Vertices.ToArray(), idx.ToArray(), tf.ToArray());
    }

    public JsonObject ToNode()
    {
        var o = new JsonObject { ["topology"] = Topology.Name, ["vertices"] = new JsonArray(Vertices.Select(v => (JsonNode)Json.Vec(v)).ToArray()) };
        if (FaceSlots is not null) o["faceSlots"] = new JsonArray(FaceSlots.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
        return o;
    }

    /// <summary>Reads a stored mesh; its topology must be the one <paramref name="piece"/> has (<c>code</c> otherwise).</summary>
    public static EditMesh FromNode(JsonNode? n, PieceSpec piece, string what, string code)
    {
        var o = Json.Obj(n, what, code);
        var name = Json.ReqStr(o, "topology", what, code);
        var expected = MeshTopology.For(piece);
        if (name != expected.Name) throw new MapException(code, $"{what}.topology '{name}' is not the shape of a {piece.Info.Label.ToLowerInvariant()} ('{expected.Name}').");
        var verts = Json.Arr(o["vertices"], what + ".vertices", code);
        if (verts.Count != expected.VertexCount) throw new MapException(code, $"{what}: a '{name}' mesh has {expected.VertexCount} vertices, not {verts.Count}.");
        var list = verts.Select((v, i) => Json.Vec(v, $"{what}.vertices[{i}]", code)).ToList();
        List<int>? slots = o["faceSlots"] is { } sn ? Json.Arr(sn, what + ".faceSlots", code).Select((s, i) => Json.Int(s, $"{what}.faceSlots[{i}]", code)).ToList() : null;
        try { return new EditMesh(expected, list, slots); }
        catch (MapException e) { throw new MapException(code, $"{what}: {e.Message}"); }
    }

    public bool SameAs(EditMesh? other) =>
        other is not null && other.Topology == Topology && other.Vertices.SequenceEqual(Vertices) && (other.FaceSlots ?? []).SequenceEqual(FaceSlots ?? []);
}

/// <summary>
/// One frozen piece topology (contract: never change a listed one; add a new name instead). Vertex order, face order and face winding are part of the file format.
/// <list type="bullet">
/// <item><c>box</c> (block, invisible wall, cover, spawn area, spawn door, kill zone, box movement wall): vertex i at x = i&amp;1 ? +0.5 : -0.5, y = i&amp;2 ? +0.5 : -0.5, z = i&amp;4 ? 1 : 0
/// (the same 8 corners as <see cref="PieceMesh"/>); faces 0 bottom (-Z), 1 top (+Z), 2 -Y, 3 +Y, 4 -X, 5 +X.</item>
/// <item><c>ramp</c>: vertices 0 (-,-,0) 1 (+,-,0) 2 (+,+,0) 3 (-,+,0) 4 (+,+,1) 5 (-,+,1) (the ramp of <see cref="PieceMesh"/>); faces 0 bottom, 1 slope, 2 back (+Y), 3 left (-X), 4 right (+X).</item>
/// <item><c>column12</c> (column) and <c>cylinder16</c> (capture point, round movement wall): an N-sided prism inscribed in the unit circle (radius 0.5, so an unedited piece measures exactly its
/// scale); bottom ring 0..N-1 at the deterministic angles <see cref="DetMath.Ring"/>, top ring N..2N-1 above it; faces 0 bottom, 1 top, 2 + i the side between ring steps i and i + 1.
/// column12 has the column's own 12 sides; cylinder16 the 16 sides of Classic's round walls (inscribed here, where <see cref="CustomMap.WallMesh"/> circumscribes: ours, docs/changes-from-original.md).</item>
/// </list>
/// </summary>
public sealed class MeshTopology
{
    public string Name { get; }
    public IReadOnlyList<Vector3> StockVertices { get; }
    public IReadOnlyList<int[]> Faces { get; }
    public IReadOnlyList<(int A, int B)> Edges { get; }
    /// <summary>Short names of the faces for tools (<c>+x</c>, <c>top</c>...), one per face; may be empty strings.</summary>
    public IReadOnlyList<string> FaceNames { get; }
    public int VertexCount => StockVertices.Count;
    readonly Dictionary<(int, int), int> _edgeIds = [];

    MeshTopology(string name, Vector3[] vertices, int[][] faces, string[] faceNames)
    {
        Name = name; StockVertices = vertices; Faces = faces; FaceNames = faceNames;
        var edges = new List<(int, int)>();
        foreach (var f in faces)
            for (int k = 0; k < f.Length; k++)
            {
                int a = f[k], b = f[(k + 1) % f.Length];
                var key = a < b ? (a, b) : (b, a);
                if (_edgeIds.TryAdd(key, edges.Count)) edges.Add(key);
            }
        Edges = edges;
    }

    public int EdgeId(int a, int b) => _edgeIds.TryGetValue(a < b ? (a, b) : (b, a), out var id) ? id : -1;

    /// <summary>The face with this tool name (<c>+x</c>, <c>-z</c>, <c>top</c>, <c>bottom</c>...) or face number, or -1.</summary>
    public int FaceNamed(string name)
    {
        if (int.TryParse(name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) return n >= 0 && n < Faces.Count ? n : -1;
        for (int i = 0; i < FaceNames.Count; i++)
            foreach (var alias in FaceNames[i].Split('|'))
                if (alias.Length > 0 && string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public static readonly MeshTopology Box = MakeBox();
    public static readonly MeshTopology Ramp = MakeRamp();
    public static readonly MeshTopology Column12 = MakePrism("column12", 12);
    public static readonly MeshTopology Cylinder16 = MakePrism("cylinder16", 16);
    public static readonly IReadOnlyList<MeshTopology> All = [Box, Ramp, Column12, Cylinder16];

    /// <summary>The topology of a piece's shape. Every piece kind has one (every Build and gameplay category is editable).</summary>
    public static MeshTopology For(PieceSpec piece) => piece.Kind switch
    {
        PieceKind.Ramp => Ramp,
        PieceKind.Column => Column12,
        PieceKind.CapturePoint => Cylinder16,
        PieceKind.MoveWall when piece.IsCylinder => Cylinder16,
        _ => Box,
    };

    static MeshTopology MakeBox()
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? 0f : 1f);
        int[][] f = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        return new MeshTopology("box", v, f, ["-z|bottom", "+z|top", "-y|front", "+y|back", "-x|left", "+x|right"]);
    }

    static MeshTopology MakeRamp()
    {
        Vector3[] v = [new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0), new(0.5f, 0.5f, 0), new(-0.5f, 0.5f, 0), new(0.5f, 0.5f, 1), new(-0.5f, 0.5f, 1)];
        int[][] f = [[0, 3, 2, 1], [0, 1, 4, 5], [3, 5, 4, 2], [0, 5, 3], [1, 2, 4]];
        return new MeshTopology("ramp", v, f, ["-z|bottom", "slope|top", "+y|back", "-x|left", "+x|right"]);
    }

    static MeshTopology MakePrism(string name, int n)
    {
        var v = new Vector3[2 * n];
        for (int i = 0; i < n; i++)
        {
            var (c, s) = DetMath.Ring(i, n);   // deterministic: the stock vertices are the same bits on every OS
            v[i] = new Vector3((float)(0.5 * c), (float)(0.5 * s), 0);
            v[n + i] = new Vector3((float)(0.5 * c), (float)(0.5 * s), 1);
        }
        var faces = new List<int[]> { Enumerable.Range(0, n).Reverse().ToArray(), Enumerable.Range(n, n).ToArray() };
        var names = new List<string> { "-z|bottom", "+z|top" };
        for (int i = 0; i < n; i++) { int j = (i + 1) % n; faces.Add([i, j, n + j, n + i]); names.Add("side" + i); }
        return new MeshTopology(name, v, [.. faces], [.. names]);
    }
}
